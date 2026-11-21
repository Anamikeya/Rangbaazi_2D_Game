extends TestCase
## The menus, lobby and settings of rooms v1 (docs/design/rooms-teams-view-ui-v1.md, section 3,
## batch 4): every error the menu shows offers a next step, the touch controls follow Settings'
## size and opacity (and the camera's floor line with them), the creator never names a fighter
## "Custom", the arena picker draws the stage to scale, and no control in client/ cycles.

const TOUCH_SIZES := [Vector2(1920, 1080), Vector2(2400, 1080), Vector2(1920, 1440)]


func test_every_menu_error_offers_a_next_step() -> void:
	for web: bool in [true, false]:
		for code: String in ["full", "no_room", "taken", "old_client", "size", "stage", "closed", "connect", "timeout", "", "something_new"]:
			var actions := MenuScreen.error_actions(code, "K7QPM", web)
			check(not actions.is_empty(), "%s (web %s): no button" % [code, web])
			for a: Array in actions:
				check(str(a[1]) in ["host", "rejoin", "refresh", "update", "dismiss"], "%s: unknown action %s" % [code, a[1]])
	check_eq(MenuScreen.error_actions("full", "", true)[0], ["Host a room", "host"], "a full room: host your own")
	check_eq(MenuScreen.error_actions("old_client", "", true)[0], ["Refresh", "refresh"], "out of date on the web: refresh")
	check_eq(MenuScreen.error_actions("old_client", "", false)[0], ["Update", "update"], "out of date in the app: update")
	check_eq(MenuScreen.error_actions("closed", "K7QPM", true)[0], ["Rejoin K7QPM", "rejoin"], "a lost room: rejoin it")


func test_the_relay_refuses_an_old_client_with_its_own_code() -> void:
	var s := RoomSession.new("K7QPM", false, 10, "Raju", "shahn", false)
	s._on_text(JSON.stringify({"type": "error", "code": "old_client", "message": "This copy of the game is out of date."}))
	check_eq(s.phase, RoomSession.Phase.ENDED, "the session ends")
	check_eq(s.end_code, "old_client", "with the code the banner reads")
	check_eq(MenuScreen.error_actions(s.end_code, s.room, false)[0][1], "update", "and offers the update")


func _touch(size: Vector2) -> TouchControls:
	var t := TouchControls.new()
	t.size = size
	return t


func test_touch_size_scales_every_button_and_moves_the_floor_line() -> void:
	var before := GameSettings.touch_scale
	for size: Vector2 in TOUCH_SIZES:
		var lines := []
		for k: float in GameSettings.TOUCH_SCALES:
			GameSettings.touch_scale = k
			var t := _touch(size)
			var n := TouchControls.PAD_BUTTONS.size()
			for i in n:
				var a: Array = TouchControls.PAD_BUTTONS[i]
				var ca := t.point_for(a[0])
				var ra := TouchControls.radius_of(i)
				check(is_equal_approx(ra, float(a[2]) * k), "%s's radius follows the size" % a[1])
				check(Rect2(Vector2.ZERO, size).encloses(Rect2(ca - Vector2(ra, ra), Vector2(ra, ra) * 2.0)), "%s is off the %s screen at %.2f" % [a[1], size, k])
				for j in range(i + 1, n):
					var b: Array = TouchControls.PAD_BUTTONS[j]
					check(ca.distance_to(t.point_for(b[0])) >= ra + TouchControls.radius_of(j) + 6.0, "%s and %s overlap on %s at %.2f" % [a[1], b[1], size, k])
			# The pad's right edge stays clear of the right-hand cluster.
			var pad_right := t.point_for(Buttons.RIGHT).x + TouchControls.move_radius()
			for i in n:
				check(t.point_for(TouchControls.PAD_BUTTONS[i][0]).x - TouchControls.radius_of(i) > pad_right + 100.0, "the pad meets %s at %.2f" % [TouchControls.PAD_BUTTONS[i][1], k])
			check(t._down_zone() >= TouchControls.DOWN_ZONE_MIN, "the Down zone at %.2f" % k)
			check_eq(t._move_buttons(t.point_for(Buttons.UP)), Buttons.UP, "Up still presses Up at %.2f" % k)
			check_eq(t._move_buttons(t.point_for(Buttons.DOWN)), Buttons.DOWN, "Down still presses Down at %.2f" % k)
			lines.append(TouchControls.floor_clear_y(size))
			t.free()
		check(lines[0] > lines[1] and lines[1] > lines[2], "bigger buttons raise the floor line: %s" % [lines])
	GameSettings.touch_scale = before


func test_touch_opacity_fades_the_fills_but_never_past_opaque() -> void:
	var before := GameSettings.touch_alpha
	GameSettings.touch_alpha = GameSettings.TOUCH_ALPHAS[0]
	check(TouchControls._faded(TouchControls.FILL).a < TouchControls.FILL.a, "Low is fainter")
	GameSettings.touch_alpha = GameSettings.TOUCH_ALPHAS[2]
	check(TouchControls._faded(TouchControls.FILL).a > TouchControls.FILL.a, "High is more solid")
	check(TouchControls._faded(Color(1, 1, 1, 0.9)).a <= 1.0, "never past opaque")
	GameSettings.touch_alpha = before


func test_frozen_touch_controls_press_nothing() -> void:
	var t := _touch(Vector2(1920, 1080))
	t.frozen = true
	var e := InputEventScreenTouch.new()
	e.index = 0
	e.pressed = true
	e.position = t.point_for(Buttons.LIGHT)
	t._input(e)
	check_eq(t.buttons(), Buttons.NONE, "asleep under How to play")
	t.free()


func test_the_how_to_play_callouts_name_every_button_and_never_overlap() -> void:
	for size: Vector2 in TOUCH_SIZES:
		var t := _touch(size)
		var c := HelpCallouts.new()
		c.touch = t
		c.size = size
		var out := c.callouts()
		var texts := PackedStringArray()
		for item: Dictionary in out:
			texts.append(str(item["text"]))
			var r: Rect2 = item["rect"]
			check(Rect2(Vector2.ZERO, size).encloses(r), "%s at %s is off the %s screen" % [item["text"], r, size])
			check(r.position.y >= c.card_bottom(), "%s runs under the card on %s" % [item["text"], size])
			for other: Dictionary in out:
				if other != item:
					check(not r.intersects(other["rect"]), "%s overlaps %s on %s" % [item["text"], other["text"], size])
		check(c.card_bottom() >= size.y * HelpCallouts.CARD_MIN, "the card has %.0f px above the callouts on %s" % [c.card_bottom(), size])
		for name in ["Move", "Up", "Down", "Kick", "Jump", "Heavy", "Dodge", "Combo"]:
			var found := false
			for text in texts:
				found = found or text.begins_with(name)
			check(found, "no callout for %s on %s" % [name, size])
		c.free()
		t.free()


func test_an_unnamed_creation_is_called_after_its_head() -> void:
	for id in ["akmal", "shahn"]:
		check_eq(ArtLibrary.fighter_name(ArtLibrary.make_mix(id, "rajat", "rajat", "rajat", "")),
			GameSettings.clean_name(ArtLibrary.fighter_name(id) + CharacterCreator.NAME_SUFFIX), "a %s head" % id)
	check(ArtLibrary.fighter_name(ArtLibrary.make_mix("artha", "rajat", "rajat", "rajat", "")).begins_with("Chandan"),
		"the display name, not the id (artha shows as Chandan)")


func test_the_arena_picker_draws_the_floor_where_the_painting_has_it() -> void:
	var card := Rect2(100, 50, 720, 405)
	var stage := StageDefinition.by_id("rooftops")
	var floor := ArenaPicker.stage_rect(stage.platforms[0], card)
	check(absf(floor.position.x - card.position.x) < 1.0 and absf(floor.end.x - card.end.x) < 1.0, "the 32 m floor spans the card")
	var top_y := card.position.y + ArenaPicker.ART_TOP / (ArenaPicker.ART_TOP - ArenaPicker.ART_BOTTOM) * card.size.y
	check(absf(floor.position.y - top_y) < 1.0, "the floor's top sits at y 0 of the art (%.1f, want %.1f)" % [floor.position.y, top_y])
	for p in stage.platforms:
		check(card.encloses(ArenaPicker.stage_rect(p, card)), "every platform is inside the card")


## Rooms v1, fix 26: nothing in client/ is a button that steps through hidden choices. The old
## culprits are gone (the panel's CPU level button, the two-tap quit), and the only `% size()` steps
## left on a choice are the F3 key, kept as a hidden extra next to its chips.
func test_no_control_cycles() -> void:
	var main := FileAccess.get_file_as_string("res://game/main.gd")
	check(not main.contains("func _on_level_pressed"), "the dead CPU level cycler is gone")
	check_eq(main.count("% LEVEL_NAMES.size()"), 1, "one level step left: the F3 key")
	check(main.contains("_guts_row") and main.contains("_level_row") and main.contains("_auto_row"), "Guts, CPU skill and Auto play are chips")
	check(not FileAccess.get_file_as_string("res://game/ui_theme.gd").contains("Tap again to quit"), "no two-tap quit")
	for path in _scripts("res://game"):
		var text := FileAccess.get_file_as_string(path)
		check(not text.contains("OptionButton"), "%s has a drop-down" % path)
		check(not text.contains("Tap again"), "%s swaps a label to ask again" % path)


func _scripts(dir: String) -> PackedStringArray:
	var out := PackedStringArray()
	var d := DirAccess.open(dir)
	if d == null:
		return out
	for f in d.get_files():
		if f.ends_with(".gd"):
			out.append(dir.path_join(f))
	for sub in d.get_directories():
		out.append_array(_scripts(dir.path_join(sub)))
	return out
