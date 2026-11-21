extends TestCase
## The generated weapon table (client/sim/weapon_table.gd) must hold the same rows as the C#
## reference's. The golden (client/tests/golden/weapon_table.json, written by GoldenTraceTests.cs)
## has one hash per row over its columns in order, and one over the shared spread list: a
## generated file that is stale in this sim fails here, and one that is stale in C# fails there.
## Also the helpers built from the table, and the match record's reading side.


func _golden() -> Dictionary:
	var text := FileAccess.get_file_as_string("res://tests/golden/weapon_table.json")
	if not check(text != "", "missing golden weapon_table.json"):
		return {}
	return JSON.parse_string(text)


func test_every_row_matches_the_reference() -> void:
	var g := _golden()
	if g.is_empty():
		return
	check_eq(WeaponTable.COLUMN_COUNT, int(g["columns"]), "column count")
	var rows: Array = g["rows"]
	if not check_eq(WeaponTable.ROWS.size(), rows.size(), "row count"):
		return
	check_eq(WeaponTable.COUNT, WeaponTable.ROWS.size(), "COUNT")
	for id in WeaponTable.ROWS.size():
		var row := WeaponTable.ROWS[id]
		check_eq(row.size(), WeaponTable.COLUMN_COUNT, "row %d columns" % id)
		check_eq(row[WeaponTable.C_ID], id, "row %d is indexed by its id" % id)
		var h := StateHasher.new()
		for v in row:
			h.add_u64(v)
		check_eq(StateHasher.hex(h.value()), rows[id], "row %d (%s)" % [id, WeaponInfo.name(id)])


func test_the_spread_list_matches_the_reference() -> void:
	var g := _golden()
	if g.is_empty():
		return
	var h := StateHasher.new()
	for v in WeaponTable.SPREADS:
		h.add_u64(v)
	check_eq(StateHasher.hex(h.value()), g["spreads"], "SPREADS")


func test_the_crate_list_matches_the_reference() -> void:
	var g := _golden()
	if g.is_empty():
		return
	var want := PackedInt32Array()
	for id in g["crate_list"]:
		want.append(int(id))
	check_eq(Weapons.crate_list(), want, "crate list")
	for id in Weapons.crate_list():
		var row := WeaponTable.ROWS[id]
		check(row[WeaponTable.C_BUILT] != 0 and row[WeaponTable.C_CRATE] != 0, "%s can't be in a crate" % WeaponInfo.name(id))


func test_moves_find_their_rows() -> void:
	var seen := {}
	for id in range(1, WeaponTable.COUNT):
		var move: int = WeaponTable.ROWS[id][WeaponTable.C_MOVE]
		check(not seen.has(move), "move %d is used twice" % move)
		seen[move] = true
		check_eq(Weapons.row_of_move(move), id, "row of move %d" % move)
		check(Weapons.move_for(move) == Weapons.move_data_of(id), "move %d has its row's frame data" % move)
		check(MoveSet.all_rounder().get_move(move) == Weapons.move_data_of(id), "the move set finds move %d" % move)
	for fist in [MoveData.Id.NONE, MoveData.Id.JAB, MoveData.Id.NEUTRAL_HEAVY, MoveData.Id.SIDE_HEAVY, MoveData.Id.UP_HEAVY,
			MoveData.Id.DOWN_HEAVY, MoveData.Id.GROUND_SLAM, MoveData.Id.WEAPON_THROW, 200]:
		check_eq(Weapons.row_of_move(fist), 0, "move %d is no row's" % fist)
	check(Weapons.move_for(MoveData.Id.WEAPON_THROW) == Weapons.weapon_throw(), "the throw is a shared move")
	check(Weapons.move_for(MoveData.Id.JAB) == null, "a fist move is not a weapon move")
	# The twelve keep their ids and their named moves.
	check_eq(Weapons.move_of(Weapons.BELAN), MoveData.Id.BELAN_BONK, "the belan's move")
	check_eq(Weapons.move_of(Weapons.BLACK_HOLE_BOMB), MoveData.Id.HOLE_LOB, "the black hole's move")
	check_eq(Weapons.BLACK_HOLE_BOMB, 12, "the twelfth weapon")
	check_eq(Weapons.move_of(13), 20, "rows after the twelve: 7 + the id")


func test_a_row_becomes_frame_data() -> void:
	var m := Weapons.move_data_of(Weapons.SHOTGUN)
	check_eq([m.startup, m.active, m.recovery, m.damage, m.hitstun_bonus], [12, 2, 30, 18, 4], "the shotgun's frames")
	check_eq([m.base_knockback, m.knockback_growth], [Fx.ratio(20, 100), Fx.ratio(28, 10000)], "the shotgun's knockback")
	check_eq([m.launch_x, m.launch_y], [Fx.ratio(9063, 10000), Fx.ratio(4226, 10000)], "25 degrees")
	check_eq([m.hitbox_near, m.hitbox_far, m.hitbox_bottom, m.hitbox_top], [Fx.ratio(2, 5), Fx.ratio(9, 2), Fx.HALF, Fx.ratio(3, 2)], "the shotgun's box")
	check_eq([m.impulse_frame, m.lunge_speed], [12, Fx.ratio(-15, 100)], "the kick, truncated towards zero")
	check_eq([m.effect, m.effect_n], [WeaponTable.EFFECT_NONE, 0], "no effect yet")
	check_eq(WeaponTable.ROWS[Weapons.BOWLING_BALL][WeaponTable.C_CARRY], 80, "the ball is heavy")
	check_eq(WeaponTable.ROWS[Weapons.NONE][WeaponTable.C_CARRY], 100, "an empty hand is not")
	check_eq(WeaponTable.SPREADS[WeaponTable.ROWS[Weapons.UZI][WeaponTable.C_SPREAD_AT] + 2], Fx.ratio(-366, 10000), "the Uzi's third shot dips")


func test_flight_flags() -> void:
	var flies := [WeaponTable.FLIGHT_ROLL, WeaponTable.FLIGHT_BULLET, WeaponTable.FLIGHT_BOMB, WeaponTable.FLIGHT_ARROW, WeaponTable.FLIGHT_THROWN,
		WeaponTable.FLIGHT_BOOMERANG, WeaponTable.FLIGHT_MISSILE, WeaponTable.FLIGHT_HOLE, WeaponTable.FLIGHT_LOB]
	var placed := [WeaponTable.FLIGHT_PAD, WeaponTable.FLIGHT_MINE, WeaponTable.FLIGHT_ZONE, WeaponTable.FLIGHT_WALL, WeaponTable.FLIGHT_SPEAKER,
		WeaponTable.FLIGHT_GATE, WeaponTable.FLIGHT_DECOY]
	check_eq(WeaponTable.FLIES.size(), 18, "one flag per flight")
	check_eq(WeaponTable.PLACED.size(), 18, "one flag per flight")
	for k in 18:
		check_eq(WeaponTable.FLIES[k], flies.has(k), "flies %d" % k)
		check_eq(WeaponTable.PLACED[k], placed.has(k), "placed %d" % k)
	check_eq(Weapons.SHOT_HOLE, 10, "the first ten flights keep their numbers")


func test_prop_slots_and_limits() -> void:
	check_eq([Weapons.prop_slots(2), Weapons.prop_slots(4), Weapons.prop_slots(12), Weapons.prop_slots(16)], [24, 32, 64, 64], "prop slots")
	check_eq([Weapons.box_slots(2), Weapons.box_slots(15)], [Weapons.cap(2) + 2, Weapons.cap(15) + 2], "box slots")
	check_eq([Weapons.placed_limit(8), Weapons.placed_limit(9)], [4, 2], "placed limit")
	check_eq(GameState.new(15, 0).props.size(), 64, "a full room's table")
	check_eq(GameState.new(2, 0).weapon_stats.size(), 2 * Weapons.RECORD_INTS, "the record")
	check_eq(Weapons.RECORD_INTS, 25, "six slots of four, and the total")


func test_presentation_covers_every_row() -> void:
	for list in [WeaponInfo.KEYS, WeaponInfo.NAMES, WeaponInfo.VERBS, WeaponInfo.SHOUTS, WeaponInfo.CLIPS, WeaponInfo.LENGTHS, WeaponInfo.WORDS]:
		check_eq(list.size(), WeaponTable.COUNT, "one entry per row")
	check_eq(WeaponInfo.name(Weapons.BASEBALL_BAT), "Baseball Bat", "a name")
	check_eq(WeaponInfo.verb(0), "Kick", "the fists' verb")
	check_eq(WeaponInfo.shout(Weapons.BOW), "LONG SHOT!", "a shout")
	check_eq(WeaponInfo.key(Weapons.BELAN), "w011", "a pack key")
	check_eq(WeaponInfo.words(Weapons.UZI), ["Uzi", "Trigger", "Bullet", "Rat-a-tat"], "nickname words")
	check_eq(WeaponInfo.name(999), "", "an id that is no row")
	for id in range(1, WeaponTable.COUNT):
		check(WeaponInfo.key(id) != "", "row %d has a sprite to draw (its own, or its weapon's)" % id)
		check_eq(WeaponInfo.words(id).size(), 4, "row %d has four words" % id)


## One slot of a fighter's record, added to their total.
func _record(state: GameState, fighter: int, slot: int, weapon: int, damage: int, kos: int, sweets: int) -> void:
	var from := fighter * Weapons.RECORD_INTS
	var at := from + slot * Weapons.RECORD_SLOT_INTS
	state.weapon_stats[at] = weapon + 1
	state.weapon_stats[at + 1] = damage
	state.weapon_stats[at + 2] = kos
	state.weapon_stats[at + 3] = sweets
	state.weapon_stats[from + Weapons.RECORD_SLOTS * Weapons.RECORD_SLOT_INTS] += damage


func test_the_main_weapon_and_the_nickname_code() -> void:
	var s := GameState.new(2, 0)
	check_eq(Simulation.main_weapon(s, 0), [-1, false], "no damage yet: an all-rounder")
	check_eq(Nicknames.code_for(s, 0), 127, "an all-rounder's code")
	_record(s, 0, 0, Weapons.NONE, 30, 0, 0)
	_record(s, 0, 1, Weapons.BOW, 50, 1, 3)
	_record(s, 0, 2, Weapons.UZI, 20, 0, 0)
	check_eq(Simulation.main_weapon(s, 0), [Weapons.BOW, true], "the bow did half the damage, with sweet spots and a KO")
	check_eq(Nicknames.code_for(s, 0), Weapons.BOW | 128, "a master's code")
	check_eq([Simulation.weapon_stat(s, 0, Weapons.BOW, 0), Simulation.weapon_stat(s, 0, Weapons.BOW, 1), Simulation.weapon_stat(s, 0, Weapons.BOW, 2)], [50, 1, 3], "the bow's slot")
	check_eq(Simulation.weapon_stat(s, 0, Weapons.MINES, 0), 0, "a weapon with no slot")
	check_eq(Simulation.damage_dealt(s, 0), 100, "the total")
	check_eq(Simulation.main_weapon(s, 1), [-1, false], "the other fighter's record is its own")

	var tie := GameState.new(2, 0)
	_record(tie, 0, 0, Weapons.BOW, 40, 0, 0)
	_record(tie, 0, 1, Weapons.BELAN, 40, 0, 0)
	check_eq(Simulation.main_weapon(tie, 0), [Weapons.BELAN, false], "a tie goes to the lower id, whichever slot it is in")
	_record(tie, 0, 2, 100, 40, 0, 0)
	check_eq(Simulation.main_weapon(tie, 0), [-1, false], "three at a third each: nothing reaches 40%")

	var high := GameState.new(2, 0)
	_record(high, 0, 0, 100, 60, 1, 3) # the hundredth weapon
	check_eq(Nicknames.code_for(high, 0), 100 | 128, "a weapon above 15 is its id, not an all-rounder")


func test_a_nickname_is_made_from_the_weapons_own_words() -> void:
	var s := GameState.new(2, 0)
	s.match_over = true
	s.winner_index = 0
	s.frame = 600
	var name := Nicknames.for_winner(s, Weapons.UZI)
	check(name != "", "a winner has a nickname")
	var found := false
	for word: String in WeaponInfo.words(Weapons.UZI):
		found = found or name.begins_with("\"" + word + " ")
	check(found, "the Uzi's nickname starts with one of its words: " + name)
	var all_rounder := Nicknames.for_winner(s, Nicknames.ALL_ROUNDER)
	found = false
	for word: String in Nicknames.ALL_ROUNDER_WORDS:
		found = found or all_rounder.begins_with("\"" + word + " ")
	check(found, "an all-rounder's nickname: " + all_rounder)
	found = false
	for title: String in Nicknames.MASTER_TITLES:
		found = found or Nicknames.for_winner(s, Weapons.BOW | Nicknames.MASTER).ends_with(" " + title + "\"")
	check(found, "a master's title")
