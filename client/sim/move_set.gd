class_name MoveSet
extends RefCounted
## A fighter's six moves (MoveSet.cs). ALL_ROUNDER is the first hero's set.

var jab: MoveData
var neutral_heavy: MoveData
var side_heavy: MoveData
var up_heavy: MoveData
var down_heavy: MoveData
var ground_slam: MoveData

static var _all_rounder: MoveSet


func get_move(id: int) -> MoveData:
	match id:
		MoveData.Id.JAB: return jab
		MoveData.Id.NEUTRAL_HEAVY: return neutral_heavy
		MoveData.Id.SIDE_HEAVY: return side_heavy
		MoveData.Id.UP_HEAVY: return up_heavy
		MoveData.Id.DOWN_HEAVY: return down_heavy
		MoveData.Id.GROUND_SLAM: return ground_slam
	var weapon_move := Weapons.move_for(id)
	if weapon_move != null:
		return weapon_move
	push_error("No move data for move %d" % id)
	return null


static func all_rounder() -> MoveSet:
	if _all_rounder != null:
		return _all_rounder
	var s := MoveSet.new()
	s.jab = MoveData.make({
		startup = 4, active = 3, recovery = 8,
		damage = 6,
		base_knockback = Fx.ratio(12, 100), knockback_growth = Fx.ratio(3, 1000),
		launch_x = Fx.ratio(7071, 10000), launch_y = Fx.ratio(7071, 10000), # 45 degrees
		hitbox_near = Fx.ratio(1, 5), hitbox_far = Fx.ratio(11, 10),
		hitbox_bottom = Fx.ratio(3, 5), hitbox_top = Fx.ratio(7, 5),
	})
	s.neutral_heavy = MoveData.make({
		startup = 14, active = 4, recovery = 22,
		damage = 14,
		base_knockback = Fx.ratio(16, 100), knockback_growth = Fx.ratio(225, 100000),
		launch_x = Fx.ratio(7660, 10000), launch_y = Fx.ratio(6428, 10000), # 40 degrees
		hitstun_bonus = 4,
		hitbox_near = Fx.ratio(1, 5), hitbox_far = Fx.ratio(7, 5),
		hitbox_bottom = Fx.ratio(2, 5), hitbox_top = Fx.ratio(8, 5),
	})
	s.side_heavy = MoveData.make({
		startup = 16, active = 5, recovery = 24,
		damage = 16,
		base_knockback = Fx.ratio(156, 1000), knockback_growth = Fx.ratio(23, 10000),
		launch_x = Fx.ratio(8660, 10000), launch_y = Fx.HALF, # 30 degrees: flatter, for ring-outs
		hitstun_bonus = 4,
		hitbox_near = Fx.ratio(3, 10), hitbox_far = Fx.ratio(3, 2),
		hitbox_bottom = Fx.HALF, hitbox_top = Fx.ratio(7, 5),
		impulse_frame = 10, lunge_speed = Fx.ratio(16, 100),
	})
	s.up_heavy = MoveData.make({
		startup = 6, active = 8, recovery = 26,
		damage = 10,
		base_knockback = Fx.ratio(17, 100), knockback_growth = Fx.ratio(22, 10000),
		launch_x = Fx.ratio(1736, 10000), launch_y = Fx.ratio(9848, 10000), # 80 degrees
		hitstun_bonus = 2,
		hitbox_near = Fx.ratio(-3, 10), hitbox_far = Fx.ratio(9, 10),
		hitbox_bottom = Fx.ratio(4, 5), hitbox_top = Fx.ratio(12, 5),
		impulse_frame = 6, rise_speed = Fx.ratio(27, 100),
		air_control = true, uses_recovery = true,
	})
	s.down_heavy = MoveData.make({
		startup = 12, active = 4, recovery = 22,
		damage = 12,
		base_knockback = Fx.ratio(15, 100), knockback_growth = Fx.ratio(18, 10000),
		launch_x = Fx.ratio(9063, 10000), launch_y = Fx.ratio(4226, 10000), # 25 degrees
		hitstun_bonus = 2,
		hitbox_near = 0, hitbox_far = Fx.ratio(13, 10),
		hitbox_bottom = 0, hitbox_top = Fx.ratio(7, 10),
		hits_both_sides = true,
	})
	s.ground_slam = MoveData.make({
		startup = 10, active = 40, recovery = 14,
		damage = 13,
		base_knockback = Fx.ratio(17, 100), knockback_growth = Fx.ratio(21, 10000),
		launch_x = Fx.ratio(3420, 10000), launch_y = Fx.ratio(9397, 10000), # 70 degrees
		hitstun_bonus = 4,
		hitbox_near = Fx.ratio(-1, 2), hitbox_far = Fx.HALF,
		hitbox_bottom = Fx.ratio(-2, 5), hitbox_top = Fx.ratio(3, 5),
		plunge_speed = Fx.ratio(2, 5),
	})
	_all_rounder = s
	return s
