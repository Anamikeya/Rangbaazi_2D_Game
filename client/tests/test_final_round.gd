extends TestCase
## The last round of fixes to rooms v1 (docs/design/rooms-teams-view-ui-v1.md, Build notes, "Final
## round"): presentation and menus only, nothing here touches the sim.

const SCREENS := [Vector2(2400, 1080), Vector2(1920, 1080), Vector2(1920, 1440)]


func _touch(size: Vector2, weapon: int) -> TouchControls:
	var t := TouchControls.new()
	t.size = size
	t.weapon = weapon
	return t


## How to play: no callout box covers a control drawn on screen (the move pad, each button, Throw
## while armed), at every touch size, on every screen, armed or not.
func test_no_callout_covers_a_control() -> void:
	var before := GameSettings.touch_scale
	for k: float in GameSettings.TOUCH_SCALES:
		GameSettings.touch_scale = k
		for size: Vector2 in SCREENS:
			for weapon: int in [0, Weapons.crate_list()[0]]:
				var t := _touch(size, weapon)
				var c := HelpCallouts.new()
				c.touch = t
				c.size = size
				var circles := c.shown_circles()
				check_eq(circles.size(), 8 if weapon > 0 else 7, "the controls on screen")
				var out := c.callouts()
				for item: Dictionary in out:
					var r: Rect2 = item["rect"]
					check(Rect2(Vector2.ZERO, size).encloses(r), "%s is off the %s screen at %.2f" % [item["text"], size, k])
					for cr: Array in circles:
						check(not HelpCallouts.rect_meets_circle(r, cr[0], float(cr[1])),
							"%s covers the control at %s on %s at %.2f (armed %s)" % [item["text"], cr[0], size, k, weapon > 0])
					for other: Dictionary in out:
						if other != item:
							check(not r.intersects(other["rect"]), "%s overlaps %s on %s at %.2f" % [item["text"], other["text"], size, k])
				check(c.card_bottom() >= size.y * HelpCallouts.CARD_MIN, "the card has %.0f px on %s at %.2f (armed %s)" % [c.card_bottom(), size, k, weapon > 0])
				c.free()
				t.free()
	GameSettings.touch_scale = before


## The KO feed between the buttons: two 12-letter names (ArenaLimits.MaxNameLength) fit the band at
## every touch size, cut rather than shrunk past FEED_MIN_FONT.
func test_long_names_in_the_ko_feed_fit_between_the_buttons() -> void:
	var font := ThemeDB.fallback_font
	var before := GameSettings.touch_scale
	for k: float in GameSettings.TOUCH_SCALES:
		GameSettings.touch_scale = k
		for size: Vector2 in SCREENS:
			var band := TouchControls.free_band(size)
			for names: Array in [["Rajkumar1234", "Mohammad_Ali"], ["WWWWWWWWWWWW", "MMMMMMMMMMMM"], ["Shan", "Aastha"]]:
				var parts: Array = [[names[0], Color.WHITE, true], ["  KO  ", Color.WHITE, false], [names[1], Color.WHITE, true]]
				var fitted := ArenaView.fit_feed_line(font, parts, band.size.x - 24.0)
				check(float(fitted[2]) <= band.size.x - 24.0 + 0.5, "%s KO %s is %.0f px in a %.0f px band on %s at %.2f" % [names[0], names[1], fitted[2], band.size.x, size, k])
				check(int(fitted[1]) >= ArenaView.FEED_MIN_FONT, "the type stays %d px or more" % ArenaView.FEED_MIN_FONT)
				check_eq(str(fitted[0][1][0]), "  KO  ", "the KO itself is never cut")
	GameSettings.touch_scale = before
	var short := ArenaView.fit_feed_line(font, [["Shan", Color.WHITE, true], ["  KO  ", Color.WHITE, false], ["Aastha", Color.WHITE, true]], 600.0)
	check_eq(str(short[0][0][0]) + str(short[0][2][0]), "ShanAastha", "short names stay whole")
	check_eq(int(short[1]), ArenaView.FEED_FONT, "at full size")


## The winners' celebration runs at one frame a draw whether the state stands still (practice,
## rooms) or goes on counting behind the result (a duel): never two.
func test_the_result_animates_at_one_frame_a_draw() -> void:
	check_eq(ArenaView.anim_frame(500, -1, 0), 500, "a running match: its own frame")
	# Practice: the state stands at 900 from the end; the draws carry it on.
	var last := -1
	for d in range(1, 30):
		var f := ArenaView.anim_frame(900, 900, d)
		check(last < 0 or f == last + 1, "practice steps by 1 (%d after %d)" % [f, last])
		last = f
	# A duel: the state counts on, one a tick, as the draws do.
	last = -1
	for d in range(1, 30):
		var f := ArenaView.anim_frame(900 + d - 1, 900, d)
		check(last < 0 or f == last + 1, "a duel steps by 1, not 2 (%d after %d)" % [f, last])
		last = f


## Touch labels fit inside their buttons at every size (Combo and Throw ran past their rims).
func test_every_touch_label_fits_its_button() -> void:
	var font := ThemeDB.fallback_font
	var before := GameSettings.touch_scale
	for k: float in GameSettings.TOUCH_SCALES:
		GameSettings.touch_scale = k
		for i in TouchControls.PAD_BUTTONS.size():
			var b: Array = TouchControls.PAD_BUTTONS[i]
			var r := TouchControls.radius_of(i)
			var font_size := maxi(30, int(r * 0.34))
			var text_y := font_size * 0.35
			if b[0] == Buttons.DROP_WEAPON:
				text_y = r * 0.72
				font_size = 26
			var fitted := TouchControls.label_fit(font, str(b[1]), font_size, r, text_y)
			var middle := text_y - fitted * 0.35
			var chord := 2.0 * sqrt(r * r - middle * middle)
			var width := font.get_string_size(str(b[1]), HORIZONTAL_ALIGNMENT_LEFT, -1, fitted).x + 2.0 * TouchControls.OUTLINE_SIZE
			check(width <= chord, "%s is %.0f px on a %.0f px chord at %.2f" % [b[1], width, chord, k])
			check(fitted >= TouchControls.LABEL_MIN, "%s stays readable" % b[1])
	GameSettings.touch_scale = before


## Dodge (the parry, timed to a hit) is the nearer of the upper row to Kick; Combo and Throw are at
## least 112 and 100 px across at Medium.
func test_dodge_is_within_easy_reach_and_small_buttons_are_big_enough() -> void:
	var t := _touch(Vector2(2400, 1080), Weapons.crate_list()[0])
	var kick := t.point_for(Buttons.LIGHT)
	check(kick.distance_to(t.point_for(Buttons.DODGE)) < kick.distance_to(t.point_for(Buttons.HEAVY)), "Dodge is nearer Kick than Heavy")
	check(t.size.x - t.point_for(Buttons.DODGE).x <= 450.0, "Dodge's centre is within 450 px of the right edge")
	for i in TouchControls.PAD_BUTTONS.size():
		var b: Array = TouchControls.PAD_BUTTONS[i]
		if b[0] == TouchControls.COMBO:
			check(float(b[2]) >= 66.0, "Combo is r%.0f" % b[2])
		if b[0] == Buttons.DROP_WEAPON:
			check(float(b[2]) >= 56.0, "Throw is r%.0f" % b[2])
	t.free()


## A win on fewer falls says so under the title, in teams and in a free-for-all.
func test_a_win_on_falls_says_so() -> void:
	var rules := MatchRules.new()
	rules.teams = PackedInt32Array([1, 2, 1, 2])
	var state := Simulation.create_initial_state(rules, 4, 7)
	for i in 4:
		state.fighters[i].kos = [6, 5, 5, 6][i]
		state.fighters[i].falls = [5, 6, 6, 6][i]
	state.match_over = true
	state.winner_index = 0
	check_eq(ResultScreen.tiebreak_line(state, rules), "on fewer falls (11 v 12)", "ICE 11 KOs / 11 falls beats 11 / 12")
	state.fighters[1].kos = 9
	check_eq(ResultScreen.tiebreak_line(state, rules), "", "a win on KOs needs no line")
	var ffa := MatchRules.new()
	var s2 := Simulation.create_initial_state(ffa, 3, 7)
	for i in 3:
		s2.fighters[i].kos = [4, 4, 1][i]
		s2.fighters[i].falls = [1, 3, 0][i]
	s2.match_over = true
	s2.winner_index = 0
	check_eq(ResultScreen.tiebreak_line(s2, ffa), "on fewer falls (1 v 3)", "a free-for-all on falls")
	s2.winner_index = -1
	check_eq(ResultScreen.tiebreak_line(s2, ffa), "", "no winner, no line")


## A local test page's ?relay= goes into the share link; anything else is dropped.
func test_the_share_link_keeps_a_local_relay() -> void:
	check_eq(preload("res://game/main.gd").share_link_for("http://127.0.0.1:8121", "/index.html", "44VQ2", "ws://127.0.0.1:8120"),
		"http://127.0.0.1:8121/index.html?room=44VQ2&relay=ws%3A%2F%2F127.0.0.1%3A8120", "a local test relay rides along")
	check_eq(preload("res://game/main.gd").share_link_for("https://example.test", "/", "44VQ2", "wss://evil.test"),
		"https://example.test/?room=44VQ2", "any other relay is dropped")
	check_eq(preload("res://game/main.gd").share_link_for("https://example.test", "/", "44VQ2"), "https://example.test/?room=44VQ2", "none")


## The whole-level view keeps EDGE_MARGIN beside the level: a fighter 1 m past either end of the big
## level is on screen whole (its art box, 1.2 m wide and 2.2 m tall), at 16:9, 16:10, 4:3 and on the
## phone with touch.
func test_a_fighter_just_past_the_edge_is_seen_whole() -> void:
	var stage := StageDefinition.by_id("rooftops", StageDefinition.SIZE_BIG)
	var rules := MatchRules.room(stage, 10, PackedInt32Array(), PackedInt32Array())
	for view_size: Array in [[Vector2(1920, 1080), false], [Vector2(1920, 1200), false], [Vector2(1920, 1440), false], [Vector2(2400, 1080), true]]:
		var size: Vector2 = view_size[0]
		var view := ArenaView.new()
		view.rules = rules
		view.state = Simulation.create_initial_state(rules, 15, 3)
		view.room_mode = true
		view.touch_layout = view_size[1]
		view.place_camera(size)
		var half := view.half_width()
		for side: float in [-1.0, 1.0]:
			var x := side * (half + 1.0)
			var a := view._screen_point(int((x - 0.6) * Fx.ONE), 0)
			var b := view._screen_point(int((x + 0.6) * Fx.ONE), int(2.2 * Fx.ONE))
			check(a.x >= 0.0 and b.x <= size.x, "a fighter at %.0f m is cut off on %s (%.0f to %.0f px)" % [x, size, a.x, b.x])
		view.free()


## The sky above the painting deepens upwards from skyTop (never a flat panel), and the three arenas
## whose painting's top is a canopy or arches have the painted sky's blue (tools/arena_colours.gd).
func test_the_sky_above_the_painting_reads_as_sky() -> void:
	for hex in ["#4dccfa", "#0a1756", "#735578", "#c8c8c8"]:
		var c := Color(hex)
		var deep := ArenaView.sky_deep(c)
		check(deep.v < c.v, "%s deepens (%s)" % [hex, deep.to_html(false)])
		check(absf(deep.h - c.h) < 0.01 or c.s < 0.05, "%s keeps its hue" % hex)
	for id in ["siraj", "suhani", "avipray"]:
		var sky := Color(str(ArtLibrary.arena(id).get("skyTop", "#000000")))
		check(sky.b > sky.r + 0.2 and sky.b > 0.8, "%s's skyTop %s is the painted sky's blue" % [id, sky.to_html(false)])
