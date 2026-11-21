extends TestCase
## The fixes from the review of rooms v1 (docs/design/rooms-teams-view-ui-v1.md, Build notes, "As
## built"): presentation only, nothing here touches the sim.


## A duel's state goes on counting frames behind its result; the nickname must not change with it.
func test_the_winners_nickname_stays_put_while_the_match_is_over() -> void:
	var rules := MatchRules.new()
	var state := Simulation.create_initial_state(rules, 2, 7)
	state.match_over = true
	state.winner_index = 0
	var kept := Nicknames.Kept.new()
	var first := kept.for_match(state)
	check(first != "", "a winner has a nickname")
	var seen := {}
	for n in 40:
		state.frame += 1
		seen[Nicknames.for_winner(state)] = true
		check_eq(kept.for_match(state), first, "the same name %d frames on" % (n + 1))
	check(seen.size() > 1, "(for_winner alone does change with the frame, which is why it is kept)")
	state.match_over = false
	check_eq(kept.for_match(state), "", "a new match has none")
	state.match_over = true
	state.frame += 5
	check_eq(kept.for_match(state), Nicknames.for_winner(state), "and works it out afresh when it ends")


func test_a_free_for_all_draw_says_who_drew() -> void:
	var rules := MatchRules.new()
	var state := Simulation.create_initial_state(rules, 4, 7)
	var names := PackedStringArray(["CPU 1", "CPU 2", "CPU 3", "CPU 4"])
	for i in 4:
		state.fighters[i].kos = [1, 3, 0, 3][i]
		state.fighters[i].falls = [2, 2, 1, 2][i]
	check_eq(ResultScreen.draw_line_for(state, names), "CPU 2 and CPU 4 level on 3 KOs and 2 falls", "two level")
	state.fighters[0].kos = 3
	check_eq(ResultScreen.draw_line_for(state, names), "CPU 1, CPU 2 and CPU 4 level on 3 KOs and 2 falls", "three level")
	state.fighters[3].falls = 3
	state.fighters[0].falls = 0
	check_eq(ResultScreen.draw_line_for(state, names), "", "nobody level")


func test_a_cut_name_ends_in_an_ellipsis() -> void:
	check_eq(ArenaView.short_tag_name("Shraddha"), "Shraddha", "every cast name fits whole")
	check_eq(ArenaView.short_tag_name("Abhipray"), "Abhipray", "8 letters fit")
	check_eq(ArenaView.short_tag_name("Tiger9999"), "Tiger99…", "a longer one is cut with an ellipsis")
	for row: Array in ArenaStages.IDS:
		check(ArtLibrary.fighter_name(row[1]).length() <= ArenaView.TAG_NAME_MAX, "%s fits a tag" % ArtLibrary.fighter_name(row[1]))


## Under touch the KO feed sits in the band under the floor line, clear of every button.
func test_the_ko_feed_band_is_under_the_floor_and_clear_of_the_buttons() -> void:
	var before := GameSettings.touch_scale
	for k: float in GameSettings.TOUCH_SCALES:
		GameSettings.touch_scale = k
		for size: Vector2 in [Vector2(2400, 1080), Vector2(1920, 1080), Vector2(1920, 1440)]:
			var band := TouchControls.free_band(size)
			check(band.position.y >= TouchControls.floor_clear_y(size) - 0.5, "the band is under the floor line on %s at %.2f" % [size, k])
			check(band.size.x >= 250.0, "the band is %.0f px wide on %s at %.2f" % [band.size.x, size, k])
			check(band.size.y >= 4 * 38.0, "four feed lines fit on %s at %.2f" % [size, k])
			for c: Array in TouchControls.drawn_circles(size):
				var centre: Vector2 = c[0]
				var r: float = c[1]
				var nearest := Vector2(clampf(centre.x, band.position.x, band.end.x), clampf(centre.y, band.position.y, band.end.y))
				check(nearest.distance_to(centre) >= r, "the band meets a control at %s on %s at %.2f" % [centre, size, k])
	GameSettings.touch_scale = before


## The relay's refusal codes come through even when only the close arrives (see test_protocol).
## A guest tries again by joining; a host whose room was never opened hosts again (final round).
func test_a_busy_server_offers_try_again() -> void:
	var actions := MenuScreen.error_actions("busy", "K7QPM", false)
	check_eq(actions[0], ["Try again", "rejoin"], "a guest joins again")
	for code: String in ["busy", "connect", "timeout", "closed"]:
		check_eq(MenuScreen.error_actions(code, "K7QPM", false, true)[0], ["Try again", "host"], "a host's %s hosts again" % code)
	check_eq(MenuScreen.error_actions("no_room", "K7QPM", true, true)[0], ["Host a new room", "host"], "a host's lost room: a new one")


## The gap before Chat (main.gd: 2 x MENU_MARGIN + 2.6 x MENU_BUTTON + HUD_GAP, read from main.gd)
## still leaves every chip of a four-team room of 15 on a 1920 px screen, with room to spare: 8 px
## narrower still fits (final round: it had 1.6 px).
func test_the_gap_before_chat_keeps_every_chip() -> void:
	var main := preload("res://game/main.gd")
	var teams := PackedInt32Array()
	for i in 15:
		teams.append(1 + i % 4)
	var kos := PackedInt32Array()
	kos.resize(15)
	var inset: float = 2.0 * main.MENU_MARGIN + 2.6 * main.MENU_BUTTON + main.HUD_GAP
	for w: float in [1920.0, 2400.0]:
		check(not bool(ArenaView.hud_strip(w, inset, teams, 0, kos)["collapsed"]), "all 15 chips at %d px" % w)
		check(not bool(ArenaView.hud_strip(w - 8.0, inset, teams, 0, kos)["collapsed"]), "and with 8 px less at %d px" % w)


## Every mode draws the pill tags now (final round): practice's CPU is named in its pill.
func test_practice_fighters_get_pill_tags() -> void:
	var view := ArenaView.new()
	view.rules = MatchRules.new()
	view.rules.stage = StageDefinition.rooftops()
	view.state = Simulation.create_initial_state(view.rules, 2, 7)
	view.names = PackedStringArray(["Shan", "Aastha"])
	view.tags = PackedStringArray(["YOU", "CPU"])
	view.state.fighters[1].josh = 36
	check_eq(view.tag_text(0), "YOU 0%", "yours")
	check_eq(view.tag_text(1), "Aastha 36%", "the CPU's, with CPU leading the pill")
	view.free()
