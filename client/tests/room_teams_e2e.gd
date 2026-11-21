extends SceneTree
## End-to-end check of teams in rooms (docs/design/rooms-teams-view-ui-v1.md, 6.4) against a real
## server. Start one with short matches, so the test can watch a match end:
##   $env:ARENA_MATCH_SECONDS = 40; dotnet run --project server/Rangbaazi.Relay
##   godot --headless --path client -s res://tests/room_teams_e2e.gd -- --relay ws://127.0.0.1:8080
## (Against a relay with the normal 10-minute matches the host ends the last match itself, and the
## winner check is skipped; the result line says so.)
##
## 1. A host and a guest join; the host picks 2 teams and 13 CPUs: the room grows to 4 teams of
##    15, every member on a team of at most 4. A friend joining then takes a CPU's place, and the
##    CPU comes back when they leave. The host starts: both see 15 fighters whose teams
##    match the lobby, on the big level (rooms v1, 2.2: "size" "big" in start, a fighter more than
##    17 m out in the server's frames, past the normal level's edge), then the host ends the match.
## 2. Back in the lobby the host sets 2 teams and 2 CPUs; the guest picks team 2: both lobbies
##    show 2 v 2 with the guest on team 2.
## 3. Start: both sessions' rules.team_of match the lobby, and the host's and guest's fighters are
##    not allies; four fighters play the normal level.
## 4. The guest leaves: the host's lobby marks them left, and their fighter keeps moving (the CPU
##    plays it).
## 5. The match ends: the winner byte is a fighter on the team with the most KOs (then the fewest
##    falls) by Standings, or -1 when the best two are level.
## 6. The host is back in the lobby, without the guest.
## Exit code 0 = pass.

const TIMEOUT_MS := 180_000


func _initialize() -> void:
	var rng := RandomNumberGenerator.new()
	rng.randomize()
	var room := "RT%04d" % rng.randi_range(0, 9999)
	var host := RoomSession.new(room, true, 10, "Host")
	var guest: RoomSession = null
	var started := Time.get_ticks_msec()
	var next_tick := started
	var step := "join"
	var result := ""
	var note := ""
	var wait := 0
	var guest_fighter := -1
	var seen_x: Array[int] = []
	var moves := 0
	print("room teams e2e: %s" % RoomSession.url_for_room(room, true, 10, "Host"))

	while result == "":
		var now := Time.get_ticks_msec()
		if now < next_tick:
			OS.delay_msec(next_tick - now)
			continue
		next_tick += 16
		host.tick(Buttons.NONE)
		if guest != null and step != "left" and step != "over" and step != "back":
			guest.tick(Buttons.NONE)
		if host.phase == RoomSession.Phase.ENDED or (guest != null and guest.phase == RoomSession.Phase.ENDED and step != "left" and step != "over" and step != "back"):
			result = "FAIL ended early at %s (host: %s / guest: %s)" % [step, host.end_message, guest.end_message if guest != null else ""]
			break
		if Time.get_ticks_msec() - started > TIMEOUT_MS:
			result = "FAIL timed out at step %s" % step
			break

		match step:
			"join":
				if host.phase == RoomSession.Phase.LOBBY and host.is_host():
					guest = RoomSession.new(room, false, 10, "Guest")
					step = "lobby"
			"lobby":
				if guest.phase == RoomSession.Phase.LOBBY and host.players.size() == 2:
					host.set_teams(2)
					host.set_bots(13)
					step = "fifteen"
			"fifteen":
				if guest.players.size() == 15 and guest.team_count == 4 and host.team_count == 4:
					var sizes := guest.team_sizes()
					for t in range(1, 5):
						if sizes[t] < 1 or sizes[t] > RoomSession.TEAM_SIZE:
							result = "FAIL team %d has %d members" % [t, sizes[t]]
					for p: Dictionary in guest.players:
						if int(p.get("team", 0)) < 1:
							result = "FAIL %s has no team" % str(p)
					if result != "":
						break
					var refusals := _refusals(room, [host, guest])
					if refusals != "":
						result = "FAIL refusals: " + refusals
						break
					print("room teams e2e: an unknown code and an old client end with no_room and old_client")
					var seat := _cpu_seat(room, host, guest)
					if seat != "":
						result = "FAIL a friend joining the full room: " + seat
						break
					print("room teams e2e: a 16th joiner took a CPU's place (15 with 12 CPUs), and the CPU came back when they left")
					host.start_match()
					step = "start15"
			"start15":
				if host.phase == RoomSession.Phase.PLAYING and guest.phase == RoomSession.Phase.PLAYING \
						and guest.state != null and guest.state.frame > 30 and host.state != null and host.state.frame > 30:
					var bad := _teams_mismatch(host) + _teams_mismatch(guest)
					if guest.state.fighters.size() != 15 or bad != "":
						result = "FAIL 15-player start: %d fighters %s" % [guest.state.fighters.size(), bad]
						break
					var used := {}
					for i in 15:
						used[guest.rules.team_of(i)] = true
					if used.size() != 4:
						result = "FAIL the 15 are on %d teams" % used.size()
						break
					var level := _level_problem(host, true) + _level_problem(guest, true)
					if level != "":
						result = "FAIL 15-player level: %s" % level
						break
					print("room teams e2e: 15 fighters on 4 teams, sizes %s, on the big level" % str(guest.team_sizes().slice(1)))
					host.end_match()
					step = "back15"
			"back15":
				if host.phase == RoomSession.Phase.LOBBY and guest.phase == RoomSession.Phase.LOBBY and host.room_phase == "lobby":
					host.set_bots(2)
					host.set_teams(2)
					step = "two"
			"two":
				if host.players.size() == 4 and host.team_count == 2 and guest.team_count == 2 and guest.players.size() == 4:
					if guest.my_team() != 2:
						guest.pick_team(2)
					step = "picked"
			"picked":
				if guest.my_team() == 2 and host.team_count == 2 and _two_v_two(host) and _two_v_two(guest) and _team_of_id(host, guest.my_id) == 2:
					host.start_match()
					step = "start"
			"start":
				if host.phase == RoomSession.Phase.PLAYING and guest.phase == RoomSession.Phase.PLAYING \
						and guest.state != null and guest.state.frame > 30 and host.state != null and host.state.frame > 30:
					var bad := _teams_mismatch(host) + _teams_mismatch(guest)
					if bad != "":
						result = "FAIL teams in the rules: %s" % bad
						break
					if Simulation.allies(host.rules, host.my_fighter, guest.my_fighter) or Simulation.allies(guest.rules, host.my_fighter, guest.my_fighter):
						result = "FAIL host (%d) and guest (%d) are allies" % [host.my_fighter, guest.my_fighter]
						break
					if guest.rules.team_of(guest.my_fighter) != 2:
						result = "FAIL the guest's fighter is on team %d" % guest.rules.team_of(guest.my_fighter)
						break
					var level := _level_problem(host, false) + _level_problem(guest, false)
					if level != "":
						result = "FAIL 4-player level: %s" % level
						break
					guest_fighter = guest.my_fighter
					guest.close()
					step = "left"
			"left":
				# The host's lobby marks the guest left; their fighter goes on moving.
				if host.fighter_left(guest_fighter):
					var f := host.state.fighters[guest_fighter]
					if seen_x.is_empty() or seen_x[-1] != f.pos_x:
						moves += 1
					seen_x.append(f.pos_x)
					wait += 1
					if wait >= 240:
						if moves < 20:
							result = "FAIL the leaver's fighter moved on only %d of 240 frames" % moves
							break
						if host.rules.time_limit_frames > 120 * 60:
							note = " (winner check skipped: start the relay with ARENA_MATCH_SECONDS to watch a match end)"
							host.end_match()
							step = "back"
						else:
							step = "over"
			"over":
				if host.state != null and host.state.match_over:
					var check := _winner_check(host)
					if check != "":
						result = "FAIL " + check
						break
					print("room teams e2e: match over, winner %d, team KOs %d v %d" % [host.state.winner_index,
						Standings.team_kos(host.state, host.rules, 1), Standings.team_kos(host.state, host.rules, 2)])
					step = "back"
			"back":
				if host.phase == RoomSession.Phase.LOBBY and host.room_phase == "lobby":
					if host.players.size() != 3 or _team_of_id(host, guest.my_id) != -1:
						result = "FAIL back in the lobby with %d members" % host.players.size()
						break
					result = "PASS 15 on 4 teams, 2 v 2 with the guest on team 2, the leaver played by the CPU, the winner from the best team, back to the lobby" + note

	host.close()
	if guest != null:
		guest.close()
	for n in 10:
		host.tick(0)
		if guest != null:
			guest.tick(0)
		OS.delay_msec(10)
	print("ROOM TEAMS E2E " + result)
	quit(0 if result.begins_with("PASS") else 1)


## A friend opening the link of this room of 15 (2 people, 13 CPUs) gets in, in the last-joined
## CPU's place (final round: CPUs never keep a person out), and when they leave the CPU comes back.
## "" when both hold.
func _cpu_seat(room: String, host: RoomSession, guest: RoomSession) -> String:
	var friend := RoomSession.new(room, false, 10, "Friend")
	var seen := false
	for n in 600:
		friend.tick(0)
		host.tick(Buttons.NONE)
		guest.tick(Buttons.NONE)
		if friend.phase == RoomSession.Phase.ENDED:
			return "refused: %s (%s)" % [friend.end_code, friend.end_message]
		if friend.phase == RoomSession.Phase.LOBBY and host.players.size() == 15 and host.bot_count() == 12 and _team_of_id(host, friend.my_id) > 0:
			seen = true
			break
		OS.delay_msec(16)
	if not seen:
		friend.close()
		return "the host's lobby shows %d members, %d CPUs" % [host.players.size(), host.bot_count()]
	friend.close()
	for n in 600:
		friend.tick(0)
		host.tick(Buttons.NONE)
		guest.tick(Buttons.NONE)
		if host.players.size() == 15 and host.bot_count() == 13:
			return ""
		OS.delay_msec(16)
	return "after the friend left: %d members, %d CPUs" % [host.players.size(), host.bot_count()]


## The relay's refusals reach the game with their codes, though Godot's socket drops the error text
## that comes before the close (RoomSession.REFUSALS): an unknown code is "no_room", a client one
## protocol version behind "old_client" (a room of 15 people is "full": the relay's tests cover it,
## since a room with CPUs now lets a person in). "" when both hold.
## `others` keep ticking meanwhile.
func _refusals(room: String, others: Array) -> String:
	var rng := RandomNumberGenerator.new()
	rng.randomize()
	var old := RoomSession.new("OC%04d" % rng.randi_range(0, 9999), true, 10, "Old", "shahn", false)
	old._url = old._url.replace("v=%d" % RoomSession.PROTOCOL_VERSION, "v=%d" % (RoomSession.PROTOCOL_VERSION - 1))
	old._ws.connect_to_url(old._url)
	var cases := [
		["no_room", RoomSession.new("NR%04d" % rng.randi_range(0, 9999), false, 10, "Lost")],
		["old_client", old],
	]
	var problems := ""
	for c: Array in cases:
		var s: RoomSession = c[1]
		for n in 300:
			s.tick(0)
			for o: RoomSession in others:
				o.tick(Buttons.NONE)
			if s.phase == RoomSession.Phase.ENDED:
				break
			OS.delay_msec(16)
		if s.end_code != c[0] or s.end_message != str(RoomSession.REFUSALS[c[0]]):
			problems += "%s got '%s' (%s); " % [c[0], s.end_code, s.end_message]
		s.close()
	return problems


## "" when the session plays the level it should (big or not), its fighters are where that level
## puts them (one more than 17 m out on the big level, none on the normal one), else what is wrong.
func _level_problem(s: RoomSession, big: bool) -> String:
	var want := StageDefinition.SIZE_BIG if big else StageDefinition.SIZE_NORMAL
	if s.rules.stage.size != want or s.rules.stage.platforms[0].max_x != Fx.from_int(24 if big else 16):
		return "[size %d, floor to %d]" % [s.rules.stage.size, s.rules.stage.platforms[0].max_x / Fx.ONE]
	var widest := 0
	for f in s.state.fighters:
		widest = maxi(widest, absi(f.pos_x))
	if (widest > Fx.from_int(17)) != big:
		return "[the widest fighter is %.1f m out]" % Fx.to_float(widest)
	return ""


## "" when every fighter's team in the session's rules is its lobby team, else what differs.
static func _teams_mismatch(s: RoomSession) -> String:
	if not s.rules.has_teams():
		return "[no teams in the rules]"
	var out := ""
	for i in s.fighter_ids.size():
		var lobby_team := _team_of_id(s, s.fighter_ids[i])
		if lobby_team != s.rules.team_of(i) or s.fighter_teams[i] != s.rules.team_of(i):
			out += "[fighter %d: lobby %d, rules %d]" % [i, lobby_team, s.rules.team_of(i)]
	return out


static func _team_of_id(s: RoomSession, id: int) -> int:
	for p: Dictionary in s.players:
		if int(p.get("id", -1)) == id:
			return int(p.get("team", 0))
	return -1


static func _two_v_two(s: RoomSession) -> bool:
	var sizes := s.team_sizes()
	return s.players.size() == 4 and sizes[1] == 2 and sizes[2] == 2


## "" when the winner byte agrees with the team totals (Standings), else why not.
static func _winner_check(s: RoomSession) -> String:
	var ranked := Standings.teams_ranked(s.state, s.rules)
	if ranked.size() < 2:
		return "only %d teams ranked" % ranked.size()
	var best: Dictionary = ranked[0]
	var second: Dictionary = ranked[1]
	var level: bool = best["kos"] == second["kos"] and best["falls"] == second["falls"]
	var w := s.state.winner_index
	if level:
		return "" if w == -1 else "teams level on %d KOs and %d falls but the winner is %d" % [best["kos"], best["falls"], w]
	if w < 0:
		return "no winner, but team %d leads with %d KOs" % [best["team"], best["kos"]]
	if s.rules.team_of(w) != int(best["team"]):
		return "the winner %d is on team %d, but team %d has the most KOs" % [w, s.rules.team_of(w), best["team"]]
	if Simulation.timed_team_winner(s.state.fighters, s.rules) != w:
		return "the winner byte %d is not the best team's MVP %d" % [w, Simulation.timed_team_winner(s.state.fighters, s.rules)]
	return ""
