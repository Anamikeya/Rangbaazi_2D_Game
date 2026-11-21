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
const FIGHTER_BYTES := 34
const PROP_BYTES := 17
## The arena protocol version sent as v= (ArenaLimits.ProtocolVersion): 2 added weapons, 3 the last hit and main weapon,
## 4 five more weapons and each prop's timer, 5 the layout for all 100 weapons (combat spec v0.4,
## section 3.4): statuses on a fighter, and a prop's 16-bit timer, owner, supply and wear; and
## teams in lobby and start, where in a team match the STATE winner is the winning team's MVP.
const PROTOCOL_VERSION := 5
# A fighter's status bits (byte 31 of a fighter in STATE).
const STATUS_SLOWED := 1
const STATUS_ROOTED := 2
const STATUS_DOT := 4         # poisoned or burning
const STATUS_MARKED := 8
const STATUS_STUN_IMMUNE := 16
const STATUS_HIDDEN := 32     # in a cloud (Simulation.is_hidden; the view works it out from the props too)
const STATUS_GUARDING := 64   # holding a guard up (Simulation.guarding)
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
## Teams (ArenaLimits): 2 to 4 of them, at most TEAM_SIZE each.
const MAX_TEAMS := 4
const TEAM_SIZE := 4
## The start message's "size" values and the level each one plays on (StageDefinition sizes).
const STAGE_SIZES := {"normal": StageDefinition.SIZE_NORMAL, "big": StageDefinition.SIZE_BIG}
## Server errors that are notices: the session goes on, the lobby shows the message for a while.
const NOTICE_CODES := ["not_allowed", "team_full", "teams_needed"]
const NOTICE_TICKS := 2 * 60
## How long the server shows a match's result before it returns everyone to the lobby
## (ArenaLimits.OverFrames on the relay): the result panel counts down from it.
const RESULT_TICKS := 15 * 60
## How the relay refuses someone: one text {"type":"error","code","message"}, then a close at once.
## Godot's native WebSocketPeer often goes CONNECTING -> CLOSED in one poll and drops that text, so
## the close is read too: its reason is the code (an older relay said "old client"), and the close
## status names it when the reason is missing. The messages are the relay's own (ArenaConnection,
## RelayApp).
const REFUSALS := {
	"old_client": "This copy of the game is out of date: refresh the page or install the new app.",
	"full": "The room is full (15 players).",
	"busy": "The server is busy. Try again in a minute.",
	"taken": "That room code is already in use.",
	"no_room": "No room with that code. Check it with the host.",
}
const REFUSAL_CLOSE_CODES := {4000: "old_client", 4001: "full", 4002: "busy", 4004: "taken", 4005: "no_room"}
## Ticks to wait for a closing socket to finish, so its close code can be read.
const CLOSING_TICKS := 2 * 60

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
var players: Array = [] # [{id, name, cpu, character, team, left}]
var stage_id := "rooftops"
var room_phase := "lobby"
## 0 in a free-for-all; 2 to 4 in a teams room.
var team_count := 0
## The last notice from the server ("That team is full.") and the tick it came; "" for none.
var notice := ""
var notice_tick := -1000000

# The match, from "start" and the STATE frames.
var rules: MatchRules
var state: GameState
## Each fighter's main weapon so far, as the server sends it: a weapon id (0 is fists), or 127
## (Nicknames.ALL_ROUNDER) for an all-rounder, +128 for a master (Simulation.main_weapon). For the
## winner's nickname.
var main_weapons: PackedInt32Array = []
## Each fighter's status bits from the last frame (STATUS_*), kept as the server sent them. The two
## with no field in Fighter, hidden and guarding, are not read from here: ArenaView works them out
## from the decoded state (Simulation.is_hidden, Simulation.guarding), which gives the same answer.
var status_bits: PackedInt32Array = []
var fighter_names: PackedStringArray = []
var fighter_cpu: Array[bool] = []
var fighter_characters: PackedStringArray = []
## Each fighter's team (0 in a free-for-all) and lobby id, from "start".
var fighter_teams: PackedInt32Array = []
var fighter_ids: PackedInt32Array = []
var my_fighter := -1
var frames_left := 0
## The tick the match was first seen over (-1: not over), for the result panel's countdown.
var over_tick := -1
## Quick chat, newest last: [{fighter, name, text, tick}].
var chat_log: Array = []

var _ws := WebSocketPeer.new()
var _url := ""
var _tick := 0
var _connect_tick := 0
var _buffer: Array[PackedByteArray] = []
var _playing_back := false
var _last_sent := -1
# Auto play (autopilot_buttons): where our fighter was at the last frame seen, and its air jump.
var _auto_x := 0
var _auto_y := 0
var _auto_frame := -1
var _auto_air_jumped := false
var _last_sent_tick := 0
var _closing_tick := -1


## The arena URL for a room: the relay's address (see OnlineSession) with the host's settings.
static func url_for_room(code: String, as_host: bool, match_minutes: int, player_name: String, character := "shahn") -> String:
	var base := str(OnlineSession.relay_url_for_room(code)).replace("/relay/room/", "/relay/arena/")
	return "%s?host=%d&minutes=%d&name=%s&character=%s&v=%d" % [base, 1 if as_host else 0, match_minutes,
		player_name.uri_encode(), character.uri_encode(), PROTOCOL_VERSION]


## dial: false makes a session that never connects (tests feed it messages through _on_text).
func _init(code: String, as_host: bool, match_minutes: int, player_name: String, character := "shahn", dial := true) -> void:
	room = code
	host_requested = as_host
	minutes = match_minutes
	_url = url_for_room(code, as_host, match_minutes, player_name, character)
	if not dial:
		return
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
	if ready == WebSocketPeer.STATE_CLOSING and _tick - _closing_since(true) < CLOSING_TICKS:
		return # the close code arrives with CLOSED
	if ready != WebSocketPeer.STATE_OPEN:
		var refused := refusal_code(_ws.get_close_code(), _ws.get_close_reason()) if ready == WebSocketPeer.STATE_CLOSED else ""
		if refused != "":
			_end(str(REFUSALS[refused]), refused)
		else:
			_end("Lost the connection to the game server." if phase != Phase.CONNECTING else "Couldn't reach the game server.", "closed")
		return
	_closing_since(false)
	if _tick % KEEPALIVE_TICKS == 0:
		_ws.send_text("{\"type\":\"ping\"}")
	if phase == Phase.PLAYING:
		_send_input(buttons)
		_play_buffer()


## Sends the keepalive now, outside tick(): on the web a hidden tab gets no frames, so main.gd's page
## timer calls this to keep the relay from closing a silent connection after 60 s.
func keepalive() -> void:
	if phase != Phase.ENDED and _ws.get_ready_state() == WebSocketPeer.STATE_OPEN:
		_ws.send_text("{\"type\":\"ping\"}")


## The buttons a SparringBot would press for our fighter (auto play). The server's frames leave out
## some of what the bot reads, so it is filled in here from what this side knows: the buttons we
## last sent, our speed from the last two frames, whether we have jumped in the air, and that
## crates and loose weapons are lying still. Close enough for a stand-in; it decides nothing in
## the match, the server only ever gets buttons.
func autopilot_buttons(level: int) -> int:
	if state == null or rules == null or my_fighter < 0 or my_fighter >= state.fighters.size():
		return 0
	var me := state.fighters[my_fighter]
	var last := maxi(_last_sent, 0)
	me.previous_buttons = last
	var gap := state.frame - _auto_frame
	if gap != 0:
		if _auto_frame >= 0 and gap > 0 and gap < 10:
			me.vel_x = (me.pos_x - _auto_x) / gap
			me.vel_y = (me.pos_y - _auto_y) / gap
		_auto_x = me.pos_x
		_auto_y = me.pos_y
		_auto_frame = state.frame
	if me.grounded:
		_auto_air_jumped = false
	me.air_jumps_left = 0 if _auto_air_jumped else 1
	for p in state.props:
		if p.kind == Weapons.PROP_BOX or p.kind == Weapons.PROP_ITEM:
			p.grounded = true
	var b := SparringBot.think(state, rules, my_fighter, level)
	if not me.grounded and (b & Buttons.JUMP) != 0 and (last & Buttons.JUMP) == 0:
		_auto_air_jumped = true
	return b


# ---- requests (the server checks who may do what) ----

func set_minutes(value: int) -> void:
	_send({"type": "settings", "minutes": clampi(value, MIN_MINUTES, MAX_MINUTES)})


func set_bots(count: int) -> void:
	_send({"type": "bots", "count": clampi(count, 0, MAX_PLAYERS - 1)})


func set_stage(id: String) -> void:
	_send({"type": "stage", "id": id})


func set_character(id: String) -> void:
	_send({"type": "character", "id": id})


## Free-for-all (0) or 2 to 4 teams. Host only; the server never goes below what the room needs.
func set_teams(count: int) -> void:
	_send({"type": "teams", "count": count if count == 0 else clampi(count, 2, MAX_TEAMS)})


## Asks to join a team (1 to team_count); the server says "team_full" if it can't.
func pick_team(team: int) -> void:
	_send({"type": "team", "team": team})


## The fewest teams that hold this many members (ArenaLimits.TeamsNeeded): 1 to 8 need 2, 9 to 12
## three, 13 to 15 four.
static func teams_needed(members: int) -> int:
	return maxi(2, (members + TEAM_SIZE - 1) / TEAM_SIZE)


## This player's team in the lobby list (0 for none).
func my_team() -> int:
	for p: Dictionary in players:
		if int(p.get("id", -1)) == my_id:
			return int(p.get("team", 0))
	return 0


## Members on each team, people and CPUs: index 1 to team_count (index 0 unused).
func team_sizes() -> PackedInt32Array:
	var sizes := PackedInt32Array()
	sizes.resize(MAX_TEAMS + 1)
	for p: Dictionary in players:
		var t := int(p.get("team", 0))
		if t >= 1 and t <= MAX_TEAMS:
			sizes[t] += 1
	return sizes


## People (not CPUs) on each team, index 1 to team_count.
func team_people() -> PackedInt32Array:
	var sizes := PackedInt32Array()
	sizes.resize(MAX_TEAMS + 1)
	for p: Dictionary in players:
		var t := int(p.get("team", 0))
		if t >= 1 and t <= MAX_TEAMS and not bool(p.get("cpu", false)):
			sizes[t] += 1
	return sizes


## Whether the person playing fighter i left the match (the CPU plays it now), by the lobby's "left".
func fighter_left(i: int) -> bool:
	if i < 0 or i >= fighter_ids.size():
		return false
	for p: Dictionary in players:
		if int(p.get("id", -1)) == fighter_ids[i]:
			return bool(p.get("left", false))
	return false


## The notice to show now ("" once it is NOTICE_TICKS old).
func current_notice() -> String:
	return notice if _tick - notice_tick < NOTICE_TICKS else ""


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
			team_count = int(msg.get("teams", 0)) # an older server sends none: a free-for-all
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
			if NOTICE_CODES.has(code):
				# A tap the server turned down (host only, a full team, one team in use): the
				# session goes on, and the lobby shows why for a moment.
				notice = str(msg.get("message", ""))
				notice_tick = _tick
				return
			_end(str(msg.get("message", "The server refused.")), code)


func _begin_match(msg: Dictionary) -> void:
	# "size": the big level for 9 or more (rooms v1, 5.3). Missing: an older server, the normal level;
	# a size this game doesn't know means it is out of date.
	var size_name := str(msg.get("size", "normal"))
	if not STAGE_SIZES.has(size_name):
		_end("This game is too old for the room: update it.", "size")
		return
	var stage := StageDefinition.by_id(str(msg.get("stage", "")), STAGE_SIZES[size_name])
	if stage == null:
		_end("This game is too old for the room: update it.", "stage")
		return
	var fighters: Array = msg.get("fighters", [])
	fighter_names = PackedStringArray()
	fighter_cpu.clear()
	fighter_characters = PackedStringArray()
	fighter_teams = PackedInt32Array()
	fighter_ids = PackedInt32Array()
	for f: Dictionary in fighters:
		fighter_names.append(str(f.get("name", "?")))
		fighter_cpu.append(bool(f.get("cpu", false)))
		fighter_characters.append(str(f.get("character", "shahn")))
		fighter_teams.append(int(f.get("team", 0))) # missing: an older server, a free-for-all
		fighter_ids.append(int(f.get("id", -1)))
	var teams_in_match := int(msg.get("teams", 0))
	rules = MatchRules.room(stage, int(msg.get("minutes", MIN_MINUTES)), PackedInt32Array(),
		fighter_teams if teams_in_match > 0 else PackedInt32Array())
	rules.time_limit_frames = int(msg.get("time_limit_frames", rules.time_limit_frames))
	my_fighter = int(msg.get("you", -1))
	state = GameState.new(fighters.size(), 0)
	frames_left = rules.time_limit_frames
	over_tick = -1
	_buffer.clear()
	_playing_back = false
	_last_sent = -1
	phase = Phase.PLAYING


func _leave_match() -> void:
	state = null
	over_tick = -1
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
	note_over()


## Notes the tick a match is first seen over, so the result panel can count down to the lobby.
func note_over() -> void:
	if state != null and state.match_over and over_tick < 0:
		over_tick = _tick


## The result's countdown to the lobby, as the fraction left (1 to 0), or -1 while the match runs.
func result_left() -> float:
	if over_tick < 0:
		return -1.0
	return clampf(1.0 - float(_tick - over_tick) / RESULT_TICKS, 0.0, 1.0)


## Writes a STATE frame into `state` (see ArenaBinary.EncodeState on the server).
func _apply(p: PackedByteArray) -> void:
	if state == null or p.size() < HEADER_BYTES or p[11] != state.fighters.size():
		return
	if main_weapons.size() != p[11]:
		main_weapons.resize(p[11])
		status_bits.resize(p[11])
	if decode_state(p, state, main_weapons, status_bits):
		frames_left = p.decode_u32(5)


## Decodes a STATE frame into a state with as many fighters as the frame, filling `mains` (each
## fighter's main-weapon code) and `statuses` (each fighter's status bits). False for a frame that
## is too short. A status bit sets that status's frames to 1: the frame says that it is on, not for
## how long.
static func decode_state(p: PackedByteArray, into: GameState, mains: PackedInt32Array, statuses: PackedInt32Array) -> bool:
	var n := p[11]
	if n != into.fighters.size() or p.size() < HEADER_BYTES + FIGHTER_BYTES * n:
		return false
	into.frame = p.decode_u32(1)
	into.match_over = (p[9] & 1) != 0
	into.winner_index = p.decode_s8(10)
	for i in n:
		var o := HEADER_BYTES + FIGHTER_BYTES * i
		var f := into.fighters[i]
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
		if i < mains.size():
			mains[i] = p[o + 30]
		var status := p[o + 31]
		if i < statuses.size():
			statuses[i] = status
		f.slow_frames = 1 if (status & STATUS_SLOWED) != 0 else 0
		f.root_frames = 1 if (status & STATUS_ROOTED) != 0 else 0
		f.dot_frames = 1 if (status & STATUS_DOT) != 0 else 0
		f.mark_frames = 1 if (status & STATUS_MARKED) != 0 else 0
		f.stun_immune = 1 if (status & STATUS_STUN_IMMUNE) != 0 else 0
		f.dot_weapon = p[o + 32]
		f.stun_weapon = p[o + 33]
	# The props in use, into the first slots; the rest are cleared.
	var at := HEADER_BYTES + FIGHTER_BYTES * n
	var count := p[at] if p.size() > at else 0
	at += 1
	for i in into.props.size():
		var prop := into.props[i]
		prop.clear()
		if i >= count or p.size() < at + PROP_BYTES:
			continue
		prop.kind = p[at]
		prop.weapon = p[at + 1]
		prop.shot = p[at + 2]
		prop.pos_x = p.decode_s32(at + 3)
		prop.pos_y = p.decode_s32(at + 7)
		prop.vel_x = p.decode_s8(at + 11)
		prop.timer = p.decode_u16(at + 12)
		prop.owner = -1 if p[at + 14] == 255 else p[at + 14]
		prop.supply = p.decode_s8(at + 15)
		prop.ignore_fighter = p[at + 16] # a shot's wear; 0 on a box
		at += PROP_BYTES
	return true


## The relay's refusal code from a close ("" when the close isn't a refusal): the reason when it is
## a known code ("old client" from an older relay counts as old_client), else the close status. A
## 4000 with another reason is a bad duel code, not a refusal of this game.
static func refusal_code(close_code: int, reason: String) -> String:
	var r := reason.strip_edges().replace(" ", "_")
	if REFUSALS.has(r):
		return r
	if close_code == 4000 and r != "":
		return ""
	return str(REFUSAL_CLOSE_CODES.get(close_code, ""))


# The tick the socket was first seen closing (reset with started false).
func _closing_since(started: bool) -> int:
	if not started:
		_closing_tick = -1
	elif _closing_tick < 0:
		_closing_tick = _tick
	return _closing_tick


func _end(message: String, code: String) -> void:
	if phase == Phase.ENDED:
		return
	phase = Phase.ENDED
	end_message = message
	end_code = code
	if _ws.get_ready_state() == WebSocketPeer.STATE_OPEN:
		_ws.close(1000, "bye")
