extends TestCase
## In GDScript copy_from() and add_to() are hand-written lists of fields, so a field can be left
## out of the copy or of the checksum without anything failing: a rollback would then restore a
## state that is not the one it saved, or two peers would desync without noticing. The C#
## reference proves this by reflection (DeterminismTests); this does the same here, for every
## script variable of Fighter, Prop and GameState, and then rolls a whole match with every weapon
## back and forth (combat spec v0.4, section 6).

const LEVELS := [SparringBot.Level.HARD, SparringBot.Level.NORMAL, SparringBot.Level.HARD, SparringBot.Level.EASY]
const STATUSES := ["slow_frames", "root_frames", "dot_frames", "dot_by", "dot_weapon", "mark_frames", "stun_immune", "stun_weapon"]


## The names of an object's script variables, in the order they are declared.
func _script_vars(o: Object) -> PackedStringArray:
	var names := PackedStringArray()
	for p in o.get_property_list():
		if (int(p["usage"]) & PROPERTY_USAGE_SCRIPT_VARIABLE) != 0 and not str(p["name"]).begins_with("_"):
			names.append(str(p["name"]))
	return names


## A value that differs from this one, of the same type.
func _other(v: Variant) -> Variant:
	if v is bool:
		return not v
	return int(v) + 7


func _state() -> GameState:
	var rules := MatchRules.new()
	rules.weapons = true
	var s := Simulation.create_initial_state(rules, 2, 1)
	# A live prop: an empty slot is all zeros by rule (see Prop), so the fields are tried on this one.
	var p := s.props[3]
	p.kind = Weapons.PROP_SHOT
	p.shot = Weapons.SHOT_ARROW
	p.weapon = Weapons.BOW
	return s


func test_checksum_and_copy_cover_every_fighter_field() -> void:
	var names := _script_vars(Fighter.new())
	check(names.size() >= 45, "only %d Fighter fields found" % names.size())
	for name in names:
		var base := _state()
		var changed := base.clone()
		var f := changed.fighters[1]
		var value: Variant = _other(f.get(name))
		f.set(name, value)
		check(changed.checksum() != base.checksum(), "the checksum ignores Fighter.%s" % name)
		var copy := GameState.new(2, 0)
		copy.copy_from(changed)
		check_eq(copy.fighters[1].get(name), value, "copy_from leaves out Fighter.%s" % name)
		check_eq(copy.checksum(), changed.checksum(), "a copy with Fighter.%s changed" % name)
		copy.fighters[1].set(name, _other(value))
		check_eq(f.get(name), value, "a copy shares Fighter.%s with its original" % name)


func test_checksum_copy_and_clear_cover_every_prop_field() -> void:
	var names := _script_vars(Prop.new())
	check_eq(names.size(), Prop.FIELDS, "Prop.FIELDS is the number of fields an empty slot hashes as zeros")
	for name in names:
		var base := _state()
		var changed := base.clone()
		var p := changed.props[3]
		var value: Variant = _other(p.get(name))
		p.set(name, value)
		check(changed.checksum() != base.checksum(), "the checksum ignores Prop.%s" % name)
		var copy := GameState.new(2, 0)
		copy.copy_from(changed)
		check_eq(copy.props[3].get(name), value, "copy_from leaves out Prop.%s" % name)
		check_eq(copy.checksum(), changed.checksum(), "a copy with Prop.%s changed" % name)
		copy.props[3].set(name, _other(value))
		check_eq(p.get(name), value, "a copy shares Prop.%s with its original" % name)

	# clear() is C#'s default(Prop): every field back to what a new Prop has.
	var fresh := Prop.new()
	var used := Prop.new()
	for name in names:
		used.set(name, _other(used.get(name)))
	used.clear()
	for name in names:
		check_eq(used.get(name), fresh.get(name), "clear() leaves Prop.%s" % name)
	check_eq(fresh.kind, 0, "a new prop is an empty slot")


func test_an_empty_slot_hashes_and_copies_like_seventeen_zeros() -> void:
	# The shortcut in Prop.add_to: one multiplication instead of seventeen values.
	var long_way := StateHasher.new()
	long_way.add_int(42)
	for i in Prop.FIELDS:
		long_way.add_u64(0)
	var short_way := StateHasher.new()
	short_way.add_int(42)
	Prop.new().add_to(short_way)
	check_eq(short_way.value(), long_way.value(), "an empty slot's hash")
	var by_fields := StateHasher.new()
	by_fields.add_int(42)
	var p := Prop.new()
	for i in 4:
		by_fields.add_int(0) # kind, weapon, shot, supply
	by_fields.add_int(p.owner)
	by_fields.add_bool(p.owner_clear)
	for i in 4:
		by_fields.add_u64(0) # position and velocity
	by_fields.add_bool(p.grounded)
	for i in 6:
		by_fields.add_int(0) # timer, age, hit mask, hits, ignore fighter, ignore timer
	check_eq(by_fields.value(), long_way.value(), "the seventeen fields of an empty slot, one by one")

	# Copying an empty slot over a live one empties it.
	var live := Prop.new()
	live.kind = Weapons.PROP_SHOT
	live.timer = 9
	live.owner = 2
	live.copy_from(Prop.new())
	var names := _script_vars(live)
	for name in names:
		check_eq(live.get(name), Prop.new().get(name), "an empty slot copied over a live one leaves Prop.%s" % name)


func test_checksum_and_copy_cover_every_game_state_field() -> void:
	var covered_elsewhere := ["fighters", "props", "weapon_stats"]
	for name in _script_vars(GameState.new(2, 0)):
		if covered_elsewhere.has(name):
			continue
		var base := _state()
		var changed := base.clone()
		var value: Variant = _other(changed.get(name))
		changed.set(name, value)
		check(changed.checksum() != base.checksum(), "the checksum ignores GameState.%s" % name)
		var copy := GameState.new(2, 0)
		copy.copy_from(changed)
		check_eq(copy.get(name), value, "copy_from leaves out GameState.%s" % name)
		check_eq(copy.checksum(), changed.checksum(), "a copy with GameState.%s changed" % name)

	# The record: every entry, and a copy has its own array.
	var s := _state()
	check_eq(s.weapon_stats.size(), 2 * Weapons.RECORD_INTS, "the record's size")
	for i in s.weapon_stats.size():
		var changed := s.clone()
		changed.weapon_stats[i] += 1
		check(changed.checksum() != s.checksum(), "the checksum ignores weapon_stats[%d]" % i)
		check_eq(changed.clone().checksum(), changed.checksum(), "a copy with weapon_stats[%d] changed" % i)
		check_eq(s.weapon_stats[i], 0, "a clone shares the record with its original")

	# The tables are the match's own: as many props as the fighter count gives.
	for n in [1, 2, 4, 9, 15]:
		var big := GameState.new(n, 0)
		check_eq(big.props.size(), Weapons.prop_slots(n), "prop slots for %d fighters" % n)
		check_eq(big.clone().props.size(), big.props.size(), "a clone's prop slots")
		check_eq(big.weapon_stats.size(), n * Weapons.RECORD_INTS, "the record for %d fighters" % n)


func test_the_eight_statuses_are_the_ones_cleared() -> void:
	var names := _script_vars(Fighter.new())
	var f := Fighter.new()
	var before := {}
	for name in names:
		f.set(name, _other(f.get(name)))
		before[name] = f.get(name)
	f.clear_statuses()
	for name in names:
		if STATUSES.has(name):
			check_eq(f.get(name), 0, "clear_statuses leaves Fighter.%s" % name)
		else:
			check_eq(f.get(name), before[name], "clear_statuses touches Fighter.%s" % name)
	# They are the last eight fields, after hit_count, in this order (the checksum's order).
	check_eq(Array(names.slice(names.size() - 8)), STATUSES, "the statuses' place and order")
	check_eq(names[names.size() - 9], "hit_count", "the field before them")


func test_a_ko_and_a_respawn_clear_every_status() -> void:
	var rules := MatchRules.new()
	rules.weapons = true
	var s := Simulation.create_initial_state(rules, 2, 1)
	s.prop_timer = 1000000
	var inputs := PackedInt32Array([0, 0])
	var f := s.fighters[1]
	for name: String in STATUSES:
		f.set(name, 30)
	Simulation.advance(s, rules, inputs)
	# They count down; the hold (root_frames, stun_weapon) ends because a fighter just spawned is in
	# the air; the poison keeps its poisoner and weapon.
	check_eq([f.slow_frames, f.dot_frames, f.mark_frames, f.stun_immune], [29, 29, 29, 29], "the timers count down")
	check_eq([f.root_frames, f.stun_weapon, f.dot_by, f.dot_weapon], [0, 0, 30, 30], "nobody is held in the air")
	f.pos_y = Fx.from_int(-30) # knocked out through the bottom
	Simulation.advance(s, rules, inputs)
	check_eq(f.action, Fighter.Action.RESPAWNING, "knocked out")
	for name: String in STATUSES:
		check_eq(f.get(name), 0, "a KO leaves %s" % name)
	for name: String in STATUSES:
		f.set(name, 30) # whatever is set while it waits is gone when it spawns
	for i in rules.respawn_frames:
		Simulation.advance(s, rules, inputs)
	check(f.is_active(), "back in")
	for name: String in STATUSES:
		check_eq(f.get(name), 0, "a respawn leaves %s" % name)


func test_teams_come_from_the_rules() -> void:
	var rules := MatchRules.new()
	check(Simulation.allies(rules, 1, 1), "a fighter is its own ally")
	check(not Simulation.allies(rules, 0, 1), "with no teams nobody else is")
	check_eq(rules.team_of(0), 0, "no team")
	var with_teams := MatchRules.room(StageDefinition.by_id("rooftops"), 10, PackedInt32Array(), PackedInt32Array([1, 1, 2, 0, 0]))
	check(Simulation.allies(with_teams, 0, 1) and Simulation.allies(with_teams, 1, 0), "the same team")
	check(not Simulation.allies(with_teams, 0, 2), "another team")
	check(not Simulation.allies(with_teams, 3, 4), "team 0 is no team")
	check_eq(with_teams.team_of(9), 0, "past the list: no team")
	check(with_teams.fingerprint() != MatchRules.room(StageDefinition.by_id("rooftops"), 10).fingerprint(), "teams are part of the rules' fingerprint")


## One frame of the all-weapons match: every 90 frames each fighter is handed the next weapon a
## crate can hold, the CPUs think, the sim advances.
func _advance(state: GameState, rules: MatchRules, inputs: PackedInt32Array) -> void:
	var crates := Weapons.crate_list()
	if state.frame % 90 == 0:
		for p in state.fighters.size():
			if state.fighters[p].is_active():
				var w := crates[(state.frame / 90 + p) % crates.size()]
				state.fighters[p].weapon = w
				state.fighters[p].weapon_supply = Weapons.supply_of(w)
	for p in state.fighters.size():
		inputs[p] = SparringBot.think(state, rules, p, LEVELS[p])
	Simulation.advance(state, rules, inputs)


func test_rollback_through_a_match_with_every_weapon() -> void:
	var rules := MatchRules.new()
	rules.weapons = true
	rules.flurry = true
	var state := Simulation.create_initial_state(rules, 4, 77)
	var inputs := PackedInt32Array()
	inputs.resize(4)
	var names := _script_vars(Prop.new())
	var fresh := Prop.new()
	var frames := 90 * Weapons.crate_list().size()
	var busiest := 0
	var from := 60
	while from + 120 <= frames:
		while state.frame < from:
			_advance(state, rules, inputs)
		var saved := state.clone()
		var sums := PackedInt64Array()
		for i in 120:
			_advance(state, rules, inputs)
			sums.append(state.checksum())
			# An empty slot is all zeros, the rule Prop's shortcuts stand on.
			var used := 0
			for p in state.props:
				if p.kind != Weapons.PROP_NONE:
					used += 1
					continue
				for name in names:
					if p.get(name) != fresh.get(name):
						check(false, "frame %d: an empty slot holds Prop.%s = %s" % [state.frame, name, str(p.get(name))])
						return
			busiest = maxi(busiest, used)
		var replay := state.clone()
		replay.copy_from(saved) # load the save into a state that has moved on
		for i in 120:
			_advance(replay, rules, inputs)
			if not check_eq(replay.checksum(), sums[i], "the replay from frame %d, %d frames later" % [from, i + 1]):
				return
		from += 150
	check(busiest >= 4, "the match never had four props out at once (%d)" % busiest)
