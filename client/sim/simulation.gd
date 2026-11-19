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
const SIM_VERSION := 6 # 6: arena side blast zones at the screen's edge (17.2 m)

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
			_update_props(state, frozen, rules)
		if rules.hazards:
			_apply_hazards(state, rules)
		for i in fighters.size():
			_end_plunge_on_landing(fighters[i], rules.tuning)
			if rules.weapons:
				_return_on_fall(state, i, rules)
			_check_blast_zone(state, i, rules)
		_check_match_over(state, rules)
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
			gravity = _handle_control(state, f, input, pressed, rules)

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
	if f.last_hit_timer > 0:
		f.last_hit_timer -= 1
	if f.guts_timer > 0:
		f.guts_timer -= 1
		if f.guts_timer == 0:
			f.josh = maxi(0, f.josh - rules.guts_heal)
			f.armor_frames = rules.guts_armor_frames


## Movement, then at most one of: a dodge, a heavy, a jab or a jump, in that order of priority.
## A jump pressed on the same frame as an attack or dodge is ignored. Returns whether gravity applies.
static func _handle_control(state: GameState, f: Fighter, input: int, pressed: int, rules: MatchRules) -> bool:
	var t := rules.tuning
	var dir := _horizontal(input)
	if dir != 0:
		f.facing = dir

	var target_vx := dir * (t.run_speed if f.grounded else t.air_speed)
	if f.weapon == Weapons.BOWLING_BALL:
		target_vx = target_vx * 4 / 5 # the ball is heavy
	f.vel_x = Fx.move_toward(f.vel_x, target_vx, t.ground_accel if f.grounded else t.air_accel)

	if (pressed & Buttons.DODGE) != 0 and f.dodge_cooldown == 0 and (f.grounded or not f.air_dodge_used):
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
		if (pressed & Buttons.JUMP) != 0:
			_jump(f, t)
		return true

	f.action = A_ATTACK
	f.move = move
	f.action_frame = 0
	f.attack_connected = false
	f.uzi_shots = 0
	return _attack_motion(f, input, t, false)


## Armed, the attack buttons are one button: Light, Heavy or both fire the weapon's attack once,
## or throw it when it's empty. Drop weapon throws it. Up + Heavy stays the recovery.
static func _armed_move(f: Fighter, input: int, pressed: int) -> int:
	if (pressed & Buttons.HEAVY) != 0 and (input & Buttons.UP) != 0:
		return MoveData.Id.NONE if f.recovery_used else MoveData.Id.UP_HEAVY
	if (pressed & Buttons.DROP_WEAPON) != 0:
		return MoveData.Id.WEAPON_THROW
	if (pressed & (Buttons.LIGHT | Buttons.HEAVY)) == 0:
		return MoveData.Id.NONE
	return Weapons.move_of(f.weapon) if f.weapon_supply > 0 else MoveData.Id.WEAPON_THROW


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
	# The Uzi keeps firing while an attack button is held (and it has rounds).
	if f.move == MoveData.Id.UZI_FIRE and f.action_frame == m.startup + Weapons.UZI_INTERVAL \
			and (input & (Buttons.LIGHT | Buttons.HEAVY)) != 0 and f.weapon_supply > 0:
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
		if f.grounded and f.move == MoveData.Id.UZI_FIRE: # walk at half speed while firing, without turning
			f.vel_x = Fx.move_toward(f.vel_x, _horizontal(input) * t.run_speed / 2, t.ground_accel)
		elif f.grounded:
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
static func _resolve_hits(state: GameState, frozen: Array[bool], rules: MatchRules) -> void:
	var fighters := state.fighters
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
			_apply_hit(state, contact_attacker[i], contact_target[i], contact_move[i], rules)


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
	var no_hit_stop := false    # Uzi bullets: the target doesn't freeze
	var max_hit_stop := false   # sweet spots always freeze the longest
	var weapon := 0             # what it came from (0: fists or a hazard)
	var sweet := false          # a sweet-spot hit

	func _init(m: MoveData) -> void:
		damage = m.damage
		base_knockback = m.base_knockback
		knockback_growth = m.knockback_growth
		launch_x = m.launch_x
		launch_y = m.launch_y
		hitstun_bonus = m.hitstun_bonus

	## A sweet-spot hit: 1.25x damage and base knockback, the longest hit-stop.
	func sweeten() -> void:
		percent = percent * Weapons.SWEET_PERCENT / 100
		base_knockback = base_knockback * Weapons.SWEET_PERCENT / 100
		max_hit_stop = true
		sweet = true


## A fighter's move connects: fists, or a melee weapon (belan, bat, shotgun).
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
	if move == MoveData.Id.BELAN_BONK or move == MoveData.Id.BAT_SWING or move == MoveData.Id.SHOTGUN_BLAST:
		weapon = atk.weapon
		_melee_weapon_hit(atk, def, move, hit, rules)
	if weapon != Weapons.NONE and rules.signature_of(attacker) == weapon:
		hit.percent = hit.percent * Weapons.SIGNATURE_PERCENT / 100
	hit.weapon = weapon
	_land_hit(state, attacker, target, hit, side, true, rules)


## A melee weapon's sweet spot and quirks. Belan: bonking someone in the middle of their own attack
## dizzies them longer. Bat: the very tip (1.3 m or more away) launches flat, the "six". Shotgun:
## point blank (within 1.2 m) is the sweet spot; beyond 3 m it does only 10. Belan and bat lose a
## hit of durability.
static func _melee_weapon_hit(atk: Fighter, def: Fighter, move: int, hit: Hit, rules: MatchRules) -> void:
	var hurt := hurtbox(def, rules.tuning)
	var near := hurt.min_x - atk.pos_x if atk.facing > 0 else atk.pos_x - hurt.max_x
	if move == MoveData.Id.BELAN_BONK:
		if atk.weapon_supply > 0:
			atk.weapon_supply -= 1
		if def.action == A_ATTACK:
			hit.sweeten()
			hit.hitstun_bonus += 6
	elif move == MoveData.Id.BAT_SWING:
		if atk.weapon_supply > 0:
			atk.weapon_supply -= 1
		if near >= Weapons.BAT_TIP:
			hit.sweeten()
			hit.launch_x = Weapons.SIX_LAUNCH_X
			hit.launch_y = Weapons.SIX_LAUNCH_Y
	else:
		if near > Weapons.SHOTGUN_FAR:
			hit.damage = Weapons.SHOTGUN_FAR_DAMAGE
		elif near <= Weapons.POINT_BLANK:
			hit.sweeten()


## Applies a hit: rage, the hidden second wind, Josh, hit-stop, Guts, then knockback in direction
## side. Projectiles don't freeze their owner.
static func _land_hit(state: GameState, attacker: int, target: int, hit: Hit, side: int, freeze_attacker: bool, rules: MatchRules) -> void:
	var fighters := state.fighters
	var atk := fighters[attacker]
	var def := fighters[target]

	# Rage: the attacker's own Josh makes its hits stronger (a comeback when close to death).
	var rage := rage_multiplier(atk.josh, rules)
	var damage := hit.damage * rage / 100
	if hit.percent != 100:
		damage = damage * hit.percent / 100

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
		# The attacker's record with this weapon, for the winner's nickname.
		var stats := (attacker * Weapons.COUNT + hit.weapon) * 3
		state.weapon_stats[stats] += damage
		if hit.sweet:
			state.weapon_stats[stats + 2] += 1
	if not hit.no_hit_stop:
		var stop := rules.max_hit_stop if hit.max_hit_stop else hit_stop_frames(damage, rules)
		if freeze_attacker:
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


static func _check_blast_zone(state: GameState, index: int, rules: MatchRules) -> void:
	var fighters := state.fighters
	var f := fighters[index]
	if not f.is_active() or rules.stage.blast_zone.contains(f.pos_x, f.pos_y):
		return

	# The KO goes to whoever hit this fighter last, if that was recent; a lone fall scores none.
	f.falls += 1
	if f.last_hit_timer > 0 and f.last_hit_by > 0 and f.last_hit_by - 1 != index:
		fighters[f.last_hit_by - 1].kos += 1
		state.weapon_stats[((f.last_hit_by - 1) * Weapons.COUNT + f.last_hit_weapon) * 3 + 1] += 1 # the weapon's KO
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
	f.action = A_RESPAWNING if timed or f.stocks > 0 else A_ELIMINATED


static func _check_match_over(state: GameState, rules: MatchRules) -> void:
	if rules.time_limit_frames > 0:
		if state.frame + 1 < rules.time_limit_frames:
			return
		state.match_over = true
		state.winner_index = timed_winner(state.fighters)
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


## A fighter's main weapon in this match, for their nickname: [weapon, master]. The weapon (0 is
## fists) is the one that did the most of their damage, if that is at least 40% of it; otherwise
## -1, an all-rounder. Master: they also landed 3 or more sweet spots and a KO with it.
static func main_weapon(state: GameState, fighter: int) -> Array:
	var total := 0
	var best := -1
	var best_damage := 0
	var from := fighter * Weapons.COUNT * 3
	for w in Weapons.COUNT:
		var damage := state.weapon_stats[from + w * 3]
		total += damage
		if damage > best_damage:
			best = w
			best_damage = damage
	if best < 0 or best_damage * 100 < total * 40:
		return [-1, false]
	return [best, state.weapon_stats[from + best * 3 + 2] >= 3 and state.weapon_stats[from + best * 3 + 1] >= 1]


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
	f.uzi_shots = 0
	f.uzi_streak = 0
	f.uzi_target = 0
	f.flurry_next = MoveData.Id.NONE


# ---- Weapons (SimulationWeapons.cs) ---------------------------------------------------------------
# Prop Boxes dropping, picking up and swapping, the weapons' shots, projectiles flying, hitting and
# exploding, the bat batting them back, and a dead fighter's weapon coming back in a box. Runs only
# when rules.weapons is on.


## A prop's box: its position is its bottom centre.
static func prop_box(p: Prop) -> FxRect:
	var h := Weapons.shot_half(p.shot) if p.kind == Weapons.PROP_SHOT else Weapons.BOX_HALF
	return FxRect.new(p.pos_x - h, p.pos_y, p.pos_x + h, p.pos_y + h * 2)


static func _rand_range(state: GameState, lo: int, hi: int) -> int:
	var rng := SimRandom.new(state.random_state)
	var v := rng.range_int(lo, hi)
	state.random_state = rng.state
	return v


## Weapon moves release their shot on their startup frame; the shotgun spends a shell then.
static func _fire_shots(state: GameState, frozen: Array[bool], rules: MatchRules) -> void:
	var fighters := state.fighters
	for i in fighters.size():
		var f := fighters[i]
		if frozen[i] or f.action != A_ATTACK or not Weapons.is_weapon_move(f.move):
			continue
		if f.action_frame != rules.tuning.moves.get_move(f.move).startup or f.weapon == Weapons.NONE:
			continue
		var hand_x := f.pos_x + Weapons.HAND_REACH * f.facing
		var hand_y := f.pos_y + Weapons.HAND_HEIGHT
		match f.move:
			MoveData.Id.WEAPON_THROW:
				_add_shot(state, Weapons.SHOT_THROWN, f.weapon, i, hand_x, hand_y, Weapons.THROW_VX * f.facing, Weapons.THROW_VY, false)
				f.weapon = Weapons.NONE
				f.weapon_supply = 0
			MoveData.Id.SHOTGUN_BLAST:
				if f.weapon_supply > 0:
					f.weapon_supply -= 1
			MoveData.Id.BALL_ROLL:
				_add_shot(state, Weapons.SHOT_ROLL, f.weapon, i, hand_x, f.pos_y, Weapons.ROLL_SPEED * f.facing, 0, f.grounded)
				f.weapon = Weapons.NONE # the ball has left the hand; it lies there when it stops
				f.weapon_supply = 0
			MoveData.Id.UZI_FIRE:
				_add_shot(state, Weapons.SHOT_BULLET, f.weapon, i, hand_x, hand_y, Weapons.BULLET_SPEED * f.facing, Weapons.uzi_spread_at(f.uzi_shots), false)
				f.uzi_shots += 1
				if f.weapon_supply > 0:
					f.weapon_supply -= 1
			MoveData.Id.BOMB_LOB:
				_add_shot(state, Weapons.SHOT_BOMB, f.weapon, i, hand_x, hand_y, Weapons.BOMB_VX * f.facing, Weapons.BOMB_VY, false)
				if f.weapon_supply > 0:
					f.weapon_supply -= 1
			MoveData.Id.BOW_SHOT:
				_add_shot(state, Weapons.SHOT_ARROW, f.weapon, i, hand_x, hand_y, Weapons.ARROW_SPEED * f.facing, 0, false)
				if f.weapon_supply > 0:
					f.weapon_supply -= 1


static func _add_shot(state: GameState, kind: int, weapon: int, owner: int, at_x: int, at_y: int, vx: int, vy: int, grounded: bool) -> void:
	var slot := _free_slot(state, true)
	if slot < 0:
		return
	var h := Weapons.shot_half(kind)
	var p := state.props[slot]
	p.clear()
	p.kind = Weapons.PROP_SHOT
	p.weapon = weapon
	p.shot = kind
	p.owner = owner
	p.pos_x = at_x
	p.pos_y = at_y if kind == Weapons.SHOT_ROLL else at_y - h
	p.vel_x = vx
	p.vel_y = vy
	p.grounded = grounded
	p.timer = Weapons.BOMB_FUSE if kind == Weapons.SHOT_BOMB else 0


## The first empty prop slot. A shot that finds none replaces the oldest bullet; boxes wait.
static func _free_slot(state: GameState, for_shot: bool) -> int:
	var props := state.props
	for i in props.size():
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
	return oldest


static func _update_props(state: GameState, frozen: Array[bool], rules: MatchRules) -> void:
	var props := state.props
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
	match p.shot:
		Weapons.SHOT_ROLL:
			_fall(p, rules.stage, Weapons.SHOT_GRAVITY, true)
			if p.age >= Weapons.ROLL_FRAMES:
				# It stops and lies there: anyone can pick it up and roll it again.
				p.kind = Weapons.PROP_ITEM
				p.shot = Weapons.SHOT_NONE
				p.supply = 1
				p.owner = -1
				p.owner_clear = false
				p.vel_x = 0
				p.vel_y = 0
				p.timer = 0
				p.age = 0
				p.hit_mask = 0
				p.hits = 0
		Weapons.SHOT_BULLET:
			p.pos_x += p.vel_x
			p.pos_y += p.vel_y
			if p.age >= Weapons.BULLET_FRAMES:
				_miss_bullet(state, p)
				p.clear()
				return
		Weapons.SHOT_ARROW:
			p.vel_y = p.vel_y - Weapons.ARROW_GRAVITY
			p.pos_x += p.vel_x
			p.pos_y += p.vel_y
			if p.age >= Weapons.ARROW_FRAMES:
				p.clear()
				return
		Weapons.SHOT_BOMB:
			p.timer -= 1
			_fall(p, rules.stage, Weapons.SHOT_GRAVITY, true)
			if p.grounded:
				p.vel_x = Fx.move_toward(p.vel_x, 0, Fx.ratio(2, 100))
		Weapons.SHOT_THROWN:
			_fall(p, rules.stage, Weapons.SHOT_GRAVITY, true)
			if p.grounded:
				p.clear() # a thrown weapon is gone once it lands
				return
	if p.kind != Weapons.PROP_NONE and not rules.stage.blast_zone.contains(p.pos_x, p.pos_y):
		if p.shot == Weapons.SHOT_BULLET:
			_miss_bullet(state, p)
		p.clear()


## An Uzi bullet that hit nobody breaks its shooter's streak.
static func _miss_bullet(state: GameState, p: Prop) -> void:
	if p.hits == 0 and p.owner >= 0 and p.owner < state.fighters.size():
		state.fighters[p.owner].uzi_streak = 0


static func _shot_hits(state: GameState, frozen: Array[bool], rules: MatchRules) -> void:
	var fighters := state.fighters
	var props := state.props
	var t := rules.tuning
	for i in props.size():
		var p := props[i]
		if p.kind != Weapons.PROP_SHOT:
			continue
		var box := prop_box(p)

		# The release grace: it can't hit its owner until it has stopped touching them once.
		if not p.owner_clear and (p.owner < 0 or not fighters[p.owner].is_active() or not box.overlaps(hurtbox(fighters[p.owner], t))):
			p.owner_clear = true

		# A bat swing bats it back: it becomes the batter's, reversed and 1.25x faster.
		for b in fighters.size():
			var batter := fighters[b]
			if frozen[b] or batter.move != MoveData.Id.BAT_SWING:
				continue
			var bat := active_hitbox(batter, t)
			if bat == null:
				continue
			if signi(p.vel_x) == batter.facing or not bat.overlaps(box):
				continue
			p.owner = b
			p.owner_clear = false
			p.hit_mask = 0
			p.hits = 0
			p.vel_x = absi(p.vel_x) * Weapons.SWEET_PERCENT / 100 * batter.facing
			if p.shot == Weapons.SHOT_BULLET or p.shot == Weapons.SHOT_ARROW:
				p.age = 0
			break

		if p.shot == Weapons.SHOT_BOMB and p.timer <= 0:
			_explode(state, i, -1, rules)
			continue

		for d in fighters.size():
			var def := fighters[d]
			if not def.is_active() or (p.hit_mask & (1 << d)) != 0:
				continue
			if d == p.owner and not p.owner_clear:
				continue
			if not box.overlaps(hurtbox(def, t)):
				continue

			if p.shot == Weapons.SHOT_BOMB:
				if d == p.owner:
					continue # it bursts on touching anyone but its thrower
				_explode(state, i, d, rules)
				break
			if in_parry_window(def, t):
				_parry_reward(def, rules) # parrying a projectile destroys it
				p.clear()
				break
			if is_intangible(def, t):
				continue # dodged: it flies on

			p.hit_mask |= 1 << d
			_shot_hit(state, i, d, rules)
			p.hits += 1
			if p.shot == Weapons.SHOT_BULLET or p.shot == Weapons.SHOT_THROWN:
				p.clear()
				break


## A projectile hits a fighter, its owner included. Sweet spots (never on yourself): the ball's
## second and later fighters ("Strike!"), the Uzi from the 6th bullet in a row on one target, an
## arrow that flew far (a long shot), a thrown weapon on an airborne target.
static func _shot_hit(state: GameState, index: int, target: int, rules: MatchRules) -> void:
	var p := state.props[index]
	var fighters := state.fighters
	var owner := p.owner if p.owner >= 0 else target
	var is_self := owner == target
	var move: int = MoveData.Id.WEAPON_THROW
	match p.shot:
		Weapons.SHOT_ROLL: move = MoveData.Id.BALL_ROLL
		Weapons.SHOT_BULLET: move = MoveData.Id.UZI_FIRE
		Weapons.SHOT_ARROW: move = MoveData.Id.BOW_SHOT
	var hit := Hit.new(rules.tuning.moves.get_move(move))
	match p.shot:
		Weapons.SHOT_ROLL:
			if not is_self and p.hits >= 1:
				hit.sweeten()
		Weapons.SHOT_BULLET:
			hit.no_hit_stop = true
			if not is_self:
				var shooter := fighters[owner]
				if shooter.uzi_target == target + 1:
					shooter.uzi_streak += 1
				else:
					shooter.uzi_target = target + 1
					shooter.uzi_streak = 1
				if shooter.uzi_streak >= Weapons.UZI_STREAK_SWEET:
					hit.damage = 3 # keeping on target: the stream holds them
					hit.hitstun_bonus = 0
					hit.sweet = true
		Weapons.SHOT_ARROW:
			if not is_self and p.age >= Weapons.LONG_SHOT_FRAMES:
				hit.sweeten()
		Weapons.SHOT_THROWN:
			hit.damage = Weapons.throw_damage(p.weapon)
			if not is_self and not fighters[target].grounded:
				hit.sweeten()
	if rules.signature_of(owner) == p.weapon:
		hit.percent = hit.percent * Weapons.SIGNATURE_PERCENT / 100
	hit.weapon = p.weapon
	var side := -1 if p.vel_x < 0 else 1
	_land_hit(state, owner, target, hit, side, false, rules)


## A cherry bomb bursts: a 2.4 m box that hits everyone in it, its thrower too, launching them away
## from its centre. A fighter it burst on is a direct hit (a sweet spot).
static func _explode(state: GameState, index: int, direct: int, rules: MatchRules) -> void:
	var p := state.props[index]
	var px := p.pos_x
	var py := p.pos_y
	var weapon := p.weapon
	var owner := p.owner
	p.clear()
	var fighters := state.fighters
	var cy := py + Weapons.shot_half(Weapons.SHOT_BOMB)
	var blast := FxRect.new(px - Weapons.BLAST_HALF, cy - Weapons.BLAST_HALF, px + Weapons.BLAST_HALF, cy + Weapons.BLAST_HALF)
	for d in fighters.size():
		var def := fighters[d]
		if not def.is_active() or is_intangible(def, rules.tuning):
			continue
		if not blast.overlaps(hurtbox(def, rules.tuning)):
			continue
		var from := owner if owner >= 0 else d
		var hit := Hit.new(Weapons.move_for(MoveData.Id.BOMB_LOB))
		if d == direct and from != d:
			hit.sweeten()
		if rules.signature_of(from) == weapon:
			hit.percent = hit.percent * Weapons.SIGNATURE_PERCENT / 100
		hit.weapon = weapon
		var side := 1 if def.pos_x >= px else -1
		_land_hit(state, from, d, hit, side, false, rules)


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
		if taker.weapon != Weapons.NONE and (taker.weapon_supply > 0 or taker.weapon == Weapons.BOWLING_BALL):
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
		taker.uzi_shots = 0
		taker.uzi_streak = 0
		taker.uzi_target = 0


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

	# Any weapon but the last box's, with equal odds.
	var weapon := 0
	if state.last_box_weapon == Weapons.NONE:
		weapon = _rand_range(state, 1, Weapons.COUNT)
	else:
		var r := _rand_range(state, 1, Weapons.COUNT - 1)
		if r >= state.last_box_weapon:
			r += 1
		weapon = r
	_place_box(state, rules, slot, weapon, Weapons.supply_of(weapon))
	state.last_box_weapon = weapon
	state.prop_timer = _rand_range(state, Weapons.BOX_INTERVAL_MIN, Weapons.BOX_INTERVAL_MAX)


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
	if f.weapon_supply > 0 or f.weapon == Weapons.BOWLING_BALL:
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
