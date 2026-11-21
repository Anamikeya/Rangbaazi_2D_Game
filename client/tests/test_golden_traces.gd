extends TestCase
## The GDScript sim must match the C# reference bit for bit. The goldens (client/tests/golden,
## written by tests/Rangbaazi.Sim.Tests/GoldenTraceTests.cs) hold every frame's inputs and the
## checksum after it, for random-input and bot-vs-bot matches. Bot scenarios also check that the
## GDScript SparringBot chooses exactly the same buttons.

const LEVELS := { "easy": SparringBot.Level.EASY, "normal": SparringBot.Level.NORMAL, "hard": SparringBot.Level.HARD }


func _replay(file_name: String) -> void:
	var text := FileAccess.get_file_as_string("res://tests/golden/%s.json" % file_name)
	if not check(text != "", "missing golden %s" % file_name):
		return
	var g: Dictionary = JSON.parse_string(text)
	var rules := MatchRules.new()
	var time_limit := int(g.get("time_limit_frames", 0))
	if time_limit > 0: # a room scenario: MatchRules.room with a short clock, on the big level for "stage_size": "big"
		var stage_size := StageDefinition.SIZE_BIG if str(g.get("stage_size", "normal")) == "big" else StageDefinition.SIZE_NORMAL
		rules = MatchRules.room(StageDefinition.by_id(str(g["stage"]), stage_size), 1)
		rules.time_limit_frames = time_limit
	else:
		rules.stocks = int(g.get("stocks", 3))
	rules.guts = g["guts"]
	rules.weapons = bool(g.get("weapons", false))
	rules.flurry = bool(g.get("flurry", false))
	rules.hazards = bool(g.get("hazards", false))
	var sigs := PackedInt32Array()
	for w in g.get("signatures", []):
		sigs.append(int(w))
	rules.signatures = sigs
	var teams := PackedInt32Array()
	for team in g.get("teams", []):
		teams.append(int(team))
	rules.teams = teams
	var players := int(g["players"])
	var state := Simulation.create_initial_state(rules, players, int(g["seed"]))
	if not check_eq(StateHasher.hex(state.checksum()), g["initial"], "%s initial checksum" % file_name):
		return
	var bots: Variant = g["bots"]
	var inputs := PackedInt32Array()
	inputs.resize(players)
	var steps: Array = g["steps"]
	var arm_newer := bool(g.get("arm_newer", false))
	var arm_every := int(g.get("arm_every", 0))
	var crates := Weapons.crate_list()
	var arm_lists: Array = g.get("arm_lists", []) # per fighter p, arm_lists[p % size] instead of the crate list
	for f in steps.size():
		var step: Array = steps[f]
		if arm_newer and f % 90 == 0: # every fighter is handed one of the five newer weapons
			for p in players:
				if state.fighters[p].is_active():
					var w := Weapons.BOOMERANG + (f / 90 + p) % 5
					state.fighters[p].weapon = w
					state.fighters[p].weapon_supply = Weapons.supply_of(w)
		if arm_every > 0 and f % arm_every == 0: # every fighter is handed the next weapon a crate can hold
			for p in players:
				if state.fighters[p].is_active():
					var list: Array = arm_lists[p % arm_lists.size()] if not arm_lists.is_empty() else Array(crates)
					var w := int(list[(f / arm_every + p) % list.size()])
					state.fighters[p].weapon = w
					state.fighters[p].weapon_supply = Weapons.supply_of(w)
		var expected_inputs: Array = step[0]
		for p in players:
			if bots != null:
				inputs[p] = SparringBot.think(state, rules, p, LEVELS[bots[p]])
				if not check_eq(inputs[p], int(expected_inputs[p]), "%s frame %d P%d bot buttons" % [file_name, f, p + 1]):
					return
			else:
				inputs[p] = int(expected_inputs[p])
		Simulation.advance(state, rules, inputs)
		if not check_eq(StateHasher.hex(state.checksum()), step[1], "%s checksum after frame %d" % [file_name, f]):
			_dump(state)
			return


func _dump(state: GameState) -> void:
	for i in state.fighters.size():
		var f := state.fighters[i]
		errors.append("  P%d pos (%d, %d) vel (%d, %d) action %d move %d af %d josh %d stocks %d hitstop %d" % [
			i + 1, f.pos_x, f.pos_y, f.vel_x, f.vel_y, f.action, f.move, f.action_frame, f.josh, f.stocks, f.hit_stop])


func test_random_inputs_two_players() -> void:
	_replay("random_2p")


func test_random_inputs_four_players() -> void:
	_replay("random_4p")


func test_bots_normal_vs_hard() -> void:
	_replay("bots_normal_vs_hard")


func test_bots_hard_vs_hard_without_guts() -> void:
	_replay("bots_hard_vs_hard_no_guts")


func test_bots_easy_vs_normal() -> void:
	_replay("bots_easy_vs_normal")


func test_room_random_inputs_fifteen_players() -> void:
	_replay("room_random_15p")


func test_room_bots_six_players() -> void:
	_replay("room_bots_6p")


func test_weapons_random_inputs_four_players() -> void:
	_replay("weapons_random_4p")


func test_weapons_bots_four_players() -> void:
	_replay("weapons_bots_4p")


func test_newer_weapons_random_inputs_four_players() -> void:
	_replay("newer_weapons_random_4p")


func test_newer_weapons_bots_four_players() -> void:
	_replay("newer_weapons_bots_4p")


func test_all_weapons_random_inputs_four_players() -> void:
	_replay("all_weapons_random_4p")


func test_all_weapons_bots_four_players() -> void:
	_replay("all_weapons_bots_4p")


func test_all_weapons_room_six_players() -> void:
	_replay("all_weapons_room_6p")


func test_all_weapons_two_teams_of_three() -> void:
	_replay("all_weapons_teams_6p")


## Fifteen fighters all firing: the placed limit of a big room (2), a full prop table, a shot put over
## a bullet or its firer's own placed prop, and shots that fizzle (GoldenTraceTests checks it plays them).
func test_big_room_prop_table_fifteen_players() -> void:
	_replay("big_room_props_15p")


## Four teams of 15 CPUs on the big level (rooms v1, 6.2), weapons and hazards on.
func test_room_big_teams_15p() -> void:
	_replay("room_big_teams_15p")


## Every arena at both sizes: "<id>" and "<id>@big" (StageDefinition.big, StageScaling.Big).
func test_arena_stages_match_the_reference() -> void:
	var text := FileAccess.get_file_as_string("res://tests/golden/arena_stages.json")
	if not check(text != "", "missing golden arena_stages.json"):
		return
	var expected: Dictionary = JSON.parse_string(text)
	check_eq(expected.size(), 2 * ArenaStages.IDS.size(), "arena count, both sizes")
	for e: Array in ArenaStages.IDS:
		for stage_size: int in [StageDefinition.SIZE_NORMAL, StageDefinition.SIZE_BIG]:
			var s := StageDefinition.by_id(e[0], stage_size)
			check_eq(s.size, stage_size, "arena %s size" % e[0])
			var key: String = e[0] + ("@big" if stage_size == StageDefinition.SIZE_BIG else "")
			var h := StateHasher.new()
			s.add_to(h)
			check_eq(StateHasher.hex(h.value()), expected.get(key, ""), "arena %s" % key)


func test_hasher_matches_reference_values() -> void:
	# FNV-1a 64 of nothing is the offset basis; of a single 0 ulong it is a known value.
	var h := StateHasher.new()
	check_eq(StateHasher.hex(h.value()), "CBF29CE484222325", "empty hash")
	h.add_u64(0)
	check_eq(StateHasher.hex(h.value()), "A8C7F832281A39C5", "hash of one 0")


func test_clone_copies_everything() -> void:
	var rules := MatchRules.new()
	var state := Simulation.create_initial_state(rules, 2, 99)
	var inputs := PackedInt32Array([Buttons.RIGHT | Buttons.LIGHT, Buttons.LEFT | Buttons.HEAVY])
	for i in 200:
		Simulation.advance(state, rules, inputs)
	var copy := state.clone()
	check_eq(copy.checksum(), state.checksum(), "clone checksum")
	Simulation.advance(state, rules, inputs)
	check(copy.checksum() != state.checksum(), "a clone must not share fighters with the original")
