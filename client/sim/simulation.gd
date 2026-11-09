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
const SIM_VERSION := 1

const DIAGONAL := 46340 # Fx.ratio(7071, 10000), ~sin(45 deg)

const A_IDLE := Fighter.Action.IDLE
const A_ATTACK := Fighter.Action.ATTACK
const A_DODGE := Fighter.Action.DODGE
const A_HITSTUN := Fighter.Action.HITSTUN
const A_STUNNED := Fighter.Action.STUNNED
const A_RESPAWNING := Fighter.Action.RESPAWNING
const A_ELIMINATED := Fighter.Action.ELIMINATED


static func create_initial_state(rules: MatchRules, fighter_count: int, seed: int) -> GameState:
	assert(fighter_count >= 1 and fighter_count <= 8, "1-8 fighters (GDD B3).")
	var state := GameState.new(fighter_count, seed)
	for i in fighter_count:
		var f := state.fighters[i]
		f.stocks = rules.stocks
		_spawn(f, i, rules)
	return state


## Advances the match one frame. inputs[i] are fighter i's buttons (see Buttons).
static func advance(state: GameState, rules: MatchRules, inputs: PackedInt32Array) -> void:
	assert(inputs.size() == state.fighters.size(), "Expected one input per fighter.")
	if not state.match_over:
		var fighters := state.fighters
		var frozen: Array[bool] = []
		frozen.resize(fighters.size())
		for i in fighters.size():
			frozen[i] = _update_fighter(fighters[i], i, inputs[i], rules)
		_resolve_hits(fighters, frozen, rules)
		for f in fighters:
			_end_plunge_on_landing(f, rules.tuning)
			_check_blast_zone(f, rules)
		_check_match_over(state)
	state.frame += 1


## Hit-stop for a hit of this much damage: 2 + damage / 2, clamped to the rules' range.
static func hit_stop_frames(damage: int, rules: MatchRules) -> int:
	return clampi(2 + damage / 2, rules.min_hit_stop, rules.max_hit_stop)


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
static func _update_fighter(f: Fighter, index: int, input: int, rules: MatchRules) -> bool:
	var t := rules.tuning

	if f.action == A_ELIMINATED or f.action == A_RESPAWNING:
		f.previous_buttons = input
		if f.action == A_RESPAWNING:
			f.action_frame += 1
			if f.action_frame >= rules.respawn_frames:
				_spawn(f, index, rules)
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
	_tick_timers(f, rules)

	var gravity := true
	match f.action:
		A_HITSTUN, A_STUNNED:
			f.hitstun_frames -= 1
			if f.hitstun_frames <= 0:
				f.action = A_IDLE
			if f.grounded:
				f.vel_x = Fx.move_toward(f.vel_x, 0, t.ground_accel)
		A_ATTACK:
			gravity = _update_attack(f, input, t)
		A_DODGE:
			gravity = _update_dodge(f, t)
		A_IDLE:
			gravity = _handle_control(f, input, pressed, t)

	_apply_physics(f, rules, gravity)
	return false


## A ground slam that has reached the ground stops plunging. Runs after _resolve_hits, so the
## frame it lands on still has its hitbox out; that frame becomes its last active one.
static func _end_plunge_on_landing(f: Fighter, t: FighterTuning) -> void:
	var m := current_move(f, t)
	if f.grounded and m != null and m.plunge_speed != 0 and m.is_active_on(f.action_frame):
		f.action_frame = m.startup + m.active - 1


static func _tick_timers(f: Fighter, rules: MatchRules) -> void:
	if f.dodge_cooldown > 0:
		f.dodge_cooldown -= 1
	if f.armor_frames > 0:
		f.armor_frames -= 1
	if f.guts_timer > 0:
		f.guts_timer -= 1
		if f.guts_timer == 0:
			f.josh = maxi(0, f.josh - rules.guts_heal)
			f.armor_frames = rules.guts_armor_frames


## Movement, then at most one of: a dodge, a heavy, a jab or a jump, in that order of priority.
## A jump pressed on the same frame as an attack or dodge is ignored. Returns whether gravity applies.
static func _handle_control(f: Fighter, input: int, pressed: int, t: FighterTuning) -> bool:
	var dir := _horizontal(input)
	if dir != 0:
		f.facing = dir

	var target_vx := dir * (t.run_speed if f.grounded else t.air_speed)
	f.vel_x = Fx.move_toward(f.vel_x, target_vx, t.ground_accel if f.grounded else t.air_accel)

	if (pressed & Buttons.DODGE) != 0 and f.dodge_cooldown == 0 and (f.grounded or not f.air_dodge_used):
		_start_dodge(f, input, t)
		return false

	var move: int = MoveData.Id.NONE
	if (pressed & Buttons.HEAVY) != 0:
		move = _heavy_for(f, input, dir)
	if move == MoveData.Id.NONE and (pressed & Buttons.LIGHT) != 0:
		move = MoveData.Id.JAB
	if move == MoveData.Id.NONE:
		if (pressed & Buttons.JUMP) != 0:
			_jump(f, t)
		return true

	f.action = A_ATTACK
	f.move = move
	f.action_frame = 0
	f.attack_connected = false
	return _attack_motion(f, input, t, false)


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
		return MoveData.Id.NONE if f.recovery_used else MoveData.Id.UP_HEAVY
	if (input & Buttons.DOWN) != 0:
		return MoveData.Id.DOWN_HEAVY if f.grounded else MoveData.Id.GROUND_SLAM
	return MoveData.Id.SIDE_HEAVY if dir != 0 else MoveData.Id.NEUTRAL_HEAVY


static func _update_attack(f: Fighter, input: int, t: FighterTuning) -> bool:
	var m := t.moves.get_move(f.move)
	f.action_frame += 1
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
		if f.grounded:
			f.vel_x = Fx.move_toward(f.vel_x, 0, t.ground_accel)
		elif m.air_control:
			f.vel_x = Fx.move_toward(f.vel_x, _horizontal(input) * t.air_speed, t.air_accel)
		elif m.lunge_speed != 0 and f.action_frame > m.impulse_frame:
			f.vel_x = Fx.move_toward(f.vel_x, 0, t.air_accel)

	if f.action_frame == m.impulse_frame:
		if m.lunge_speed != 0:
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


## Finds every attacker whose active hitbox touches another fighter, then applies parries and hits.
## Fighters that spent this frame frozen in hit-stop don't attack.
static func _resolve_hits(fighters: Array[Fighter], frozen: Array[bool], rules: MatchRules) -> void:
	var t := rules.tuning
	var n := fighters.size()

	# Collect first, apply after, so simultaneous hits trade fairly regardless of player index.
	# Each contact records the move, because applying a parry clears the attacker's move.
	var contact_attacker: PackedInt32Array = []
	var contact_target: PackedInt32Array = []
	var contact_move: PackedInt32Array = []
	var contact_parried: Array[bool] = []

	for a in n:
		var atk := fighters[a]
		if atk.attack_connected or frozen[a]:
			continue
		var hitbox := active_hitbox(atk, t)
		if hitbox == null:
			continue
		for d in n:
			var def := fighters[d]
			if d == a or not def.is_active():
				continue
			if not hitbox.overlaps(hurtbox(def, t)):
				continue
			var parried := in_parry_window(def, t)
			if not parried and is_intangible(def, t):
				continue # dodged: the attack can still hit later
			contact_attacker.append(a)
			contact_target.append(d)
			contact_move.append(atk.move)
			contact_parried.append(parried)

	# Parries before hits: an attacker parried by one fighter and hit by another on the same frame
	# ends up knocked back, not just stunned.
	for i in contact_attacker.size():
		if contact_parried[i]:
			_apply_parry(fighters[contact_attacker[i]], fighters[contact_target[i]], rules)
	for i in contact_attacker.size():
		if not contact_parried[i]:
			_apply_hit(fighters[contact_attacker[i]], fighters[contact_target[i]], t.moves.get_move(contact_move[i]), rules)


## The attacker is stunned in place and stops rising; the defender's dodge ends at once with its
## air dodge given back; both freeze for the longest hit-stop.
static func _apply_parry(atk: Fighter, def: Fighter, rules: MatchRules) -> void:
	atk.attack_connected = true
	atk.action = A_STUNNED
	atk.move = MoveData.Id.NONE
	atk.action_frame = 0
	atk.hitstun_frames = rules.tuning.parry_stun_frames
	atk.vel_x = 0
	atk.vel_y = mini(atk.vel_y, 0)
	atk.hit_stop = maxi(atk.hit_stop, rules.max_hit_stop)

	if def.action == A_DODGE: # only once, if it parries two attackers on one frame
		def.action = A_IDLE
		def.action_frame = 0
		def.air_dodge_used = false
		if not def.grounded:
			def.vel_x = def.vel_x / 2
			def.vel_y = def.vel_y / 2
	def.hit_stop = maxi(def.hit_stop, rules.max_hit_stop)


static func _apply_hit(atk: Fighter, def: Fighter, m: MoveData, rules: MatchRules) -> void:
	atk.attack_connected = true

	def.josh = mini(rules.max_josh, def.josh + m.damage)
	var stop := hit_stop_frames(m.damage, rules)
	atk.hit_stop = maxi(atk.hit_stop, stop)
	def.hit_stop = maxi(def.hit_stop, stop)
	def.frozen_by_hit = true

	if rules.guts and not def.guts_used and def.josh >= rules.guts_josh:
		def.guts_used = true
		def.guts_timer = rules.guts_delay_frames

	if def.armor_frames > 0:
		return # Guts armor: takes the Josh, keeps its footing

	# A dodge cut short by a hit still costs its cooldown.
	if def.action == A_DODGE:
		def.dodge_cooldown = rules.tuning.dodge_cooldown_frames

	# Launch away from the attacker. For moves whose hitbox reaches behind the attacker (the up
	# heavy, the slam, the sweep) that depends on which side the target is on.
	var kb := m.base_knockback + m.knockback_growth * def.josh
	var side := atk.facing
	var reaches_behind := m.hits_both_sides or m.hitbox_near < 0
	if reaches_behind and def.pos_x != atk.pos_x:
		side = 1 if def.pos_x > atk.pos_x else -1

	def.vel_x = Fx.mul(kb, m.launch_x) * side
	def.vel_y = Fx.mul(kb, m.launch_y)
	def.grounded = false
	def.action = A_HITSTUN
	def.move = MoveData.Id.NONE
	def.action_frame = 0
	def.hitstun_frames = 8 + def.josh / 8 + m.hitstun_bonus
	def.air_dodge_used = false # getting hit gives back the air dodge and the recovery
	def.recovery_used = false


static func _check_blast_zone(f: Fighter, rules: MatchRules) -> void:
	if not f.is_active() or rules.stage.blast_zone.contains(f.pos_x, f.pos_y):
		return
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
	f.action = A_RESPAWNING if f.stocks > 0 else A_ELIMINATED


static func _check_match_over(state: GameState) -> void:
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
