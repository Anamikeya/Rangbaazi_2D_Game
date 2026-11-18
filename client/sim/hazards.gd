class_name Hazards
## The arena hazards' shared clock and numbers (Hazards in StageDefinition.cs). A hazard's phase is
## a pure function of the match frame (each one offset from the last), so hazards add nothing to
## the rollback state, and a scene can draw them from state.frame alone.

# HazardKind
const LAUNCH := 0 # a spring, bounce pad or steam vent: flings up a fighter standing on it
const HIT := 1    # a shutter, crusher, laser or burner: hits whoever is in it
const PUSH := 2   # a gust, conveyor or current: carries whoever is in it sideways

const PERIOD := 600      # a 10 s cycle
const WARN_AT := 420     # 0.75 s of warning ...
const ACTIVE_AT := 465   # ... then 1 s active ...
const COOLDOWN_AT := 525 # ... then cooling down until the cycle restarts
const STAGGER := 200     # each hazard runs this many frames ahead of the one before
const HIT_EVERY := 20    # a HIT hazard strikes on these frames while active

static var LAUNCH_SPEED := Fx.ratio(36, 100)
static var PUSH_SPEED := Fx.ratio(7, 100)

static var _strike: MoveData


## What a HIT hazard does to a fighter: 8 damage, launched up and away at 60 degrees.
static func strike() -> MoveData:
	if _strike == null:
		_strike = MoveData.make({
			damage = 8, base_knockback = Fx.ratio(15, 100), knockback_growth = Fx.ratio(22, 10000),
			launch_x = Fx.ratio(5000, 10000), launch_y = Fx.ratio(8660, 10000),
			hitstun_bonus = 2,
		})
	return _strike


## Where hazard `index` is in its cycle on this frame: 0 to PERIOD - 1.
static func cycle_at(frame: int, index: int) -> int:
	return (frame + index * STAGGER) % PERIOD


## "idle", "warning", "active" or "cooldown": the art's state names.
static func phase_name(frame: int, index: int) -> String:
	var at := cycle_at(frame, index)
	if at < WARN_AT:
		return "idle"
	if at < ACTIVE_AT:
		return "warning"
	return "active" if at < COOLDOWN_AT else "cooldown"
