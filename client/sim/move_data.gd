class_name MoveData
extends RefCounted
## Frame data for one move (MoveData.cs; numbers documented in docs/design/combat-spec-v0.1.md).
## Immutable once built. Fix fields are in Fx units.

enum Id { NONE, JAB, NEUTRAL_HEAVY, SIDE_HEAVY, UP_HEAVY, DOWN_HEAVY, GROUND_SLAM }

var startup := 0
var active := 0
var recovery := 0
var damage := 0
var base_knockback := 0
var knockback_growth := 0
var launch_x := 0
var launch_y := 0
var hitstun_bonus := 0
var hitbox_near := 0
var hitbox_far := 0
var hitbox_bottom := 0
var hitbox_top := 0
var hits_both_sides := false
var impulse_frame := 0
var lunge_speed := 0
var rise_speed := 0
var air_control := false
var plunge_speed := 0
var uses_recovery := false


func total_frames() -> int:
	return startup + active + recovery


func is_active_on(action_frame: int) -> bool:
	return action_frame >= startup and action_frame < startup + active


static func make(values: Dictionary) -> MoveData:
	var m := MoveData.new()
	for key in values:
		m.set(key, values[key])
	return m
