class_name SparringBot
## A simple CPU opponent (port of Diagnostics/SparringBot.cs, checked against its golden traces).
## Not the real AI.
##
## A pure function of (state, player, level): it reads GameState and returns buttons, never
## writes state or draws from the match RNG. Every online peer therefore computes the same
## buttons from the same state, so it is safe for CPU players and soak tests under rollback.
##
## Walks to the nearest opponent, faces them, attacks in reach, jumps after them onto platforms
## above, walks off a floating platform if they're below, and recovers (air jump, up heavy, air
## dodge). Jabs by default; heavies to punish and as finishers at high Josh. Spot-dodges attacks
## about to hit it, timed to parry. Levels set attack odds, reaction time and dodge odds.

enum Level { EASY, NORMAL, HARD }

const FINISHER_JOSH := 70
const FINISHER_ODDS := 3
const PARRY_LEAD := 3

const HESITATE_SALT := -6510698340416275396 # 0xA5A55A5AC3C33C3C
const HEAVY_SALT := -7046029254386353131    # 0x9E3779B97F4A7C15
const DODGE_SALT := 4354685564936845355     # 0x3C6EF372FE94F82B

const A_IDLE := Fighter.Action.IDLE
const A_ATTACK := Fighter.Action.ATTACK
const A_STUNNED := Fighter.Action.STUNNED

static var JAB_RANGE := Fx.ratio(6, 5)
static var CHASE_HEIGHT := Fx.ratio(3, 2)
static var CHASE_DISTANCE := Fx.from_int(4)
static var EDGE_MARGIN := Fx.ONE
static var STOP_DISTANCE := Fx.ratio(1, 8)
static var LANDING_MARGIN := Fx.HALF
static var LUNGE_ROOM := Fx.ratio(3, 2)
static var THREAT_PADDING := Fx.HALF


## [attack odds, reaction frames, dodge odds] per level.
static func skill_for(level: int) -> PackedInt32Array:
	match level:
		Level.EASY: return PackedInt32Array([16, 10, 4])
		Level.NORMAL: return PackedInt32Array([6, 6, 3])
		Level.HARD: return PackedInt32Array([2, 3, 2])
	push_error("Unknown CPU level %d" % level)
	return PackedInt32Array([2, 3, 2])


## The bot's buttons for this frame.
static func think(state: GameState, rules: MatchRules, self_index: int, level: int = Level.HARD) -> int:
	var skill := skill_for(level)
	var fighters := state.fighters
	var me := fighters[self_index]
	var stage: FxRect = rules.stage.platforms[0]
	var t := rules.tuning

	if me.hit_stop > 0:
		return Buttons.NONE
	if me.action == A_ATTACK:
		if me.move == MoveData.Id.UZI_FIRE and me.uzi_shots < UZI_BURST:
			return Buttons.LIGHT
		return _steer_during_move(me, stage, t)
	if me.action != A_IDLE:
		return Buttons.NONE

	var last := me.previous_buttons # jump, attacks and dodge only fire on a fresh press
	var can_jump := (last & Buttons.JUMP) == 0
	var can_jab := (last & Buttons.LIGHT) == 0
	var can_heavy := (last & Buttons.HEAVY) == 0
	var can_dodge := (last & Buttons.DODGE) == 0 and me.dodge_cooldown == 0 and (me.grounded or not me.air_dodge_used)

	# Recovery: off the sides of the main stage, or below its top surface.
	var off_stage := me.pos_x < stage.min_x or me.pos_x > stage.max_x or (not me.grounded and me.pos_y < stage.max_y)
	if off_stage:
		return _recover(me, stage, can_jump, can_heavy, can_dodge)

	var target := _nearest_opponent(fighters, self_index)
	if target < 0:
		return Buttons.NONE
	var op := fighters[target]

	# Defence first: a spot dodge doesn't move, so it can't take the bot off the stage.
	if me.grounded and can_dodge and _should_dodge(state, rules, self_index, skill):
		return Buttons.DODGE

	var dx := op.pos_x - me.pos_x
	var dy := op.pos_y - me.pos_y
	var dir := 1 if dx >= 0 else -1
	var toward := Buttons.RIGHT if dir > 0 else Buttons.LEFT

	var b := Buttons.NONE
	var floor_index := _standing_on(me, rules.stage) if me.grounded else -1

	# Weapons: empty-handed, fetch a box or loose weapon on the same platform; with a gun, the bow,
	# the bombs or the ball, fire from range once lined up. Melee weapons fight like fists.
	if rules.weapons and floor_index >= 0:
		if me.weapon == Weapons.NONE:
			var item := _nearest_item(state, me, self_index, rules.stage.platforms[floor_index])
			if item >= 0 and absi(dx) > JAB_RANGE:
				var to_item := state.props[item].pos_x - me.pos_x
				return Buttons.RIGHT if to_item > 0 else Buttons.LEFT
		else:
			var reach := _ranged_reach(me.weapon)
			if reach > 0 and absi(dy) < Fx.ONE and absi(dx) <= reach and absi(dx) >= _ranged_min(me.weapon):
				if me.facing != dir:
					return toward
				return Buttons.LIGHT if can_jab and not _hesitates(state.frame, self_index, skill[0]) else Buttons.NONE
	if floor_index > 0 and dy < -Fx.ONE:
		# On a floating platform with them below: walk off the end on their side.
		var p: FxRect = rules.stage.platforms[floor_index]
		b |= Buttons.LEFT if op.pos_x * 2 < p.min_x + p.max_x else Buttons.RIGHT
	elif absi(dx) <= JAB_RANGE and _safe_to_attack(me, stage, t):
		if me.facing != dir:
			b |= toward # turn to face them
		elif absi(dy) < Fx.ONE and not _hesitates(state.frame, self_index, skill[0]):
			# Lunge room is measured on whatever it stands on: a floating platform's end is an edge too.
			var ground: FxRect = rules.stage.platforms[floor_index] if floor_index >= 0 else stage
			b |= _attack(me, op, ground, t, state.frame, self_index, toward, can_jab, can_heavy)
	else:
		# Walk after them, but only as far as the edge margin.
		var goal := clampi(op.pos_x, stage.min_x + EDGE_MARGIN, stage.max_x - EDGE_MARGIN)
		var to_goal := goal - me.pos_x
		if to_goal > STOP_DISTANCE:
			b |= Buttons.RIGHT
		elif to_goal < -STOP_DISTANCE:
			b |= Buttons.LEFT

	# Jump after them only when they stand on a platform above.
	if me.grounded and can_jump and op.grounded and dy > CHASE_HEIGHT and absi(dx) < CHASE_DISTANCE:
		b |= Buttons.JUMP
	return b


static func _attack(me: Fighter, op: Fighter, ground: FxRect, t: FighterTuning, frame: int, self_index: int,
		toward: int, can_jab: bool, can_heavy: bool) -> int:
	if me.grounded and can_heavy:
		if op.action == A_STUNNED:
			if op.hit_stop + op.hitstun_frames > t.moves.neutral_heavy.startup:
				return Buttons.HEAVY
			return Buttons.LIGHT if can_jab else Buttons.NONE # too little stun left for the heavy to land

		var lunge_end := me.pos_x + LUNGE_ROOM * me.facing
		var room := lunge_end > ground.min_x and lunge_end < ground.max_x

		# Punish a move with no active frame left, with whichever heavy lands before the opponent
		# can act again: the side heavy if it fits and there's room, else the neutral one.
		var theirs := Simulation.current_move(op, t)
		if theirs != null and op.action_frame + 1 >= theirs.startup + theirs.active:
			var left := op.hit_stop + theirs.total_frames() - op.action_frame
			if room and left > t.moves.side_heavy.startup:
				return Buttons.HEAVY | toward
			if left > t.moves.neutral_heavy.startup:
				return Buttons.HEAVY
		if op.josh >= FINISHER_JOSH and _picks_heavy(frame, self_index):
			return Buttons.HEAVY | toward if room else Buttons.HEAVY
	return Buttons.LIGHT if can_jab else Buttons.NONE


## Uzi bullets the bot fires in one held burst.
const UZI_BURST := 8


## How far a shooting weapon is worth firing from (0: a melee weapon, used like fists).
static func _ranged_reach(w: int) -> int:
	match w:
		Weapons.SHOTGUN: return Fx.from_int(4)
		Weapons.UZI: return Fx.from_int(9)
		Weapons.BOWLING_BALL: return Fx.from_int(12)
		Weapons.CHERRY_BOMBS: return Fx.from_int(6)
		Weapons.BOW: return Fx.from_int(15)
	return 0


## Closer than this, lobbing a bomb would land it behind them (or on the bot).
static func _ranged_min(w: int) -> int:
	return Fx.ratio(5, 2) if w == Weapons.CHERRY_BOMBS else 0


## The nearest box or loose weapon resting on this platform within 5 m, or -1.
static func _nearest_item(state: GameState, me: Fighter, self_index: int, floor_rect: FxRect) -> int:
	var best := -1
	var best_dist := 0
	for i in state.props.size():
		var p := state.props[i]
		if (p.kind != Weapons.PROP_BOX and p.kind != Weapons.PROP_ITEM) or not p.grounded or p.ignore_fighter == self_index + 1:
			continue
		if p.pos_y != floor_rect.max_y or p.pos_x < floor_rect.min_x or p.pos_x > floor_rect.max_x:
			continue
		var d := absi(p.pos_x - me.pos_x)
		if d > Fx.from_int(5):
			continue
		if best < 0 or d < best_dist:
			best = i
			best_dist = d
	return best


## Off the stage: steer home, air-jump, then up heavy, then air-dodge up and home, each saved
## until falling below the stage top.
static func _recover(me: Fighter, stage: FxRect, can_jump: bool, can_heavy: bool, can_dodge: bool) -> int:
	var home := Buttons.RIGHT if me.pos_x < 0 else Buttons.LEFT
	var falling := me.vel_y < 0 and me.pos_y < stage.max_y
	if not falling:
		return home
	if me.air_jumps_left > 0:
		return home | Buttons.JUMP if can_jump else home
	if not me.recovery_used:
		return home | Buttons.UP | Buttons.HEAVY if can_heavy else home
	return home | Buttons.UP | Buttons.DODGE if can_dodge else home


## During a move the bot can steer (the up heavy), steer home if it is off the side.
static func _steer_during_move(me: Fighter, stage: FxRect, t: FighterTuning) -> int:
	var m := Simulation.current_move(me, t)
	if m == null or not m.air_control or me.grounded:
		return Buttons.NONE
	if me.pos_x > stage.max_x - EDGE_MARGIN:
		return Buttons.LEFT
	if me.pos_x < stage.min_x + EDGE_MARGIN:
		return Buttons.RIGHT
	return Buttons.NONE


## True when an opponent's attack is about to hit the bot, it can react in time to parry, and
## the per-attack roll says this is one it dodges.
static func _should_dodge(state: GameState, rules: MatchRules, self_index: int, skill: PackedInt32Array) -> bool:
	var fighters := state.fighters
	var me := fighters[self_index]
	var hb := Simulation.hurtbox(me, rules.tuning)

	for i in fighters.size():
		if i == self_index:
			continue
		var op := fighters[i]
		var m := Simulation.current_move(op, rules.tuning)
		if m == null or op.attack_connected or op.hit_stop > 0:
			continue

		var a := op.action_frame
		var arrives := m.startup
		if m.plunge_speed != 0:
			arrives = _slam_arrival(op, m, a, hb, rules.stage)
			if arrives < 0:
				continue

		# Is the bot in the way? Judge where it could be by the time the attack arrives.
		var box := Simulation.move_hitbox(op, m)
		var pad := THREAT_PADDING + Fx.ONE if m.lunge_speed != 0 else THREAT_PADDING
		var bottom := hb.min_y if m.plunge_speed != 0 else box.min_y - pad
		var danger := FxRect.new(box.min_x - pad, bottom, box.max_x + pad, maxi(box.max_y + pad, hb.max_y))
		if not danger.overlaps(_reach(me, hb, arrives - a, rules.tuning)):
			continue

		# One chance per attack, on the frame it would press to parry.
		var press_at := maxi(skill[1], arrives - PARRY_LEAD)
		if a != press_at or press_at >= arrives:
			continue
		if dodges_attack(state.frame - a, i, self_index, skill[2]):
			return true
	return false


## The hurtbox stretched sideways over where the fighter could be in `frames` frames.
static func _reach(me: Fighter, hb: FxRect, frames: int, t: FighterTuning) -> FxRect:
	var vx := me.vel_x
	if vx == 0 or frames <= 0:
		return hb
	var brake := t.ground_accel if me.grounded else t.air_accel
	var travel := absi(vx) * frames + Fx.div(Fx.mul(vx, vx), brake * 2)
	if vx > 0:
		return FxRect.new(hb.min_x, hb.min_y, hb.max_x + travel, hb.max_y)
	return FxRect.new(hb.min_x - travel, hb.min_y, hb.max_x, hb.max_y)


## When a ground slam on attack frame `a` will first come down to the height of the hurtbox, as an
## attack frame; -1 if it never will (C#'s SlamArrival returning false).
static func _slam_arrival(op: Fighter, m: MoveData, a: int, hb: FxRect, stage: StageDefinition) -> int:
	# It lands on the highest platform top under it.
	var land := stage.blast_zone.min_y
	for plat in stage.platforms:
		if op.pos_x >= plat.min_x and op.pos_x <= plat.max_x and plat.max_y <= op.pos_y and plat.max_y > land:
			land = plat.max_y
	if land + m.hitbox_bottom >= hb.max_y:
		return -1 # stops above the bot's head
	if maxi(op.pos_y - m.plunge_speed, land) + m.hitbox_top <= hb.min_y:
		return -1
	var start := maxi(a + 1, m.startup)
	var gap := op.pos_y + m.hitbox_bottom - hb.max_y
	var arrives := start if gap < 0 else start + Fx.floor_to_int(Fx.div(gap, m.plunge_speed))
	return arrives if arrives < m.startup + m.active else -1


## In the air, only attack if the jab's drift still ends over the main stage.
static func _safe_to_attack(me: Fighter, stage: FxRect, t: FighterTuning) -> bool:
	if me.grounded:
		return true
	var vx := me.vel_x
	var brake := Fx.div(Fx.mul(vx, absi(vx)), t.air_accel * 2)
	var end_x := me.pos_x + vx * t.moves.jab.total_frames() + brake
	return end_x >= stage.min_x + LANDING_MARGIN and end_x <= stage.max_x - LANDING_MARGIN


## Index of the platform the fighter is standing on, or -1. Platform 0 is the main stage.
static func _standing_on(me: Fighter, stage: StageDefinition) -> int:
	for i in stage.platforms.size():
		var p := stage.platforms[i]
		if me.pos_y == p.max_y and me.pos_x >= p.min_x and me.pos_x <= p.max_x:
			return i
	return -1


static func _nearest_opponent(fighters: Array[Fighter], self_index: int) -> int:
	var at_x := fighters[self_index].pos_x
	var at_y := fighters[self_index].pos_y
	var best := -1
	var best_dist := 0
	for i in fighters.size():
		if i == self_index or not fighters[i].is_active():
			continue
		var dx := fighters[i].pos_x - at_x
		var dy := fighters[i].pos_y - at_y
		var d := Fx.mul(dx, dx) + Fx.mul(dy, dy)
		if best < 0 or d < best_dist:
			best = i
			best_dist = d
	return best


static func _hesitates(frame: int, self_index: int, odds: int) -> bool:
	var roll := SimRandom.new(((frame & 0xFFFFFFFF) << 8) ^ (self_index & 0xFFFFFFFF) ^ HESITATE_SALT)
	return roll.chance(odds - 1, odds)


static func _picks_heavy(frame: int, self_index: int) -> bool:
	var roll := SimRandom.new(((frame & 0xFFFFFFFF) << 8) ^ (self_index & 0xFFFFFFFF) ^ HEAVY_SALT)
	return roll.chance(1, FINISHER_ODDS)


## Dodges 1 attack in `odds`, rolled once per attack (keyed by when it started and who threw it).
static func dodges_attack(attack_start: int, attacker: int, self_index: int, odds: int) -> bool:
	var roll := SimRandom.new(((attack_start & 0xFFFFFFFF) << 16) ^ ((attacker & 0xFFFFFFFF) << 8)
		^ (self_index & 0xFFFFFFFF) ^ DODGE_SALT)
	return roll.chance(1, odds)
