extends TestCase
## Teams in rooms (docs/design/rooms-teams-view-ui-v1.md, section 1): the team win rule, the
## ring-out guard, the bat guard and the CPU's dodge. Each mirrors a test in tests/Rangbaazi.Sim.Tests
## (RoomRulesTests, SparringBotTests), so both sims are held to the same cases.


## A 1 km floor with a far-away blast zone (TestStages.Wide).
static func _wide() -> StageDefinition:
	return load("res://tests/test_weapon_rules.gd").wide()


func _scores(scores: Array) -> Array[Fighter]:
	var fighters: Array[Fighter] = []
	for s in scores:
		var f := Fighter.new()
		f.kos = s[0]
		f.falls = s[1]
		fighters.append(f)
	return fighters


func _with_teams(teams: Array, weapons := false) -> MatchRules:
	var rules := MatchRules.new()
	rules.stage = _wide()
	rules.time_limit_frames = 100000
	rules.weapons = weapons
	rules.teams = PackedInt32Array(teams)
	return rules


static func _cm(v: int) -> int:
	return Fx.ratio(v, 100)


func _standing(rules: MatchRules, xs: Array) -> GameState:
	var s := Simulation.create_initial_state(rules, xs.size(), 1)
	for i in xs.size():
		var f := s.fighters[i]
		f.pos_x = _cm(xs[i])
		f.pos_y = rules.stage.platforms[0].max_y
		f.vel_x = 0
		f.vel_y = 0
		f.grounded = true
		f.facing = 1 if i == 0 else -1
	s.prop_timer = 1000000
	return s


func _step(s: GameState, rules: MatchRules, buttons := {}) -> void:
	var inputs := PackedInt32Array()
	inputs.resize(s.fighters.size())
	for i in buttons:
		inputs[i] = buttons[i]
	Simulation.advance(s, rules, inputs)


func test_team_kos_add_up_and_the_winner_is_the_best_teams_mvp() -> void:
	var f := _scores([[3, 4], [5, 0], [3, 1], [0, 2]])
	var rules := _with_teams([1, 2, 1, 2])
	check(rules.has_teams(), "has teams")
	check_eq(Simulation.timed_winner(f), 1, "alone, fighter 1 has the most KOs")
	check_eq(Simulation.timed_team_winner(f, rules), 2, "team 1 wins 6 to 5; its MVP has fewer falls")

	var timed := _with_teams([1, 2, 1, 2])
	timed.time_limit_frames = 10
	var s := _standing(timed, [-300, -100, 100, 300])
	for i in 4:
		s.fighters[i].kos = f[i].kos
		s.fighters[i].falls = f[i].falls
	while not s.match_over:
		_step(s, timed)
	check_eq(s.winner_index, 2, "the match's winner is the MVP")


func test_level_team_kos_go_to_fewer_falls_then_a_draw() -> void:
	var rules := _with_teams([1, 2, 1, 2])
	var f := _scores([[2, 3], [4, 1], [2, 0], [0, 1]])
	check_eq(Simulation.timed_team_winner(f, rules), 1, "level on KOs: fewer falls")
	f[0].falls = 2
	check_eq(Simulation.timed_team_winner(f, rules), -1, "level on both: a draw")
	var three := _with_teams([1, 2, 3])
	check_eq(Simulation.timed_team_winner(_scores([[2, 1], [2, 1], [0, 0]]), three), -1, "two level at the top")
	check_eq(Simulation.timed_team_winner(_scores([[2, 1], [2, 1], [3, 5]]), three), 2, "a third team on top")


func test_mvp_ties_go_to_the_lower_index() -> void:
	var rules := _with_teams([2, 1, 2, 1, 2])
	var f := _scores([[1, 1], [0, 3], [2, 1], [0, 0], [2, 1]])
	check_eq(Simulation.timed_team_winner(f, rules), 2, "level MVPs: the lower index")
	f[4].falls = 0
	check_eq(Simulation.timed_team_winner(f, rules), 4, "fewer falls")


func test_with_no_teams_the_timed_winner_is_unchanged() -> void:
	var random := SimRandom.new(77)
	var none := MatchRules.new()
	check(not none.has_teams(), "no teams")
	for round in 500:
		var n := 2 + random.range_int(0, 14)
		var scores := []
		for i in n:
			scores.append([random.range_int(0, 4), random.range_int(0, 4)])
		var f := _scores(scores)
		var expected := Simulation.timed_winner(f)
		var zeros := PackedInt32Array()
		zeros.resize(n)
		var all_zero := _with_teams(Array(zeros))
		check(not all_zero.has_teams(), "all-zero teams are none")
		if not check_eq(Simulation.timed_team_winner(f, none), expected, "no teams, round %d" % round):
			return
		if not check_eq(Simulation.timed_team_winner(f, all_zero), expected, "all zero, round %d" % round):
			return


func test_a_teammate_never_gets_ring_out_credit() -> void:
	var rules := _with_teams([1, 1, 2])
	var s := _standing(rules, [0, 300, 600])
	for i in [1, 2]:
		s.fighters[i].last_hit_by = 1
		s.fighters[i].last_hit_timer = 100
		s.fighters[i].pos_x = Fx.from_int(5000)
	_step(s, rules)
	check_eq(s.fighters[0].kos, 1, "only the enemy's fall scores")
	check_eq(s.fighters[1].falls, 1, "the teammate's fall still counts")
	check_eq(s.fighters[2].falls, 1, "the enemy's fall")


func _bat_scene(rules: MatchRules) -> GameState:
	var s := _standing(rules, [0, 300, 600])
	s.fighters[0].weapon = Weapons.UZI
	s.fighters[0].weapon_supply = 1
	s.fighters[1].weapon = Weapons.BASEBALL_BAT
	s.fighters[1].weapon_supply = Weapons.supply_of(Weapons.BASEBALL_BAT)
	_step(s, rules, {1: Buttons.LIGHT}) # the batter starts swinging first ...
	for i in 8:
		_step(s, rules)
	_step(s, rules, {0: Buttons.LIGHT}) # ... so the bullet meets the bat as it comes round
	for i in 60:
		_step(s, rules)
	return s


func test_a_bat_does_not_return_a_teammates_shot() -> void:
	var s := _bat_scene(_with_teams([1, 1, 2], true))
	check_eq(s.fighters[0].josh, 0, "the shooter")
	check_eq(s.fighters[1].josh, 0, "the teammate with the bat")
	check_eq(s.fighters[2].josh, 2, "it flew on through the swing and hit the enemy")

	var free := _bat_scene(_with_teams([], true))
	check_eq(free.fighters[0].josh, 2, "with no teams it comes back to its shooter")
	check_eq(free.fighters[2].josh, 0, "and never reaches the third")


func test_bot_never_dodges_a_teammates_swing() -> void:
	var plain := MatchRules.new()
	plain.stage = StageDefinition.graybox()
	var teams := MatchRules.new()
	teams.stage = plain.stage
	teams.teams = PackedInt32Array([1, 1])
	var heavy := plain.tuning.moves.get_move(MoveData.Id.NEUTRAL_HEAVY)
	var free := 0
	for start in 120:
		for a in heavy.startup + heavy.active:
			var s := Simulation.create_initial_state(plain, 2, 1)
			s.frame = start + a
			for i in 2:
				var f := s.fighters[i]
				f.pos_x = Fx.from_int(i)
				f.pos_y = 0
				f.vel_x = 0
				f.vel_y = 0
				f.grounded = true
				f.facing = 1 if i == 0 else -1
			var op := s.fighters[1]
			op.action = Fighter.Action.ATTACK
			op.move = MoveData.Id.NEUTRAL_HEAVY
			op.action_frame = a
			for level in [SparringBot.Level.EASY, SparringBot.Level.NORMAL, SparringBot.Level.HARD]:
				if not check_eq(SparringBot.think(s, teams, 0, level) & Buttons.DODGE, 0, "dodged a teammate at %d/%d" % [start, a]):
					return
				if SparringBot.think(s, plain, 0, level) & Buttons.DODGE != 0:
					free += 1
	check(free > 0, "with no teams the same swings are dodged")
