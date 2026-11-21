extends TestCase
## Rooms v1 polish (docs/design/rooms-teams-view-ui-v1.md, section 4, batch 5): the motion curves,
## the tag declutter in a pile, the result stings, the remembered room choices, the spawn-in ring and
## the frame-time meter. Presentation only: none of this reaches the sim.


func test_motion_curves_start_at_rest_and_settle() -> void:
	check(is_equal_approx(ArenaView.ease_in(0.0), 0.0) and is_equal_approx(ArenaView.ease_in(1.0), 1.0), "ease_in runs 0 to 1")
	var last := -1.0
	for k in 21:
		var e := ArenaView.ease_in(k / 20.0)
		check(e >= last, "ease_in never goes back (%.2f)" % (k / 20.0))
		last = e
	check(ArenaView.ease_in(2.0) == 1.0 and ArenaView.ease_in(-1.0) == 0.0, "ease_in is clamped")
	# The HUD strip drops in outwards from the clock and has settled within 0.5 s of the start.
	check(ArenaView.strip_drop(0, 0.0) < -60.0, "at frame 0 the strip is above the screen")
	check(ArenaView.strip_drop(4, 600.0) < ArenaView.strip_drop(4, 0.0), "items far from the clock come later")
	for away: float in [0.0, 300.0, 900.0, 1100.0]:
		check(ArenaView.strip_drop(30, away) == 0.0, "the strip has landed by frame 30 (%.0f px out)" % away)
		check(ArenaView.strip_drop(100000, away) == 0.0, "a mid-match joiner sees it in place")
	# The team plate bumps and its "+1" pops (overshoots, then settles).
	check(ArenaView.plate_bump(-1) == 0.0 and ArenaView.plate_bump(12) == 0.0 and ArenaView.plate_bump(6) > 0.1, "the plate bumps for 0.2 s")
	check(is_equal_approx(ArenaView.plus_one_pop(0), 0.4), "+1 starts small")
	var peak := 0.0
	for s in 9:
		peak = maxf(peak, ArenaView.plus_one_pop(s))
	check(peak > 1.15, "+1 overshoots (%.2f)" % peak)
	check(is_equal_approx(ArenaView.plus_one_pop(8), 1.0) and is_equal_approx(ArenaView.plus_one_pop(40), 1.0), "+1 settles at its size")
	# The result rows slide in one by one, in rank order, and all have landed within 1.5 s.
	check(ResultScreen.row_slide(0, 0.0) < 0.0, "nothing before the panel has faded in")
	for k in 15:
		var start := -1.0
		for ms in 1500:
			if ResultScreen.row_slide(k, ms / 1000.0) >= 0.0:
				start = ms
				break
		check(start >= 0.0, "row %d appears" % k)
		check(ResultScreen.row_slide(k, 1.5) == 0.0, "row %d has landed by 1.5 s" % k)
		if k > 0:
			check(ResultScreen.row_slide(k, start / 1000.0 - 0.001) < 0.0 and ResultScreen.row_slide(k - 1, start / 1000.0) >= 0.0,
				"row %d comes after row %d" % [k, k - 1])
	# START's pulse: a smooth beat between 0 and 1.
	for k in 40:
		var p := RoomScreen.start_pulse(k * 0.1)
		check(p >= 0.0 and p <= 1.0, "the pulse stays in 0..1")
	check(RoomScreen.start_pulse(0.0) < 0.01 and RoomScreen.start_pulse(RoomScreen.PULSE_SEC / 2.0) > 0.99, "one beat a PULSE_SEC")


static func _pile(n: int, at: Vector2, w: float, h: float) -> Array[Rect2]:
	var out: Array[Rect2] = []
	for i in n:
		out.append(Rect2(at.x - w / 2.0, at.y - h, w, h))
	return out


static func _overlaps(rects: Array[Rect2]) -> int:
	var n := 0
	for a in rects.size():
		for b in range(a + 1, rects.size()):
			if rects[a].intersects(rects[b]):
				n += 1
	return n


func test_tags_in_a_pile_spread_out_and_stay_on_screen() -> void:
	var bounds := Rect2(6, 96, 1908, 978)
	# Five, then nine fighters standing on one spot (a brawl in a corner of the big level).
	for n: int in [5, 9]:
		var placed := ArenaView.place_tags(_pile(n, Vector2(960, 700), 110, 34), bounds)
		check(_overlaps(placed) == 0, "a pile of %d: %d tags overlap" % [n, _overlaps(placed)])
	# Fifteen: all on screen, yours (the first) where it wants to be, the rest spread over the 12
	# free spots near the pile.
	var wanted := _pile(15, Vector2(960, 700), 110, 34)
	var placed := ArenaView.place_tags(wanted, bounds)
	check(placed[0] == wanted[0], "your tag stays over your fighter")
	var spots := {}
	for r in placed:
		check(bounds.encloses(r), "%s stays on screen" % r)
		spots[r] = true
	check(spots.size() >= 12, "a pile of 15 uses at least 12 spots (%d)" % spots.size())
	# A pile at the screen's edges and under the HUD band still stays inside.
	for at: Vector2 in [Vector2(10, 140), Vector2(1910, 140), Vector2(1910, 1075)]:
		for r in ArenaView.place_tags(_pile(9, at, 110, 34), bounds):
			check(bounds.encloses(r), "a pile at %s: %s stays inside" % [at, r])
	# Tags with room to spare don't move.
	var apart: Array[Rect2] = [Rect2(100, 500, 100, 34), Rect2(400, 500, 100, 34), Rect2(700, 520, 100, 34)]
	check(ArenaView.place_tags(apart, bounds) == apart, "tags apart stay over their fighters")


func test_the_result_sting_follows_the_winning_side() -> void:
	var practice := MatchRules.new()
	var state := Simulation.create_initial_state(practice, 2, 1)
	state.winner_index = 0
	check(SoundDirector.result_sting(state, practice, 0) == "win", "you won practice: the win sting")
	check(SoundDirector.result_sting(state, practice, 1) == "lose", "the CPU's side hears the lose sting")
	check(SoundDirector.result_sting(state, practice, -1) == "win", "watching, or two at one keyboard: the winner's jingle")
	check(SoundDirector.result_sting(state, null, 1) == "lose", "with no rules, only the winner wins")
	state.winner_index = -1
	check(SoundDirector.result_sting(state, practice, 0) == "", "a draw has no sting")
	# Four teams of 15: the MVP's whole team hears the win, everyone else the lose sting.
	var teams := PackedInt32Array()
	for i in 15:
		teams.append(1 + i % 4)
	var room := MatchRules.room(StageDefinition.by_id("rooftops", StageDefinition.SIZE_BIG), 10, PackedInt32Array(), teams)
	var big := Simulation.create_initial_state(room, 15, 1)
	big.winner_index = 5 # team 2
	for you in 15:
		var want := "win" if room.team_of(you) == 2 else "lose"
		check(SoundDirector.result_sting(big, room, you) == want, "fighter %d (team %d) hears %s" % [you, room.team_of(you), want])


func test_the_hosts_choices_are_kept_to_what_the_lobby_offers() -> void:
	for t: int in [0, 2, 3, 4]:
		check(GameSettings.room_teams_choice(t) == t, "%d is a mode chip" % t)
	for t: int in [1, 5, -1, 99]:
		check(GameSettings.room_teams_choice(t) == 0, "%d isn't: free-for-all" % t)
	check(GameSettings.room_minutes_choice(9) == 10 and GameSettings.room_minutes_choice(16) == 15 and GameSettings.room_minutes_choice(12) == 12,
		"minutes stay on the slider's 10 to 15")
	check(RoomSession.url_for_room("ABCDE", true, GameSettings.room_minutes_choice(13), "Raju").contains("minutes=13"),
		"a host's remembered minutes go in the room's address")


func test_everyone_spawns_in_with_a_ring_in_their_colour() -> void:
	var teams := PackedInt32Array()
	for i in 15:
		teams.append(1 + i % 4)
	var rules := MatchRules.room(StageDefinition.by_id("rooftops", StageDefinition.SIZE_BIG), 10, PackedInt32Array(), teams)
	var state := Simulation.create_initial_state(rules, 15, 3)
	var view := ArenaView.new()
	view.rules = rules
	view.state = state
	view._watch_fx()
	var rings: Array = view._fx.filter(func(fx: Dictionary) -> bool: return fx["kind"] == "spawn")
	check(rings.size() == 15, "15 spawn rings at the start (%d)" % rings.size())
	for k in mini(rings.size(), 15):
		var fx: Dictionary = rings[k]
		check(int(fx["life"]) == ArenaView.SPAWN_FRAMES and ArenaView.SPAWN_FRAMES == 18, "a ring lasts 0.3 s")
		check(fx["color"] == TeamStyle.color_of(teams[k]), "fighter %d's ring is its team's colour" % k)
	# A respawn: the fighter was out last frame and is back now.
	var inputs := PackedInt32Array()
	inputs.resize(15)
	Simulation.advance(state, rules, inputs)
	view._fx.clear()
	view._seen[4][3] = false
	view._watch_fx()
	rings = view._fx.filter(func(fx: Dictionary) -> bool: return fx["kind"] == "spawn")
	check(rings.size() == 1 and rings[0]["color"] == TeamStyle.color_of(teams[4]), "a respawn flashes once, in its colour")
	view.free()


func test_the_frame_meter_reports_every_five_seconds() -> void:
	var meter := FrameMeter.new()
	var t := 1_000_000
	var lines := 0
	check(not meter.frame(t, 0), "the first frame only starts the clock")
	for k in 60 * 11:
		t += 16_667 if k != 100 else 40_000 # one slow frame
		if meter.frame(t, 2_500):
			lines += 1
			var line := meter.line("room of 15 fighters, big level")
			check(line.begins_with(FrameMeter.TAG), "the line is tagged for logcat: %s" % line)
			check(line.contains("room of 15 fighters, big level"), "it says what was measured")
			check(line.contains("fps") and line.contains("arena drawing 2.50 ms"), "it has the rate and the drawing time: %s" % line)
			if lines == 1:
				check(line.contains("slowest 40.0 ms"), "the slowest frame is in the first window: %s" % line)
				check(line.contains("16.") or line.contains("17."), "about 16.7 ms a frame: %s" % line)
	check(lines == 2, "two lines in 11 s (%d)" % lines)
	check(FrameMeter.report(5_000_000, 300, 20_000, 300_000, 300, "x").contains("16.7 ms a frame on average (60.0 fps)"), "the arithmetic")
	meter.reset()
	check(not meter.frame(t + 10_000_000, 0), "after a reset the next frame starts afresh")
