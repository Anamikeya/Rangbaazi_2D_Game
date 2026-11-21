extends TestCase
## The binary messages between players (NetProtocol), and a room's STATE frame (RoomSession).

## A STATE frame of arena protocol 5, as the server encodes it: two fighters and five props, with a
## value in every field that protocol added. The same bytes, and the state they come from, are in
## tests/Rangbaazi.Relay.Tests/ArenaTests.cs (SharedFrame, SharedState): change both together.
const SHARED_FRAME := "02D2040000CE87000000FF020080010000000000010000010000000000000000000000000000000000008700000000C0FCFF00000200FF04000000005700000000000000000014003407CE037F0D494E050120000000020000000000002C01FF0300041B0C0000FCFF00000000002C010100000407040000030000000100FF0000000000045E0E000006000000000000580201FF0104200D0000F8FF0000000000F000000002"


func test_messages_round_trip() -> void:
	var m := NetProtocol.decode(NetProtocol.hello(-1234567890123456789))
	check_eq(m.get("kind"), NetProtocol.Kind.HELLO, "hello kind")
	check_eq(m.get("version"), NetProtocol.VERSION, "hello version")
	check_eq(m.get("fingerprint"), -1234567890123456789, "hello fingerprint")

	m = NetProtocol.decode(NetProtocol.input(12345, Buttons.LEFT | Buttons.HEAVY, 12343, -3))
	check_eq(m.get("frame"), 12345, "input frame")
	check_eq(m.get("buttons"), Buttons.LEFT | Buttons.HEAVY, "input buttons")
	check_eq(m.get("sender_frame"), 12343, "input sender frame")
	check_eq(m.get("advantage"), -3, "input advantage")

	m = NetProtocol.decode(NetProtocol.checksum(600, -42))
	check_eq(m.get("frame"), 600, "checksum frame")
	check_eq(m.get("value"), -42, "checksum value")

	check_eq(NetProtocol.decode(NetProtocol.bye(NetProtocol.Bye.REMATCH)).get("reason"), NetProtocol.Bye.REMATCH, "bye")
	check_eq(NetProtocol.decode(NetProtocol.ping(NetProtocol.Kind.PONG, 99)).get("stamp"), 99, "pong")


func test_garbage_is_rejected() -> void:
	check(NetProtocol.decode(PackedByteArray()).is_empty(), "empty")
	check(NetProtocol.decode(PackedByteArray([99, 1, 2])).is_empty(), "unknown kind")
	var hello := NetProtocol.hello(1)
	check(NetProtocol.decode(hello.slice(0, hello.size() - 1)).is_empty(), "truncated")
	hello.append(0)
	check(NetProtocol.decode(hello).is_empty(), "too long")
	check(NetProtocol.decode(NetProtocol.input(-1, 0, 0, 0)).is_empty(), "negative frame")
	check(NetProtocol.decode(NetProtocol.checksum(-5, 0)).is_empty(), "negative checksum frame")


func test_unknown_buttons_are_masked() -> void:
	var b := NetProtocol.input(1, 0, 0, 0)
	b.encode_u16(5, 0xFFFF)
	check_eq(NetProtocol.decode(b).get("buttons"), Buttons.KNOWN, "only known buttons survive")


func test_relay_url() -> void:
	var url := OnlineSession.relay_url_for_room("ab12")
	check(url.ends_with("/relay/room/AB12"), "room code upper-cased at the end of the URL: " + url)
	check(url.begins_with("ws://") or url.begins_with("wss://"), "a WebSocket URL: " + url)


func test_a_state_frame_of_protocol_5_is_read() -> void:
	check_eq(RoomSession.PROTOCOL_VERSION, 5, "the arena protocol version")
	check_eq([RoomSession.FIGHTER_BYTES, RoomSession.PROP_BYTES], [34, 17], "a fighter's and a prop's bytes")
	var frame := SHARED_FRAME.hex_decode()
	check_eq(frame.size(), RoomSession.HEADER_BYTES + 2 * RoomSession.FIGHTER_BYTES + 1 + 5 * RoomSession.PROP_BYTES, "the frame's size")
	var state := GameState.new(2, 0)
	var mains := PackedInt32Array([0, 0])
	var statuses := PackedInt32Array([0, 0])
	if not check(RoomSession.decode_state(frame, state, mains, statuses), "the frame is read"):
		return
	check_eq(state.frame, 1234, "the frame number")
	check_eq(state.match_over, false, "not over")

	var a := state.fighters[0]
	check_eq([a.pos_x, a.pos_y, a.facing, a.grounded], [Fx.ratio(3, 2), 0, 1, true], "fighter 1")
	check_eq(mains[0], WeaponTable.BOW | Nicknames.MASTER, "a bow master")
	check_eq([statuses[0], a.slow_frames, a.dot_weapon, a.stun_weapon], [0, 0, 0, 0], "no statuses")

	var b := state.fighters[1]
	check_eq([b.pos_x, b.pos_y, b.facing, b.action, b.josh, b.hitstun_frames], [Fx.ratio(-13, 4), Fx.from_int(2), -1, Fighter.Action.STUNNED, 87, 20], "fighter 2")
	check_eq([b.weapon, b.weapon_supply], [WeaponTable.KATANA, 7], "a weapon above 15, which used to mean all-rounder")
	check_eq([b.last_hit_weapon, b.last_hit_sweet, b.hit_count], [WeaponTable.FREEZE_RAY, true, 3], "the last hit")
	check_eq(mains[1], Nicknames.ALL_ROUNDER, "no damage dealt: 127")
	check_eq(statuses[1], RoomSession.STATUS_SLOWED | RoomSession.STATUS_DOT | RoomSession.STATUS_MARKED, "the status bits")
	check_eq([b.slow_frames, b.root_frames, b.dot_frames, b.mark_frames, b.stun_immune], [1, 0, 1, 1, 0], "a bit sets its status's frames to 1")
	check_eq([b.dot_weapon, b.stun_weapon], [WeaponTable.VENOM_DART_LAUNCHER, WeaponTable.FREEZE_RAY], "the weapons of the poison and of the hold")

	# The props in use, in the first slots: kind, weapon, shot, x, facing, timer, owner, supply, wear.
	var want := [
		[Weapons.PROP_BOX, WeaponTable.MATKA, WeaponTable.FLIGHT_NONE, 2, 0, 300, -1, 3, 0],
		[Weapons.PROP_SHOT, WeaponTable.LOTA, WeaponTable.FLIGHT_ZONE, -4, 0, 300, 1, 0, 0],
		[Weapons.PROP_SHOT, WeaponTable.BOW, WeaponTable.FLIGHT_ARROW, 3, -1, 0, 0, 0, 0],
		[Weapons.PROP_SHOT, WeaponTable.SONIC_SPEAKER, WeaponTable.FLIGHT_SPEAKER, 6, 0, 600, 1, -1, 1],
		[Weapons.PROP_SHOT, WeaponTable.MATKA, WeaponTable.FLIGHT_WALL, -8, 0, 240, 0, 0, 2],
	]
	for i in want.size():
		var p := state.props[i]
		check_eq([p.kind, p.weapon, p.shot, p.pos_x, p.vel_x, p.timer, p.owner, p.supply, p.ignore_fighter],
			[want[i][0], want[i][1], want[i][2], Fx.from_int(want[i][3]), want[i][4], want[i][5], want[i][6], want[i][7], want[i][8]], "prop %d" % i)
	for i in range(want.size(), state.props.size()):
		check_eq(state.props[i].kind, Weapons.PROP_NONE, "slot %d is empty" % i)


func test_every_status_bit_and_a_short_frame() -> void:
	var frame := SHARED_FRAME.hex_decode()
	var bits := RoomSession.HEADER_BYTES + 31
	var fields := ["slow_frames", "root_frames", "dot_frames", "mark_frames", "stun_immune"]
	var flags := [RoomSession.STATUS_SLOWED, RoomSession.STATUS_ROOTED, RoomSession.STATUS_DOT, RoomSession.STATUS_MARKED, RoomSession.STATUS_STUN_IMMUNE]
	check_eq(flags, [1, 2, 4, 8, 16], "the status bits")
	check_eq([RoomSession.STATUS_HIDDEN, RoomSession.STATUS_GUARDING], [32, 64], "hidden and guarding")
	for k in fields.size():
		frame[bits] = flags[k]
		var state := GameState.new(2, 0)
		var mains := PackedInt32Array([0, 0])
		var statuses := PackedInt32Array([0, 0])
		check(RoomSession.decode_state(frame, state, mains, statuses), "read")
		for j in fields.size():
			check_eq(state.fighters[0].get(fields[j]), 1 if j == k else 0, "bit %d sets only %s" % [flags[k], fields[k]])
		check_eq(statuses[0], flags[k], "the bits as sent")
	frame[bits] = 127
	var all := GameState.new(2, 0)
	var all_statuses := PackedInt32Array([0, 0])
	check(RoomSession.decode_state(frame, all, PackedInt32Array([0, 0]), all_statuses), "read")
	check_eq(all_statuses[0], 127, "hidden and guarding are kept for the drawing code")

	# Too short, or for another number of fighters: refused, and nothing is half-written.
	var state := GameState.new(2, 0)
	check(not RoomSession.decode_state(frame.slice(0, RoomSession.HEADER_BYTES + 40), state, PackedInt32Array([0, 0]), PackedInt32Array([0, 0])), "a cut frame")
	check_eq(state.frame, 0, "a refused frame changes nothing")
	check(not RoomSession.decode_state(frame, GameState.new(3, 0), PackedInt32Array([0, 0, 0]), PackedInt32Array([0, 0, 0])), "another fighter count")


## A session that never dials, fed the server's TEXT messages directly.
func _offline_session() -> RoomSession:
	return RoomSession.new("TEST", true, 10, "Ravi", "shahn", false)


func test_lobby_and_start_carry_teams_and_left() -> void:
	var s := _offline_session()
	s._on_text(JSON.stringify({"type": "lobby", "room": "TEST", "phase": "lobby", "minutes": 10, "stage": "rooftops",
		"you": 1, "host": 0, "max": 15, "teams": 2, "players": [
			{"id": 0, "name": "Ravi", "cpu": false, "character": "shahn", "team": 1, "left": false},
			{"id": 1, "name": "Neha", "cpu": false, "character": "akmal", "team": 2, "left": false},
			{"id": 2, "name": "CPU 1", "cpu": true, "character": "faiz", "team": 1, "left": false},
			{"id": 3, "name": "CPU 2", "cpu": true, "character": "maaz", "team": 2, "left": false}]}))
	check_eq(s.phase, RoomSession.Phase.LOBBY, "in the lobby")
	check_eq(s.team_count, 2, "two teams")
	check_eq(s.my_team(), 2, "my team")
	check_eq(s.team_sizes()[1], 2, "team 1's size")
	check_eq(s.team_people()[2], 1, "people on team 2")
	check_eq(RoomSession.teams_needed(8), 2, "8 need 2 teams")
	check_eq(RoomSession.teams_needed(9), 3, "9 need 3")
	check_eq(RoomSession.teams_needed(13), 4, "13 need 4")

	s._on_text(JSON.stringify({"type": "start", "stage": "rooftops", "minutes": 10, "time_limit_frames": 36000, "you": 1,
		"teams": 2, "fighters": [
			{"id": 0, "name": "Ravi", "cpu": false, "character": "shahn", "team": 1},
			{"id": 1, "name": "Neha", "cpu": false, "character": "akmal", "team": 2},
			{"id": 2, "name": "CPU 1", "cpu": true, "character": "faiz", "team": 1},
			{"id": 3, "name": "CPU 2", "cpu": true, "character": "maaz", "team": 2}]}))
	check_eq(s.phase, RoomSession.Phase.PLAYING, "playing")
	check(s.rules.has_teams(), "the rules have the teams")
	check_eq([s.rules.team_of(0), s.rules.team_of(1), s.rules.team_of(2), s.rules.team_of(3)], [1, 2, 1, 2], "each fighter's team")
	check(not Simulation.allies(s.rules, 0, 1), "host and guest on different teams")
	check(not s.fighter_left(0), "nobody has left yet")

	# The host leaves mid-match: the lobby marks them, and the session finds their fighter.
	s._on_text(JSON.stringify({"type": "lobby", "room": "TEST", "phase": "playing", "minutes": 10, "stage": "rooftops",
		"you": 1, "host": 1, "max": 15, "teams": 2, "players": [
			{"id": 0, "name": "Ravi", "cpu": false, "character": "shahn", "team": 1, "left": true},
			{"id": 1, "name": "Neha", "cpu": false, "character": "akmal", "team": 2, "left": false},
			{"id": 2, "name": "CPU 1", "cpu": true, "character": "faiz", "team": 1, "left": false},
			{"id": 3, "name": "CPU 2", "cpu": true, "character": "maaz", "team": 2, "left": false}]}))
	check(s.fighter_left(0), "the leaver's fighter")
	check(not s.fighter_left(1), "not the guest's")
	check_eq(s.phase, RoomSession.Phase.PLAYING, "still playing")


func test_a_start_from_an_older_server_is_a_free_for_all() -> void:
	var s := _offline_session()
	s._on_text(JSON.stringify({"type": "lobby", "room": "TEST", "phase": "lobby", "minutes": 10, "stage": "rooftops",
		"you": 0, "host": 0, "max": 15, "players": [{"id": 0, "name": "Ravi", "cpu": false, "character": "shahn"}]}))
	check_eq(s.team_count, 0, "no teams field: a free-for-all")
	check_eq(s.my_team(), 0, "no team")
	s._on_text(JSON.stringify({"type": "start", "stage": "rooftops", "minutes": 10, "time_limit_frames": 36000, "you": 0,
		"fighters": [{"id": 0, "name": "Ravi", "cpu": false, "character": "shahn"}, {"id": 5, "name": "CPU 1", "cpu": true, "character": "faiz"}]}))
	check_eq(s.phase, RoomSession.Phase.PLAYING, "playing")
	check(not s.rules.has_teams(), "no teams in the rules")
	check_eq(s.fighter_teams, PackedInt32Array([0, 0]), "team 0 each")
	check_eq(s.rules.stage.size, StageDefinition.SIZE_NORMAL, "no size field: the normal level")
	check(s.rules.stage == StageDefinition.rooftops(), "the normal Rookie Rooftops")


## Rooms v1, 5.3: "size" in start picks the level; an unknown size means this game is out of date.
func test_the_start_size_picks_the_level() -> void:
	var fighters := []
	for i in 9:
		fighters.append({"id": i, "name": "CPU %d" % i, "cpu": i > 0, "character": "shahn", "team": 1 + i % 3})
	var s := _offline_session()
	s._on_text(JSON.stringify({"type": "start", "stage": "shubham", "minutes": 10, "time_limit_frames": 36000, "you": 0,
		"teams": 3, "size": "big", "fighters": fighters}))
	check_eq(s.phase, RoomSession.Phase.PLAYING, "playing")
	check_eq(s.rules.stage.size, StageDefinition.SIZE_BIG, "the big level")
	check(s.rules.stage == StageDefinition.by_id("shubham", StageDefinition.SIZE_BIG), "Gully Stadium's big level")
	check_eq(s.rules.stage.spawn_x.size(), 15, "15 spawn points")
	check_eq(s.rules.stage.platforms[0].max_x, Fx.from_int(24), "the 48 m floor")

	var normal := _offline_session()
	normal._on_text(JSON.stringify({"type": "start", "stage": "shubham", "minutes": 10, "time_limit_frames": 36000, "you": 0,
		"teams": 0, "size": "normal", "fighters": fighters.slice(0, 8)}))
	check_eq(normal.rules.stage.size, StageDefinition.SIZE_NORMAL, "size normal")
	check_eq(StageDefinition.size_for(8), StageDefinition.SIZE_NORMAL, "8 play the normal level")
	check_eq(StageDefinition.size_for(9), StageDefinition.SIZE_BIG, "9 the big one")

	var future := _offline_session()
	future._on_text(JSON.stringify({"type": "start", "stage": "shubham", "minutes": 10, "time_limit_frames": 36000, "you": 0,
		"teams": 0, "size": "huge", "fighters": fighters}))
	check_eq(future.phase, RoomSession.Phase.ENDED, "an unknown size ends the session")
	check_eq(future.end_message, "This game is too old for the room: update it.", "and says to update")


func test_team_full_and_teams_needed_are_notices_that_keep_the_session() -> void:
	var s := _offline_session()
	s._on_text(JSON.stringify({"type": "lobby", "room": "TEST", "phase": "lobby", "minutes": 10, "stage": "rooftops",
		"you": 0, "host": 0, "max": 15, "teams": 2, "players": []}))
	for code in ["team_full", "teams_needed", "not_allowed"]:
		s._on_text(JSON.stringify({"type": "error", "code": code, "message": "That team is full."}))
		check_eq(s.phase, RoomSession.Phase.LOBBY, "%s keeps the session" % code)
	check_eq(s.current_notice(), "That team is full.", "the notice shows")
	s._on_text(JSON.stringify({"type": "error", "code": "old_client", "message": "Out of date."}))
	check_eq(s.phase, RoomSession.Phase.ENDED, "old_client ends it")
	check_eq(s.end_code, "old_client", "with its code")


## Godot's socket can drop the refusal's text and keep only the close: the close names the code.
func test_a_refusal_close_names_its_code() -> void:
	check_eq(RoomSession.refusal_code(4000, "old_client"), "old_client", "old_client by its reason")
	check_eq(RoomSession.refusal_code(4000, "old client"), "old_client", "an older relay's reason")
	check_eq(RoomSession.refusal_code(4001, "full"), "full", "full")
	check_eq(RoomSession.refusal_code(4002, "server busy"), "busy", "busy by its status")
	check_eq(RoomSession.refusal_code(4004, "taken"), "taken", "taken")
	check_eq(RoomSession.refusal_code(4005, ""), "no_room", "no_room by its status alone")
	check_eq(RoomSession.refusal_code(4000, "bad room code"), "", "a bad code is no refusal of this game")
	check_eq(RoomSession.refusal_code(1000, "bye"), "", "a normal close")
	check_eq(RoomSession.refusal_code(-1, ""), "", "a lost connection")
	for code: String in RoomSession.REFUSALS:
		check(MenuScreen.error_actions(code, "K7QPM", false).size() >= 2, "%s has a next step" % code)
	check_eq(OnlineSession.closed_message(4001, "K7QPM"), "Room K7QPM already has two players. Try another code.", "a full duel")
	check_eq(OnlineSession.closed_message(1006, "K7QPM"), "", "a lost duel is no refusal")
