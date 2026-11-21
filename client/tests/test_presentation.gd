extends TestCase
## What the client shows for each weapon (combat spec v0.4, section 5): every weapon a crate can
## hold has a HUD name, a touch verb that fits its button, four nickname words, a pose clip, a
## sprite and a sound, so none of the 100 shows up blank or silent. The touch controls keep their
## fixed Combo and show Throw only while armed (rooms v1, fix 17). And the bursts the client sees
## from the state (BurstWatch: the flash and the boom) are the real ones.

const CLIPS := ["jab", "neutral_heavy", "side_heavy", "down_heavy"]


func test_every_crate_weapon_has_its_name_verb_words_clip_sprite_and_sound() -> void:
	var font := ThemeDB.fallback_font
	var light: Array = TouchControls.PAD_BUTTONS[0]
	check_eq(light[0], Buttons.LIGHT, "the first pad button is Light")
	var radius: float = light[2]
	var font_size := TouchControls.verb_font_size() # the caption under "Kick" while armed
	for id in Weapons.crate_list():
		var name := WeaponInfo.name(id)
		check(name != "", "weapon %d has no HUD name" % id)
		var verb := WeaponInfo.verb(id)
		check(verb != "", "%s has no touch verb" % name)
		var width := font.get_string_size(verb, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
		# The caption sits low in the circle (0.62 of the radius down), where it is 1.57 radii wide.
		check(width <= radius * 1.55, "%s: its verb \"%s\" is %d px, wider than its button" % [name, verb, int(width)])
		var words := WeaponInfo.words(id)
		check_eq(words.size(), 4, "%s's nickname words" % name)
		var seen := {}
		for w in words:
			check(str(w) != "" and not str(w).contains(" "), "%s: nickname word \"%s\" must be one word" % [name, w])
			check(not seen.has(w), "%s: nickname word %s twice" % [name, w])
			seen[w] = true
		check(CLIPS.has(WeaponInfo.clip(id)), "%s borrows no fighter clip (%s)" % [name, WeaponInfo.clip(id)])
		check(WeaponInfo.length(id) > 0.0, "%s has no held length" % name)
		check(ArtLibrary.weapon_texture(id) != null, "%s has no sprite" % name)
		check(ResourceLoader.exists("res://audio/sfx/weapon_%d.wav" % id), "%s has no sound" % name)
		# A sweet spot that shouts nothing is fine; one that shouts must say something short.
		check(WeaponInfo.shout(id).length() <= 16, "%s: its shout is too long" % name)


func test_every_weapon_gives_a_winner_a_nickname() -> void:
	var rules := MatchRules.new()
	var state := Simulation.create_initial_state(rules, 2, 1)
	state.match_over = true
	state.winner_index = 0
	for id in Weapons.crate_list():
		for master in [0, Nicknames.MASTER]:
			var nick := Nicknames.for_winner(state, id | master)
			check(nick.begins_with("\"") and nick.length() > 4, "%s: no nickname (%s)" % [WeaponInfo.name(id), nick])
			check(WeaponInfo.words(id).has(nick.substr(1).split(" ")[0]), "%s: the nickname %s isn't one of its words" % [WeaponInfo.name(id), nick])


# ---- BurstWatch ----

func _state() -> GameState:
	var rules := MatchRules.new()
	rules.weapons = true
	return Simulation.create_initial_state(rules, 2, 1)


func _put(state: GameState, weapon: int, shot: int, owner: int, x: int) -> Prop:
	var slot := state.props.size() - 1
	while state.props[slot].kind != Weapons.PROP_NONE:
		slot -= 1
	var p := state.props[slot]
	p.clear()
	p.kind = Weapons.PROP_SHOT
	p.weapon = weapon
	p.shot = shot
	p.owner = owner
	p.pos_x = Fx.from_int(x)
	return p


func test_a_bomb_that_is_gone_has_burst_with_its_blast() -> void:
	var state := _state()
	var p := _put(state, Weapons.CHERRY_BOMBS, Weapons.SHOT_BOMB, 0, 2)
	var before := BurstWatch.snapshot(state)
	check_eq(BurstWatch.bursts(before, state).size(), 0, "still there: no burst")
	p.pos_x += Fx.ratio(1, 10) # it moves a little: the same bomb
	check_eq(BurstWatch.bursts(before, state).size(), 0, "moved: no burst")
	p.clear()
	var out := BurstWatch.bursts(before, state)
	if check_eq(out.size(), 1, "gone: one burst"):
		check_eq(out[0][3], Fx.to_float(WeaponTable.ROWS[Weapons.CHERRY_BOMBS][WeaponTable.C_BLAST]), "its blast")
		check(not bool(out[0][4]), "a bomb with damage goes off")


func test_a_trap_and_a_sprung_decoy_never_burst() -> void:
	var state := _state()
	var trap := _put(state, WeaponTable.MOUSE_TRAP, Weapons.SHOT_MINE, 0, 2)
	var decoy := _put(state, WeaponTable.DECOY_CRATE, Weapons.SHOT_DECOY, 0, -2)
	var before := BurstWatch.snapshot(state)
	check_eq(before.size(), 1, "a trap is not something that bursts")
	trap.clear()
	decoy.clear() # it sprang on someone
	check_eq(BurstWatch.bursts(before, state).size(), 0, "no burst")


func test_a_detonated_decoy_bursts_with_the_detonators_blast() -> void:
	var state := _state()
	var decoy := _put(state, WeaponTable.DECOY_CRATE, Weapons.SHOT_DECOY, 0, -2)
	var before := BurstWatch.snapshot(state)
	var f := state.fighters[0]
	f.weapon = WeaponTable.CRATE_DETONATOR
	f.action = Fighter.Action.ATTACK
	f.move = WeaponTable.ROWS[WeaponTable.CRATE_DETONATOR][WeaponTable.C_MOVE]
	decoy.clear()
	var out := BurstWatch.bursts(before, state)
	if check_eq(out.size(), 1, "one burst"):
		check_eq(out[0][3], Fx.to_float(WeaponTable.ROWS[WeaponTable.CRATE_DETONATOR][WeaponTable.C_BLAST]), "the Detonator's blast")


func test_a_capsule_that_becomes_its_cloud_opens() -> void:
	var state := _state()
	var p := _put(state, WeaponTable.STINK_CLOUD_CAPSULE, Weapons.SHOT_BOMB, 0, 1)
	var before := BurstWatch.snapshot(state)
	p.shot = Weapons.SHOT_ZONE # it became its cloud, in the same slot
	var out := BurstWatch.bursts(before, state)
	if check_eq(out.size(), 1, "one burst"):
		check(bool(out[0][4]), "with no damage it only opens")
