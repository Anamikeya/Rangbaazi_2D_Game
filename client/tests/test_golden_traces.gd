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
	rules.guts = g["guts"]
	var players := int(g["players"])
	var state := Simulation.create_initial_state(rules, players, int(g["seed"]))
	if not check_eq(StateHasher.hex(state.checksum()), g["initial"], "%s initial checksum" % file_name):
		return
	var bots: Variant = g["bots"]
	var inputs := PackedInt32Array()
	inputs.resize(players)
	var steps: Array = g["steps"]
	for f in steps.size():
		var step: Array = steps[f]
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
