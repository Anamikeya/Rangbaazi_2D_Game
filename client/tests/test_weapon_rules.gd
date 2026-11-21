extends TestCase
## Weapon rules checked in the GDScript sim on its own. The goldens compare whole matches with the
## C# reference; these pin the paths that random play reaches rarely (a blink, a Chimta disarm, the
## Gauntlet's three rows, the thrown-as rows, the full prop table) and the rules the review of the
## 88-weapon build fixed (combat spec v0.4, section 12): a guard judges a shot's side by the way it
## moves, a spent guard blocks nothing, a thrown weapon reads none of its row's shot columns, the
## Sledgehammer breaks decoys and peels, the anvil marks the nearest opponent. Each mirrors a test in
## tests/Rangbaazi.Sim.Tests (GuardTrapWeaponTests, MachineWeaponTests, WeaponRulesTests).

static var _wide: StageDefinition
static var _arena: StageDefinition


## A 1 km floor with a far-away blast zone (TestStages.Wide).
static func wide() -> StageDefinition:
	if _wide == null:
		_wide = StageDefinition.new()
		_wide.id = "test-wide"
		_wide.platforms = [FxRect.new(Fx.from_int(-500), Fx.from_int(-2), Fx.from_int(500), 0)]
		_wide.spawn_x = PackedInt64Array([Fx.from_int(-2), Fx.from_int(2), Fx.from_int(-4), Fx.from_int(4)])
		_wide.spawn_y = PackedInt64Array([0, 0, 0, 0])
		_wide.blast_zone = FxRect.new(Fx.from_int(-1000), Fx.from_int(-100), Fx.from_int(1000), Fx.from_int(1000))
	return _wide


## A 32 m floor whose sides are the screen's: the blast lines are 1.2 m beyond them.
static func arena() -> StageDefinition:
	if _arena == null:
		_arena = StageDefinition.new()
		_arena.id = "test-arena"
		_arena.platforms = [FxRect.new(Fx.from_int(-16), Fx.from_int(-2), Fx.from_int(16), 0)]
		_arena.spawn_x = PackedInt64Array([Fx.from_int(-2), Fx.from_int(2)])
		_arena.spawn_y = PackedInt64Array([0, 0])
		_arena.blast_zone = FxRect.new(Fx.ratio(-86, 5), Fx.from_int(-10), Fx.ratio(86, 5), Fx.from_int(40))
	return _arena


func _rules(stage: StageDefinition = null) -> MatchRules:
	var rules := MatchRules.new()
	rules.stage = stage if stage != null else wide()
	rules.weapons = true
	rules.guts = false
	return rules


static func cm(v: int) -> int:
	return Fx.ratio(v, 100)


## Fighters standing at these x positions (cm), fighter 0 facing right and the rest left; no boxes due.
func _standing(rules: MatchRules, xs: Array) -> GameState:
	var s := Simulation.create_initial_state(rules, xs.size(), 1)
	for i in xs.size():
		var f := s.fighters[i]
		f.pos_x = cm(xs[i])
		f.pos_y = rules.stage.platforms[0].max_y
		f.vel_x = 0
		f.vel_y = 0
		f.grounded = true
		f.facing = 1 if i == 0 else -1
	s.prop_timer = 1000000
	return s


func _arm(s: GameState, i: int, w: int, supply := -1) -> void:
	s.fighters[i].weapon = w
	s.fighters[i].weapon_supply = supply if supply >= 0 else Weapons.supply_of(w)


func _step(s: GameState, rules: MatchRules, first := 0, second := 0) -> void:
	var inputs := PackedInt32Array()
	inputs.resize(s.fighters.size())
	inputs[0] = first
	if inputs.size() > 1:
		inputs[1] = second
	Simulation.advance(s, rules, inputs)


func _wait(s: GameState, rules: MatchRules, frames: int) -> void:
	for i in frames:
		_step(s, rules)


## Fighter 0 presses attack (with any other buttons), and the frames pass up to its move's startup frame.
func _fire(s: GameState, rules: MatchRules, w: int, held := 0) -> void:
	_step(s, rules, Buttons.LIGHT | held)
	for i in WeaponTable.ROWS[w][WeaponTable.C_S]:
		_step(s, rules, held)


## A prop in the last free slot: a shot at 0.8 m, or a placed one on the floor, with its row's timer.
func _put(s: GameState, w: int, shot: int, x_cm: int, owner: int, vx_cm := 0, y_cm := -1, timer := -1) -> int:
	var slot := s.props.size() - 1
	while s.props[slot].kind != Weapons.PROP_NONE:
		slot -= 1
	var row := WeaponTable.ROWS[w]
	var lying: bool = WeaponTable.PLACED[shot] or shot == Weapons.SHOT_DROP
	var p := s.props[slot]
	p.clear()
	p.kind = Weapons.PROP_SHOT
	p.shot = shot
	p.weapon = w
	p.owner = owner
	p.owner_clear = true
	p.pos_x = cm(x_cm)
	p.pos_y = cm(y_cm) if y_cm >= 0 else (0 if lying else Fx.ratio(4, 5))
	p.vel_x = cm(vx_cm)
	p.grounded = lying and y_cm <= 0
	p.timer = timer if timer >= 0 else Weapons.start_timer(row) if shot != Weapons.SHOT_WALL else row[WeaponTable.C_ZONE_LIFE]
	return slot


func _count(s: GameState, shot: int, owner := -2) -> int:
	var n := 0
	for p in s.props:
		if p.kind == Weapons.PROP_SHOT and p.shot == shot and (owner == -2 or p.owner == owner):
			n += 1
	return n


# ---- Guards ----

func test_a_guard_blocks_a_fast_shot_from_the_front_at_every_spacing_and_never_one_from_behind() -> void:
	var rules := _rules()
	for gun in [[WeaponTable.UZI, Weapons.SHOT_BULLET], [WeaponTable.CROSSBOW, Weapons.SHOT_ARROW]]:
		var speed: int = WeaponTable.ROWS[gun[0]][WeaponTable.C_VX] * 100 / Fx.ONE
		for front in [true, false]:
			var offset := 0
			while offset < speed:
				var s := _standing(rules, [0, 5000, -5000])
				_arm(s, 0, WeaponTable.PARAT)
				for i in 3:
					_step(s, rules, Buttons.LIGHT)
				var from := 200 + offset if front else -(200 + offset)
				var shot := _put(s, gun[0], gun[1], from, 1, -speed if front else speed)
				for i in 12:
					if s.props[shot].kind == Weapons.PROP_NONE:
						break
					_step(s, rules, Buttons.LIGHT)
				var label := "%s from %s %d cm" % [WeaponTable.ROWS[gun[0]][WeaponTable.C_ID], "the front" if front else "behind", from]
				if front:
					check(s.fighters[0].josh == 0 and s.fighters[0].weapon_supply == 7, label + ": blocked")
				else:
					check(s.fighters[0].josh > 0, label + ": hit")
				offset += 5


func test_a_guard_that_has_spent_its_last_supply_blocks_nothing_more() -> void:
	var rules := _rules()
	var s := _standing(rules, [0, 5000, -5000])
	_arm(s, 0, WeaponTable.TAWA, 1)
	_step(s, rules, Buttons.LIGHT)
	_wait(s, rules, 2)
	_put(s, WeaponTable.UZI, Weapons.SHOT_BULLET, 80, 1, -30)
	_step(s, rules)
	check_eq(s.fighters[0].josh, 0, "the last block")
	check_eq(s.fighters[0].weapon_supply, 0, "spent on it")
	check(not Simulation.guarding(s.fighters[0]), "no guard left")
	_put(s, WeaponTable.UZI, Weapons.SHOT_BULLET, 80, 1, -30)
	_step(s, rules)
	check_eq(s.fighters[0].josh, WeaponTable.ROWS[WeaponTable.UZI][WeaponTable.C_DMG], "the next bullet hits")

	# The Parat at 1: the block spends it and the bash comes out though Light is still held.
	var t := _standing(rules, [0, 5000, -5000])
	_arm(t, 0, WeaponTable.PARAT, 1)
	for i in 4:
		_step(t, rules, Buttons.LIGHT)
	_put(t, WeaponTable.UZI, Weapons.SHOT_BULLET, 80, 1, -30)
	_step(t, rules, Buttons.LIGHT)
	check_eq(t.fighters[0].weapon_supply, 0, "Parat: spent on the block")
	var startup: int = WeaponTable.ROWS[WeaponTable.PARAT][WeaponTable.C_S]
	for i in startup:
		if t.fighters[0].action_frame >= startup:
			break
		_step(t, rules, Buttons.LIGHT)
	check_eq(t.fighters[0].action_frame, startup, "Parat: the bash comes out at once")


# ---- Thrown weapons are plain thrown props ----

func test_a_flung_weapon_that_breaks_as_a_shot_is_not_broken_by_a_swing_or_a_burst() -> void:
	var rules := _rules()
	for w in [WeaponTable.MOUSE_TRAP, WeaponTable.VENOM_TRAP, WeaponTable.CHICKEN, WeaponTable.DARK_ENERGY, WeaponTable.SONIC_SPEAKER]:
		var s := _standing(rules, [0, 5000])
		var thrown := _put(s, w, Weapons.SHOT_THROWN, 100, 1, -1)
		s.props[thrown].supply = Weapons.supply_of(w)
		check(not Weapons.breakable(s.props[thrown]), "%d: not breakable" % w)
		_arm(s, 0, WeaponTable.BELAN)
		_step(s, rules, Buttons.LIGHT)
		for i in WeaponTable.ROWS[WeaponTable.BELAN][WeaponTable.C_S] + 2:
			s.props[thrown].pos_x = cm(100)
			s.props[thrown].pos_y = Fx.ratio(4, 5)
			s.props[thrown].vel_x = cm(-1)
			s.props[thrown].vel_y = 0
			_step(s, rules)
		check_eq(s.props[thrown].shot, Weapons.SHOT_THROWN, "%d: the swing left it" % w)
		check(not s.fighters[0].attack_connected, "%d: nothing to break" % w)
		var bomb := _put(s, WeaponTable.CHERRY_BOMBS, Weapons.SHOT_BOMB, 150, 0, 0, 0, 1)
		s.props[bomb].grounded = true
		s.props[thrown].pos_x = cm(100)
		s.props[thrown].pos_y = Fx.ratio(4, 5)
		_step(s, rules)
		check_eq(s.props[bomb].kind, Weapons.PROP_NONE, "%d: the bomb burst" % w)
		check(s.props[thrown].kind != Weapons.PROP_NONE, "%d: the burst left it" % w)


func test_a_thrown_weapon_that_bounces_as_a_shot_does_not_bounce_off_the_screen_edge() -> void:
	var rules := _rules(arena())
	for w in [WeaponTable.CHAKRAM, WeaponTable.STEEL_TUMBLER]:
		var s := _standing(rules, [-1000, -1200])
		var right := arena().blast_zone.max_x - Weapons.SCREEN_INSET
		var thrown := _put(s, w, Weapons.SHOT_THROWN, (right - Fx.ratio(1, 10)) * 100 / Fx.ONE, 0, 32, 300)
		for i in 4:
			_step(s, rules)
		var p := s.props[thrown]
		check(p.kind == Weapons.PROP_NONE or (p.vel_x > 0 and p.ignore_timer == 0), "%d flew on past the edge" % w)


func test_a_thrown_dark_energy_can_hit_its_thrower() -> void:
	var rules := _rules()
	var s := _standing(rules, [0, 5000])
	_put(s, WeaponTable.DARK_ENERGY, Weapons.SHOT_THROWN, 0, 0)
	_step(s, rules)
	check_eq(s.fighters[0].josh, WeaponTable.ROWS[WeaponTable.DARK_ENERGY][WeaponTable.C_THROW], "its throw damage")


# ---- Breakable props ----

func test_the_sledgehammer_breaks_every_trap_decoy_speaker_and_wall_in_one_swing() -> void:
	var rules := _rules()
	for e in [[WeaponTable.BANANA_PEEL, Weapons.SHOT_MINE], [WeaponTable.MOUSE_TRAP, Weapons.SHOT_MINE], [WeaponTable.DECOY_CRATE, Weapons.SHOT_DECOY],
			[WeaponTable.CRATE_DETONATOR, Weapons.SHOT_DECOY], [WeaponTable.SONIC_SPEAKER, Weapons.SHOT_SPEAKER], [WeaponTable.MATKA, Weapons.SHOT_WALL]]:
		var s := _standing(rules, [0, 5000])
		_arm(s, 0, WeaponTable.SLEDGEHAMMER)
		var prop := _put(s, e[0], e[1], 120, 1, 0, -1, 0 if e[1] == Weapons.SHOT_MINE else -1)
		check(Weapons.breakable(s.props[prop]), "%d breakable" % e[0])
		_fire(s, rules, WeaponTable.SLEDGEHAMMER)
		_wait(s, rules, 2)
		check_eq(s.props[prop].kind, Weapons.PROP_NONE, "%d broken in one swing" % e[0])


func test_two_swings_pop_a_decoy() -> void:
	var rules := _rules()
	var s := _standing(rules, [0, 5000])
	_arm(s, 0, WeaponTable.BELAN)
	var prop := _put(s, WeaponTable.DECOY_CRATE, Weapons.SHOT_DECOY, 100, 1)
	_fire(s, rules, WeaponTable.BELAN)
	_wait(s, rules, 3)
	check_eq(s.props[prop].ignore_fighter, 1, "one swing taken")
	while s.fighters[0].action == Simulation.A_ATTACK:
		_step(s, rules)
	_fire(s, rules, WeaponTable.BELAN)
	_wait(s, rules, 3)
	check_eq(s.props[prop].kind, Weapons.PROP_NONE, "popped by the second")


# ---- The anvil's mark ----

func test_the_anvil_marks_the_nearest_opponent_in_front_where_they_will_be() -> void:
	var rules := _rules()
	# [x of each fighter (cm)], [their speed (cm a frame)], the mark (cm)
	for e in [[[0, 300], [0, 0], 300], [[0, 300, 500], [0, 0, 0], 300], [[0, 300], [0, 5], 500], [[0, 300], [0, -5], 200],
			[[0, 100], [0, 0], 200], [[0, 750], [0, 0], 600], [[0, 900], [0, 0], 400], [[0, -300], [0, 0], 400]]:
		var s := _standing(rules, e[0])
		for i in e[1].size():
			s.fighters[i].vel_x = cm(e[1][i])
		var at := Simulation.drop_spot(s, rules, 0)
		check(absi(at[0] - cm(e[2])) < cm(1), "%s moving %s: marked %d, not %d cm" % [e[0], e[1], at[0] * 100 / Fx.ONE, e[2]])
		check_eq(at[1], Fx.from_int(9), "%s: 9 m up" % [e[0]])
	var teams := _rules()
	teams.teams = PackedInt32Array([1, 1, 2])
	var t := _standing(teams, [0, 300, 500])
	check_eq(Simulation.drop_spot(t, teams, 0)[0], cm(500), "past a teammate")
	_put(t, WeaponTable.STINK_CLOUD_CAPSULE, Weapons.SHOT_ZONE, 500, 1, 0, 0, 100)
	check_eq(Simulation.drop_spot(t, teams, 0)[0], Fx.from_int(4), "not at someone hidden in a cloud")


# ---- Paths random play reaches rarely ----

func test_the_teleporter_blinks_ahead_and_up_and_fizzles_over_nothing() -> void:
	var rules := _rules()
	var s := _standing(rules, [0, 5000])
	_arm(s, 0, WeaponTable.TELEPORTER)
	_fire(s, rules, WeaponTable.TELEPORTER)
	var f := s.fighters[0]
	check_eq(f.pos_x, Fx.from_int(5), "5 m ahead")
	check(f.grounded and f.vel_x == 0 and f.vel_y == 0, "standing still")
	check_eq(f.weapon_supply, 2, "spent one")

	# Up + Light on the Graybox: onto the platform above (its top is 2.75 m), from under it.
	var gray := _rules(StageDefinition.graybox())
	var u := _standing(gray, [400, -800])
	_arm(u, 0, WeaponTable.TELEPORTER)
	_fire(u, gray, WeaponTable.TELEPORTER, Buttons.UP)
	check_eq(u.fighters[0].pos_y, Fx.ratio(11, 4), "up onto the platform above")
	check_eq(u.fighters[0].pos_x, Fx.from_int(4), "straight up")

	# Off the side, facing away from the stage: nowhere to land, so nothing is spent.
	var o := _standing(gray, [0, 500])
	o.fighters[0].pos_x = Fx.from_int(-14)
	o.fighters[0].pos_y = Fx.from_int(4)
	o.fighters[0].grounded = false
	o.fighters[0].facing = -1
	_arm(o, 0, WeaponTable.TELEPORTER)
	_fire(o, gray, WeaponTable.TELEPORTER)
	check_eq(o.fighters[0].weapon_supply, 3, "a blink over nothing fizzles")


func test_the_chimtas_tip_disarms_and_a_close_pinch_does_not() -> void:
	var rules := _rules()
	var s := _standing(rules, [0, 195])
	_arm(s, 1, WeaponTable.BELAN)
	_arm(s, 0, WeaponTable.CHIMTA)
	_fire(s, rules, WeaponTable.CHIMTA)
	check_eq(s.fighters[1].josh, WeaponTable.ROWS[WeaponTable.CHIMTA][WeaponTable.C_DMG] * Weapons.SWEET_PERCENT / 100, "a sweet tip")
	check_eq(s.fighters[1].weapon, Weapons.NONE, "disarmed")
	var dropped := false
	for p in s.props:
		dropped = dropped or (p.kind == Weapons.PROP_ITEM and p.weapon == WeaponTable.BELAN and p.supply == 12)
	check(dropped, "the Belan lies at their feet with its supply")

	var t := _standing(rules, [0, 100])
	_arm(t, 1, WeaponTable.BELAN)
	_arm(t, 0, WeaponTable.CHIMTA)
	_fire(t, rules, WeaponTable.CHIMTA)
	check_eq(t.fighters[1].weapon, WeaponTable.BELAN, "a close pinch keeps it")


func test_the_jugaad_gauntlet_pulls_then_punches_then_shoots() -> void:
	var rules := _rules()
	var s := _standing(rules, [0, 300])
	_arm(s, 0, WeaponTable.JUGAAD_GAUNTLET)
	var order := [WeaponTable.ROWS[101][WeaponTable.C_MOVE], WeaponTable.ROWS[102][WeaponTable.C_MOVE], WeaponTable.ROWS[103][WeaponTable.C_MOVE]]
	for k in 6:
		var b := s.fighters[1]
		b.pos_x = Fx.from_int(3 if k % 3 == 0 else 1 if k % 3 == 1 else 5)
		b.pos_y = 0
		b.vel_x = 0
		b.vel_y = 0
		b.grounded = true
		b.action = Simulation.A_IDLE
		b.hit_stop = 0
		_step(s, rules, Buttons.LIGHT)
		check_eq(s.fighters[0].move, order[k % 3], "press %d" % k)
		if k % 3 == 2:
			_wait(s, rules, WeaponTable.ROWS[103][WeaponTable.C_S])
			var bolt := false
			for p in s.props:
				bolt = bolt or (p.kind == Weapons.PROP_SHOT and p.weapon == WeaponTable.GAUNTLET_BOLT)
			check(bolt, "the bolt carries its hidden row")
		while s.fighters[0].action == Simulation.A_ATTACK:
			_step(s, rules)
		check_eq(s.fighters[0].weapon_supply, 9 - k - 1, "one a press")
	check_eq(s.fighters[1].last_hit_weapon, WeaponTable.JUGAAD_GAUNTLET, "credited to the Gauntlet")
	check_eq(Simulation.weapon_stat(s, 0, WeaponTable.GAUNTLET_PULL, 0), 0, "never to a hidden row")


func test_a_thrown_gas_cylinder_charpai_and_spear_become_their_hidden_rows() -> void:
	var rules := _rules()
	for e in [[WeaponTable.GAS_CYLINDER, WeaponTable.THROWN_GAS_CYLINDER, Weapons.SHOT_BOMB], [WeaponTable.CHARPAI, WeaponTable.THROWN_CHARPAI, Weapons.SHOT_BOMB],
			[WeaponTable.SPEAR, WeaponTable.THROWN_SPEAR, Weapons.SHOT_ARROW]]:
		var s := _standing(rules, [0, 5000])
		_arm(s, 0, e[0])
		_step(s, rules, Buttons.DROP_WEAPON)
		_wait(s, rules, 8) # the throw lets go on its startup frame, 6
		check_eq(s.fighters[0].weapon, Weapons.NONE, "%d left the hand" % e[0])
		check_eq(_count(s, Weapons.SHOT_THROWN), 0, "%d: not a plain thrown prop" % e[0])
		var slot := -1
		for i in s.props.size():
			if s.props[i].kind == Weapons.PROP_SHOT and s.props[i].weapon == e[1]:
				slot = i
		if not check(slot >= 0, "%d became row %d" % [e[0], e[1]]):
			continue
		check_eq(s.props[slot].shot, e[2], "%d flies as its row" % e[0])
		if e[0] == WeaponTable.CHARPAI:
			for i in 80:
				if s.props[slot].shot == Weapons.SHOT_PAD:
					break
				_step(s, rules)
			check_eq(s.props[slot].shot, Weapons.SHOT_PAD, "the charpai opened into a pad")
			check(s.props[slot].timer > 300, "lying there for its zone life (%d)" % s.props[slot].timer)
			var b := s.fighters[1]
			b.pos_x = s.props[slot].pos_x
			b.pos_y = 0
			b.vel_y = 0
			b.grounded = true
			_step(s, rules)
			check_eq(s.fighters[1].vel_y, WeaponTable.ROWS[WeaponTable.THROWN_CHARPAI][WeaponTable.C_PAD_LAUNCH], "it bounces")


# ---- The prop table (combat spec v0.4, section 1.6) ----

## Fills every shot slot with idle mines of this fighter, far away, the later slots the older.
func _fill_shot_slots(s: GameState, owner: int) -> void:
	for i in range(Weapons.box_slots(s.fighters.size()), s.props.size()):
		var p := s.props[i]
		p.clear()
		p.kind = Weapons.PROP_SHOT
		p.shot = Weapons.SHOT_MINE
		p.weapon = WeaponTable.MINES
		p.owner = owner
		p.owner_clear = true
		p.pos_x = cm(30000 + i * 300)
		p.grounded = true
		p.timer = 100000
		p.age = i


func test_a_shot_into_a_full_table_fizzles_or_replaces_a_bullet_or_its_firers_own_placed_prop() -> void:
	var rules := _rules()
	var from := Weapons.box_slots(2)

	# Full of someone else's placed props: the arrow fizzles and nothing is spent.
	var s := _standing(rules, [0, 2000])
	_fill_shot_slots(s, 1)
	_arm(s, 0, WeaponTable.BOW)
	_fire(s, rules, WeaponTable.BOW)
	check_eq(s.fighters[0].weapon_supply, 6, "fizzled: nothing spent")
	check_eq(_count(s, Weapons.SHOT_ARROW), 0, "no arrow")
	check_eq(_count(s, Weapons.SHOT_MINE, 1), s.props.size() - from, "nobody else's prop touched")

	# A ball that would leave the hand stays in it.
	var b := _standing(rules, [0, 2000])
	_fill_shot_slots(b, 1)
	_arm(b, 0, WeaponTable.BOWLING_BALL)
	_fire(b, rules, WeaponTable.BOWLING_BALL)
	check_eq(b.fighters[0].weapon, WeaponTable.BOWLING_BALL, "the ball stays in the hand")

	# With bullets in it, the oldest bullet's slot is taken.
	var t := _standing(rules, [0, 2000])
	_fill_shot_slots(t, 1)
	for e in [[from + 3, 2], [from + 7, 9]]:
		var p := t.props[e[0]]
		p.clear()
		p.kind = Weapons.PROP_SHOT
		p.shot = Weapons.SHOT_BULLET
		p.weapon = WeaponTable.UZI
		p.owner = 1
		p.owner_clear = true
		p.pos_x = cm(90000)
		p.pos_y = Fx.ONE
		p.age = e[1]
	_arm(t, 0, WeaponTable.UZI)
	_fire(t, rules, WeaponTable.UZI)
	check_eq(t.props[from + 7].owner, 0, "the oldest bullet's slot is fighter 0's bullet now")
	check_eq(t.props[from + 3].owner, 1, "the younger bullet stays")

	# With no bullet, its own oldest placed prop, never another fighter's.
	var u := _standing(rules, [0, 2000])
	_fill_shot_slots(u, 1)
	for e in [[from + 2, 5, 50000], [from + 5, 40, 51000]]:
		var p := u.props[e[0]]
		p.owner = 0
		p.age = e[1]
		p.pos_x = cm(e[2])
	_arm(u, 0, WeaponTable.BOW)
	_fire(u, rules, WeaponTable.BOW)
	check_eq(u.props[from + 5].shot, Weapons.SHOT_ARROW, "its own oldest mine made room")
	check_eq(u.props[from + 2].shot, Weapons.SHOT_MINE, "its younger mine stays")
	check_eq(u.fighters[0].weapon_supply, 5, "spent one")


func test_in_a_match_of_more_than_eight_a_fighter_keeps_two_placed_props() -> void:
	var rules := _rules()
	var s := _standing(rules, [0, 10000, 20000, 30000, 40000, 50000, 60000, 70000, 80000])
	_arm(s, 0, WeaponTable.MINES, 5)
	for k in 3:
		s.fighters[0].pos_x = Fx.from_int(k * 10)
		_fire(s, rules, WeaponTable.MINES)
		while s.fighters[0].action == Simulation.A_ATTACK:
			_step(s, rules)
	check_eq(_count(s, Weapons.SHOT_MINE, 0), 2, "the third mine removed the first")
	for p in s.props:
		check(not (p.kind == Weapons.PROP_SHOT and p.pos_x < Fx.from_int(5)), "the first is the one that went")
