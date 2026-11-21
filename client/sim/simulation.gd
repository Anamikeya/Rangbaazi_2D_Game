class_name Simulation
## The deterministic step function: (state, inputs) -> next state, at a fixed 60 Hz.
## A line-for-line port of Simulation.cs (the C# reference in src/Rangbaazi.Sim), checked bit for
## bit against its golden traces (client/tests). Ints only (see Fx); gameplay lives here and in
## the other client/sim scripts, never in scenes or UI.
##
## Graybox scope: run, jump + air jump, one-way platforms, jab, four heavies plus the ground slam,
## dodge and parry, hit-stop, Josh % knockback, Guts, ring-out (docs/design/combat-spec-v0.1.md).

const TICKS_PER_SECOND := 60

## Bump when the sim's behaviour changes on purpose: online players with different versions
## refuse to play each other instead of desyncing.
## 8: the weapon table, prop slots by fighter count, fizzling shots, the placed-prop limit, the
## statuses' state, teams, the six-slot record and weighted crates (combat spec v0.4); team wins,
## big stages (rooms v1).
const SIM_VERSION := 8

## Most fighters in one match: online rooms hold up to 15 players (one spare slot).
const MAX_FIGHTERS := 16

const DIAGONAL := 46340 # Fx.ratio(7071, 10000), ~sin(45 deg)

const A_IDLE := Fighter.Action.IDLE
const A_ATTACK := Fighter.Action.ATTACK
const A_DODGE := Fighter.Action.DODGE
const A_HITSTUN := Fighter.Action.HITSTUN
const A_STUNNED := Fighter.Action.STUNNED
const A_RESPAWNING := Fighter.Action.RESPAWNING
const A_ELIMINATED := Fighter.Action.ELIMINATED


static func create_initial_state(rules: MatchRules, fighter_count: int, seed: int) -> GameState:
	assert(fighter_count >= 1 and fighter_count <= MAX_FIGHTERS, "1-16 fighters.")
	var state := GameState.new(fighter_count, seed)
	for i in fighter_count:
		var f := state.fighters[i]
		f.stocks = rules.stocks
		_spawn(f, i, rules)
	state.prop_timer = Weapons.FIRST_BOX_FRAMES
	return state


## Advances the match one frame. inputs[i] are fighter i's buttons (see Buttons).
static func advance(state: GameState, rules: MatchRules, inputs: PackedInt32Array) -> void:
	assert(inputs.size() == state.fighters.size(), "Expected one input per fighter.")
	if not state.match_over:
		var fighters := state.fighters
		var frozen: Array[bool] = []
		frozen.resize(fighters.size())
		for i in fighters.size():
			frozen[i] = _update_fighter(state, fighters[i], i, inputs[i], rules)
		if rules.weapons:
			_fire_shots(state, frozen, rules)
		_resolve_hits(state, frozen, rules)
		if rules.weapons:
			_sprays(state, frozen, rules)
		if rules.weapons:
			_update_props(state, frozen, rules)
		if rules.hazards:
			_apply_hazards(state, rules)
		for i in fighters.size():
			_end_plunge_on_landing(fighters[i], rules.tuning)
			_free_if_airborne(fighters[i])
			if rules.weapons:
				_return_on_fall(state, i, rules)
			_check_blast_zone(state, i, rules)
		_check_match_over(state, rules)
	state.frame += 1


## Hit-stop for a hit of this much damage: 2 + damage / 2, clamped to the rules' range.
static func hit_stop_frames(damage: int, rules: MatchRules) -> int:
	return clampi(2 + damage / 2, rules.min_hit_stop, rules.max_hit_stop)


## The same fighter, or two fighters of one team (MatchRules.team_of, 0 is none). Allies never hit
## each other; with no teams set this is just a == b.
static func allies(rules: MatchRules, a: int, b: int) -> bool:
	if a == b:
		return true
	var team := rules.team_of(a)
	return team != 0 and team == rules.team_of(b)


static func is_intangible(f: Fighter, t: FighterTuning) -> bool:
	return f.action == A_DODGE and f.action_frame < t.dodge_intangible_frames


static func in_parry_window(f: Fighter, t: FighterTuning) -> bool:
	return f.action == A_DODGE and f.action_frame < t.parry_window_frames


## The move's frame data while attacking, otherwise null.
static func current_move(f: Fighter, t: FighterTuning) -> MoveData:
	return t.moves.get_move(f.move) if f.action == A_ATTACK else null


## The fighter's hitbox if its move's hitbox is out this frame, otherwise null.
static func active_hitbox(f: Fighter, t: FighterTuning) -> FxRect:
	var m := current_move(f, t)
	if m == null or not m.is_active_on(f.action_frame):
		return null
	return move_hitbox(f, m)


static func move_hitbox(f: Fighter, m: MoveData) -> FxRect:
	var x := f.pos_x
	var y := f.pos_y
	if m.hits_both_sides:
		return FxRect.new(x - m.hitbox_far, y + m.hitbox_bottom, x + m.hitbox_far, y + m.hitbox_top)
	return FxRect.new(x + m.hitbox_near * f.facing, y + m.hitbox_bottom, x + m.hitbox_far * f.facing, y + m.hitbox_top)


static func hurtbox(f: Fighter, t: FighterTuning) -> FxRect:
	return FxRect.new(f.pos_x - t.half_width, f.pos_y, f.pos_x + t.half_width, f.pos_y + t.height)


## Steps one fighter. Returns true if it spent this frame frozen in hit-stop.
static func _update_fighter(state: GameState, f: Fighter, index: int, input: int, rules: MatchRules) -> bool:
	var t := rules.tuning

	if f.action == A_ELIMINATED or f.action == A_RESPAWNING:
		f.previous_buttons = input
		if f.action == A_RESPAWNING:
			f.action_frame += 1
			if f.action_frame >= rules.respawn_frames:
				_spawn(f, index, rules)
				f.shield = rules.respawn_shield # the hidden second wind, only after a respawn
		return false

	# Frozen in hit-stop: nothing moves or counts down. previous_buttons is left alone too, so a
	# button pressed during the freeze and still held afterwards counts as a fresh press.
	if f.hit_stop > 0:
		f.hit_stop -= 1
		if f.hit_stop == 0:
			f.frozen_by_hit = false
		return true

	var pressed := input & ~f.previous_buttons
	f.previous_buttons = input
	_tick_timers(state, f, index, rules)

	# Each fresh attack press works a root 12 frames looser, in the middle of an attack too.
	if f.root_frames > 0 and (pressed & (Buttons.LIGHT | Buttons.HEAVY)) != 0:
		f.root_frames = maxi(0, f.root_frames - Weapons.ROOT_PRESS_FRAMES)
		if f.root_frames == 0:
			f.stun_weapon = 0

	var gravity := true
	match f.action:
		A_HITSTUN, A_STUNNED:
			f.hitstun_frames -= 1
			if f.hitstun_frames <= 0:
				f.action = A_IDLE
				if f.root_frames == 0:
					f.stun_weapon = 0 # a stun, freeze or slip is over
			if f.grounded:
				f.vel_x = Fx.move_toward(f.vel_x, 0, t.ground_accel)
		A_ATTACK:
			gravity = _update_attack(f, input, t)
		A_DODGE:
			gravity = _update_dodge(f, t)
		A_IDLE:
			gravity = _handle_control(state, f, input, pressed, rules)

	_apply_physics(f, rules, gravity)
	return false


## A ground slam that has reached the ground stops plunging. Runs after _resolve_hits, so the
## frame it lands on still has its hitbox out; that frame becomes its last active one.
static func _end_plunge_on_landing(f: Fighter, t: FighterTuning) -> void:
	var m := current_move(f, t)
	if f.grounded and m != null and m.plunge_speed != 0 and m.is_active_on(f.action_frame):
		f.action_frame = m.startup + m.active - 1


## The fighter's timers count down, statuses included (so they pause in hit-stop like the rest).
## Poison or burn ticks while it runs, on every frame where (frame + index) is a multiple of its
## row's dotEvery: the phase doesn't depend on the timer, so refreshing it every frame neither stops
## the ticks nor speeds them up.
static func _tick_timers(state: GameState, f: Fighter, index: int, rules: MatchRules) -> void:
	if f.dodge_cooldown > 0:
		f.dodge_cooldown -= 1
	if f.armor_frames > 0:
		f.armor_frames -= 1
	if f.last_hit_timer > 0:
		f.last_hit_timer -= 1
	if f.guts_timer > 0:
		f.guts_timer -= 1
		if f.guts_timer == 0:
			f.josh = maxi(0, f.josh - rules.guts_heal)
			f.armor_frames = rules.guts_armor_frames
	if f.slow_frames > 0:
		f.slow_frames -= 1
	if f.mark_frames > 0:
		f.mark_frames -= 1
	if f.stun_immune > 0:
		f.stun_immune -= 1
	if f.root_frames > 0:
		f.root_frames -= 1
		if f.root_frames == 0:
			f.stun_weapon = 0
	if f.dot_frames > 0:
		if (state.frame + index) % WeaponTable.ROWS[f.dot_weapon][WeaponTable.C_DOT_EVERY] == 0:
			_dot_tick(state, f, index, rules)
		f.dot_frames -= 1
		if f.dot_frames == 0:
			f.dot_by = 0
			f.dot_weapon = 0


## One poison or burn tick: 1 damage, and nothing else a hit does (no knockback, hitstun, hit-stop,
## rage, mark or hit count). It goes through the hidden second wind, can trigger Guts, and goes into
## the poisoner's record. It takes the KO credit only when nobody else holds it, so poison never
## steals a ring-out from whoever launched the victim; a fighter's own poison never takes it.
static func _dot_tick(state: GameState, f: Fighter, index: int, rules: MatchRules) -> void:
	var damage := Weapons.DOT_DAMAGE
	var absorbed := mini(f.shield, damage)
	f.shield -= absorbed
	f.josh = mini(rules.max_josh, f.josh + damage - absorbed)
	var by := f.dot_by - 1
	if by >= 0 and by != index:
		if f.last_hit_timer == 0 or f.last_hit_by == 0 or f.last_hit_by == f.dot_by or f.last_hit_by == index + 1:
			f.last_hit_by = f.dot_by
			f.last_hit_timer = rules.ko_credit_frames
			f.last_hit_weapon = f.dot_weapon
			f.last_hit_sweet = false
		_record_hit(state, by, f.dot_weapon, damage, false)
	if rules.guts and not f.guts_used and f.josh >= rules.guts_josh:
		f.guts_used = true
		f.guts_timer = rules.guts_delay_frames


## A hold (root, stun, freeze) ends on the first frame its fighter is not on the ground: nobody is
## ever held while falling. A parry's stun has no hold weapon and is left alone.
static func _free_if_airborne(f: Fighter) -> void:
	if f.grounded or f.stun_weapon == 0 or not f.is_active():
		return
	_end_hold(f)


## Ends a hold: a root, or a stun, freeze or slip (which leave the fighter idle).
static func _end_hold(f: Fighter) -> void:
	if f.stun_weapon == 0:
		return
	if f.root_frames == 0 and f.action == A_STUNNED:
		f.action = A_IDLE
		f.hitstun_frames = 0
	f.root_frames = 0
	f.stun_weapon = 0


## Movement, then at most one of: a dodge, a heavy, a jab or a jump, in that order of priority.
## A jump pressed on the same frame as an attack or dodge is ignored. Returns whether gravity applies.
static func _handle_control(state: GameState, f: Fighter, input: int, pressed: int, rules: MatchRules) -> bool:
	var t := rules.tuning
	var dir := _horizontal(input)
	if dir != 0:
		f.facing = dir

	var target_vx := dir * (t.run_speed if f.grounded else t.air_speed)
	var carry: int = WeaponTable.ROWS[f.weapon][WeaponTable.C_CARRY]
	if carry != 100:
		target_vx = target_vx * carry / 100 # a heavy weapon slows its carrier
	if f.slow_frames > 0:
		target_vx = target_vx / 2 # slowed: half speed
	if f.root_frames > 0:
		target_vx = 0 # rooted: it can turn, not walk
	f.vel_x = Fx.move_toward(f.vel_x, target_vx, t.ground_accel if f.grounded else t.air_accel)

	var rooted := f.root_frames > 0 # no jump, dodge or recovery either
	if (pressed & Buttons.DODGE) != 0 and not rooted and f.dodge_cooldown == 0 and (f.grounded or not f.air_dodge_used):
		_start_dodge(f, input, t)
		return false

	var move: int = MoveData.Id.NONE
	f.flurry_next = MoveData.Id.NONE
	var both := Buttons.LIGHT | Buttons.HEAVY
	if f.weapon != Weapons.NONE:
		move = _armed_move(f, input, pressed)
	elif rules.flurry and (pressed & both) == both and (input & Buttons.UP) == 0:
		# The fist flurry: the match's own random stream picks it, so every device agrees and
		# nobody can choose the result.
		var roll := _rand_range(state, 0, 4)
		move = MoveData.Id.JAB if roll == 0 or roll == 2 else MoveData.Id.NEUTRAL_HEAVY
		if roll >= 2:
			f.flurry_next = MoveData.Id.NEUTRAL_HEAVY
	else:
		if (pressed & Buttons.HEAVY) != 0:
			move = _heavy_for(f, input, dir)
		if move == MoveData.Id.NONE and (pressed & Buttons.LIGHT) != 0:
			move = MoveData.Id.JAB
	if move == MoveData.Id.NONE:
		if (pressed & Buttons.JUMP) != 0 and not rooted:
			_jump(f, t)
		return true

	f.action = A_ATTACK
	f.move = move
	f.action_frame = 0
	f.attack_connected = false
	f.burst_shots = 0
	return _attack_motion(f, input, t, false)


## Armed, the attack buttons are one button: Light, Heavy or both fire the weapon's attack once,
## or throw it when it's empty. Drop weapon throws it. Up + Heavy stays the recovery. A weapon that
## cycles (the Jugaad Gauntlet) has no attack of its own: each press starts the next of its hidden
## rows, picked by the supply left (pull, punch, bolt, from a full one).
static func _armed_move(f: Fighter, input: int, pressed: int) -> int:
	if (pressed & Buttons.HEAVY) != 0 and (input & Buttons.UP) != 0:
		return MoveData.Id.NONE if f.recovery_used or f.root_frames > 0 else MoveData.Id.UP_HEAVY
	if (pressed & Buttons.DROP_WEAPON) != 0:
		return MoveData.Id.WEAPON_THROW
	if (pressed & (Buttons.LIGHT | Buttons.HEAVY)) == 0:
		return MoveData.Id.NONE
	if f.weapon_supply <= 0:
		return MoveData.Id.WEAPON_THROW
	var row := WeaponTable.ROWS[f.weapon]
	var count: int = row[WeaponTable.C_CYCLE_COUNT]
	if row[WeaponTable.C_USE] == WeaponTable.USE_CYCLE and count > 0:
		return Weapons.move_of(row[WeaponTable.C_CYCLE_FIRST] + (count - f.weapon_supply % count) % count)
	return row[WeaponTable.C_MOVE]


static func _jump(f: Fighter, t: FighterTuning) -> void:
	if f.grounded:
		f.vel_y = t.jump_velocity
		f.grounded = false
	elif f.air_jumps_left > 0:
		f.vel_y = t.air_jump_velocity
		f.air_jumps_left -= 1


## Up: up heavy (nothing if the recovery is spent). Down: down heavy, or the ground slam in the
## air. Left or right: side heavy. Otherwise neutral heavy.
static func _heavy_for(f: Fighter, input: int, dir: int) -> int:
	if (input & Buttons.UP) != 0:
		return MoveData.Id.NONE if f.recovery_used or f.root_frames > 0 else MoveData.Id.UP_HEAVY
	if (input & Buttons.DOWN) != 0:
		return MoveData.Id.DOWN_HEAVY if f.grounded else MoveData.Id.GROUND_SLAM
	return MoveData.Id.SIDE_HEAVY if dir != 0 else MoveData.Id.NEUTRAL_HEAVY


static func _update_attack(f: Fighter, input: int, t: FighterTuning) -> bool:
	var m := t.moves.get_move(f.move)
	f.action_frame += 1
	# A repeating weapon fires again every interval while it has rounds: while an attack button is
	# held, or until the row's burst is out whether held or not (the Burst Rifle's three), and never
	# more than its burstMax in one hold (the Minigun overheats).
	# A spray's stream loops through its tick while a button is held and it has supply left.
	var row := WeaponTable.ROWS[Weapons.row_of_move(f.move)]
	var held := (input & (Buttons.LIGHT | Buttons.HEAVY)) != 0
	if row[WeaponTable.C_USE] == WeaponTable.USE_SPRAY:
		if f.action_frame == m.startup + m.active and f.weapon_supply > 0 and held:
			f.action_frame = m.startup
	elif row[WeaponTable.C_DEFENCE] == WeaponTable.DEFENCE_HOLD:
		# The Parat: while a button is held the tray stays up, waiting on its last startup frame, for
		# at most 90 frames (counted in burst_shots). Then the bash comes out. A tray that has spent its
		# last supply on a block guards nothing, so it isn't held up either.
		if f.action_frame == m.startup and held and f.burst_shots < Weapons.HOLD_MAX_FRAMES and f.weapon_supply > 0:
			f.action_frame = m.startup - 1
			f.burst_shots += 1
	elif row[WeaponTable.C_INTERVAL] > 0 and f.action_frame == m.startup + row[WeaponTable.C_INTERVAL] and f.weapon_supply > 0 \
			and (row[WeaponTable.C_BURST_MAX] == 0 or f.burst_shots < row[WeaponTable.C_BURST_MAX]) \
			and (held or f.burst_shots < row[WeaponTable.C_BURST]):
		f.action_frame = m.startup
	if f.action_frame >= m.total_frames() and f.flurry_next != MoveData.Id.NONE:
		# The flurry's second hit starts the frame after the first one ends: no gap to dodge in.
		f.move = f.flurry_next
		f.flurry_next = MoveData.Id.NONE
		f.action_frame = 0
		f.attack_connected = false
		return _attack_motion(f, input, t, false)
	if f.action_frame >= m.total_frames():
		f.action = A_IDLE
		f.move = MoveData.Id.NONE
		if f.grounded:
			f.vel_x = Fx.move_toward(f.vel_x, 0, t.ground_accel)
		return true
	return _attack_motion(f, input, t, true)


## The current move's motion on its current frame. steer is false on the press frame, which has
## already had normal movement. Returns whether gravity applies.
static func _attack_motion(f: Fighter, input: int, t: FighterTuning, steer: bool) -> bool:
	var m := t.moves.get_move(f.move)
	if steer:
		# Attacks lock steering: friction on the ground, drift kept in the air. Exceptions: a move
		# with air control steers, and a lunge brakes in the air too.
		# Slowed, a walk or an air steer is half as fast; rooted, there is no walking at all.
		if f.grounded and WeaponTable.ROWS[Weapons.row_of_move(f.move)][WeaponTable.C_WALK] != 0: # walk at half speed while firing, without turning
			var walk := _horizontal(input) * t.run_speed / 2
			if f.slow_frames > 0:
				walk = walk / 2
			if f.root_frames > 0:
				walk = 0
			f.vel_x = Fx.move_toward(f.vel_x, walk, t.ground_accel)
		elif f.grounded:
			f.vel_x = Fx.move_toward(f.vel_x, 0, t.ground_accel)
		elif m.air_control:
			var toward := _horizontal(input) * t.air_speed
			if f.slow_frames > 0:
				toward = toward / 2
			f.vel_x = Fx.move_toward(f.vel_x, toward, t.air_accel)
		elif m.lunge_speed != 0 and f.action_frame > m.impulse_frame:
			f.vel_x = Fx.move_toward(f.vel_x, 0, t.air_accel)

	if f.action_frame == m.impulse_frame:
		if m.lunge_speed != 0 and f.root_frames == 0: # a rooted fighter doesn't lunge or kick
			f.vel_x = m.lunge_speed * f.facing
		if m.rise_speed != 0:
			f.vel_y = m.rise_speed
			f.grounded = false
			if m.uses_recovery:
				f.recovery_used = true # here, not at the press: landing would refund it

	if m.plunge_speed != 0:
		if f.action_frame < m.startup:
			f.vel_x = 0 # hang still while winding up
			f.vel_y = 0
			return false
		if m.is_active_on(f.action_frame):
			f.vel_x = 0
			f.vel_y = -m.plunge_speed
			return false
	return true


## Starts a dodge in the held direction. On the ground, left or right dashes and anything else is
## a spot dodge; in the air it dashes in any of 8 directions, or hangs still with none.
static func _start_dodge(f: Fighter, input: int, t: FighterTuning) -> void:
	f.action = A_DODGE
	f.move = MoveData.Id.NONE
	f.action_frame = 0
	f.attack_connected = false

	var dx := _horizontal(input)
	if f.grounded:
		f.vel_x = t.ground_dodge_speed * dx
		f.vel_y = 0
		return

	f.air_dodge_used = true
	var dy := (1 if (input & Buttons.UP) != 0 else 0) - (1 if (input & Buttons.DOWN) != 0 else 0)
	var speed := Fx.mul(t.air_dodge_speed, DIAGONAL) if dx != 0 and dy != 0 else t.air_dodge_speed
	f.vel_x = speed * dx
	f.vel_y = speed * dy


## A dodge moves in a straight line, without gravity, while it can't be hit. Then an airborne
## fighter keeps half that speed and falls; a grounded one slows to a stop.
static func _update_dodge(f: Fighter, t: FighterTuning) -> bool:
	f.action_frame += 1
	if f.action_frame >= t.dodge_total_frames:
		f.action = A_IDLE
		f.dodge_cooldown = t.dodge_cooldown_frames
	elif f.action_frame < t.dodge_intangible_frames:
		return false
	elif f.action_frame == t.dodge_intangible_frames and not f.grounded:
		f.vel_x = f.vel_x / 2
		f.vel_y = f.vel_y / 2

	if f.grounded:
		f.vel_x = Fx.move_toward(f.vel_x, 0, t.ground_accel)
	return true


static func _horizontal(input: int) -> int:
	return (1 if (input & Buttons.RIGHT) != 0 else 0) - (1 if (input & Buttons.LEFT) != 0 else 0)


static func _apply_physics(f: Fighter, rules: MatchRules, gravity: bool) -> void:
	var t := rules.tuning
	if gravity and not f.grounded:
		f.vel_y = maxi(f.vel_y - t.gravity, -t.max_fall_speed)

	var prev_y := f.pos_y
	var next_x := f.pos_x + f.vel_x
	var next_y := f.pos_y + f.vel_y

	# One-way platforms: land when moving down through a top surface. Highest surface wins.
	var landed := false
	var land_y := 0
	if f.vel_y <= 0:
		for p in rules.stage.platforms:
			if next_x < p.min_x or next_x > p.max_x:
				continue
			if prev_y < p.max_y or next_y > p.max_y:
				continue
			if not landed or p.max_y > land_y:
				landed = true
				land_y = p.max_y

	if landed:
		next_y = land_y
		f.vel_y = 0
		f.grounded = true
		f.air_jumps_left = t.air_jumps
		f.air_dodge_used = false
		f.recovery_used = false
	else:
		f.grounded = false

	f.pos_x = next_x
	f.pos_y = next_y


# What a contact of _resolve_hits' collect pass is.
const CONTACT_HIT := 0
const CONTACT_PARRIED := 1
const CONTACT_BLOCKED := 2
const CONTACT_PROP := 3


## Finds every attacker whose active hitbox touches another fighter or a breakable prop, then
## applies them, in this order across all attackers: parries, blocks, prop swings, hits (combat spec
## v0.4, section 1.7). Every contact's kind is decided from the state before any of them is applied,
## so two hits on one frame never depend on the fighters' indices: a guard knocked away by one
## attacker still blocks another's hit of the same frame. Fighters that spent this frame frozen in
## hit-stop don't attack.
static func _resolve_hits(state: GameState, frozen: Array[bool], rules: MatchRules) -> void:
	var fighters := state.fighters
	var props := state.props
	var t := rules.tuning
	var n := fighters.size()

	# Each contact records the move, because applying a parry clears the attacker's move. A prop
	# contact's target is the prop's slot.
	var contact_attacker: PackedInt32Array = []
	var contact_target: PackedInt32Array = []
	var contact_move: PackedInt32Array = []
	var contact_kind: PackedInt32Array = []

	for a in n:
		var atk := fighters[a]
		if atk.attack_connected or frozen[a]:
			continue
		var hitbox := active_hitbox(atk, t)
		if hitbox == null:
			continue

		var atk_row := WeaponTable.ROWS[Weapons.row_of_move(atk.move)]
		if atk_row[WeaponTable.C_USE] == WeaponTable.USE_SPRAY:
			continue # a spray's stream is the sprays pass
		var first: bool = atk_row[WeaponTable.C_FIRST] != 0
		var guard_break: bool = atk_row[WeaponTable.C_GBREAK] != 0
		var ax := atk.pos_x

		# Every breakable prop in the box that isn't the attacker's side's takes a swing (a first
		# row only touches the wall that stops it, below).
		if not first:
			for i in props.size():
				var p := props[i]
				if not Weapons.breakable(p) or (p.owner >= 0 and allies(rules, a, p.owner)):
					continue
				if not hitbox.overlaps(prop_box(p)):
					continue
				contact_attacker.append(a)
				contact_target.append(i)
				contact_move.append(atk.move)
				contact_kind.append(CONTACT_PROP)

		# A row with `first` (the Sniper Rifle) keeps only its nearest contact, by horizontal
		# distance, the lower index on a tie. A dodging fighter was never a contact, so it doesn't
		# shield the fighter behind it; a parrying or guarding one is, and does.
		var first_at := contact_attacker.size()
		var first_dist := 0
		for d in n:
			var def := fighters[d]
			if allies(rules, a, d) or not def.is_active():
				continue # nobody hits a teammate, fists included
			if not hitbox.overlaps(hurtbox(def, t)):
				continue
			var parried := in_parry_window(def, t)
			if not parried and is_intangible(def, t):
				continue # dodged: the attack can still hit later
			if wall_between(state, ax, def.pos_x, hitbox):
				continue # a wall is in the way
			var blocked := not parried and not guard_break and guarding(def) and faces_toward(def, ax)
			if first:
				var dist := absi(def.pos_x - ax)
				if contact_attacker.size() > first_at and dist >= first_dist:
					continue
				# This one is nearer: it takes the place of the one kept so far.
				contact_attacker.resize(first_at)
				contact_target.resize(first_at)
				contact_move.resize(first_at)
				contact_kind.resize(first_at)
				first_dist = dist
			contact_attacker.append(a)
			contact_target.append(d)
			contact_move.append(atk.move)
			contact_kind.append(CONTACT_PARRIED if parried else (CONTACT_BLOCKED if blocked else CONTACT_HIT))

		# A first row stops in the nearest wall on its line when nobody stands before it, and takes a
		# swing off it (unless it is its own side's).
		if first:
			var wall := _nearest_wall(state, ax, hitbox)
			if wall >= 0 and (contact_attacker.size() == first_at or absi(props[wall].pos_x - ax) < first_dist) \
					and Weapons.breakable(props[wall]) and (props[wall].owner < 0 or not allies(rules, a, props[wall].owner)):
				contact_attacker.resize(first_at)
				contact_target.resize(first_at)
				contact_move.resize(first_at)
				contact_kind.resize(first_at)
				contact_attacker.append(a)
				contact_target.append(wall)
				contact_move.append(atk.move)
				contact_kind.append(CONTACT_PROP)

	# Parries before blocks before hits: an attacker parried by one fighter and hit by another on the
	# same frame ends up knocked back, not just stunned.
	for i in contact_attacker.size():
		if contact_kind[i] == CONTACT_PARRIED:
			_apply_parry(fighters[contact_attacker[i]], fighters[contact_target[i]], rules)
	for i in contact_attacker.size():
		if contact_kind[i] == CONTACT_BLOCKED:
			_apply_block(fighters[contact_attacker[i]], fighters[contact_target[i]], rules)
	for i in contact_attacker.size():
		if contact_kind[i] == CONTACT_PROP:
			_swing_prop(state, contact_attacker[i], contact_target[i], contact_move[i])
	for i in contact_attacker.size():
		if contact_kind[i] == CONTACT_HIT:
			_apply_hit(state, contact_attacker[i], contact_target[i], contact_move[i], rules)


## A guard is up: the fighter's move is a guard (or the Parat's held guard) on one of its guarding
## frames, from frame 2 to the end of its startup, and its weapon has supply left (each block costs
## one: a guard that has spent its last blocks nothing more). It covers the side it faces.
static func guarding(f: Fighter) -> bool:
	if f.action != A_ATTACK:
		return false
	var row_id := Weapons.row_of_move(f.move)
	if row_id == 0:
		return false
	var row := WeaponTable.ROWS[row_id]
	var defence: int = row[WeaponTable.C_DEFENCE]
	return (defence == WeaponTable.DEFENCE_GUARD or defence == WeaponTable.DEFENCE_HOLD) \
			and f.action_frame >= Weapons.GUARD_FROM and f.action_frame < row[WeaponTable.C_S] \
			and (f.weapon_supply > 0 or row[WeaponTable.C_SPEND] == WeaponTable.SPEND_NEVER)


## Whether a guard held by f covers something at x: on the side it faces, or exactly where it stands.
static func faces_toward(f: Fighter, x: int) -> bool:
	return x == f.pos_x or signi(x - f.pos_x) == f.facing


## Whether a shot comes at a guard held by f from the front: by the way it moves (towards the guard's
## face), not by where it is, because a fast bullet is often first seen overlapping the guard after it
## has passed the guard's middle. A shot with no sideways speed is judged by where it is.
static func shot_from_front(f: Fighter, p: Prop) -> bool:
	return signi(p.vel_x) == -f.facing if p.vel_x != 0 else faces_toward(f, p.pos_x)


## A wall stands between an attacker at ax and a target at tx: its centre lies between their two x
## positions and its box overlaps the attack's box. Anyone's wall stops a swing, a blast, a spray or a
## pulse.
static func wall_between(state: GameState, ax: int, tx: int, box: FxRect) -> bool:
	var lo := mini(ax, tx)
	var hi := maxi(ax, tx)
	for p in state.props:
		if p.kind != Weapons.PROP_SHOT or p.shot != Weapons.SHOT_WALL:
			continue
		if p.pos_x > lo and p.pos_x < hi and box.overlaps(prop_box(p)):
			return true
	return false


## The wall in a box whose centre is nearest ax (the lower slot on a tie), or -1.
static func _nearest_wall(state: GameState, ax: int, box: FxRect) -> int:
	var props := state.props
	var best := -1
	var best_dist := 0
	for i in props.size():
		var p := props[i]
		if p.kind != Weapons.PROP_SHOT or p.shot != Weapons.SHOT_WALL or not box.overlaps(prop_box(p)):
			continue
		var dist := absi(p.pos_x - ax)
		if best < 0 or dist < best_dist:
			best = i
			best_dist = dist
	return best


## A fighter hidden in a cloud (a zone whose row hides): its middle is inside one. No screen draws it,
## the CPU doesn't see it from more than 2 m, and a homing missile doesn't steer at it. Worked out from
## the props, so it adds no state.
static func is_hidden(state: GameState, rules: MatchRules, fighter: int) -> bool:
	var f := state.fighters[fighter]
	if not f.is_active():
		return false
	var mx := f.pos_x
	var my := f.pos_y + rules.tuning.height / 2
	for p in state.props:
		if p.kind != Weapons.PROP_SHOT or p.shot != Weapons.SHOT_ZONE or WeaponTable.ROWS[Weapons.shot_row(p)][WeaponTable.C_HIDES] == 0:
			continue
		if prop_box(p).contains(mx, my):
			return true
	return false


## A swing or blast blocked by a guard: the attacker's move has connected and it is pushed back, both
## freeze for the shortest hit-stop, and the guard's weapon loses one.
static func _apply_block(atk: Fighter, def: Fighter, rules: MatchRules) -> void:
	atk.attack_connected = true
	atk.vel_x = Weapons.BLOCK_PUSH * def.facing
	atk.hit_stop = maxi(atk.hit_stop, rules.min_hit_stop)
	def.hit_stop = maxi(def.hit_stop, rules.min_hit_stop)
	_guard_cost(def)


## Each block, of a swing, a shot or a burst, costs the guard one of its weapon's supply.
static func _guard_cost(def: Fighter) -> void:
	if def.weapon_supply > 0:
		def.weapon_supply -= 1


## A swing or a blast takes one swing off a breakable prop (counted in ignore_fighter); at its row's
## brk it is gone, without bursting. A row with gbreak breaks it at once.
static func _swing_prop(state: GameState, attacker: int, index: int, move: int) -> void:
	var p := state.props[index]
	state.fighters[attacker].attack_connected = true
	if not Weapons.breakable(p):
		return # another swing of this frame already broke it
	var row := WeaponTable.ROWS[Weapons.shot_row(p)]
	var swung := WeaponTable.ROWS[Weapons.row_of_move(move)]
	var brk: int = row[WeaponTable.C_BRK]
	p.ignore_fighter = brk if swung[WeaponTable.C_GBREAK] != 0 else p.ignore_fighter + 1
	if p.ignore_fighter >= brk:
		p.clear()


## The rage multiplier, in percent, for hits by a fighter at this Josh:
## 100 + rage_percent x min(josh, rage_josh) / rage_josh.
static func rage_multiplier(josh: int, rules: MatchRules) -> int:
	if rules.rage_percent <= 0:
		return 100
	return 100 + mini(josh, rules.rage_josh) * rules.rage_percent / rules.rage_josh


## The attacker is stunned in place and stops rising; the defender's dodge ends at once with its
## air dodge given back; both freeze for the longest hit-stop.
static func _apply_parry(atk: Fighter, def: Fighter, rules: MatchRules) -> void:
	atk.attack_connected = true
	atk.action = A_STUNNED
	atk.move = MoveData.Id.NONE
	atk.flurry_next = MoveData.Id.NONE
	atk.action_frame = 0
	atk.hitstun_frames = rules.tuning.parry_stun_frames
	atk.vel_x = 0
	atk.vel_y = mini(atk.vel_y, 0)
	atk.hit_stop = maxi(atk.hit_stop, rules.max_hit_stop)
	_parry_reward(def, rules)


## The parrying defender's reward, for a fighter's attack or a projectile.
static func _parry_reward(def: Fighter, rules: MatchRules) -> void:
	if def.action == A_DODGE: # only once, if it parries two attackers on one frame
		def.action = A_IDLE
		def.action_frame = 0
		def.air_dodge_used = false
		if not def.grounded:
			def.vel_x = def.vel_x / 2
			def.vel_y = def.vel_y / 2
	def.hit_stop = maxi(def.hit_stop, rules.max_hit_stop)


## A hit about to land: a move's numbers after weapon bonuses (Simulation.Hit).
class Hit:
	var damage := 0
	var percent := 100          # damage multiplier: the signature bonus and sweet spots
	var base_knockback := 0
	var knockback_growth := 0
	var launch_x := 0
	var launch_y := 0
	var hitstun_bonus := 0
	var effect := 0             # what the hit adds (WeaponTable.EFFECT_*) and its number
	var effect_n := 0
	var no_hit_stop := false    # chip hits (Uzi bullets): the target doesn't freeze
	var max_hit_stop := false   # sweet spots always freeze the longest
	var soft := false           # damage and effect only: no knockback, hitstun or hit-stop
	var armor_break := false    # gets past Guts armor
	var weapon := 0             # what it came from (0: fists or a hazard)
	var sweet := false          # a sweet-spot hit

	func _init(m: MoveData) -> void:
		damage = m.damage
		base_knockback = m.base_knockback
		knockback_growth = m.knockback_growth
		launch_x = m.launch_x
		launch_y = m.launch_y
		hitstun_bonus = m.hitstun_bonus
		effect = m.effect
		effect_n = m.effect_n

	## A row's hit flags: chip, soft and armor break.
	func flags(row: PackedInt64Array) -> void:
		no_hit_stop = row[WeaponTable.C_CHIP] != 0
		soft = row[WeaponTable.C_SOFT] != 0
		armor_break = row[WeaponTable.C_ARMORBREAK] != 0

	## A sweet-spot hit: 1.25x damage and base knockback, the longest hit-stop.
	func sweeten() -> void:
		percent = percent * Weapons.SWEET_PERCENT / 100
		base_knockback = base_knockback * Weapons.SWEET_PERCENT / 100
		max_hit_stop = true
		sweet = true


## A fighter's move connects: fists, or a weapon's swing or blast.
static func _apply_hit(state: GameState, attacker: int, target: int, move: int, rules: MatchRules) -> void:
	var fighters := state.fighters
	var atk := fighters[attacker]
	var def := fighters[target]
	atk.attack_connected = true
	var m := rules.tuning.moves.get_move(move)
	var hit := Hit.new(m)

	# Launch away from the attacker. For moves whose hitbox reaches behind the attacker (the up
	# heavy, the slam, the sweep) that depends on which side the target is on.
	var side := atk.facing
	var reaches_behind := m.hits_both_sides or m.hitbox_near < 0
	if reaches_behind and def.pos_x != atk.pos_x:
		side = 1 if def.pos_x > atk.pos_x else -1

	var weapon := Weapons.NONE
	var row_id := Weapons.row_of_move(move)
	if row_id != 0:
		weapon = atk.weapon
		hit.flags(WeaponTable.ROWS[row_id])
		_melee_weapon_hit(atk, def, WeaponTable.ROWS[row_id], hit, rules)
	if weapon != Weapons.NONE and rules.signature_of(attacker) == weapon:
		hit.percent = hit.percent * Weapons.SIGNATURE_PERCENT / 100
	hit.weapon = weapon
	_land_hit(state, attacker, target, hit, side, true, rules)


## A swing's or a blast's row rules. A row that spends per hit loses one. Beyond its far distance a
## blast does its far damage (the shotgun past 3 m). Otherwise its sweet spot: hitting someone in the
## middle of their own attack, the tip (at least so far away), point blank (at most so far), a target
## in the air, or a target facing away from the attacker (from behind). A sweet hit is 1.25x; the
## row may also give it its own launch angle (the bat's flat "six") and extra hitstun (the belan's
## dizzy).
static func _melee_weapon_hit(atk: Fighter, def: Fighter, row: PackedInt64Array, hit: Hit, rules: MatchRules) -> void:
	var hurt := hurtbox(def, rules.tuning)
	var near := hurt.min_x - atk.pos_x if atk.facing > 0 else atk.pos_x - hurt.max_x
	if row[WeaponTable.C_SPEND] == WeaponTable.SPEND_HIT and atk.weapon_supply > 0:
		atk.weapon_supply -= 1

	var sweet := false
	var rule: int = row[WeaponTable.C_SWEET]
	if row[WeaponTable.C_FAR_DMG] > 0 and near > row[WeaponTable.C_FAR_DIST]:
		hit.damage = row[WeaponTable.C_FAR_DMG]
	elif rule == WeaponTable.SWEET_MIDATTACK:
		sweet = def.action == A_ATTACK
	elif rule == WeaponTable.SWEET_TIP:
		sweet = near >= row[WeaponTable.C_SWEET_ARG]
	elif rule == WeaponTable.SWEET_POINTBLANK:
		sweet = near <= row[WeaponTable.C_SWEET_ARG]
	elif rule == WeaponTable.SWEET_AIRBORNE:
		sweet = not def.grounded
	elif rule == WeaponTable.SWEET_BACK:
		sweet = def.facing == (1 if def.pos_x > atk.pos_x else (-1 if def.pos_x < atk.pos_x else atk.facing))
	if row[WeaponTable.C_SWEET_ONLY] != 0 and not sweet:
		hit.effect = WeaponTable.EFFECT_NONE # the Chimta disarms with its tip only
	if not sweet:
		return

	hit.sweeten()
	if row[WeaponTable.C_SWEET_X] != 0 or row[WeaponTable.C_SWEET_Y] != 0:
		hit.launch_x = row[WeaponTable.C_SWEET_X]
		hit.launch_y = row[WeaponTable.C_SWEET_Y]
	hit.hitstun_bonus += row[WeaponTable.C_SWEET_STUN]


## Applies a hit: rage, sweet and signature percent, the mark's 125%, the hidden second wind, Josh,
## the last-hit record and stats, hit-stop (not for chip or soft hits), Guts, the armor check
## (skipped by armor break), the row's effect, and then, unless the hit is soft or a hold took,
## knockback in direction side. Projectiles don't freeze their owner.
static func _land_hit(state: GameState, attacker: int, target: int, hit: Hit, side: int, freeze_attacker: bool, rules: MatchRules) -> void:
	var fighters := state.fighters
	var atk := fighters[attacker]
	var def := fighters[target]

	# Rage: the attacker's own Josh makes its hits stronger (a comeback when close to death).
	var rage := rage_multiplier(atk.josh, rules)
	var damage := hit.damage * rage / 100
	if hit.percent != 100:
		damage = damage * hit.percent / 100
	if def.mark_frames > 0:
		damage = damage * Weapons.MARK_PERCENT / 100 # marked: 25% more from every hit

	# The hidden second wind soaks damage first.
	var absorbed := mini(def.shield, damage)
	def.shield -= absorbed
	def.josh = mini(rules.max_josh, def.josh + damage - absorbed)
	def.last_hit_by = attacker + 1
	def.last_hit_timer = rules.ko_credit_frames
	def.last_hit_weapon = hit.weapon
	def.last_hit_sweet = hit.sweet
	def.hit_count += 1
	if attacker != target:
		_record_hit(state, attacker, hit.weapon, damage, hit.sweet) # for the winner's nickname
	if not hit.no_hit_stop and not hit.soft:
		var stop := rules.max_hit_stop if hit.max_hit_stop else hit_stop_frames(damage, rules)
		if freeze_attacker:
			atk.hit_stop = maxi(atk.hit_stop, stop)
		def.hit_stop = maxi(def.hit_stop, stop)
		def.frozen_by_hit = true

	if rules.guts and not def.guts_used and def.josh >= rules.guts_josh:
		def.guts_used = true
		def.guts_timer = rules.guts_delay_frames

	if def.armor_frames > 0 and not hit.armor_break:
		return # Guts armor: takes the Josh, keeps its footing, gets no status

	var held := hit.effect != WeaponTable.EFFECT_NONE and _apply_effect(state, attacker, target, hit, damage, rules)
	if hit.soft or held:
		return # a soft hit, or a hold instead of knockback

	# A dodge cut short by a hit still costs its cooldown.
	if def.action == A_DODGE:
		def.dodge_cooldown = rules.tuning.dodge_cooldown_frames
	def.root_frames = 0 # a hit with knockback ends any hold
	def.stun_weapon = 0
	if hit.effect == WeaponTable.EFFECT_REEL:
		side = -side # reeled in: launched towards the attacker

	var kb := hit.base_knockback + hit.knockback_growth * def.josh
	if rage != 100:
		kb = kb * rage / 100
	def.vel_x = Fx.mul(kb, hit.launch_x) * side
	def.vel_y = Fx.mul(kb, hit.launch_y)
	def.grounded = false
	def.action = A_HITSTUN
	def.move = MoveData.Id.NONE
	def.flurry_next = MoveData.Id.NONE
	def.action_frame = 0
	def.hitstun_frames = maxi(1, 8 + def.josh / 8 + hit.hitstun_bonus)
	def.air_dodge_used = false # getting hit gives back the air dodge and the recovery
	def.recovery_used = false


## What a hit adds (combat spec v0.4, section 2.4). True when a hold took, which replaces the hit's
## knockback. A hold (root, stun, freeze) takes only on a fighter who is on the ground, not
## stun-immune and not in Guts armor; on one in the air the hit is an ordinary hit and adds slow of
## the same length instead.
static func _apply_effect(state: GameState, attacker: int, target: int, hit: Hit, damage: int, rules: MatchRules) -> bool:
	var def := state.fighters[target]
	var n := hit.effect_n
	var can_hold := def.grounded and def.stun_immune == 0 and def.armor_frames == 0
	match hit.effect:
		WeaponTable.EFFECT_SLOW:
			_add_slow(def, n)
			return false
		WeaponTable.EFFECT_CHILL:
			# Chill builds up as slow; enough of it on someone standing freezes them solid.
			_add_slow(def, n)
			if def.slow_frames < Weapons.FREEZE_AT or not can_hold:
				return false
			_stun(def, Weapons.FREEZE_FRAMES, hit.weapon, rules)
			def.slow_frames = 0
			def.stun_immune = Weapons.FREEZE_IMMUNE_FRAMES
			return true
		WeaponTable.EFFECT_ROOT, WeaponTable.EFFECT_STUN, WeaponTable.EFFECT_SLIP:
			if not can_hold:
				if not def.grounded:
					_add_slow(def, n)
				return false
			if hit.effect == WeaponTable.EFFECT_ROOT:
				def.root_frames = n
				def.stun_weapon = hit.weapon
			elif hit.effect == WeaponTable.EFFECT_SLIP:
				_slip(def, n, hit.weapon, rules)
			else:
				_stun(def, n, hit.weapon, rules)
			def.stun_immune = n + Weapons.HOLD_IMMUNE_FRAMES
			return true
		WeaponTable.EFFECT_DISARM:
			_disarm(state, target)
			return false
		WeaponTable.EFFECT_POISON, WeaponTable.EFFECT_BURN:
			# Never stacks: the longer of the two, credited to the newest.
			def.dot_frames = maxi(def.dot_frames, n)
			def.dot_by = attacker + 1
			def.dot_weapon = hit.weapon
			return false
		WeaponTable.EFFECT_MARK:
			def.mark_frames = maxi(def.mark_frames, n)
			return false
		WeaponTable.EFFECT_LEECH:
			var atk := state.fighters[attacker]
			atk.josh = maxi(0, atk.josh - damage)
			return false
		WeaponTable.EFFECT_SWAP:
			_swap(state, attacker, target, rules)
			return false
	return false


## The swap (the Swap Ray): the attacker (the shot's owner) and the target change places, both
## standing still, if both spots are safe (safe_spot). Otherwise nothing happens but the damage. The
## shooter's own mines and bombs are no part of the test: swapping someone onto them is the trick.
static func _swap(state: GameState, attacker: int, target: int, rules: MatchRules) -> void:
	if attacker == target:
		return
	var a := state.fighters[attacker]
	var b := state.fighters[target]
	if not a.is_active() or not safe_spot(state, rules, a.pos_x, a.pos_y) or not safe_spot(state, rules, b.pos_x, b.pos_y):
		return
	var ax := a.pos_x
	var ay := a.pos_y
	var grounded := a.grounded
	a.pos_x = b.pos_x
	a.pos_y = b.pos_y
	a.grounded = b.grounded
	b.pos_x = ax
	b.pos_y = ay
	b.grounded = grounded
	a.vel_x = 0
	a.vel_y = 0
	b.vel_x = 0
	b.vel_y = 0


static func _add_slow(f: Fighter, frames: int) -> void:
	f.slow_frames = mini(Weapons.SLOW_CAP_FRAMES, f.slow_frames + frames)


## A slip: a stun hold, sliding the way the fighter faces at 0.3 m a frame, which ground friction
## brings to a stop in about 2.3 m. Off a ledge the hold ends (_free_if_airborne).
static func _slip(f: Fighter, frames: int, weapon: int, rules: MatchRules) -> void:
	_stun(f, frames, weapon, rules)
	f.vel_x = Weapons.SLIP_SPEED * f.facing


## The disarm: the target's weapon drops at its feet as a loose weapon with the supply it had,
## ignoring the target for 1 s, and its burst and streak counters are cleared. Nothing happens if it
## has no weapon, an empty one, or no prop slot is free.
static func _disarm(state: GameState, target: int) -> void:
	var def := state.fighters[target]
	if def.weapon == Weapons.NONE:
		return
	if def.weapon_supply <= 0 and WeaponTable.ROWS[def.weapon][WeaponTable.C_SPEND] != WeaponTable.SPEND_NEVER:
		return
	var slot := _free_slot(state, false)
	if slot < 0:
		return
	var p := state.props[slot]
	p.clear()
	p.kind = Weapons.PROP_ITEM
	p.weapon = def.weapon
	p.supply = def.weapon_supply
	p.owner = -1
	p.pos_x = def.pos_x
	p.pos_y = def.pos_y
	p.grounded = def.grounded
	p.ignore_fighter = target + 1
	p.ignore_timer = Weapons.NO_SWAP_BACK_FRAMES
	def.weapon = Weapons.NONE
	def.weapon_supply = 0
	def.burst_shots = 0
	def.streak = 0
	def.streak_target = 0


## A stun hold, as a parried attacker is stunned: move cleared, sideways speed zero, no rising. The
## weapon is kept in stun_weapon, which is how the client draws it.
static func _stun(f: Fighter, frames: int, weapon: int, rules: MatchRules) -> void:
	if f.action == A_DODGE:
		f.dodge_cooldown = rules.tuning.dodge_cooldown_frames
	f.action = A_STUNNED
	f.move = MoveData.Id.NONE
	f.flurry_next = MoveData.Id.NONE
	f.action_frame = 0
	f.hitstun_frames = frames
	f.vel_x = 0
	f.vel_y = mini(f.vel_y, 0)
	f.stun_weapon = weapon


static func _check_blast_zone(state: GameState, index: int, rules: MatchRules) -> void:
	var fighters := state.fighters
	var f := fighters[index]
	if not f.is_active() or rules.stage.blast_zone.contains(f.pos_x, f.pos_y):
		return

	# The KO goes to whoever hit this fighter last, if that was recent; a lone fall scores none,
	# and so does a fall a teammate set up (whatever path set the credit).
	f.falls += 1
	if f.last_hit_timer > 0 and f.last_hit_by > 0 and not allies(rules, f.last_hit_by - 1, index):
		fighters[f.last_hit_by - 1].kos += 1
		var slot := _record_slot(state, f.last_hit_by - 1, f.last_hit_weapon)
		if slot >= 0:
			state.weapon_stats[slot + 2] += 1 # the weapon's KO, if the record still holds it
	f.last_hit_by = 0
	f.last_hit_timer = 0

	var timed := rules.time_limit_frames > 0
	if not timed:
		f.stocks -= 1
	f.vel_x = 0
	f.vel_y = 0
	f.josh = 0
	f.action_frame = 0
	f.move = MoveData.Id.NONE
	f.hit_stop = 0
	f.frozen_by_hit = false
	f.guts_timer = 0 # losing the stock cancels a pending Guts
	f.armor_frames = 0
	f.shield = 0
	f.clear_statuses()
	f.action = A_RESPAWNING if timed or f.stocks > 0 else A_ELIMINATED


static func _check_match_over(state: GameState, rules: MatchRules) -> void:
	if rules.time_limit_frames > 0:
		if state.frame + 1 < rules.time_limit_frames:
			return
		state.match_over = true
		state.winner_index = timed_team_winner(state.fighters, rules) if rules.has_teams() else timed_winner(state.fighters)
		return

	if state.fighters.size() < 2:
		return
	var alive := 0
	var last := -1
	for i in state.fighters.size():
		if state.fighters[i].action == A_ELIMINATED:
			continue
		alive += 1
		last = i
	if alive <= 1:
		state.match_over = true
		state.winner_index = last if alive == 1 else -1


## Where the slot holding this weapon starts in state.weapon_stats, or -1.
static func _record_slot(state: GameState, fighter: int, weapon: int) -> int:
	var from := fighter * Weapons.RECORD_INTS
	for k in Weapons.RECORD_SLOTS:
		if state.weapon_stats[from + k * Weapons.RECORD_SLOT_INTS] == weapon + 1:
			return from + k * Weapons.RECORD_SLOT_INTS
	return -1


## A hit on someone else goes into the attacker's record: the total, and the slot of the weapon it
## is credited to (0: fists). A weapon with no slot takes the first empty one; with none empty it
## takes over the slot with the least damage (the lowest on a tie), cleared.
static func _record_hit(state: GameState, fighter: int, weapon: int, damage: int, sweet: bool) -> void:
	var record := state.weapon_stats
	var from := fighter * Weapons.RECORD_INTS
	record[from + Weapons.RECORD_SLOTS * Weapons.RECORD_SLOT_INTS] += damage
	var slot := _record_slot(state, fighter, weapon)
	if slot < 0:
		slot = from
		for k in Weapons.RECORD_SLOTS:
			var at := from + k * Weapons.RECORD_SLOT_INTS
			if record[at] == 0:
				slot = at
				break
			if record[at + 1] < record[slot + 1]:
				slot = at
		record[slot] = weapon + 1
		record[slot + 1] = 0
		record[slot + 2] = 0
		record[slot + 3] = 0
	record[slot + 1] += damage
	if sweet:
		record[slot + 3] += 1
	state.weapon_stats = record


## A fighter's record with a weapon (0: fists): stat 0 is the damage dealt to others, 1 the KOs,
## 2 the sweet-spot hits. 0 for a weapon the record holds no slot for.
static func weapon_stat(state: GameState, fighter: int, weapon: int, stat: int) -> int:
	var slot := _record_slot(state, fighter, weapon)
	return 0 if slot < 0 else state.weapon_stats[slot + 1 + stat]


## All the damage a fighter has done to others, with anything.
static func damage_dealt(state: GameState, fighter: int) -> int:
	return state.weapon_stats[fighter * Weapons.RECORD_INTS + Weapons.RECORD_SLOTS * Weapons.RECORD_SLOT_INTS]


## A fighter's main weapon in this match, for their nickname: [weapon, master]. The weapon (0 is
## fists) is the one that did the most of their damage (the lower id on a tie), if that is at least
## 40% of all the damage they did; otherwise -1, an all-rounder. Master: they also landed 3 or more
## sweet spots and a KO with it.
static func main_weapon(state: GameState, fighter: int) -> Array:
	var record := state.weapon_stats
	var from := fighter * Weapons.RECORD_INTS
	var best := -1
	var best_weapon := -1
	var best_damage := 0
	for k in Weapons.RECORD_SLOTS:
		var at := from + k * Weapons.RECORD_SLOT_INTS
		if record[at] == 0:
			continue
		var weapon := record[at] - 1
		var damage := record[at + 1]
		if damage > best_damage or (damage == best_damage and damage > 0 and weapon < best_weapon):
			best = at
			best_weapon = weapon
			best_damage = damage
	if best < 0 or best_damage * 100 < damage_dealt(state, fighter) * 40:
		return [-1, false]
	return [best_weapon, record[best + 3] >= 3 and record[best + 2] >= 1]


## The stage's hazards, while active: a launch pad flings up a fighter standing in it, a pusher
## carries everyone in it sideways, and a striker hits everyone in it every Hazards.HIT_EVERY
## frames. A dodging fighter is untouched. Hazard hits credit nobody with a KO.
static func _apply_hazards(state: GameState, rules: MatchRules) -> void:
	var stage := rules.stage
	var fighters := state.fighters
	for k in stage.hazard_kinds.size():
		var at := Hazards.cycle_at(state.frame, k)
		if at < Hazards.ACTIVE_AT or at >= Hazards.COOLDOWN_AT:
			continue
		var zone := stage.hazard_zones[k]
		for i in fighters.size():
			var f := fighters[i]
			if not f.is_active() or is_intangible(f, rules.tuning):
				continue
			if not zone.overlaps(hurtbox(f, rules.tuning)):
				continue
			match stage.hazard_kinds[k]:
				Hazards.LAUNCH:
					if f.grounded and f.action != A_HITSTUN:
						f.vel_y = Hazards.LAUNCH_SPEED
						f.grounded = false
				Hazards.PUSH:
					f.pos_x += Hazards.PUSH_SPEED * stage.hazard_dirs[k]
				Hazards.HIT:
					if (at - Hazards.ACTIVE_AT) % Hazards.HIT_EVERY != 0:
						continue
					var hit := Hit.new(Hazards.strike())
					var side := 1 if f.pos_x * 2 >= zone.min_x + zone.max_x else -1
					_land_hit(state, i, i, hit, side, false, rules)


## The timed match's winner: most KOs, then fewest falls. -1 if the best two are level on both.
static func timed_winner(fighters: Array[Fighter]) -> int:
	var best := -1
	var tied := false
	for i in fighters.size():
		if best < 0:
			best = i
			continue
		var f := fighters[i]
		var b := fighters[best]
		if f.kos > b.kos or (f.kos == b.kos and f.falls < b.falls):
			best = i
			tied = false
		elif f.kos == b.kos and f.falls == b.falls:
			tied = true
	return -1 if tied else best


## A timed team match's winner (rooms v1, section 1.7): the team with the most KOs, then the
## fewest falls, summed over its members (a team-0 fighter is a team of one); -1 if the best two
## teams are level on both. Otherwise that team's MVP: the most KOs, then the fewest falls, then
## the lowest index.
static func timed_team_winner(fighters: Array[Fighter], rules: MatchRules) -> int:
	var n := fighters.size()
	var kos := PackedInt32Array()
	kos.resize(n)
	var falls := PackedInt32Array()
	falls.resize(n)
	var leader := PackedInt32Array()
	leader.resize(n)
	for i in n:
		var j := 0
		while not allies(rules, i, j):
			j += 1 # the lowest index on i's team (i itself at worst)
		leader[i] = j
		kos[j] += fighters[i].kos
		falls[j] += fighters[i].falls

	var best := -1
	var tied := false
	for i in n:
		if leader[i] != i:
			continue
		if best < 0:
			best = i
			continue
		if kos[i] > kos[best] or (kos[i] == kos[best] and falls[i] < falls[best]):
			best = i
			tied = false
		elif kos[i] == kos[best] and falls[i] == falls[best]:
			tied = true
	if best < 0 or tied:
		return -1

	var mvp := -1
	for i in n:
		if leader[i] != best:
			continue
		if mvp < 0 or fighters[i].kos > fighters[mvp].kos or (fighters[i].kos == fighters[mvp].kos and fighters[i].falls < fighters[mvp].falls):
			mvp = i
	return mvp


static func _spawn(f: Fighter, index: int, rules: MatchRules) -> void:
	var stage := rules.stage
	var s := index % stage.spawn_x.size()
	f.pos_x = stage.spawn_x[s]
	f.pos_y = stage.spawn_y[s]
	f.vel_x = 0
	f.vel_y = 0
	f.facing = 1 if f.pos_x <= 0 else -1
	f.grounded = false
	f.air_jumps_left = rules.tuning.air_jumps
	f.josh = 0
	f.action = A_IDLE
	f.move = MoveData.Id.NONE
	f.action_frame = 0
	f.hitstun_frames = 0
	f.attack_connected = false
	f.hit_stop = 0
	f.frozen_by_hit = false
	f.dodge_cooldown = 0
	f.air_dodge_used = false
	f.recovery_used = false
	f.guts_used = false # a new stock gets a new Guts
	f.guts_timer = 0
	f.armor_frames = 0
	f.shield = 0
	f.last_hit_by = 0
	f.last_hit_timer = 0
	f.weapon = Weapons.NONE
	f.weapon_supply = 0
	f.burst_shots = 0
	f.streak = 0
	f.streak_target = 0
	f.flurry_next = MoveData.Id.NONE
	f.clear_statuses()


# ---- Weapons (SimulationWeapons.cs) ---------------------------------------------------------------
# Prop Boxes dropping, picking up and swapping, the weapons' shots, projectiles flying, hitting and
# exploding, the bat batting them back, and a dead fighter's weapon coming back in a box. Runs only
# when rules.weapons is on. Every weapon's numbers come from its row of WeaponTable; this code
# branches on a row's use, its shot's flight and its other columns, never on which weapon it is.


## A prop's box: its position is its bottom centre. A zone or a wall is as wide and as tall as its
## row says; every other prop is a square.
static func prop_box(p: Prop) -> FxRect:
	if p.kind != Weapons.PROP_SHOT:
		return FxRect.new(p.pos_x - Weapons.BOX_HALF, p.pos_y, p.pos_x + Weapons.BOX_HALF, p.pos_y + Weapons.BOX_HALF * 2)
	if p.shot == Weapons.SHOT_ZONE or p.shot == Weapons.SHOT_WALL:
		var row := WeaponTable.ROWS[Weapons.shot_row(p)]
		var half_w: int = row[WeaponTable.C_ZONE_W] / 2
		return FxRect.new(p.pos_x - half_w, p.pos_y, p.pos_x + half_w, p.pos_y + row[WeaponTable.C_ZONE_H])
	var h := Weapons.shot_half(p)
	return FxRect.new(p.pos_x - h, p.pos_y, p.pos_x + h, p.pos_y + h * 2)


static func _rand_range(state: GameState, lo: int, hi: int) -> int:
	var rng := SimRandom.new(state.random_state)
	var v := rng.range_int(lo, hi)
	state.random_state = rng.state
	return v


## Weapon moves act on their startup frame: a throw lets go of the weapon, a blast spends a shell,
## and a row that shoots releases its shot.
static func _fire_shots(state: GameState, frozen: Array[bool], rules: MatchRules) -> void:
	var fighters := state.fighters
	for i in fighters.size():
		var f := fighters[i]
		if frozen[i] or f.action != A_ATTACK:
			continue
		var thrown := f.move == MoveData.Id.WEAPON_THROW
		var row_id := Weapons.row_of_move(f.move)
		if not thrown and row_id == 0:
			continue
		if f.action_frame != rules.tuning.moves.get_move(f.move).startup or f.weapon == Weapons.NONE:
			continue
		var hand_x := f.pos_x + Weapons.HAND_REACH * f.facing
		var hand_y := f.pos_y + Weapons.HAND_HEIGHT
		if row_id == 0:
			# A weapon thrown as a hidden row (the Gas Cylinder, Charpai and Spear) fires that row's
			# shot instead of a plain thrown prop.
			var thrown_as: int = WeaponTable.ROWS[f.weapon][WeaponTable.C_THROWN_AS]
			var placed := false
			if thrown_as > 0:
				var shot := WeaponTable.ROWS[thrown_as]
				placed = _add_shot(state, shot[WeaponTable.C_FLIGHT], thrown_as, i, hand_x, hand_y - shot[WeaponTable.C_SIZE],
						shot[WeaponTable.C_VX] * f.facing, shot[WeaponTable.C_VY], false, shot[WeaponTable.C_FUSE], 0)
			else:
				placed = _add_shot(state, Weapons.SHOT_THROWN, f.weapon, i, hand_x, hand_y - Weapons.THROWN_HALF,
						Weapons.THROW_VX * f.facing, Weapons.THROW_VY, false, 0, 0)
			if not placed:
				continue # no room for it: it stays in the hand
			f.weapon = Weapons.NONE
			f.weapon_supply = 0
			continue
		var row := WeaponTable.ROWS[row_id]
		if row[WeaponTable.C_USE] == WeaponTable.USE_SWING:
			# A swing that is spent per use (the Thunder Hammer, the Gauntlet's punch), hit or miss.
			if row[WeaponTable.C_SPEND] == WeaponTable.SPEND_USE and f.weapon_supply > 0:
				f.weapon_supply -= 1
		elif row[WeaponTable.C_USE] == WeaponTable.USE_BLAST:
			if row[WeaponTable.C_SPEND] == WeaponTable.SPEND_USE and f.weapon_supply > 0:
				f.weapon_supply -= 1
		elif row[WeaponTable.C_USE] == WeaponTable.USE_SPRAY:
			# Each pass through the startup frame is a tick of the stream: it spends one.
			if row[WeaponTable.C_SPEND] == WeaponTable.SPEND_USE and f.weapon_supply > 0:
				f.weapon_supply -= 1
			f.burst_shots += 1
		elif row[WeaponTable.C_USE] == WeaponTable.USE_SHOOT:
			_shoot(state, f, i, row, hand_x, hand_y, rules)
		elif row[WeaponTable.C_USE] == WeaponTable.USE_DETONATE:
			_detonate(state, f, i, row, hand_x, rules)
		elif row[WeaponTable.C_USE] == WeaponTable.USE_BLINK:
			_blink(f, row, rules)
		var fires: int = row[WeaponTable.C_FIRES]
		if fires > 0:
			# A row that also fires another row's shot beside its own attack (the Thunder Hammer's
			# wave), with that row's own numbers.
			var also := WeaponTable.ROWS[fires]
			var also_feet: bool = also[WeaponTable.C_FEET] != 0
			var also_y: int = f.pos_y if also_feet else hand_y - also[WeaponTable.C_SIZE]
			_add_shot(state, also[WeaponTable.C_FLIGHT], fires, i, hand_x, also_y, also[WeaponTable.C_VX] * f.facing, also[WeaponTable.C_VY],
					also_feet and f.grounded, also[WeaponTable.C_FUSE], 0)


## Releases a row's shot: from the hand, or at the feet. A use fires the row's pellets (one, or the
## Ninja Stars' three), each with the upward speed its spread adds. The use is spent once; a weapon
## that leaves the hand goes with the shot, carrying what it has left. A use there is no room for
## fizzles: nothing is spent, and a weapon that would have left the hand stays in it. A fizzled shot
## of a repeating weapon still counts as a shot of its burst, so the burst ends. A gate opens only on
## a safe spot (safe_spot): anywhere else the use fizzles too. A speaker keeps its wielder's facing
## in its supply, and a decoy the weapon it pretends to hold.
static func _shoot(state: GameState, f: Fighter, index: int, row: PackedInt64Array, hand_x: int, hand_y: int, rules: MatchRules) -> void:
	var shot := f.burst_shots # which shot of this burst it is, from 0
	if row[WeaponTable.C_INTERVAL] > 0:
		f.burst_shots += 1
	var feet: bool = row[WeaponTable.C_FEET] != 0
	var leave: bool = row[WeaponTable.C_LEAVE] != 0
	var spend_use: bool = row[WeaponTable.C_SPEND] == WeaponTable.SPEND_USE
	var flight: int = row[WeaponTable.C_FLIGHT]
	var at := _shot_start(state, rules, index, row, hand_x, hand_y)
	if flight == Weapons.SHOT_GATE and not safe_spot(state, rules, at[0], at[1]):
		return # no gate over the pit or by a blast line
	var carried := maxi(0, f.weapon_supply - 1) if leave and spend_use else 0
	if flight == Weapons.SHOT_SPEAKER:
		carried = f.facing
	elif flight == Weapons.SHOT_DECOY:
		carried = Weapons.decoy_looks(state)
	# A hidden row's shot (the Gauntlet's bolt) carries the hidden row's id, so it finds its own
	# numbers; its hits are credited to the weapon (the row's credit).
	var weapon: int = row[WeaponTable.C_ID] if row[WeaponTable.C_CREDIT] != row[WeaponTable.C_ID] else f.weapon
	var placed := false
	for pellet in row[WeaponTable.C_PELLETS]:
		var vy: int = row[WeaponTable.C_VY] + _spread(row, shot, pellet, f.weapon_supply)
		if _add_shot(state, flight, weapon, index, at[0], at[1], row[WeaponTable.C_VX] * f.facing, vy,
				feet and f.grounded, Weapons.start_timer(row), carried):
			placed = true
	if not placed:
		return
	if leave:
		f.weapon = Weapons.NONE # it has left the hand: it comes back, or lies there when it stops
		f.weapon_supply = 0
	elif spend_use and f.weapon_supply > 0:
		f.weapon_supply -= 1


## Where a row's shot starts, [x, y]: from the hand, or at the feet for a row that rolls or is put
## down. A gate opens where the wielder stands. An anvil hangs over its mark (drop_spot).
static func _shot_start(state: GameState, rules: MatchRules, index: int, row: PackedInt64Array, hand_x: int, hand_y: int) -> PackedInt64Array:
	var f := state.fighters[index]
	var flight: int = row[WeaponTable.C_FLIGHT]
	if flight == Weapons.SHOT_DROP:
		return drop_spot(state, rules, index)
	if flight == Weapons.SHOT_GATE:
		return PackedInt64Array([f.pos_x, f.pos_y])
	if row[WeaponTable.C_FEET] != 0:
		return PackedInt64Array([hand_x, f.pos_y])
	return PackedInt64Array([hand_x, hand_y - row[WeaponTable.C_SIZE]])


## Where an anvil appears, [x, y]. Its mark is over the nearest opponent in front of its wielder
## (within 8 m ahead and 9 m up or down, not a teammate, not hidden in a cloud), where they will be
## DROP_LEAD frames on at their speed, but 2 to 6 m ahead; with nobody to aim at, 4 m ahead. It is
## kept at least 2 m inside the side blast lines, and 9 m above the wielder's feet or 2 m under the top
## blast line, whichever is lower (the rule a Prop Box follows), so it is never lost as it appears.
static func drop_spot(state: GameState, rules: MatchRules, index: int) -> PackedInt64Array:
	var f := state.fighters[index]
	var ahead := Weapons.DROP_AHEAD
	var target := -1
	var nearest := 0
	for d in state.fighters.size():
		var t := state.fighters[d]
		if allies(rules, index, d) or not t.is_active():
			continue
		var dx := (t.pos_x - f.pos_x) * f.facing
		if dx <= 0 or dx > Weapons.DROP_SEEK or absi(t.pos_y - f.pos_y) > Weapons.DROP_HEIGHT or is_hidden(state, rules, d):
			continue
		if target < 0 or dx < nearest:
			target = d
			nearest = dx
	if target >= 0:
		var t := state.fighters[target]
		ahead = clampi((t.pos_x + t.vel_x * Weapons.DROP_LEAD - f.pos_x) * f.facing, Weapons.DROP_MIN, Weapons.DROP_MAX)
	var zone := rules.stage.blast_zone
	var x := clampi(f.pos_x + ahead * f.facing, zone.min_x + Weapons.BLAST_MARGIN, zone.max_x - Weapons.BLAST_MARGIN)
	var y := mini(f.pos_y + Weapons.DROP_HEIGHT, zone.max_y - Weapons.BLAST_MARGIN)
	return PackedInt64Array([x, y])


## The Crate Detonator (use detonate), on its startup frame. If the wielder owns any decoy, rigged
## crate or Decoy Crate, every one of them bursts with the Detonator's numbers (its owner too, if they
## stand in it). Otherwise it puts down a rigged crate: a decoy that doesn't spring. Either way the
## press spends one; a crate with no slot for it fizzles.
static func _detonate(state: GameState, f: Fighter, index: int, row: PackedInt64Array, hand_x: int, rules: MatchRules) -> void:
	var props := state.props
	var burst := false
	for k in props.size():
		var q := props[k]
		if q.kind != Weapons.PROP_SHOT or q.shot != Weapons.SHOT_DECOY or q.owner != index:
			continue
		q.weapon = row[WeaponTable.C_ID] # it bursts with the Detonator's numbers, credited to the Detonator
		_explode(state, k, -1, rules)
		burst = true
	if not burst and not _add_shot(state, Weapons.SHOT_DECOY, row[WeaponTable.C_ID], index, hand_x, f.pos_y, 0, 0, f.grounded,
			row[WeaponTable.C_LIFE], Weapons.decoy_looks(state)):
		return
	if f.weapon_supply > 0:
		f.weapon_supply -= 1


## The Teleporter (use blink), on its startup frame: the wielder is put on the landing spot
## blink_spot finds, standing still, with the air jump, air dodge and recovery back as any landing
## gives, and a root ends. It spends one. With no landing spot it fizzles. Up held on this frame looks
## above first (Up + Light; Up + Heavy is still the recovery).
static func _blink(f: Fighter, row: PackedInt64Array, rules: MatchRules) -> void:
	var up := (f.previous_buttons & Buttons.UP) != 0 # this frame's buttons: a fighter not frozen has just stored them
	var spot := blink_spot(rules.stage, f, row[WeaponTable.C_REACH], up)
	if spot.is_empty():
		return
	f.pos_x = spot[0]
	f.pos_y = spot[1]
	f.vel_x = 0
	f.vel_y = 0
	f.grounded = true
	f.air_jumps_left = rules.tuning.air_jumps
	f.air_dodge_used = false
	f.recovery_used = false
	f.root_frames = 0 # a blink ends a root
	f.stun_weapon = 0
	if f.weapon_supply > 0:
		f.weapon_supply -= 1


## The Teleporter's landing spot, [x, y], or empty when there is nowhere to land: the point `reach`
## metres ahead, then 1 m nearer each time down to 2 m, on a platform top within 6 m of the feet (the
## nearest in height, the higher on a tie). `up` looks straight above first, and takes only tops more
## than 0.5 m above the feet.
static func blink_spot(stage: StageDefinition, f: Fighter, reach: int, up: bool) -> PackedInt64Array:
	if up:
		var above := _landing_at(stage, f.pos_x, f.pos_y, true)
		if not above.is_empty():
			return above
	var d := reach
	while d >= Weapons.BLINK_MIN:
		var spot := _landing_at(stage, f.pos_x + d * f.facing, f.pos_y, up)
		if not spot.is_empty():
			return spot
		d -= Fx.ONE
	return PackedInt64Array()


## The platform top at x nearest the feet in height (within 6 m; above them, for an upward blink),
## as [x, y], or empty.
static func _landing_at(stage: StageDefinition, x: int, feet: int, above: bool) -> PackedInt64Array:
	var found := false
	var best := 0
	var top := 0
	for r in stage.platforms:
		if x < r.min_x or x > r.max_x:
			continue
		var rise := r.max_y - feet
		if absi(rise) > Weapons.BLINK_HEIGHT or (above and rise <= Weapons.BLINK_ABOVE):
			continue
		var gap := absi(rise)
		if not found or gap < best or (gap == best and r.max_y > top):
			found = true
			best = gap
			top = r.max_y
	return PackedInt64Array([x, top]) if found else PackedInt64Array()


## A safe spot (combat spec v0.4, section 1.6), for the Portal Disc and the Swap Ray: at least 2 m
## inside the blast zone on every side, a platform top straight below within 4 m, and, with hazards
## on, out of every hazard that is warning or active (a fighter standing there would not touch it).
static func safe_spot(state: GameState, rules: MatchRules, x: int, y: int) -> bool:
	var zone := rules.stage.blast_zone
	var m := Weapons.SAFE_MARGIN
	if x < zone.min_x + m or x > zone.max_x - m or y < zone.min_y + m or y > zone.max_y - m:
		return false
	var top := top_below(rules.stage, x, y)
	if top.is_empty() or y - top[0] > Weapons.SAFE_DROP:
		return false
	if not rules.hazards:
		return true
	var t := rules.tuning
	var body := FxRect.new(x - t.half_width, y, x + t.half_width, y + t.height)
	var stage := rules.stage
	for k in stage.hazard_kinds.size():
		var at := Hazards.cycle_at(state.frame, k)
		if at >= Hazards.WARN_AT and at < Hazards.COOLDOWN_AT and stage.hazard_zones[k].overlaps(body):
			return false
	return true


## The highest platform top straight below a point (or at it), as [top], or empty if there is none.
static func top_below(stage: StageDefinition, x: int, y: int) -> PackedInt64Array:
	var found := false
	var top := 0
	for r in stage.platforms:
		if x < r.min_x or x > r.max_x or r.max_y > y:
			continue
		if not found or r.max_y > top:
			found = true
			top = r.max_y
	return PackedInt64Array([top]) if found else PackedInt64Array()


## Sprays and beams (combat spec v0.4, section 2.5), after _resolve_hits. Every stream is read from
## the state before any of them acts, so two wielders spraying each other don't depend on their
## indices. Each frame a stream's box is out it touches every other active fighter in it who isn't
## dodging or a teammate (only the nearest for a row with first; only armed ones for armedOnly):
## drift moves them sideways (a pull stops 1 m in front; on someone in the air it is capped at 60%
## of air speed, so a gust at a ledge can slow a recovery but never hold it out), and lift floats
## them up. On a tick frame (each pass through the startup frame) the row's hit lands too, within
## tickNear when the row has it, without freezing the wielder. propDrift moves boxes and loose
## weapons, and blow turns round every flying shot of someone else's side that comes at the wielder.
## Sprays never break props.
static func _sprays(state: GameState, frozen: Array[bool], rules: MatchRules) -> void:
	var fighters := state.fighters
	var t := rules.tuning
	var n := fighters.size()
	var who := PackedInt32Array()
	var boxes: Array[FxRect] = []
	var xs := PackedInt64Array()
	var facings := PackedInt32Array()
	var ticks: Array[bool] = []
	var rows := PackedInt32Array()
	for a in n:
		var f := fighters[a]
		if frozen[a]:
			continue
		var row_id := Weapons.row_of_move(f.move)
		if row_id == 0 or WeaponTable.ROWS[row_id][WeaponTable.C_USE] != WeaponTable.USE_SPRAY:
			continue
		var box := active_hitbox(f, t)
		if box == null:
			continue
		who.append(a)
		boxes.append(box)
		xs.append(f.pos_x)
		facings.append(f.facing)
		ticks.append(f.action_frame == WeaponTable.ROWS[row_id][WeaponTable.C_S])
		rows.append(row_id)

	for k in who.size():
		var a := who[k]
		var row := WeaponTable.ROWS[rows[k]]
		var box := boxes[k]
		var ax := xs[k]
		var facing := facings[k]
		var first: bool = row[WeaponTable.C_FIRST] != 0

		var only := -1
		if first:
			var best := 0
			for d in n:
				if not _spray_touches(state, rules, a, d, row, box, ax):
					continue
				var dist := absi(fighters[d].pos_x - ax)
				if only < 0 or dist < best:
					only = d
					best = dist

		for d in n:
			var reached: bool = (d == only) if first else _spray_touches(state, rules, a, d, row, box, ax)
			if not reached:
				continue
			var def := fighters[d]
			var drift: int = row[WeaponTable.C_DRIFT]
			if drift != 0:
				var step := drift
				if not def.grounded:
					var cap := t.air_speed * Weapons.AIR_DRIFT_PERCENT / 100
					step = clampi(step, -cap, cap)
				def.pos_x += _drift_by((def.pos_x - ax) * facing, step) * facing
			var lift: int = row[WeaponTable.C_LIFT]
			if lift != 0:
				if def.vel_y < lift:
					def.vel_y = lift
				def.grounded = false
			if not ticks[k] or (row[WeaponTable.C_DMG] == 0 and row[WeaponTable.C_EFFECT] == WeaponTable.EFFECT_NONE):
				continue
			if row[WeaponTable.C_TICK_NEAR] != 0 and absi(def.pos_x - ax) > row[WeaponTable.C_TICK_NEAR]:
				continue
			var hit := Hit.new(Weapons.move_data_of(rows[k]))
			hit.flags(row)
			var weapon := fighters[a].weapon
			if weapon != Weapons.NONE and rules.signature_of(a) == weapon:
				hit.percent = hit.percent * Weapons.SIGNATURE_PERCENT / 100
			hit.weapon = weapon
			_land_hit(state, a, d, hit, facing, false, rules)

		var prop_drift: int = row[WeaponTable.C_PROP_DRIFT]
		var blow: bool = row[WeaponTable.C_BLOW] != 0
		if prop_drift == 0 and not blow:
			continue
		for p in state.props:
			if p.kind == Weapons.PROP_BOX or p.kind == Weapons.PROP_ITEM:
				if prop_drift == 0 or not box.overlaps(prop_box(p)):
					continue
				p.pos_x += _drift_by((p.pos_x - ax) * facing, prop_drift) * facing
			elif p.kind == Weapons.PROP_SHOT and blow and WeaponTable.FLIES[p.shot]:
				if p.owner >= 0 and allies(rules, a, p.owner):
					continue
				if signi(p.vel_x) != -facing or not box.overlaps(prop_box(p)):
					continue
				p.vel_x = -p.vel_x # blown round: it flies away from the wielder, still its owner's


## How far a spray's drift moves something `ahead` metres in front of the wielder, along the way they
## face: a push all of it, a pull no nearer than 1 m.
static func _drift_by(ahead: int, step: int) -> int:
	if step >= 0:
		return step
	return maxi(Weapons.PULL_STOP, ahead + step) - ahead if ahead > Weapons.PULL_STOP else 0


## A fighter a spray's stream reaches: active, not dodging, not on the wielder's side, in the box
## (armed, for armedOnly), with no wall in between and not holding the Parat's guard towards the
## wielder (a plain guard doesn't stop a stream).
static func _spray_touches(state: GameState, rules: MatchRules, a: int, d: int, row: PackedInt64Array, box: FxRect, ax: int) -> bool:
	var def := state.fighters[d]
	if allies(rules, a, d) or not def.is_active() or is_intangible(def, rules.tuning):
		return false
	if row[WeaponTable.C_ARMED_ONLY] != 0 and def.weapon == Weapons.NONE:
		return false
	if not box.overlaps(hurtbox(def, rules.tuning)):
		return false
	if guarding(def) and WeaponTable.ROWS[Weapons.row_of_move(def.move)][WeaponTable.C_DEFENCE] == WeaponTable.DEFENCE_HOLD and faces_toward(def, ax):
		return false
	return not wall_between(state, ax, def.pos_x, box)


## The upward speed a row's spread adds to one shot: an entry of its list, picked by the row's
## spreadBy. By the shots already fired in this burst: burst goes round the list (the Uzi), hold
## stays on its last entry (the AK's climb), widen goes through it once and then takes its last two
## in turn (the Tommy Gun stays wide). pellet: by the pellet's number. supply: by the supply left,
## going round.
static func _spread(row: PackedInt64Array, shot: int, pellet: int, supply: int) -> int:
	var n: int = row[WeaponTable.C_SPREAD_LEN]
	var k := 0
	match row[WeaponTable.C_SPREAD_BY]:
		WeaponTable.SPREAD_BY_BURST: k = shot % n
		WeaponTable.SPREAD_BY_HOLD: k = mini(shot, n - 1)
		WeaponTable.SPREAD_BY_WIDEN: k = shot if shot < n else maxi(0, n - 2 + (shot - n) % 2)
		WeaponTable.SPREAD_BY_PELLET: k = pellet % n
		WeaponTable.SPREAD_BY_SUPPLY: k = supply % n
		_: return 0
	return WeaponTable.SPREADS[row[WeaponTable.C_SPREAD_AT] + k]


## Puts a shot on the stage. False when there is no slot for it (it fizzles). A placed prop first
## makes room under its owner's limits: a second speaker removes the first, a third gate closes the
## oldest, and at the placed limit their oldest placed prop vanishes without bursting.
static func _add_shot(state: GameState, kind: int, weapon: int, owner: int, at_x: int, at_y: int, vx: int, vy: int, grounded: bool, timer: int, supply: int) -> bool:
	if WeaponTable.PLACED[kind]:
		var own := Weapons.flight_limit(kind)
		if own > 0:
			var found := _oldest_placed(state, owner, kind)
			while found[1] >= own:
				state.props[found[0]].clear()
				found = _oldest_placed(state, owner, kind)
		_make_placed_room(state, owner)
	var slot := _free_slot(state, true, owner)
	if slot < 0:
		return false
	var p := state.props[slot]
	p.clear()
	p.kind = Weapons.PROP_SHOT
	p.weapon = weapon
	p.shot = kind
	p.owner = owner
	p.supply = supply
	p.pos_x = at_x
	p.pos_y = at_y
	p.vel_x = vx
	p.vel_y = vy
	p.grounded = grounded
	p.timer = timer # a fuse, a mine's arming, a pad's life
	return true


## Under a fighter's placed-prop limit, room for one more: at the limit, their oldest vanishes without
## bursting.
static func _make_placed_room(state: GameState, owner: int) -> void:
	var limit := Weapons.placed_limit(state.fighters.size())
	var found := _oldest_placed(state, owner)
	while found[1] >= limit:
		state.props[found[0]].clear()
		found = _oldest_placed(state, owner)


## [the oldest placed prop of a fighter (the lower slot on a tie), or -1; how many they have out];
## of one flight only, when `only` is set.
static func _oldest_placed(state: GameState, owner: int, only := Weapons.SHOT_NONE) -> PackedInt32Array:
	var props := state.props
	var oldest := -1
	var count := 0
	for i in props.size():
		var p := props[i]
		if p.kind != Weapons.PROP_SHOT or p.owner != owner or not WeaponTable.PLACED[p.shot]:
			continue
		if only != Weapons.SHOT_NONE and p.shot != only:
			continue
		count += 1
		if oldest < 0 or p.age > props[oldest].age:
			oldest = i
	return PackedInt32Array([oldest, count])


## An empty prop slot, or -1. A box, a pending box or a loose weapon takes the first empty slot
## anywhere. A shot only uses the slots after the box slots; with none empty it replaces the oldest
## bullet, or else its firer's own oldest placed prop, and never another fighter's.
static func _free_slot(state: GameState, for_shot: bool, owner := -1) -> int:
	var props := state.props
	for i in range(Weapons.box_slots(state.fighters.size()) if for_shot else 0, props.size()):
		if props[i].kind == Weapons.PROP_NONE:
			return i
	if not for_shot:
		return -1
	var oldest := -1
	for i in props.size():
		if props[i].kind != Weapons.PROP_SHOT or props[i].shot != Weapons.SHOT_BULLET:
			continue
		if oldest < 0 or props[i].age > props[oldest].age:
			oldest = i
	return oldest if oldest >= 0 else _oldest_placed(state, owner)[0]


static func _update_props(state: GameState, frozen: Array[bool], rules: MatchRules) -> void:
	var props := state.props
	_swing_props(state, frozen, rules)
	for i in props.size():
		var p := props[i]
		match p.kind:
			Weapons.PROP_BOX, Weapons.PROP_ITEM:
				if p.ignore_timer > 0:
					p.ignore_timer -= 1
					if p.ignore_timer == 0:
						p.ignore_fighter = 0
				_fall(p, rules.stage, Weapons.SHOT_GRAVITY, true)
				p.timer += 1
				if p.timer >= Weapons.ITEM_LIFE_FRAMES or not rules.stage.blast_zone.contains(p.pos_x, p.pos_y):
					p.clear()
			Weapons.PROP_PENDING_BOX:
				p.timer -= 1
				if p.timer <= 0:
					_place_box(state, rules, i, p.weapon, p.supply)
			Weapons.PROP_SHOT:
				_move_shot(state, i, rules)
	_shot_hits(state, frozen, rules)
	_pickups(state, rules)
	_drop_boxes(state, rules)


## Props in a swing (scoop, combat spec v0.4, section 2.7). On its active frames a swing with fling
## (the Hockey Stick) turns every box and loose weapon in its box into a plain thrown shot of the
## swinger's, flying the way they face at the throw speed and carrying the weapon's supply; with pull
## (the Rake) each is moved to 1.2 m in front of them.
static func _swing_props(state: GameState, frozen: Array[bool], rules: MatchRules) -> void:
	var fighters := state.fighters
	for a in fighters.size():
		var f := fighters[a]
		if frozen[a]:
			continue
		var row_id := Weapons.row_of_move(f.move)
		if row_id == 0:
			continue
		var scoop: int = WeaponTable.ROWS[row_id][WeaponTable.C_SCOOP]
		if scoop == WeaponTable.SCOOP_NONE:
			continue
		var box := active_hitbox(f, rules.tuning)
		if box == null:
			continue
		for p in state.props:
			if (p.kind != Weapons.PROP_BOX and p.kind != Weapons.PROP_ITEM) or not box.overlaps(prop_box(p)):
				continue
			if scoop == WeaponTable.SCOOP_PULL:
				p.pos_x = f.pos_x + Weapons.SCOOP_PULL_TO * f.facing
				continue
			var supply := p.supply
			_become(p, Weapons.SHOT_THROWN, WeaponTable.ROWS[p.weapon])
			p.owner = a
			p.supply = supply
			p.vel_x = Weapons.THROW_VX * f.facing
			p.vel_y = Weapons.THROW_VY
			p.grounded = false


## The one way a prop changes what it is (combat spec v0.4, section 3.2): a burst leaving a zone,
## wall or pad; a swing flinging a box; a shot coming to rest. Every field is written, so nothing
## stale from the old role carries over. The prop keeps what it carries, where it is and whether it is
## on the ground, and a shot keeps its owner; everything else starts afresh: a zone, wall or pad lives
## for its row's zoneLife, and flight FLIGHT_NONE makes it a loose weapon.
static func _become(p: Prop, flight: int, row: PackedInt64Array) -> void:
	p.kind = Weapons.PROP_ITEM if flight == Weapons.SHOT_NONE else Weapons.PROP_SHOT
	# weapon, pos_x, pos_y and grounded are kept.
	p.shot = flight
	p.supply = 0
	if flight == Weapons.SHOT_NONE:
		p.owner = -1
	p.owner_clear = false
	p.vel_x = 0
	p.vel_y = 0
	p.timer = row[WeaponTable.C_ZONE_LIFE] if flight == Weapons.SHOT_ZONE or flight == Weapons.SHOT_WALL or flight == Weapons.SHOT_PAD else 0
	p.age = 0
	p.hit_mask = 0
	p.hits = 0
	p.ignore_fighter = 0
	p.ignore_timer = 0


## Gravity and one-way platforms for a prop, the way fighters land.
static func _fall(p: Prop, stage: StageDefinition, gravity: int, platforms: bool) -> void:
	if not p.grounded:
		p.vel_y = maxi(p.vel_y - gravity, -Weapons.MAX_FALL)
	var prev_y := p.pos_y
	var next_x := p.pos_x + p.vel_x
	var next_y := p.pos_y + p.vel_y
	var landed := false
	var land_y := 0
	if platforms and p.vel_y <= 0:
		for r in stage.platforms:
			if next_x < r.min_x or next_x > r.max_x:
				continue
			if prev_y < r.max_y or next_y > r.max_y:
				continue
			if not landed or r.max_y > land_y:
				landed = true
				land_y = r.max_y
	if landed:
		next_y = land_y
		p.vel_y = 0
	p.grounded = landed
	p.pos_x = next_x
	p.pos_y = next_y


static func _move_shot(state: GameState, index: int, rules: MatchRules) -> void:
	var p := state.props[index]
	p.age += 1
	var row := WeaponTable.ROWS[Weapons.shot_row(p)]
	match p.shot:
		Weapons.SHOT_ROLL:
			_fall(p, rules.stage, Weapons.SHOT_GRAVITY, true)
			if row[WeaponTable.C_REHIT] > 0 and p.age % row[WeaponTable.C_REHIT] == 0:
				p.hit_mask = 0 # every so often it forgets who it has hit (the Chicken)
			if p.age >= row[WeaponTable.C_LIFE]:
				if row[WeaponTable.C_REST] == 0:
					p.clear()
					return
				_rest(p, row[WeaponTable.C_SUPPLY]) # it stops and lies there: anyone can use it again
		Weapons.SHOT_BULLET:
			p.pos_x += p.vel_x
			p.pos_y += p.vel_y
			if p.age >= row[WeaponTable.C_LIFE]:
				_miss_bullet(state, p, row)
				p.clear()
				return
		Weapons.SHOT_ARROW:
			p.vel_y = p.vel_y - row[WeaponTable.C_GRAV]
			p.pos_x += p.vel_x
			p.pos_y += p.vel_y
			if p.age >= row[WeaponTable.C_LIFE]:
				p.clear()
				return
		Weapons.SHOT_BOMB, Weapons.SHOT_HOLE:
			p.timer -= 1
			var flying := not p.grounded
			var down := maxi(p.vel_y - row[WeaponTable.C_GRAV], -Weapons.MAX_FALL) # the speed it comes down with
			_fall(p, rules.stage, row[WeaponTable.C_GRAV], true)
			# A bomb that bounces (the Grenade Launcher) counts its floor bounces in hits; after the
			# last it lies and slides like any bomb.
			if p.grounded and flying and p.shot == Weapons.SHOT_BOMB and p.hits < row[WeaponTable.C_BOUNCES]:
				p.hits += 1
				_bounce_off_floor(p, row, down)
			elif p.grounded:
				p.vel_x = Fx.move_toward(p.vel_x, 0, Weapons.GROUND_BRAKE)
			if p.grounded and row[WeaponTable.C_LAND] != 0:
				p.timer = 0 # it bursts where it comes down (the Chakla)
		Weapons.SHOT_LOB:
			# Arcs, lands on platforms and hits whoever it touches. A floor bounce, counted in timer, if
			# it has one left; with none left it is gone when it lands.
			var down := maxi(p.vel_y - row[WeaponTable.C_GRAV], -Weapons.MAX_FALL)
			_fall(p, rules.stage, row[WeaponTable.C_GRAV], true)
			if p.grounded:
				if p.timer >= row[WeaponTable.C_BOUNCES]:
					p.clear()
					return
				p.timer += 1
				_bounce_off_floor(p, row, down)
			if row[WeaponTable.C_LIFE] > 0 and p.age >= row[WeaponTable.C_LIFE]:
				p.clear()
				return
		Weapons.SHOT_BOOMERANG:
			if not _move_boomerang(state, p, row):
				return
		Weapons.SHOT_PAD, Weapons.SHOT_ZONE, Weapons.SHOT_WALL, Weapons.SHOT_SPEAKER, Weapons.SHOT_DECOY:
			# It lies where it was put or left, falling to the platform below, for its life.
			_fall(p, rules.stage, Weapons.SHOT_GRAVITY, true)
			p.timer -= 1
			if p.timer <= 0:
				p.clear()
				return
		Weapons.SHOT_GATE:
			# It floats where it was opened, for its life.
			p.timer -= 1
			if p.timer <= 0:
				p.clear()
				return
		Weapons.SHOT_DROP:
			# It hangs for its fuse, then falls under its own gravity through everyone, and is gone when
			# it lands.
			if p.timer > 0:
				p.timer -= 1
			else:
				_fall(p, rules.stage, row[WeaponTable.C_GRAV], true)
				if p.grounded:
					p.clear()
					return
		Weapons.SHOT_MINE:
			_fall(p, rules.stage, Weapons.SHOT_GRAVITY, true)
			if p.timer > 0:
				p.timer -= 1
		Weapons.SHOT_MISSILE:
			_steer_missile(state, p, row, rules)
			p.pos_x += p.vel_x
			p.pos_y += p.vel_y
		Weapons.SHOT_THROWN:
			_fall(p, rules.stage, Weapons.SHOT_GRAVITY, true)
			if p.grounded:
				_end_shot(p) # a thrown weapon is gone once it lands; a flung one lies there
				return
	if p.kind == Weapons.PROP_NONE:
		return
	if row[WeaponTable.C_EDGE] > 0 and p.ignore_timer < row[WeaponTable.C_EDGE]:
		_edge_bounce(p, rules.stage)
	if not rules.stage.blast_zone.contains(p.pos_x, p.pos_y):
		if p.shot == Weapons.SHOT_BULLET:
			_miss_bullet(state, p, row)
		if p.shot == Weapons.SHOT_THROWN and p.supply > 0:
			# A flung weapon comes back in a box.
			var weapon := p.weapon
			var supply := p.supply
			p.clear()
			p.kind = Weapons.PROP_PENDING_BOX
			p.weapon = weapon
			p.supply = supply
			p.owner = -1
			p.timer = Weapons.RETURN_FRAMES
		else:
			p.clear()


## A shot that stops (it hits, lands, or is destroyed) is gone, unless it is a box or loose weapon
## flung by a swing (a thrown shot that carries supply): that comes to rest where it is, holding what
## it held, so flinging never takes a weapon out of the match.
static func _end_shot(p: Prop) -> void:
	if p.shot == Weapons.SHOT_THROWN and p.supply > 0:
		_rest(p, p.supply)
	else:
		p.clear()


## Off the floor again at the row's keep percent of the speed it came down with, and at least 0.08.
static func _bounce_off_floor(p: Prop, row: PackedInt64Array, down: int) -> void:
	p.vel_y = maxi(-down * row[WeaponTable.C_KEEP] / 100, Weapons.BOUNCE_MIN)
	p.grounded = false


## A screen-edge bounce (edge): a shot that crosses a side of the screen, 1.2 m inside the side blast
## line, is put back on it with its sideways speed reversed, and can hit everyone again. The bounces
## done are kept in ignore_timer.
static func _edge_bounce(p: Prop, stage: StageDefinition) -> void:
	var left := stage.blast_zone.min_x + Weapons.SCREEN_INSET
	var right := stage.blast_zone.max_x - Weapons.SCREEN_INSET
	if p.pos_x < left and p.vel_x < 0:
		p.pos_x = left
	elif p.pos_x > right and p.vel_x > 0:
		p.pos_x = right
	else:
		return
	p.vel_x = -p.vel_x
	p.ignore_timer += 1
	p.hit_mask = 0


## A shot that has bounced off the floor or the side of the screen at least once (the bounced sweet spot).
static func _bounced(p: Prop) -> bool:
	return p.ignore_timer > 0 or (p.shot == Weapons.SHOT_LOB and p.timer > 0) or (p.shot == Weapons.SHOT_BOMB and p.hits > 0)


## A shot comes to rest as a loose weapon anyone can pick up. It keeps whether it was on the ground,
## as the bowling ball always has.
static func _rest(p: Prop, supply: int) -> void:
	_become(p, Weapons.SHOT_NONE, WeaponTable.ROWS[p.weapon])
	p.supply = supply


## The boomerang's flight: out, slowing to a stop (leg 0), then back to its thrower's hand (leg 1),
## able to hit everyone again. Not caught in time, or its thrower gone, it drops and lies there with
## the throws it has left (an empty one is gone). False when it is gone.
static func _move_boomerang(state: GameState, p: Prop, row: PackedInt64Array) -> bool:
	var owned := p.owner >= 0 and p.owner < state.fighters.size() and state.fighters[p.owner].is_active()
	if p.age >= row[WeaponTable.C_LIFE] or (p.timer != 0 and not owned):
		if p.supply > 0:
			_rest(p, p.supply)
			p.grounded = false
		else:
			p.clear()
		return false
	if p.timer == 0:
		p.vel_x = Fx.move_toward(p.vel_x, 0, row[WeaponTable.C_DRAG])
		if p.vel_x == 0:
			p.timer = 1
			p.hit_mask = 0
	else:
		var o := state.fighters[p.owner]
		var dx := o.pos_x - p.pos_x
		var dy: int = o.pos_y + Weapons.HAND_HEIGHT - row[WeaponTable.C_SIZE] - p.pos_y
		p.vel_x = Fx.move_toward(p.vel_x, row[WeaponTable.C_VX] * signi(dx), row[WeaponTable.C_DRAG] * 2)
		p.vel_y = clampi(dy, -Weapons.BOOMERANG_CLIMB, Weapons.BOOMERANG_CLIMB)
	p.pos_x += p.vel_x
	p.pos_y += p.vel_y
	return true


## The missile turns a little each frame towards the nearest fighter who isn't its owner (on a tie,
## the lower index); with nobody to chase it flies on. A row with no turn (the RPG) is a rocket: it
## flies straight.
static func _steer_missile(state: GameState, p: Prop, row: PackedInt64Array, rules: MatchRules) -> void:
	if row[WeaponTable.C_TURN] == 0:
		return
	var fighters := state.fighters
	var cy: int = p.pos_y + row[WeaponTable.C_SIZE]
	var target := -1
	var best := 0
	for d in fighters.size():
		if (p.owner >= 0 and allies(rules, p.owner, d)) or not fighters[d].is_active():
			continue # never its owner or their team
		if is_hidden(state, rules, d):
			continue # nor anyone it can't see in a cloud
		var dist := absi(fighters[d].pos_x - p.pos_x) + absi(fighters[d].pos_y + rules.tuning.height / 2 - cy)
		if target < 0 or dist < best:
			target = d
			best = dist
	if target < 0:
		return
	var dx := fighters[target].pos_x - p.pos_x
	var dy := fighters[target].pos_y + rules.tuning.height / 2 - cy
	var up := 0 if absi(dy) <= Weapons.MISSILE_LEVEL else signi(dy)
	p.vel_x = Fx.move_toward(p.vel_x, row[WeaponTable.C_VX] * signi(dx), row[WeaponTable.C_TURN])
	p.vel_y = Fx.move_toward(p.vel_y, row[WeaponTable.C_VX] * up, row[WeaponTable.C_TURN])


## A bullet of a streak weapon (the Uzi) that hit nobody breaks its shooter's streak.
static func _miss_bullet(state: GameState, p: Prop, row: PackedInt64Array) -> void:
	if row[WeaponTable.C_SWEET] == WeaponTable.SWEET_STREAK and p.hits == 0 and p.owner >= 0 and p.owner < state.fighters.size():
		state.fighters[p.owner].streak = 0


static func _shot_hits(state: GameState, frozen: Array[bool], rules: MatchRules) -> void:
	var fighters := state.fighters
	var props := state.props
	var t := rules.tuning
	var walls := false
	for q in props:
		if q.kind == Weapons.PROP_SHOT and q.shot == Weapons.SHOT_WALL:
			walls = true
			break
	var pulsed := [0] # fighters a speaker has pulsed this frame: one pulse a frame each
	for i in props.size():
		var p := props[i]
		if p.kind != Weapons.PROP_SHOT:
			continue
		var box := prop_box(p)
		var row := WeaponTable.ROWS[Weapons.shot_row(p)]

		# The release grace: it can't hit its owner until it has stopped touching them once.
		if not p.owner_clear and (p.owner < 0 or not fighters[p.owner].is_active() or not box.overlaps(hurtbox(fighters[p.owner], t))):
			p.owner_clear = true

		# A sweep (the Jhaadu) takes every trap, peel, mine and patch in its reach away, whoever owns
		# it, without bursting. Not a cloud.
		if (p.shot == Weapons.SHOT_MINE or (p.shot == Weapons.SHOT_ZONE and row[WeaponTable.C_HIDES] == 0)) and _swept(state, frozen, box, t):
			p.clear()
			continue
		if p.shot == Weapons.SHOT_PAD:
			_bounce(state, p, row, box, rules)
			continue
		if p.shot == Weapons.SHOT_MINE:
			_trip_mine(state, i, row, box, rules)
			continue
		if p.shot == Weapons.SHOT_ZONE:
			_zone_touch(state, p, row, box, rules)
			continue
		if p.shot == Weapons.SHOT_WALL:
			_wall_push(state, box, rules)
			continue
		if p.shot == Weapons.SHOT_SPEAKER:
			if row[WeaponTable.C_REHIT] > 0 and state.frame % row[WeaponTable.C_REHIT] == 0:
				_pulse(state, p, row, rules, pulsed)
			continue
		if p.shot == Weapons.SHOT_GATE:
			_pass_gate(state, i, box, rules)
			continue
		if p.shot == Weapons.SHOT_DECOY:
			if row[WeaponTable.C_TOUCH] != 0:
				_spring(state, i, row, box, rules)
			continue
		if p.shot == Weapons.SHOT_DROP:
			if p.timer == 0:
				_drop_on(state, i, row, box, rules)
			continue
		if not WeaponTable.FLIES[p.shot]:
			continue # nothing else acts on a shot that doesn't fly

		# A breaking swing (the Lathi) knocks a shot of someone else's side out of the air.
		if _broken(state, frozen, p, box, rules):
			_end_shot(p)
			continue

		# A bat swing bats it back: it becomes the batter's, reversed and 1.25x faster. A light bat
		# (the racket's batMax) only returns light shots, nothing that bursts and nothing that does
		# more than its limit, and each return costs it supply (batCost).
		for b in fighters.size():
			var batter := fighters[b]
			var swung := WeaponTable.ROWS[Weapons.row_of_move(batter.move)]
			if frozen[b] or swung[WeaponTable.C_DEFENCE] != WeaponTable.DEFENCE_BAT:
				continue
			var bat := active_hitbox(batter, t)
			if bat == null:
				continue
			if p.owner >= 0 and b != p.owner and allies(rules, b, p.owner):
				continue # never a teammate's shot
			if signi(p.vel_x) == batter.facing or not bat.overlaps(box):
				continue
			if swung[WeaponTable.C_BAT_MAX] > 0:
				var thrown := p.shot == Weapons.SHOT_THROWN
				var damage: int = WeaponTable.ROWS[p.weapon][WeaponTable.C_THROW] if thrown else row[WeaponTable.C_DMG]
				if damage > swung[WeaponTable.C_BAT_MAX] or (not thrown and row[WeaponTable.C_BLAST] != 0):
					continue
			if batter.weapon_supply < swung[WeaponTable.C_BAT_COST]:
				continue
			batter.weapon_supply -= swung[WeaponTable.C_BAT_COST]
			p.owner = b
			p.owner_clear = false
			p.hit_mask = 0
			p.hits = 0
			p.vel_x = absi(p.vel_x) * Weapons.SWEET_PERCENT / 100 * batter.facing
			if p.shot == Weapons.SHOT_BULLET or p.shot == Weapons.SHOT_ARROW:
				p.age = 0
			if p.shot == Weapons.SHOT_BOOMERANG:
				p.timer = 0 # it flies out again, and comes back to the batter
			break

		if walls and _stop_at_wall(state, i, box, rules):
			continue

		if ((p.shot == Weapons.SHOT_BOMB or p.shot == Weapons.SHOT_HOLE) and p.timer <= 0) \
				or (p.shot == Weapons.SHOT_MISSILE and p.age >= row[WeaponTable.C_LIFE]):
			_explode(state, i, -1, rules)
			continue
		if p.shot == Weapons.SHOT_HOLE:
			# It doesn't burst on touch: for the end of its fuse it pulls everyone near it in.
			if p.timer <= row[WeaponTable.C_PULL_FRAMES]:
				_pull(state, p, row, rules)
			continue

		for d in fighters.size():
			var def := fighters[d]
			if not def.is_active() or (p.hit_mask & (1 << d)) != 0:
				continue
			if d == p.owner and not p.owner_clear:
				continue
			if d != p.owner and p.owner >= 0 and allies(rules, p.owner, d):
				continue # it flies through a teammate
			if row[WeaponTable.C_NO_SELF] != 0 and p.owner >= 0 and allies(rules, p.owner, d):
				continue # it never hits its caster (Dark Energy)
			if not box.overlaps(hurtbox(def, t)):
				continue

			# A guard destroys a shot that comes at it from the front (by the way it moves), as a parry
			# does. A bomb or missile is destroyed only where it would have burst on touch.
			var bursts := p.shot == Weapons.SHOT_BOMB or p.shot == Weapons.SHOT_MISSILE
			if guarding(def) and shot_from_front(def, p) and (not bursts or (row[WeaponTable.C_TOUCH] != 0 and d != p.owner)):
				_guard_cost(def)
				_end_shot(p)
				break

			if p.shot == Weapons.SHOT_BOOMERANG and d == p.owner:
				# Back to the hand: caught on the way back, empty-handed. Otherwise it flies past.
				if p.timer == 0 or def.weapon != Weapons.NONE:
					continue
				def.weapon = p.weapon
				def.weapon_supply = p.supply
				def.burst_shots = 0
				def.streak = 0
				def.streak_target = 0
				p.clear()
				break
			if p.shot == Weapons.SHOT_BOMB or p.shot == Weapons.SHOT_MISSILE:
				if row[WeaponTable.C_TOUCH] == 0 or d == p.owner:
					continue # it bursts on touching anyone but its thrower
				_explode(state, i, d, rules)
				break
			if in_parry_window(def, t):
				_parry_reward(def, rules) # parrying a projectile destroys it
				_end_shot(p)
				break
			if is_intangible(def, t):
				continue # dodged: it flies on

			p.hit_mask |= 1 << d
			_shot_hit(state, i, d, row, rules)
			p.hits += 1
			if p.shot == Weapons.SHOT_BULLET or p.shot == Weapons.SHOT_THROWN \
					or ((p.shot == Weapons.SHOT_ARROW or p.shot == Weapons.SHOT_LOB) and row[WeaponTable.C_PIERCE] == 0):
				_end_shot(p) # it stops in the first fighter it hits; only an arrow or a lob that pierces flies on
				break
			if row[WeaponTable.C_MAX_HITS] > 0 and p.hits >= row[WeaponTable.C_MAX_HITS]:
				p.clear() # it fades after its last fighter (the Dark Energy orb's fourth)
				break


## A trampoline bounces up everyone touching it who isn't already rising, its owner too once they
## have stepped off it. It does no damage and credits nobody.
static func _bounce(state: GameState, p: Prop, row: PackedInt64Array, box: FxRect, rules: MatchRules) -> void:
	var fighters := state.fighters
	for d in fighters.size():
		var f := fighters[d]
		if not f.is_active() or is_intangible(f, rules.tuning) or f.action == A_HITSTUN:
			continue
		if d == p.owner and not p.owner_clear:
			continue
		if f.vel_y > 0 or not box.overlaps(hurtbox(f, rules.tuning)):
			continue
		f.vel_y = row[WeaponTable.C_PAD_LAUNCH]
		f.grounded = false


## A speaker's pulse. Every speaker pulses together, on the match clock (each frame that is a multiple
## of its row's rehit): its row's hit on every undodging fighter but its owner and their team whose
## hurtbox touches the box its reach long and 2 m high in front of it, launched the way it faces (its
## supply). A fighter takes one pulse a frame at most, from the speaker in the lowest slot, so two
## speakers never take turns on anyone; a wall in between stops it; and it never pulses at someone in
## the air with no platform anywhere below them, so it can't hold a recovering fighter out.
static func _pulse(state: GameState, p: Prop, row: PackedInt64Array, rules: MatchRules, pulsed: Array) -> void:
	var fighters := state.fighters
	var t := rules.tuning
	var facing := -1 if p.supply < 0 else 1
	var box := FxRect.new(p.pos_x, p.pos_y, p.pos_x + row[WeaponTable.C_REACH] * facing, p.pos_y + Weapons.PULSE_HEIGHT)
	var credit := Weapons.credit_of(p)
	for d in fighters.size():
		var f := fighters[d]
		if (pulsed[0] & (1 << d)) != 0 or not f.is_active() or is_intangible(f, t):
			continue
		if p.owner >= 0 and allies(rules, p.owner, d):
			continue # never its owner or their team
		if not box.overlaps(hurtbox(f, t)):
			continue
		if not f.grounded and top_below(rules.stage, f.pos_x, f.pos_y).is_empty():
			continue # nothing under them: it lets them come back
		if wall_between(state, p.pos_x, f.pos_x, box):
			continue
		pulsed[0] |= 1 << d
		var from := p.owner if p.owner >= 0 else d
		var hit := Hit.new(Weapons.move_data_of(row[WeaponTable.C_ID]))
		hit.flags(row)
		if rules.signature_of(from) == credit:
			hit.percent = hit.percent * Weapons.SIGNATURE_PERCENT / 100
		hit.weapon = credit
		_land_hit(state, from, d, hit, facing, false, rules)


## A gate (the Portal Disc). Two gates with one owner are a pair; one with no partner does nothing. An
## undodging fighter touching a gate that hasn't marked it comes out of the partner: put on the
## partner's spot, its speed zero, its air jump, air dodge and recovery given back. Both gates then
## mark it (a bit in hit_mask), and each clears its mark once the fighter stops touching it, so nobody
## bounces between them. Anyone may use them; the opener only once they have stepped off the gate
## they opened. Shots don't.
static func _pass_gate(state: GameState, index: int, box: FxRect, rules: MatchRules) -> void:
	var props := state.props
	var fighters := state.fighters
	var t := rules.tuning
	var p := props[index]
	var partner := -1
	for i in props.size():
		if i != index and props[i].kind == Weapons.PROP_SHOT and props[i].shot == Weapons.SHOT_GATE and props[i].owner == p.owner:
			partner = i
			break
	for d in fighters.size():
		var f := fighters[d]
		var bit := 1 << d
		if not f.is_active() or not box.overlaps(hurtbox(f, t)):
			p.hit_mask &= ~bit # it has stepped off: it can go through again
			continue
		if (p.hit_mask & bit) != 0 or partner < 0 or is_intangible(f, t):
			continue
		if d == p.owner and not p.owner_clear:
			continue # the opener is standing in it
		f.pos_x = props[partner].pos_x
		f.pos_y = props[partner].pos_y
		f.vel_x = 0
		f.vel_y = 0
		var top := top_below(rules.stage, f.pos_x, f.pos_y)
		f.grounded = not top.is_empty() and top[0] == f.pos_y
		f.air_jumps_left = t.air_jumps
		f.air_dodge_used = false
		f.recovery_used = false
		p.hit_mask |= bit
		props[partner].hit_mask |= bit


## A Decoy Crate springs: the first fighter, other than its owner and their team, who touches it while
## able to take a box (active, not attacking, not dodging) takes its row's hit, launched up and away
## from it, and the decoy is gone. A rigged crate (a row without touch) never springs; only the
## Detonator bursts it.
static func _spring(state: GameState, index: int, row: PackedInt64Array, box: FxRect, rules: MatchRules) -> void:
	var fighters := state.fighters
	var p := state.props[index]
	var owner := p.owner
	for d in fighters.size():
		var f := fighters[d]
		if not f.is_active() or f.action == A_ATTACK or f.action == A_DODGE:
			continue
		if owner >= 0 and allies(rules, owner, d):
			continue
		if not box.overlaps(hurtbox(f, rules.tuning)):
			continue
		var px := p.pos_x
		var credit := Weapons.credit_of(p)
		p.clear()
		var from := owner if owner >= 0 else d
		var hit := Hit.new(Weapons.move_data_of(row[WeaponTable.C_ID]))
		hit.flags(row)
		if rules.signature_of(from) == credit:
			hit.percent = hit.percent * Weapons.SIGNATURE_PERCENT / 100
		hit.weapon = credit
		_land_hit(state, from, d, hit, 1 if f.pos_x >= px else -1, false, rules)
		return


## A falling anvil (flight drop) hits each fighter it passes through once, its owner too once the
## release grace is over, never a teammate of its owner's. Nothing stops it: a dodge, or a parry, only
## saves the fighter.
static func _drop_on(state: GameState, index: int, row: PackedInt64Array, box: FxRect, rules: MatchRules) -> void:
	var fighters := state.fighters
	var t := rules.tuning
	var p := state.props[index]
	for d in fighters.size():
		var f := fighters[d]
		if not f.is_active() or (p.hit_mask & (1 << d)) != 0:
			continue
		if d == p.owner and not p.owner_clear:
			continue
		if d != p.owner and p.owner >= 0 and allies(rules, p.owner, d):
			continue
		if not box.overlaps(hurtbox(f, t)) or is_intangible(f, t):
			continue
		p.hit_mask |= 1 << d
		_shot_hit(state, index, d, row, rules)
		p.hits += 1


## An armed mine bursts on the first fighter touching it, its planter included. One that nobody
## found bursts by itself after 15 s. A trap (a mine with no blast: the Mouse Trap, Venom Trap,
## Banana Peel) instead gives that fighter its row's hit and effect alone, and is gone; one nobody
## found is gone after its life without bursting.
static func _trip_mine(state: GameState, index: int, row: PackedInt64Array, box: FxRect, rules: MatchRules) -> void:
	var p := state.props[index]
	var trap: bool = row[WeaponTable.C_BLAST] == 0
	if p.age >= row[WeaponTable.C_LIFE]:
		if trap:
			p.clear()
		else:
			_explode(state, index, -1, rules)
		return
	if p.timer > 0:
		return
	var fighters := state.fighters
	for d in fighters.size():
		var f := fighters[d]
		if not f.is_active() or is_intangible(f, rules.tuning):
			continue
		if d == p.owner and not p.owner_clear:
			continue
		if d != p.owner and p.owner >= 0 and allies(rules, p.owner, d):
			continue # a teammate doesn't set it off
		if not box.overlaps(hurtbox(f, rules.tuning)):
			continue
		if not trap:
			_explode(state, index, d, rules)
			return
		var owner := p.owner
		var px := p.pos_x
		var credit := Weapons.credit_of(p)
		p.clear()
		var from := owner if owner >= 0 else d
		var hit := Hit.new(Weapons.move_data_of(row[WeaponTable.C_ID]))
		hit.flags(row)
		if rules.signature_of(from) == credit:
			hit.percent = hit.percent * Weapons.SIGNATURE_PERCENT / 100
		hit.weapon = credit
		_land_hit(state, from, d, hit, 1 if f.pos_x >= px else -1, false, rules)
		return


## The black hole drags everyone within reach sideways towards it.
static func _pull(state: GameState, p: Prop, row: PackedInt64Array, rules: MatchRules) -> void:
	var fighters := state.fighters
	var cy: int = p.pos_y + row[WeaponTable.C_SIZE]
	var reach: int = row[WeaponTable.C_REACH]
	var pull: int = row[WeaponTable.C_PULL]
	for d in fighters.size():
		var f := fighters[d]
		if not f.is_active() or is_intangible(f, rules.tuning):
			continue
		var dx := p.pos_x - f.pos_x
		if absi(dx) > reach or absi(f.pos_y + rules.tuning.height / 2 - cy) > reach:
			continue
		f.pos_x += clampi(dx, -pull, pull)


## A projectile hits a fighter, its owner included, with its row's numbers (a thrown weapon: the
## throw's, and its row's throw damage). Sweet spots, never on yourself, by the row's rule: a second
## and later fighter ("Strike!"), a shot that flew long enough, a boomerang on its way back, a
## thrown weapon on an airborne target. A streak weapon (the Uzi) instead does 1 more damage, with
## no hitstun penalty, from so many hits in a row on one target.
static func _shot_hit(state: GameState, index: int, target: int, row: PackedInt64Array, rules: MatchRules) -> void:
	var p := state.props[index]
	var fighters := state.fighters
	var owner := p.owner if p.owner >= 0 else target
	var is_self := owner == target
	var hit: Hit
	if p.shot == Weapons.SHOT_THROWN:
		hit = Hit.new(rules.tuning.moves.get_move(MoveData.Id.WEAPON_THROW))
		hit.damage = WeaponTable.ROWS[p.weapon][WeaponTable.C_THROW]
		if not is_self and not fighters[target].grounded:
			hit.sweeten()
	else:
		hit = Hit.new(Weapons.move_data_of(row[WeaponTable.C_ID]))
		hit.flags(row)
		var rule: int = row[WeaponTable.C_SWEET]
		if not is_self and rule == WeaponTable.SWEET_STREAK:
			var shooter := fighters[owner]
			if shooter.streak_target == target + 1:
				shooter.streak += 1
			else:
				shooter.streak_target = target + 1
				shooter.streak = 1
			if shooter.streak >= row[WeaponTable.C_SWEET_ARG]:
				hit.damage += 1 # keeping on target: the stream holds them
				hit.hitstun_bonus = 0
				hit.sweet = true
		elif not is_self and ((rule == WeaponTable.SWEET_STRIKE and p.hits >= 1) \
				or (rule == WeaponTable.SWEET_LONGSHOT and p.age >= row[WeaponTable.C_SWEET_ARG]) \
				or (rule == WeaponTable.SWEET_RETURN and p.timer != 0) \
				or (rule == WeaponTable.SWEET_BOUNCED and _bounced(p))):
			hit.sweeten()
		if row[WeaponTable.C_SWEET_ONLY] != 0 and not hit.sweet:
			hit.effect = WeaponTable.EFFECT_NONE
	var credit: int = WeaponTable.ROWS[p.weapon][WeaponTable.C_CREDIT]
	if rules.signature_of(owner) == credit:
		hit.percent = hit.percent * Weapons.SIGNATURE_PERCENT / 100
	hit.weapon = credit
	var side := -1 if p.vel_x < 0 else 1
	if p.shot == Weapons.SHOT_DROP:
		side = 1 if fighters[target].pos_x >= p.pos_x else -1 # it falls straight: away from its middle
	_land_hit(state, owner, target, hit, side, false, rules)


## A bomb, mine, missile or black hole bursts: a box as wide as its row's blast that hits everyone in
## it, its thrower too, launching them away from its centre. A fighter it burst on is a direct hit
## (a sweet spot, for a row that has it). A guard whose front faces the centre takes nothing. A row
## with no damage (the cloud capsule, the thrown charpai) only opens. The burst breaks every breakable
## prop it covers that isn't its side's (walls ignore bursts, and break). Then a row that leaves
## something does not vanish: it becomes that zone, wall or pad where it burst (_become), a placed
## prop of its owner's from then on.
static func _explode(state: GameState, index: int, direct: int, rules: MatchRules) -> void:
	var p := state.props[index]
	var row := WeaponTable.ROWS[Weapons.shot_row(p)]
	var credit: int = WeaponTable.ROWS[p.weapon][WeaponTable.C_CREDIT]
	var px := p.pos_x
	var py := p.pos_y
	var owner := p.owner
	var left := Weapons.left_flight(row)
	var saved: Prop = null
	if left != Weapons.SHOT_NONE:
		saved = Prop.new()
		saved.copy_from(p)
	p.clear()
	var fighters := state.fighters
	var cy: int = py + row[WeaponTable.C_SIZE]
	var half: int = row[WeaponTable.C_BLAST]
	var burst := Weapons.move_data_of(row[WeaponTable.C_ID])
	var direct_sweet: bool = row[WeaponTable.C_SWEET] == WeaponTable.SWEET_DIRECT
	var blast := FxRect.new(px - half, cy - half, px + half, cy + half)
	if row[WeaponTable.C_DMG] > 0:
		for d in fighters.size():
			var def := fighters[d]
			if not def.is_active() or is_intangible(def, rules.tuning):
				continue
			if d != owner and owner >= 0 and allies(rules, owner, d):
				continue # it hurts its thrower, never their team
			if not blast.overlaps(hurtbox(def, rules.tuning)):
				continue
			if guarding(def) and faces_toward(def, px):
				_guard_cost(def) # a burst in front of a guard doesn't hit it
				continue
			var from := owner if owner >= 0 else d
			var hit := Hit.new(burst)
			hit.flags(row)
			if direct_sweet and d == direct and from != d:
				hit.sweeten()
			if row[WeaponTable.C_SWEET_ONLY] != 0 and not hit.sweet:
				hit.effect = WeaponTable.EFFECT_NONE
			if rules.signature_of(from) == credit:
				hit.percent = hit.percent * Weapons.SIGNATURE_PERCENT / 100
			hit.weapon = credit
			var side := 1 if def.pos_x >= px else -1
			_land_hit(state, from, d, hit, side, false, rules)

	if half > 0:
		for q in state.props:
			if not Weapons.breakable(q) or (owner >= 0 and q.owner >= 0 and allies(rules, owner, q.owner)):
				continue
			if blast.overlaps(prop_box(q)):
				q.clear()

	if left == Weapons.SHOT_NONE:
		return
	if WeaponTable.PLACED[left] and owner >= 0:
		_make_placed_room(state, owner)
	p.copy_from(saved)
	_become(p, left, row)


## A sweeping swing (defence sweep, the Jhaadu) on an active frame covers this box.
static func _swept(state: GameState, frozen: Array[bool], box: FxRect, t: FighterTuning) -> bool:
	var fighters := state.fighters
	for b in fighters.size():
		var row_id := Weapons.row_of_move(fighters[b].move)
		if frozen[b] or row_id == 0 or WeaponTable.ROWS[row_id][WeaponTable.C_DEFENCE] != WeaponTable.DEFENCE_SWEEP:
			continue
		var sweep := active_hitbox(fighters[b], t)
		if sweep != null and sweep.overlaps(box):
			return true
	return false


## A breaking swing (defence break, the Lathi) on an active frame covers a flying shot of someone
## else's side.
static func _broken(state: GameState, frozen: Array[bool], p: Prop, box: FxRect, rules: MatchRules) -> bool:
	var fighters := state.fighters
	for b in fighters.size():
		var row_id := Weapons.row_of_move(fighters[b].move)
		if frozen[b] or row_id == 0 or WeaponTable.ROWS[row_id][WeaponTable.C_DEFENCE] != WeaponTable.DEFENCE_BREAK:
			continue
		if p.owner >= 0 and allies(rules, b, p.owner):
			continue
		var spin := active_hitbox(fighters[b], rules.tuning)
		if spin != null and spin.overlaps(box):
			return true
	return false


## A flying shot that touches a wall stops: a bullet, arrow, lob or thrown weapon is gone (a flung one
## lies there), a bomb, missile or black hole bursts, a rolling shot turns round, and a boomerang
## starts its way back (one already coming back drops). A shot passes its own side's walls, so a wall
## is cover to shoot from, not a trap for its thrower. True when the shot is gone or has burst.
static func _stop_at_wall(state: GameState, index: int, box: FxRect, rules: MatchRules) -> bool:
	var p := state.props[index]
	var wall: Prop = null
	for q in state.props:
		if q.kind != Weapons.PROP_SHOT or q.shot != Weapons.SHOT_WALL or not box.overlaps(prop_box(q)):
			continue
		if p.owner >= 0 and q.owner >= 0 and allies(rules, p.owner, q.owner):
			continue
		wall = q
		break
	if wall == null:
		return false
	var toward := wall.pos_x - p.pos_x
	match p.shot:
		Weapons.SHOT_BOMB, Weapons.SHOT_MISSILE, Weapons.SHOT_HOLE:
			_explode(state, index, -1, rules)
			return true
		Weapons.SHOT_ROLL:
			if signi(p.vel_x) == signi(toward):
				p.vel_x = -p.vel_x
			return false
		Weapons.SHOT_BOOMERANG:
			if p.timer == 0:
				p.timer = 1
				p.hit_mask = 0
				p.vel_x = 0
				return false
			if signi(p.vel_x) != signi(toward):
				return false # going away from it
			if p.supply > 0:
				_rest(p, p.supply)
				p.grounded = false
			else:
				p.clear()
			return true
		Weapons.SHOT_BULLET:
			_miss_bullet(state, p, WeaponTable.ROWS[Weapons.shot_row(p)])
			p.clear()
			return true
	_end_shot(p)
	return true


## A zone works on every undodging fighter touching it, its owner and their team included (a puddle is
## a puddle), with its row's effect in its standing form: slow keeps them slowed, poison keeps them
## poisoned (credited to the zone's owner; the ticks keep their own rhythm), and slip takes a grounded
## fighter who crosses it faster than 0.05 m a frame off their feet.
static func _zone_touch(state: GameState, p: Prop, row: PackedInt64Array, box: FxRect, rules: MatchRules) -> void:
	var credit := Weapons.credit_of(p)
	var n: int = row[WeaponTable.C_EFFECT_N]
	var effect: int = row[WeaponTable.C_EFFECT]
	for f in state.fighters:
		if not f.is_active() or is_intangible(f, rules.tuning) or not box.overlaps(hurtbox(f, rules.tuning)):
			continue
		match effect:
			WeaponTable.EFFECT_SLOW:
				f.slow_frames = maxi(f.slow_frames, n)
			WeaponTable.EFFECT_POISON, WeaponTable.EFFECT_BURN:
				f.dot_frames = maxi(f.dot_frames, n)
				f.dot_by = p.owner + 1 if p.owner >= 0 else 0
				f.dot_weapon = credit
			WeaponTable.EFFECT_SLIP:
				if not f.grounded or absi(f.vel_x) <= Weapons.SLIP_RUN_SPEED or f.stun_immune > 0 or f.armor_frames > 0:
					continue
				if f.action == A_HITSTUN or f.action == A_STUNNED:
					continue
				_slip(f, n, credit, rules)
				f.stun_immune = n + Weapons.HOLD_IMMUNE_FRAMES


## A wall moves every undodging fighter overlapping it out sideways, towards its nearer side, by at
## most 0.2 m a frame, and stops them moving into it. It is 1.6 m high: a jump clears it.
static func _wall_push(state: GameState, wall: FxRect, rules: MatchRules) -> void:
	var t := rules.tuning
	var centre := (wall.min_x + wall.max_x) / 2
	for f in state.fighters:
		if not f.is_active() or is_intangible(f, t) or not wall.overlaps(hurtbox(f, t)):
			continue
		if f.pos_x < centre:
			f.pos_x += maxi(wall.min_x - t.half_width - f.pos_x, -Weapons.WALL_PUSH)
			if f.vel_x > 0:
				f.vel_x = 0
		else:
			f.pos_x += mini(wall.max_x + t.half_width - f.pos_x, Weapons.WALL_PUSH)
			if f.vel_x < 0:
				f.vel_x = 0


## Touch to take: a box or loose weapon goes to the fighter touching it (nearest its centre; on a
## tie, the lower index). Their old weapon drops where they stand and ignores them for 1 s.
## Fighters mid-attack don't pick up.
static func _pickups(state: GameState, rules: MatchRules) -> void:
	var fighters := state.fighters
	var props := state.props
	for i in props.size():
		var p := props[i]
		if p.kind != Weapons.PROP_BOX and p.kind != Weapons.PROP_ITEM:
			continue
		var box := prop_box(p)
		var cy := p.pos_y + Weapons.BOX_HALF
		var best := -1
		var best_dist := 0
		for d in fighters.size():
			var f := fighters[d]
			if not f.is_active() or f.action == A_ATTACK or p.ignore_fighter == d + 1:
				continue
			if not box.overlaps(hurtbox(f, rules.tuning)):
				continue
			var dist := absi(f.pos_x - p.pos_x) + absi(f.pos_y + rules.tuning.height / 2 - cy)
			if best < 0 or dist < best_dist:
				best = d
				best_dist = dist
		if best < 0:
			continue

		var taker := fighters[best]
		var weapon := p.weapon
		var supply := p.supply
		p.clear()
		if taker.weapon != Weapons.NONE and (taker.weapon_supply > 0 or WeaponTable.ROWS[taker.weapon][WeaponTable.C_SPEND] == WeaponTable.SPEND_NEVER):
			p.kind = Weapons.PROP_ITEM
			p.weapon = taker.weapon
			p.supply = taker.weapon_supply
			p.owner = -1
			p.pos_x = taker.pos_x
			p.pos_y = taker.pos_y
			p.grounded = taker.grounded
			p.ignore_fighter = best + 1
			p.ignore_timer = Weapons.NO_SWAP_BACK_FRAMES
		taker.weapon = weapon
		taker.weapon_supply = supply
		taker.burst_shots = 0
		taker.streak = 0
		taker.streak_target = 0


## Drops a new Prop Box when one is due and there's room under the cap.
static func _drop_boxes(state: GameState, rules: MatchRules) -> void:
	var on_stage := 0
	for p in state.props:
		if p.kind == Weapons.PROP_BOX or p.kind == Weapons.PROP_ITEM or p.kind == Weapons.PROP_PENDING_BOX:
			on_stage += 1
	if on_stage >= Weapons.cap(state.fighters.size()):
		return # the timer waits while the cap is full

	if state.prop_timer > 0:
		state.prop_timer -= 1
	if state.prop_timer > 0:
		return
	var slot := _free_slot(state, false)
	if slot < 0:
		return

	var weapon := _pick_crate(state)
	_place_box(state, rules, slot, weapon, Weapons.supply_of(weapon))
	state.last_box_weapon = weapon
	state.prop_timer = _rand_range(state, Weapons.BOX_INTERVAL_MIN, Weapons.BOX_INTERVAL_MAX)


## Any weapon a crate can hold but the last box's, by weight: a row's share of crates is its weight
## (2, or 1 for the utility rows, which so come half as often). One draw a box.
static func _pick_crate(state: GameState) -> int:
	var list := Weapons.crate_list()
	var total := 0
	for w in list:
		if w != state.last_box_weapon:
			total += WeaponTable.ROWS[w][WeaponTable.C_WEIGHT]
	if total <= 0:
		return list[0] # a list of one: there is nothing else to pick
	var r := _rand_range(state, 0, total)
	for w in list:
		if w == state.last_box_weapon:
			continue
		r -= WeaponTable.ROWS[w][WeaponTable.C_WEIGHT]
		if r < 0:
			return w
	return list[list.size() - 1]


## A box above a random point on a platform (chosen by width, at least 0.5 m from its ends), 6 m
## up, so it lands quickly.
static func _place_box(state: GameState, rules: MatchRules, slot: int, weapon: int, supply: int) -> void:
	var platforms := rules.stage.platforms
	var min_width := Weapons.EDGE_MARGIN * 2 + Fx.ONE
	var total := 0
	for r in platforms:
		var w := r.max_x - r.min_x
		if w >= min_width:
			total += w >> 12
	var chosen := 0
	if total > 0:
		var roll := _rand_range(state, 0, total)
		for i in platforms.size():
			var w := platforms[i].max_x - platforms[i].min_x
			if w < min_width:
				continue
			roll -= w >> 12
			if roll < 0:
				chosen = i
				break
	var plat := platforms[chosen]
	var span := plat.max_x - plat.min_x - Weapons.EDGE_MARGIN * 2
	var x := plat.min_x + Weapons.EDGE_MARGIN + _rand_range(state, 0, span) if span > 0 else (plat.min_x + plat.max_x) / 2
	var y := mini(plat.max_y + Weapons.BOX_DROP_HEIGHT, rules.stage.blast_zone.max_y - Fx.from_int(2))
	var p := state.props[slot]
	p.clear()
	p.kind = Weapons.PROP_BOX
	p.weapon = weapon
	p.supply = supply
	p.owner = -1
	p.pos_x = x
	p.pos_y = y


## A fighter leaving the blast zone with a weapon: it comes back in a box 1 s later, with the ammo
## or durability it had left (an empty one doesn't come back).
static func _return_on_fall(state: GameState, index: int, rules: MatchRules) -> void:
	var f := state.fighters[index]
	if not f.is_active() or f.weapon == Weapons.NONE or rules.stage.blast_zone.contains(f.pos_x, f.pos_y):
		return
	if f.weapon_supply > 0 or WeaponTable.ROWS[f.weapon][WeaponTable.C_SPEND] == WeaponTable.SPEND_NEVER:
		var slot := _free_slot(state, false)
		if slot >= 0:
			var p := state.props[slot]
			p.clear()
			p.kind = Weapons.PROP_PENDING_BOX
			p.weapon = f.weapon
			p.supply = f.weapon_supply
			p.owner = -1
			p.timer = Weapons.RETURN_FRAMES
	f.weapon = Weapons.NONE
	f.weapon_supply = 0
