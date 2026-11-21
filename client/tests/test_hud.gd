extends TestCase
## The match screens of rooms v1 (docs/design/rooms-teams-view-ui-v1.md, section 3, batch 3): the
## room HUD strip fits every screen and never runs under Chat and the menu, the touch controls'
## layout (fixed Combo, Throw only while armed, the Up and Down half-moons, nothing overlapping),
## and the result panel's countdown.

## The logical sizes of the windows the scene smoke test uses (SceneSmoke.logical_size: 1920 x 1080
## stretched with "expand", so a 4:3 window is 1920 x 1440), and two narrower strips than any window
## gives, for the collapsed layouts.
var SIZES: Array = SceneSmoke.VIEW_WINDOWS.map(func(w: Vector2) -> Vector2: return SceneSmoke.logical_size(w))
const NARROW := [Vector2(1728, 1080), Vector2(1440, 1080)]
## What main.gd keeps clear at the right in a room: Chat and the menu (2 x 20 + 2.6 x 104).
const RIGHT_INSET := 310.4


static func _teams(n: int, count: int) -> PackedInt32Array:
	var t := PackedInt32Array()
	for i in n:
		t.append(0 if count == 0 else 1 + i * count / n)
	return t


static func _kos(n: int) -> PackedInt32Array:
	var k := PackedInt32Array()
	for i in n:
		k.append((i * 7) % 5)
	return k


func _check_strip(size: Vector2, n: int, count: int, you: int) -> Dictionary:
	var what := "%d fighters, %d teams, you %d, %s" % [n, count, you, size]
	var layout := ArenaView.hud_strip(size.x, RIGHT_INSET, _teams(n, count), you, _kos(n))
	var clock: Rect2 = layout["clock"]
	var rects: Array[Rect2] = [clock]
	var chips := {}
	for item: Dictionary in layout["items"]:
		var r: Rect2 = item["rect"]
		check(r.position.x >= ArenaView.BAR_MARGIN - 0.5 and r.end.x <= size.x - RIGHT_INSET + 0.5,
			"%s: %s %s runs outside the strip (%.0f..%.0f)" % [what, item["kind"], r, ArenaView.BAR_MARGIN, size.x - RIGHT_INSET])
		check(r.end.y <= 90.0, "%s: %s %s runs under the HUD band" % [what, item["kind"], r])
		for q in rects:
			check(not q.grow(-0.5).intersects(r), "%s: %s %s overlaps %s" % [what, item["kind"], r, q])
		rects.append(r)
		if item["kind"] == "chip":
			chips[int(item["i"])] = r
			check(r.size.x >= ArenaView.CHIP_MIN - 0.5, "%s: chip %d is only %.0f px wide" % [what, item["i"], r.size.x])
	if you >= 0:
		check(chips.has(you), "%s: your chip isn't shown" % what)
		if chips.has(you):
			check((chips[you] as Rect2).size.x >= ArenaView.YOU_MIN - 0.5 or (chips[you] as Rect2).size.x >= 1.5 * float(layout["chip_w"]) - 0.5,
				"%s: your chip is no wider than the others" % what)
	return {"layout": layout, "chips": chips}


func test_the_room_strip_fits_every_screen_and_keeps_clear_of_chat_and_the_menu() -> void:
	for size: Vector2 in SIZES + NARROW:
		for n: int in [2, 4, 8, 9, 12, 15]:
			for count: int in [0, 2, 3, 4]:
				if count > 0 and count < RoomSession.teams_needed(n):
					continue
				for you: int in [0, n - 1, -1]:
					_check_strip(size, n, count, you)


func test_a_full_room_shows_everyone_on_every_window() -> void:
	for size: Vector2 in SIZES:
		for count: int in [0, 4]:
			var out := _check_strip(size, 15, count, 3)
			check(not bool(out["layout"]["collapsed"]), "15 fighters, %d teams, %s: the strip collapsed" % [count, size])
			check_eq((out["chips"] as Dictionary).size(), 15, "15 fighters, %d teams, %s: chips shown" % [count, size])


func test_a_strip_too_narrow_keeps_your_team_and_the_other_teams_totals() -> void:
	var size := Vector2(1440, 1080)
	var teams := _teams(15, 4)
	var out := _check_strip(size, 15, 4, 5)
	var layout: Dictionary = out["layout"]
	check(bool(layout["collapsed"]), "1440 px with four teams of 15 collapses")
	var plates := 0
	for item: Dictionary in layout["items"]:
		if item["kind"] == "plate":
			plates += 1
	check_eq(plates, 4, "every team keeps its total plate")
	for i: int in out["chips"]:
		check_eq(teams[i], teams[5], "only your team keeps its chips (fighter %d)" % i)
	check_eq((out["chips"] as Dictionary).size(), 4, "your team's four chips")


func test_a_narrow_free_for_all_strip_shows_you_the_leaders_and_how_many_more() -> void:
	var size := Vector2(1440, 1080)
	var kos := _kos(15)
	var layout := ArenaView.hud_strip(size.x, RIGHT_INSET, _teams(15, 0), 14, kos)
	check(bool(layout["collapsed"]), "1440 px with 15 collapses")
	var shown: Array[int] = []
	var more := 0
	for item: Dictionary in layout["items"]:
		if item["kind"] == "chip":
			shown.append(int(item["i"]))
		elif item["kind"] == "more":
			more = int(item["count"])
	check(shown.has(14), "you are shown")
	check_eq(shown.size() + more, 15, "the chips and the \"+n\" add up to everyone")
	# Everyone left out has no more KOs than anyone shown (you aside).
	var least := 1000
	for i in shown:
		if i != 14:
			least = mini(least, kos[i])
	for i in 15:
		if not shown.has(i):
			check(kos[i] <= least, "fighter %d with %d KOs is left out for one with %d" % [i, kos[i], least])


# ---- the touch controls ----

func _touch(size: Vector2, weapon: int) -> TouchControls:
	var t := TouchControls.new()
	t.size = size
	t.weapon = weapon
	return t


func test_the_touch_buttons_never_overlap_and_stay_on_screen() -> void:
	var before := GameSettings.touch_scale
	for k: float in GameSettings.TOUCH_SCALES:
		GameSettings.touch_scale = k
		for size: Vector2 in SIZES:
			var t := _touch(size, Weapons.crate_list()[0])
			var n := TouchControls.PAD_BUTTONS.size()
			for i in n:
				var a: Array = TouchControls.PAD_BUTTONS[i]
				var ca := t.point_for(a[0])
				var ra := TouchControls.radius_of(i)
				check(Rect2(Vector2.ZERO, size).encloses(Rect2(ca - Vector2(ra, ra), Vector2(ra, ra) * 2.0)), "%s is off the %s screen at %.2f" % [a[1], size, k])
				for j in range(i + 1, n):
					var b: Array = TouchControls.PAD_BUTTONS[j]
					check(ca.distance_to(t.point_for(b[0])) >= ra + TouchControls.radius_of(j) + 8.0 * k, "%s and %s overlap on %s at %.2f" % [a[1], b[1], size, k])
			t.free()
	GameSettings.touch_scale = before


func test_combo_is_fixed_and_throw_shows_only_while_armed() -> void:
	var size := Vector2(2400, 1080)
	var bare := _touch(size, 0)
	var armed := _touch(size, Weapons.crate_list()[0])
	var combo := bare.point_for(TouchControls.COMBO)
	var throw := armed.point_for(Buttons.DROP_WEAPON)
	check_eq(bare._button_at(combo), armed._button_at(combo), "Combo is there armed or not")
	check(bare._button_at(combo) >= 0, "Combo is a button")
	check_eq(TouchControls.PAD_BUTTONS[bare._button_at(combo)][0], Buttons.LIGHT | Buttons.HEAVY, "Combo presses Kick and Heavy")
	check_eq(bare._button_at(throw), TouchControls.NOTHING, "no Throw empty-handed")
	check(armed._button_at(throw) >= 0 and TouchControls.PAD_BUTTONS[armed._button_at(throw)][0] == Buttons.DROP_WEAPON, "Throw while armed")
	check_eq(TouchControls.PAD_BUTTONS[armed._button_at(armed.point_for(Buttons.LIGHT))][1], "Kick", "Kick keeps its name")
	bare.free()
	armed.free()


func test_the_up_and_down_zones_are_easy_to_hit() -> void:
	for size: Vector2 in SIZES:
		var t := _touch(size, 0)
		check(t._down_zone() >= TouchControls.DOWN_ZONE_MIN, "the Down zone is %.0f px tall on %s" % [t._down_zone(), size])
		check_eq(t._move_buttons(t.point_for(Buttons.UP)), Buttons.UP, "the Up half-moon's middle")
		check_eq(t._move_buttons(t.point_for(Buttons.DOWN)), Buttons.DOWN, "the Down half-moon's middle")
		check_eq(t._target_at(Vector2(t.point_for(Buttons.DOWN).x, size.y - 2.0)), TouchControls.MOVE, "the Down zone runs to the screen's edge")
		check(TouchControls.ARROW_SIZE >= 50.0, "the arrows are at least 50 px")
		t.free()


func test_the_floor_line_is_above_the_thumbs() -> void:
	for size: Vector2 in SIZES:
		var floor_y := TouchControls.floor_clear_y(size)
		check(floor_y >= size.y * 0.64, "the floor line %.0f leaves the arena room on %s" % [floor_y, size])
		check(is_equal_approx(floor_y, size.y - TouchControls.BAND_HEIGHT), "the floor line is the band's top on %s" % size)
		# Every button, not only the pad and Kick / Jump, stays under the line (rooms v1, fix 5).
		var t := _touch(size, Weapons.crate_list()[0])
		for b: int in [Buttons.LEFT, Buttons.RIGHT]:
			check(t.point_for(b).y - TouchControls.MOVE_RADIUS >= floor_y - 0.5, "the pad rises above the floor line on %s" % size)
		for pb: Array in TouchControls.PAD_BUTTONS:
			check(t.point_for(pb[0]).y - float(pb[2]) >= floor_y - 0.5, "%s rises above the floor line on %s" % [pb[1], size])
		# The Up arrow drawn at rest is under the line too (the half-moon shows only while held).
		check(t.up_arrow_at().y - TouchControls.ARROW_SIZE / 1.6 >= floor_y - 0.5, "the Up arrow rises above the floor line on %s" % size)
		t.free()


## Nobody standing anywhere is under a drawn control (review of rooms v1): on every arena, normal
## and big, in a room's whole-level view with touch at every touch size, a fighter's waist (0.9 m
## up) at any spot along any platform's top is outside every circle the controls draw. (The view's
## zoom with no state is the fit; a pull-back only lowers everything further from the HUD, never
## below the floor line, which stays where it is.)
func test_nobody_standing_is_under_a_touch_control() -> void:
	var before := GameSettings.touch_scale
	var view := ArenaView.new()
	view.room_mode = true
	view.touch_layout = true
	var problems := 0
	var first := ""
	for k: float in GameSettings.TOUCH_SCALES:
		GameSettings.touch_scale = k
		for size: Vector2 in [Vector2(2400, 1080), Vector2(1920, 1080), Vector2(1920, 1200), Vector2(1920, 1440)]:
			var circles := TouchControls.drawn_circles(size)
			for row: Array in ArenaStages.IDS:
				for big: bool in [false, true]:
					var stage := StageDefinition.by_id(row[0], StageDefinition.SIZE_BIG if big else StageDefinition.SIZE_NORMAL)
					view.rules = MatchRules.room(stage, 10)
					view.state = null
					view.place_camera(size)
					var cam := view.camera()
					var ppm: float = cam[0]
					var origin: Vector2 = cam[1]
					for p: FxRect in stage.platforms:
						var x0 := Fx.to_float(p.min_x)
						var x1 := Fx.to_float(p.max_x)
						for step in 21:
							var x := lerpf(x0, x1, step / 20.0)
							var waist := origin + Vector2(x * ppm, -(Fx.to_float(p.max_y) + 0.9) * ppm)
							for c: Array in circles:
								if waist.distance_to(c[0]) < float(c[1]):
									problems += 1
									if first == "":
										first = "%s%s at x %.1f under a control on %s at %.2f" % [stage.id, " (big)" if big else "", x, size, k]
	check_eq(problems, 0, "standing spots under a control (first: %s)" % first)
	view.free()
	GameSettings.touch_scale = before


# ---- the result ----

func test_the_result_counts_down_from_the_moment_the_match_ends() -> void:
	var s := RoomSession.new("TEST", true, 10, "T", "shahn", false)
	s.rules = MatchRules.room(StageDefinition.rooftops(), 10)
	s.state = Simulation.create_initial_state(s.rules, 4, 1)
	check_eq(s.result_left(), -1.0, "no countdown while the match runs")
	s.note_over()
	check_eq(s.result_left(), -1.0, "still none")
	s.state.match_over = true
	s.note_over()
	check_eq(s.result_left(), 1.0, "full at the end")
	for k in RoomSession.RESULT_TICKS / 2:
		s.tick(0) # not connected: the session ends, but its ticks still count
	check(absf(s.result_left() - 0.5) < 0.01, "half way: %.3f" % s.result_left())
	s.note_over()
	check(absf(s.result_left() - 0.5) < 0.01, "noting it again doesn't restart it")


func test_the_result_ranks_ties_by_falls_then_index() -> void:
	var rules := MatchRules.room(StageDefinition.rooftops(), 10)
	var state := Simulation.create_initial_state(rules, 5, 1)
	var kos := [2, 5, 2, 0, 2]
	var falls := [1, 3, 0, 0, 1]
	for i in 5:
		state.fighters[i].kos = kos[i]
		state.fighters[i].falls = falls[i]
	check_eq(Standings.fighters_ranked(state), [1, 2, 0, 4, 3], "most KOs, then fewest falls, then index")
