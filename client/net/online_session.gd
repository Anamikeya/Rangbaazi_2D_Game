class_name OnlineSession
extends RefCounted
## One online 1v1 through the relay (server/Rangbaazi.Relay): the scene calls tick() once per
## physics tick with the local buttons and draws `state`.
##
## Phases: CONNECTING (to the relay) -> WAITING_FOR_OPPONENT (alone in the room) -> STARTING (the
## relay paired us and gave a seed; exchanging hellos: same protocol, rules and sim, or the match
## is refused) -> RUNNING -> ENDED (end_message says why; the connection is closed). rematch()
## reconnects to the same room for a new match once the opponent does too.
##
## While running, every 30 frames each side sends the checksum of a frame whose inputs are final
## on its side; the other compares once that frame is final there too. A difference is a desync,
## found at the frame it shows, and ends the match on both sides.
##
## The relay connection is a WebSocketPeer, or any object with the same methods (poll,
## get_ready_state, send, get_available_packet_count, get_packet, was_string_packet, close),
## which is how the tests run two sessions against an in-memory relay.

enum Phase { CONNECTING, WAITING_FOR_OPPONENT, STARTING, RUNNING, ENDED }

const PREDICTION := 8
## Timeouts in ticks (60 per second).
const CONNECT_TIMEOUT := 10 * 60
const INTERRUPTED_AFTER := 60
const LOST_AFTER := 8 * 60
const STALL_TIMEOUT := 15 * 60
const PING_INTERVAL := 60
const START_TIMEOUT := 10 * 60
## The relay closes connections it hears nothing from for 60 s (a player waiting alone sends nothing else).
const KEEPALIVE_INTERVAL := 20 * 60
## While starting, the hello is repeated this often, in case one is lost.
const HELLO_INTERVAL := 30

var phase: int = Phase.CONNECTING
var room := ""
var local_slot := -1
var state: GameState
var rules := MatchRules.new()
var end_message := ""
var interrupted := false
var stalled := false
var ping_ms := 0
var frames_behind := 0
var rollback_last := 0
var rollback_max := 0
var rollback_median := 0
var checksums_verified := 0

## The rollback core of the current match (null before the relay starts one).
var core: RollbackCore
## Test hook, passed to each match's RollbackCore.
var after_advance: Callable

var _url := ""
var _input_delay := 2
var _socket_factory: Callable
var _ws: Object
var _tick := 0
var _phase_tick := 0
var _last_heard_tick := 0
var _stall_ticks := 0
var _skip_ticks := 0
var _peer_hello := false
var _sent_hello := false
var _last_hello_tick := -1000000


## relay_url: e.g. wss://example.cloudfront.net/relay/room/ABCD (see relay_url_for_room).
## socket_factory: optional Callable returning a WebSocketPeer-like object (tests).
func _init(relay_url: String, room_code: String, input_delay: int = 2, socket_factory: Callable = Callable()) -> void:
	_url = relay_url
	room = room_code.to_upper()
	rules.flurry = true
	rules.hazards = true
	rules.weapons = true # duels have weapons; no signature bonus, since peers don't swap fighters yet
	_input_delay = input_delay
	_socket_factory = socket_factory
	_connect()


## The relay URL for a room. On the web: the same host as the page (CloudFront sends /relay/* to
## the relay server). Elsewhere (Android, desktop tests): --relay <ws(s)://host> on the command
## line, else the address saved in res://net/relay_base_url.txt by the deploy script.
static func relay_url_for_room(room_code: String) -> String:
	var base := ""
	var args := OS.get_cmdline_user_args()
	var i := args.find("--relay")
	if i >= 0 and i + 1 < args.size():
		base = args[i + 1]
	elif OS.has_feature("web"):
		var protocol: String = str(JavaScriptBridge.eval("window.location.protocol"))
		var host: String = str(JavaScriptBridge.eval("window.location.host"))
		base = ("wss://" if protocol == "https:" else "ws://") + host
		# Local testing only: ?relay=ws://127.0.0.1:<port> points a locally served build at a local relay.
		var param: String = str(JavaScriptBridge.eval("new URLSearchParams(window.location.search).get('relay') || ''"))
		if param.begins_with("ws://127.0.0.1:") or param.begins_with("ws://localhost:"):
			base = param
	elif FileAccess.file_exists("res://net/relay_base_url.txt"):
		base = FileAccess.get_file_as_string("res://net/relay_base_url.txt").strip_edges()
	else:
		base = "ws://127.0.0.1:8080"
	return "%s/relay/room/%s" % [base.trim_suffix("/"), room_code.to_upper().uri_encode()]


func tick(local_buttons: int) -> void:
	_tick += 1
	stalled = false
	if phase == Phase.ENDED:
		return
	_ws.poll()
	var ready: int = _ws.get_ready_state()
	if ready == WebSocketPeer.STATE_CONNECTING:
		if _tick - _phase_tick > CONNECT_TIMEOUT:
			_end("Couldn't reach the game server.", -1)
		return
	if phase == Phase.STARTING and _tick - _phase_tick > START_TIMEOUT:
		_end("Couldn't start the match: your opponent's game didn't answer.", NetProtocol.Bye.GAVE_UP)
		return
	if phase == Phase.STARTING and _tick - _last_hello_tick >= HELLO_INTERVAL:
		_send_hello()
	if ready == WebSocketPeer.STATE_OPEN and _tick % KEEPALIVE_INTERVAL == 0:
		_ws.send_text("{\"type\":\"ping\"}")
	while phase != Phase.ENDED and _ws.get_available_packet_count() > 0:
		var packet: PackedByteArray = _ws.get_packet()
		if _ws.was_string_packet():
			_on_relay_message(packet.get_string_from_utf8())
		else:
			_on_peer_message(packet)
	if phase != Phase.ENDED and ready != WebSocketPeer.STATE_OPEN:
		_end("Couldn't reach the game server." if phase == Phase.CONNECTING else "Lost the connection to the game server.", -1)
		return
	if phase == Phase.RUNNING:
		_run(local_buttons)


## Leaves this match (asking the opponent for a rematch) and joins the room again for the next one.
func rematch() -> void:
	if phase != Phase.ENDED:
		_end("You asked for a rematch.", NetProtocol.Bye.REMATCH)
	_connect()


func close() -> void:
	if phase != Phase.ENDED:
		_end("You left the match.", NetProtocol.Bye.LEFT)


# ---- relay messages ----

func _on_relay_message(text: String) -> void:
	var msg: Variant = JSON.parse_string(text)
	if typeof(msg) != TYPE_DICTIONARY:
		return
	match str(msg.get("type", "")):
		"welcome":
			local_slot = int(msg.get("slot", -1))
			if phase == Phase.CONNECTING:
				_set_phase(Phase.WAITING_FOR_OPPONENT)
		"start":
			_start_match(str(msg.get("seed", "0")).to_int(), int(msg.get("slot", local_slot)))
		"peer_left":
			if phase == Phase.RUNNING:
				_end("Your opponent left.", -1)
			elif phase == Phase.STARTING:
				# The match never began (e.g. the other player was still leaving the last one):
				# wait for the next opponent instead of giving up.
				core = null
				state = null
				_set_phase(Phase.WAITING_FOR_OPPONENT)
		"full":
			_end("Room %s already has two players. Try another code." % room, -1)
		"busy":
			_end("The game server is busy. Try again in a minute.", -1)
		"error":
			_end("The game server refused the room: %s" % str(msg.get("message", "")), -1)


func _start_match(seed: int, slot: int) -> void:
	local_slot = slot
	core = RollbackCore.new(rules, seed, slot, _input_delay, PREDICTION)
	core.after_advance = after_advance
	state = core.state
	_peer_hello = false
	_sent_hello = true
	_stall_ticks = 0
	_skip_ticks = 0
	interrupted = false
	_set_phase(Phase.STARTING)
	_last_heard_tick = _tick
	print("[online] match seed %d, P%d" % [seed, slot + 1])
	_send_hello()


# ---- peer messages ----

func _on_peer_message(bytes: PackedByteArray) -> void:
	var msg := NetProtocol.decode(bytes)
	if msg.is_empty() or core == null:
		print("[online] dropped a %d-byte message (%s)" % [bytes.size(), "no match yet" if core == null else "malformed"])
		return
	if int(msg["kind"]) == NetProtocol.Kind.HELLO:
		print("[online] hello from the opponent (phase %s)" % Phase.keys()[phase])
	_last_heard_tick = _tick
	match int(msg["kind"]):
		NetProtocol.Kind.HELLO:
			if int(msg["version"]) != NetProtocol.VERSION:
				_end("Your opponent has a different version of the game.", NetProtocol.Bye.REJECTED)
			elif int(msg["fingerprint"]) != _fingerprint():
				_end("Your opponent's game plays by different rules (a different version or settings).", NetProtocol.Bye.REJECTED)
			elif phase == Phase.STARTING:
				_peer_hello = true
				_begin_running()
			elif phase == Phase.RUNNING and _tick - _last_hello_tick >= HELLO_INTERVAL:
				_send_hello() # the opponent is still starting: it may have missed ours
		NetProtocol.Kind.INPUT:
			if phase == Phase.RUNNING and not core.receive_remote(msg["frame"], msg["buttons"], msg["sender_frame"], msg["advantage"]):
				_end("The connection to your opponent broke (inputs out of order).", NetProtocol.Bye.ERROR)
		NetProtocol.Kind.CHECKSUM:
			core.receive_checksum(msg["frame"], msg["value"])
		NetProtocol.Kind.BYE:
			match int(msg["reason"]):
				NetProtocol.Bye.REMATCH: _end("Your opponent wants a rematch: press R.", -1)
				NetProtocol.Bye.DESYNC: _end("Your opponent's game found a desync.", -1)
				NetProtocol.Bye.REJECTED: _end("Your opponent's game refused the match (a different version or settings).", -1)
				NetProtocol.Bye.GAVE_UP: _end("Your opponent stopped waiting for you.", -1)
				NetProtocol.Bye.ERROR: _end("Your opponent's game hit an error.", -1)
				_: _end("Your opponent left.", -1)
		NetProtocol.Kind.PING:
			_send(NetProtocol.ping(NetProtocol.Kind.PONG, msg["stamp"]))
		NetProtocol.Kind.PONG:
			ping_ms = (Time.get_ticks_msec() - int(msg["stamp"])) & 0xFFFFFFFF


func _send_hello() -> void:
	_last_hello_tick = _tick
	_send(NetProtocol.hello(_fingerprint()))


func _begin_running() -> void:
	_set_phase(Phase.RUNNING)
	# Our first input_delay frames are empty: send them so the opponent's stream starts at frame 0.
	var pad := core.padding()
	for f in pad.size():
		_send(NetProtocol.input(f, pad[f], 0, 0))


# ---- running ----

func _run(local_buttons: int) -> void:
	core.apply_rollback()
	var desync := core.verify()
	if desync >= 0:
		_end("Desync: the two games differ at frame %d." % desync, NetProtocol.Bye.DESYNC)
		return

	var silent := _tick - _last_heard_tick
	interrupted = silent > INTERRUPTED_AFTER
	if silent > LOST_AFTER:
		_end("Lost the connection to your opponent.", NetProtocol.Bye.GAVE_UP)
		return

	if not core.can_advance():
		stalled = true
		_stall_ticks += 1
		if _stall_ticks > STALL_TIMEOUT:
			_end("Your opponent stopped responding.", NetProtocol.Bye.GAVE_UP)
		_update_stats()
		return
	_stall_ticks = 0

	# Time sync: this side runs ahead, so let the opponent catch up, one tick in two.
	if _skip_ticks > 0 and _tick % 2 == 0:
		_skip_ticks -= 1
		_update_stats()
		return

	var sender_frame := core.state.frame
	var frame := core.add_local_input(local_buttons)
	_send(NetProtocol.input(frame, local_buttons & Buttons.KNOWN, sender_frame, core.local_advantage()))
	core.advance()
	for check in core.new_checksums():
		_send(NetProtocol.checksum(check[0], check[1]))

	if _tick % PING_INTERVAL == 0:
		_send(NetProtocol.ping(NetProtocol.Kind.PING, Time.get_ticks_msec()))
		_skip_ticks += core.recommended_skip()
	_update_stats()


func _update_stats() -> void:
	frames_behind = maxi(0, core.state.frame - core.remote_frames())
	rollback_last = core.rollback_last
	rollback_max = core.rollback_max
	rollback_median = core.rollback_median()
	checksums_verified = core.checksums_verified


# ---- plumbing ----

func _connect() -> void:
	if _ws != null and _ws.get_ready_state() != WebSocketPeer.STATE_CLOSED:
		_ws.close()
	if _socket_factory.is_valid():
		_ws = _socket_factory.call()
	else:
		var ws := WebSocketPeer.new()
		ws.inbound_buffer_size = 1 << 16
		ws.outbound_buffer_size = 1 << 16
		var err := ws.connect_to_url(_url)
		_ws = ws
		if err != OK:
			_end("Couldn't connect to the game server (%s)." % error_string(err), -1)
			return
	core = null
	state = null
	local_slot = -1
	end_message = ""
	interrupted = false
	ping_ms = 0
	_set_phase(Phase.CONNECTING)


func _send(bytes: PackedByteArray) -> int:
	if _ws != null and _ws.get_ready_state() == WebSocketPeer.STATE_OPEN:
		return _ws.send(bytes, WebSocketPeer.WRITE_MODE_BINARY)
	return ERR_UNCONFIGURED


func _end(message: String, bye_reason: int) -> void:
	if phase == Phase.ENDED:
		return
	if bye_reason >= 0 and core != null:
		_send(NetProtocol.bye(bye_reason))
	end_message = message
	interrupted = false
	stalled = false
	_set_phase(Phase.ENDED)
	if _ws != null:
		_ws.close(1000, "bye")


func _set_phase(p: int) -> void:
	if p != phase:
		print("[online] room %s: %s -> %s %s" % [room, Phase.keys()[phase], Phase.keys()[p], end_message])
	phase = p
	_phase_tick = _tick


func _fingerprint() -> int:
	var h := StateHasher.new()
	h.add_u64(rules.fingerprint())
	h.add_int(PREDICTION)
	return h.value()
