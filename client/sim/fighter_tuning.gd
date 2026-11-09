class_name FighterTuning
extends RefCounted
## Movement and dodge numbers (FighterTuning.cs). Fix fields are in Fx units.

var run_speed := Fx.ratio(13, 100)        # ~7.8 m/s
var air_speed := Fx.ratio(11, 100)
var ground_accel := Fx.ratio(2, 100)
var air_accel := Fx.ratio(1, 100)
var gravity := Fx.ratio(1, 100)
var max_fall_speed := Fx.ratio(30, 100)
var jump_velocity := Fx.ratio(25, 100)
var air_jump_velocity := Fx.ratio(22, 100)
var air_jumps := 1                        # GDD: jump x2

var half_width := Fx.ratio(2, 5)
var height := Fx.ratio(9, 5)

# Dodge: 0.3 s invincible, one air dodge per trip into the air, 1 s cooldown.
var dodge_intangible_frames := 18
var dodge_total_frames := 24              # the last 6 frames can be hit
var dodge_cooldown_frames := 60           # counted from the end of the dodge
var ground_dodge_speed := Fx.ratio(16, 100)
var air_dodge_speed := Fx.ratio(16, 100)

# Parry: a hit in the first parry_window_frames of a dodge is parried.
var parry_window_frames := 6
var parry_stun_frames := 36

var moves: MoveSet = MoveSet.all_rounder()

static var _default: FighterTuning


static func default_tuning() -> FighterTuning:
	if _default == null:
		_default = FighterTuning.new()
	return _default


func add_to(h: StateHasher) -> void:
	for v in [run_speed, air_speed, ground_accel, air_accel, gravity, max_fall_speed, jump_velocity,
			air_jump_velocity, air_jumps, half_width, height, dodge_intangible_frames, dodge_total_frames,
			dodge_cooldown_frames, ground_dodge_speed, air_dodge_speed, parry_window_frames, parry_stun_frames]:
		h.add_u64(v)
	for id in range(MoveData.Id.JAB, MoveData.Id.GROUND_SLAM + 1):
		var m: MoveData = moves.get_move(id)
		for v in [m.startup, m.active, m.recovery, m.damage, m.base_knockback, m.knockback_growth, m.launch_x,
				m.launch_y, m.hitstun_bonus, m.hitbox_near, m.hitbox_far, m.hitbox_bottom, m.hitbox_top,
				m.hits_both_sides, m.impulse_frame, m.lunge_speed, m.rise_speed, m.air_control, m.plunge_speed,
				m.uses_recovery]:
			h.add_u64(int(v))
