class_name FakeRelay
extends RefCounted
## An in-memory stand-in for server/Rangbaazi.Relay, for headless tests: same welcome/start/
## peer_left/full text messages, binary messages forwarded in order with a latency of
## `latency` steps plus up to `jitter` more (never reordered, like a WebSocket). Call step() once
## per test tick before ticking the sessions. `blocked` drops everything, like a dead link.

var latency := 0
var jitter := 0
var blocked := false
var seed_counter := 1000

var _tick := 0
var _rng := RandomNumberGenerator.new()
var _rooms := {}          # code -> Array of FakeSocket (slot = index, null = free)
var _sockets: Array = []


func _init(rng_seed: int = 7) -> void:
	_rng.seed = rng_seed


## A socket that connects to `room` on the next step(); pass `func(): return relay.socket(code)` as the session's socket factory.
func socket(room: String) -> FakeSocket:
	var s := FakeSocket.new(self, room.to_upper())
	_sockets.append(s)
	return s


func step() -> void:
	_tick += 1
	for s in _sockets:
		if s.state == WebSocketPeer.STATE_CONNECTING:
			_join(s)


func _join(s: FakeSocket) -> void:
	var members: Array = _rooms.get(s.room, [null, null])
	var slot := members.find(null)
	if slot < 0:
		s.state = WebSocketPeer.STATE_OPEN
		_text(s, { type = "full" })
		s.state = WebSocketPeer.STATE_CLOSED
		return
	members[slot] = s
	_rooms[s.room] = members
	s.slot = slot
	s.state = WebSocketPeer.STATE_OPEN
	_text(s, { type = "welcome", slot = slot, room = s.room })
	if members[0] != null and members[1] != null:
		seed_counter += 1
		for m in members:
			_text(m, { type = "start", seed = str(seed_counter * 7919), slot = m.slot })


func _forward(from: FakeSocket, bytes: PackedByteArray) -> void:
	if blocked:
		return
	var members: Array = _rooms.get(from.room, [null, null])
	var to: FakeSocket = members[1 - from.slot] if from.slot >= 0 else null
	if to != null and to.state == WebSocketPeer.STATE_OPEN:
		to.deliver(false, bytes.duplicate(), _tick + latency + (_rng.randi_range(0, jitter) if jitter > 0 else 0))


func _leave(s: FakeSocket) -> void:
	var members: Array = _rooms.get(s.room, [])
	if s.slot < 0 or members.is_empty() or members[s.slot] != s:
		return
	members[s.slot] = null
	var other: FakeSocket = members[1 - s.slot]
	if other != null and not blocked:
		_text(other, { type = "peer_left" })
	if members[0] == null and members[1] == null:
		_rooms.erase(s.room)


func _text(to: FakeSocket, msg: Dictionary) -> void:
	to.deliver(true, JSON.stringify(msg).to_utf8_buffer(), _tick + latency)


func now() -> int:
	return _tick


class FakeSocket:
	extends RefCounted
	## The WebSocketPeer methods OnlineSession uses.

	var relay: FakeRelay
	var room: String
	var slot := -1
	var state := WebSocketPeer.STATE_CONNECTING
	var _queue: Array = []     # [due, is_text, bytes], in delivery order
	var _ready: Array = []
	var _last_due := 0
	var _last_was_text := false
	var texts_sent := 0

	func _init(relay_: FakeRelay, room_: String) -> void:
		relay = relay_
		room = room_

	func deliver(is_text: bool, bytes: PackedByteArray, due: int) -> void:
		_last_due = maxi(_last_due, due) # a WebSocket never reorders
		_queue.append([_last_due, is_text, bytes])

	func poll() -> void:
		while not _queue.is_empty() and _queue[0][0] <= relay.now():
			_ready.append(_queue.pop_front())

	func get_ready_state() -> int:
		return state

	func send(bytes: PackedByteArray, _mode: int = 0) -> int:
		if state == WebSocketPeer.STATE_OPEN:
			relay._forward(self, bytes)
		return OK

	func send_text(_text: String) -> int:
		texts_sent += 1 # the real relay ignores text from players; it only counts as activity
		return OK

	func get_available_packet_count() -> int:
		return _ready.size()

	func get_packet() -> PackedByteArray:
		var item: Array = _ready.pop_front()
		_last_was_text = item[1]
		return item[2]

	func was_string_packet() -> bool:
		return _last_was_text

	func close(_code: int = 1000, _reason: String = "") -> void:
		if state != WebSocketPeer.STATE_CLOSED:
			state = WebSocketPeer.STATE_CLOSED
			relay._leave(self)
