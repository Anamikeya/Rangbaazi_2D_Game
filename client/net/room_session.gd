class_name RoomSession
extends RefCounted
## A room of up to 15 players whose match the server runs (server/Rangbaazi.Relay, arena rooms).
## Unlike OnlineSession (rollback 1v1), nothing is simulated here: the client sends its buttons and
## draws the GameState the server sends every frame. A small jitter buffer plays those frames at an
## even 60 per second, a few frames behind, so a late network packet doesn't make the picture stutter.
##
## CONNECTING -> LOBBY (host picks the minutes and CPUs, then starts) -> PLAYING (state.match_over
## shows the result; the server goes back to the lobby by itself) -> LOBBY ... ENDED on an error or
## when the connection closes.

enum Phase { CONNECTING, LOBBY, PLAYING, ENDED }

const INPUT := 1
const STATE := 2
const HEADER_BYTES := 12
const FIGHTER_BYTES := 31
const PROP_BYTES := 12
## The arena protocol version sent as v= (ArenaLimits.ProtocolVersion): 2 added weapons, 3 the last hit and main weapon.
const PROTOCOL_VERSION := 3
## Frames the jitter buffer tries to keep ready; past MAX_BUFFER it skips ahead to catch up.
const TARGET_BUFFER := 3
const MAX_BUFFER := 10
const CONNECT_TIMEOUT := 10 * 60 # ticks
const KEEPALIVE_TICKS := 20 * 60
## An unchanged input is still re-sent this often, in case the server missed the last one.
const INPUT_REFRESH_TICKS := 15
const MIN_MINUTES := 10
const MAX_MINUTES := 15
const MAX_PLAYERS := 15

## The quick-chat phrases (the server only carries their numbers, so nobody can type abuse).
const PHRASES := [
	"I will cook you!", "Come here!", "Too easy!", "Nice hit!",
	"GG!", "Oops!", "Watch this!", "Run!",
	"Help!", "Revenge time!", "Bas kar!", "Rangbaazi!",
	"Hello!", "Good luck!", "One more?", "Wow!",
]

var phase: int = Phase.CONNECTING
var room := ""
var host_requested := false
## Why the session ended, for the menu; end_code is the server's error code ("taken", "no_room", ...).
var end_message := ""
var end_code := ""

# Lobby, from the latest "lobby" message.
var my_id := -1
var host_id := -1
var minutes := MIN_MINUTES
var players: Array = [] # [{id, name, cpu, character}]
var stage_id := "rooftops"
var room_phase := "lobby"

# The match, from "start" and the STATE frames.
var rules: MatchRules
var state: GameState
## Each fighter's main weapon so far, as the server sends it: 0-7 (0 is fists), or 15 for an
## all-rounder, +128 for a master (Simulation.main_weapon). For the winner's nickname.
var main_weapons: PackedInt32Array = []
var fighter_names: PackedStringArray = []
var fighter_cpu: Array[bool] = []
var fighter_characters: PackedStringArray = []
var my_fighter := -1
var frames_left := 0
## Quick chat, newest last: [{fighter, name, text, tick}].
var chat_log: Array = []

var _ws := WebSocketPeer.new()
var _url := ""
var _tick := 0
var _connect_tick := 0
var _buffer: Array[PackedByteArray] = []
var _playing_back := false
var _last_sent := -1
var _last_sent_tick := 0


## The arena URL for a room: the relay's address (see OnlineSession) with the host's settings.
static func url_for_room(code: String, as_host: bool, match_minutes: int, player_name: String, character := "shahn") -> String:
	var base := str(OnlineSession.relay_url_for_room(code)).replace("/relay/room/", "/relay/arena/")
	return "%s?host=%d&minutes=%d&name=%s&character=%s&v=%d" % [base, 1 if as_host else 0, match_minutes,
		player_name.uri_encode(), character.uri_encode(), PROTOCOL_VERSION]


func _init(code: String, as_host: bool, match_minutes: int, player_name: String, character := "shahn") -> void:
	room = code
	host_requested = as_host
	minutes = match_minutes
	_url = url_for_room(code, as_host, match_minutes, player_name, character)
	var err := _ws.connect_to_url(_url)
	if err != OK:
		_end("Couldn't reach the game server.", "connect")


## Ticks since the session started (chat bubbles age by it).
func ticks() -> int:
	return _tick


func is_host() -> bool:
	return my_id >= 0 and my_id == host_id


func tick(buttons: int) -> void:
	_tick += 1
	if phase == Phase.ENDED:
		return
	_ws.poll()
	var ready := _ws.get_ready_state()
	if ready == WebSocketPeer.STATE_CONNECTING:
		if _tick - _connect_tick > CONNECT_TIMEOUT:
			_end("Couldn't reach the game server.", "timeout")
		return
	while phase != Phase.ENDED and _ws.get_available_packet_count() > 0:
		var packet := _ws.get_packet()
		if _ws.was_string_packet():
			_on_text(packet.get_string_from_utf8())
		elif packet.size() >= HEADER_BYTES and packet[0] == STATE:
			_buffer.append(packet)
	if phase == Phase.ENDED:
		return
	if ready != WebSocketPeer.STATE_OPEN:
		_end("Lost the connection to the game server." if phase != Phase.CONNECTING else "Couldn't reach the game server.", "closed")
		return
	if _tick % KEEPALIVE_TICKS == 0:
		_ws.send_text("{\"type\":\"ping\"}")
	if phase == Phase.PLAYING:
		_send_input(buttons)
		_play_buffer()


# ---- requests (the server checks who may do what) ----

func set_minutes(value: int) -> void:
	_send({"type": "settings", "minutes": clampi(value, MIN_MINUTES, MAX_MINUTES)})


func set_bots(count: int) -> void:
	_send({"type": "bots", "count": clampi(count, 0, MAX_PLAYERS - 1)})


func set_stage(id: String) -> void:
	_send({"type": "stage", "id": id})


func set_character(id: String) -> void:
	_send({"type": "character", "id": id})


## The character this player has in the lobby list, or "".
func my_character() -> String:
	for p: Dictionary in players:
		if int(p.get("id", -1)) == my_id:
			return str(p.get("character", ""))
	return ""


func start_match() -> void:
	_send({"type": "start"})


func end_match() -> void:
	_send({"type": "end"})


func say(phrase: int) -> void:
	if phrase >= 0 and phrase < PHRASES.size():
		_send({"type": "chat", "phrase": phrase})


func close() -> void:
	if phase != Phase.ENDED:
		_end("You left the room.", "left")


func bot_count() -> int:
	var n := 0
	for p: Dictionary in players:
		if bool(p.get("cpu", false)):
			n += 1
	return n


# ---- internals ----

func _send(message: Dictionary) -> void:
	if _ws.get_ready_state() == WebSocketPeer.STATE_OPEN:
		_ws.send_text(JSON.stringify(message))


func _send_input(buttons: int) -> void:
	if my_fighter < 0:
		return
	if buttons == _last_sent and _tick - _last_sent_tick < INPUT_REFRESH_TICKS:
		return
	var bytes := PackedByteArray([INPUT, buttons & 0xFF, (buttons >> 8) & 0xFF])
	_ws.put_packet(bytes)
	_last_sent = buttons
	_last_sent_tick = _tick


func _on_text(text: String) -> void:
	var msg: Variant = JSON.parse_string(text)
	if typeof(msg) != TYPE_DICTIONARY:
		return
	match str(msg.get("type", "")):
		"lobby":
			room = str(msg.get("room", room))
			my_id = int(msg.get("you", my_id))
			host_id = int(msg.get("host", -1))
			minutes = int(msg.get("minutes", minutes))
			stage_id = str(msg.get("stage", stage_id))
			players = msg.get("players", [])
			room_phase = str(msg.get("phase", "lobby"))
			if room_phase == "lobby":
				_leave_match()
				phase = Phase.LOBBY
			elif phase == Phase.CONNECTING:
				phase = Phase.LOBBY # joined mid-match: "start" follows
		"start":
			_begin_match(msg)
		"chat":
			var phrase := int(msg.get("phrase", -1))
			if phrase >= 0 and phrase < PHRASES.size():
				chat_log.append({"fighter": int(msg.get("fighter", -1)), "name": str(msg.get("name", "")),
					"text": PHRASES[phrase], "tick": _tick})
				if chat_log.size() > 20:
					chat_log.pop_front()
		"error":
			var code := str(msg.get("code", ""))
			if code == "not_allowed":
				return # a tap the host-only rule ignored; nothing to end
			_end(str(msg.get("message", "The server refused.")), code)


func _begin_match(msg: Dictionary) -> void:
	var stage := StageDefinition.by_id(str(msg.get("stage", "")))
	if stage == null:
		_end("This game is too old for the room: update it.", "stage")
		return
	rules = MatchRules.room(stage, int(msg.get("minutes", MIN_MINUTES)))
	rules.time_limit_frames = int(msg.get("time_limit_frames", rules.time_limit_frames))
	var fighters: Array = msg.get("fighters", [])
	fighter_names = PackedStringArray()
	fighter_cpu.clear()
	fighter_characters = PackedStringArray()
	for f: Dictionary in fighters:
		fighter_names.append(str(f.get("name", "?")))
		fighter_cpu.append(bool(f.get("cpu", false)))
		fighter_characters.append(str(f.get("character", "shahn")))
	my_fighter = int(msg.get("you", -1))
	state = GameState.new(fighters.size(), 0)
	frames_left = rules.time_limit_frames
	_buffer.clear()
	_playing_back = false
	_last_sent = -1
	phase = Phase.PLAYING


func _leave_match() -> void:
	state = null
	my_fighter = -1
	_buffer.clear()
	_playing_back = false


## Shows one frame per tick, keeping TARGET_BUFFER in hand: waits until that many have arrived,
## skips ahead when the buffer runs long (the network caught up after a stall), and holds the last
## frame when it runs dry.
func _play_buffer() -> void:
	if not _playing_back:
		if _buffer.size() < TARGET_BUFFER:
			return
		_playing_back = true
	if _buffer.is_empty():
		_playing_back = false
		return
	while _buffer.size() > MAX_BUFFER:
		_buffer.pop_front()
	_apply(_buffer.pop_front())


## Writes a STATE frame into `state` (see ArenaBinary.EncodeState on the server).
func _apply(p: PackedByteArray) -> void:
	var n := p[11]
	if state == null or n != state.fighters.size() or p.size() < HEADER_BYTES + FIGHTER_BYTES * n:
		return
	state.frame = p.decode_u32(1)
	if main_weapons.size() != n:
		main_weapons.resize(n)
	frames_left = p.decode_u32(5)
	state.match_over = (p[9] & 1) != 0
	state.winner_index = p.decode_s8(10)
	for i in n:
		var o := HEADER_BYTES + FIGHTER_BYTES * i
		var f := state.fighters[i]
		f.pos_x = p.decode_s32(o)
		f.pos_y = p.decode_s32(o + 4)
		f.facing = p.decode_s8(o + 8)
		f.action = p[o + 9]
		f.move = p[o + 10]
		var flags := p[o + 11]
		f.grounded = (flags & 1) != 0
		f.frozen_by_hit = (flags & 2) != 0
		f.attack_connected = (flags & 4) != 0
		f.guts_used = (flags & 8) != 0
		f.air_dodge_used = (flags & 16) != 0
		f.action_frame = p.decode_u16(o + 12)
		f.josh = p.decode_u16(o + 14)
		f.kos = p.decode_u16(o + 16)
		f.falls = p.decode_u16(o + 18)
		f.hit_stop = p[o + 20]
		f.armor_frames = p[o + 21]
		f.guts_timer = p.decode_u16(o + 22)
		f.hitstun_frames = p[o + 24]
		f.dodge_cooldown = p[o + 25]
		f.weapon = p[o + 26]
		f.weapon_supply = p[o + 27]
		f.last_hit_weapon = p[o + 28] & 127
		f.last_hit_sweet = (p[o + 28] & 128) != 0
		f.hit_count = p[o + 29]
		main_weapons[i] = p[o + 30]
	# The props in use, into the first slots; the rest are cleared.
	var at := HEADER_BYTES + FIGHTER_BYTES * n
	var count := p[at] if p.size() > at else 0
	at += 1
	for i in state.props.size():
		var prop := state.props[i]
		prop.clear()
		if i >= count or p.size() < at + PROP_BYTES:
			continue
		prop.kind = p[at]
		prop.weapon = p[at + 1]
		prop.shot = p[at + 2]
		prop.pos_x = p.decode_s32(at + 3)
		prop.pos_y = p.decode_s32(at + 7)
		prop.vel_x = p.decode_s8(at + 11)
		at += PROP_BYTES


func _end(message: String, code: String) -> void:
	if phase == Phase.ENDED:
		return
	phase = Phase.ENDED
	end_message = message
	end_code = code
	if _ws.get_ready_state() == WebSocketPeer.STATE_OPEN:
		_ws.close(1000, "bye")
