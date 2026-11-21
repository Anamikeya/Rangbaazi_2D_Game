class_name FighterPainter
extends RefCounted
## Draws one fighter from the art pack's cutout animation (docs/design/animation-brief-for-chatgpt.md,
## and the handoff's sampling rules): picks the move from the fighter's state, samples its keyframes
## at the fighter's own action frame (so hit-stop freezes the pose and a rollback redraws it exactly),
## swaps faces and drawn extras by slot, composes child parts on their parent, and adds the live
## springs for hats, antennae and drips. Reads the GameState only; never changes it.
##
## Coordinates: the art's logical pixels, origin at the feet, y down on screen (negative y is up),
## facing right; the arena gives the screen position of the feet and how big a logical pixel is.

const EASINGS := ["linear", "step", "in", "out", "in_out", "out_back", "out_elastic"]
const SLAM_HANG := 10 # ground slam startup (combat spec v0.1)
const BIG_JOSH := 60 # past this, a hit launches the fighter tumbling
const MOVE_CLIPS := {
	MoveData.Id.JAB: "jab", MoveData.Id.NEUTRAL_HEAVY: "neutral_heavy", MoveData.Id.SIDE_HEAVY: "side_heavy",
	MoveData.Id.UP_HEAVY: "up_heavy", MoveData.Id.DOWN_HEAVY: "down_heavy",
}

## Weapon attacks borrow a fist move's poses, stretched so the clip's strike lands on the weapon's
## release frame. Which clip a weapon borrows is in the weapon table (WeaponInfo.clip, by the row
## whose move it is); a throw borrows the jab. This is each of those clips' strike frame.
const CLIP_STRIKE := {"jab": 4, "neutral_heavy": 14, "side_heavy": 16, "down_heavy": 12}
const THROW_CLIP := "jab"
## Standing still (and posing in the fighter picker), the pack holds the fists up at eye height,
## where beside a round head they read as eyebrows. In these clips a fist stays at least
## GUARD_BELOW art pixels below the middle of the head, keeping GUARD_KEEP of any rise above that.
const GUARD_CLIPS := ["idle", "idle_fidget_a", "idle_fidget_b", "select_pose"]
## The art pack's other faces (smug, happy, effort, hurt, dizzy) are each fighter's own face with a
## stock expression pasted on top: a second pair of eyes or brows and a second mouth. Until the
## pack redraws them, every clip shows the fighter's own face. Set true to use the pack's faces.
const PACK_FACES := false
## The pack's two spring pieces per fighter ("detail" and "secondary": a leaf, a hair lock, a tie
## tip) are mostly stock teardrops and domes pinned to the head at fixed spots, so they sit over an
## eye, an ear or an antenna; the head art already has its own hat. Set true to draw them again.
const PACK_EXTRAS := false
const GUARD_BELOW := 30.0
const GUARD_KEEP := 0.15
const FIDGET_AFTER := 300 # frames standing still before an idle fidget
const HARD_LANDING := 0.22 # metres per frame of fall speed

## Match result for pick(): nothing yet, this fighter won, or it lost.
enum Result { NONE, WON, LOST }

var character_id := "shahn"
## Where the front hand was drawn last (screen pixels), for the weapon it holds.
var hand_position := Vector2.ZERO
var hand_valid := false
var _rig: Dictionary = {}
# Live springs: part -> [angle, angular velocity].
var _springs: Dictionary = {}
var _last_x := 0.0
var _last_y := 0.0
var _have_last := false
var _vx := 0.0 # metres per frame, from the fighter's positions (the server doesn't send velocity)
var _vy := 0.0
var _last_frame := -1
# One-shot moves the sim doesn't know about (landing, jumping, respawning, fidgets): the clip
# playing now and the frame it started on.
var _shot := ""
var _shot_start := 0
var _shot_air := false
var _prev_action := -1
var _prev_grounded := false
var _prev_hit_stop := 0
var _prev_running := false
var _still_frames := 0
var _fidget_b := false
var _result_start := -1
# The rig's parts that have a spring (made on first use; "-" alone when it has none).
var _spring_parts: Array[String] = []


func _init(id: String) -> void:
	character_id = id
	_rig = ArtLibrary.character(id)
	if _rig.is_empty() and id != "shahn":
		character_id = "shahn"
		_rig = ArtLibrary.character("shahn")


func is_ready() -> bool:
	return not _rig.is_empty()


## Tracks the fighter's motion once per sim frame (velocity, springs). Call before draw.
func observe(f: Fighter, frame: int) -> void:
	if frame == _last_frame:
		return
	var x := Fx.to_float(f.pos_x)
	var y := Fx.to_float(f.pos_y)
	var steady := _have_last and frame == _last_frame + 1
	var fall_speed := -_vy
	var old_vy := _vy
	if steady:
		_vx = x - _last_x
		_vy = y - _last_y
	else:
		_vx = 0.0
		_vy = 0.0
	_last_x = x
	_last_y = y
	_have_last = true
	_last_frame = frame
	_step_springs()
	if steady:
		_watch(f, frame, fall_speed, old_vy)
	else:
		_shot = ""
	_prev_action = f.action
	_prev_grounded = f.grounded
	_prev_hit_stop = f.hit_stop
	_prev_running = f.grounded and absf(_vx) > 0.03


## Starts the one-shot moves: landing (soft or hard), the jump and air jump, coming back after a
## KO, a parry landing, starting and stopping a run, and a fidget after standing still a while.
func _watch(f: Fighter, frame: int, fall_speed: float, old_vy: float) -> void:
	var idle := f.action == Fighter.Action.IDLE
	var running := f.grounded and absf(_vx) > 0.03
	if _prev_action == Fighter.Action.RESPAWNING and f.is_active():
		_start("respawn", frame, true)
	elif idle and f.grounded and not _prev_grounded:
		_start("land_hard" if fall_speed >= HARD_LANDING else "land_soft", frame, false)
	elif idle and not f.grounded and _prev_grounded and _vy > 0.1:
		_start("ground_jump", frame, true)
	elif idle and not f.grounded and not _prev_grounded and _vy - old_vy > 0.1 and _prev_action == Fighter.Action.IDLE:
		_start("air_jump", frame, true)
	elif idle and f.hit_stop > 0 and _prev_hit_stop == 0 and not f.frozen_by_hit and _prev_action == Fighter.Action.DODGE:
		_start("parry_success", frame, not f.grounded)
	elif idle and running and not _prev_running and _prev_grounded and _shot == "":
		_start("run_start", frame, false)
	elif idle and f.grounded and not running and _prev_running:
		_start("skid_stop", frame, false)
	if idle and f.grounded and not running:
		_still_frames += 1
		if _still_frames >= FIDGET_AFTER and _shot == "":
			_start("idle_fidget_b" if _fidget_b else "idle_fidget_a", frame, false)
			_fidget_b = not _fidget_b
			_still_frames = 0
	else:
		_still_frames = 0


func _start(clip_name: String, frame: int, in_air: bool) -> void:
	if ArtLibrary.clip(character_id, clip_name).is_empty():
		return
	_shot = clip_name
	_shot_start = frame
	_shot_air = in_air


## The move and frame to show for this fighter: [clip name, frame]. `result` is how the match
## ended for it (Result), so winners celebrate and the rest slump. `rules` gives weapon moves
## their timing.
func pick(f: Fighter, frame: int, result: int = Result.NONE, rules: MatchRules = null) -> Array:
	var af := f.action_frame
	if result != Result.NONE and f.grounded and (f.action == Fighter.Action.IDLE):
		if _result_start < 0:
			_result_start = frame
		return ["victory_a" if result == Result.WON else "defeat", frame - _result_start]
	if result == Result.NONE:
		_result_start = -1
	match f.action:
		Fighter.Action.ATTACK:
			if f.move == MoveData.Id.GROUND_SLAM:
				if af < SLAM_HANG:
					return ["ground_slam_hang", af]
				if not f.grounded and af < 49:
					return ["ground_slam_plunge", af - SLAM_HANG]
				return ["ground_slam_impact", maxi(0, af - 49)]
			var weapon_clip := THROW_CLIP if f.move == MoveData.Id.WEAPON_THROW else WeaponInfo.clip(Weapons.row_of_move(f.move))
			if CLIP_STRIKE.has(weapon_clip) and rules != null:
				return _weapon_pose(f, rules, weapon_clip)
			return [MOVE_CLIPS.get(f.move, "jab"), af]
		Fighter.Action.DODGE:
			if not f.grounded:
				return ["dodge_air", af]
			return ["dodge_dash" if absf(_vx) > 0.04 else "dodge_spot", af]
		Fighter.Action.HITSTUN:
			if f.hit_stop > 0 and f.frozen_by_hit:
				return ["hit_impact_heavy" if f.hit_stop >= 7 else "hit_impact_light", 0]
			if not f.grounded and f.josh >= BIG_JOSH:
				return ["launch_tumble", frame]
			return ["hitstun", af]
		Fighter.Action.STUNNED:
			return ["parried_stun", af]
	# A one-shot keeps playing while the fighter stays as it was when it started (in the air or on
	# the ground) and until its clip runs out.
	if _shot != "" and f.action == Fighter.Action.IDLE:
		var played := frame - _shot_start
		var length := int(ArtLibrary.clip(character_id, _shot).get("d", 1))
		var fits := (not f.grounded) if _shot_air and _shot != "respawn" and _shot != "parry_success" else (f.grounded or _shot_air)
		var run_breaks := absf(_vx) > 0.03 and (_shot.begins_with("land") or _shot.begins_with("idle_fidget") or _shot == "skid_stop")
		if played >= 0 and played < length and fits and not run_breaks:
			return [_shot, played]
		_shot = ""
	elif f.action != Fighter.Action.IDLE:
		_shot = ""
	if not f.grounded:
		return ["rising" if _vy > 0.02 else ("apex" if _vy > -0.04 else "falling_loop"), frame]
	if absf(_vx) > 0.03:
		return ["run_loop", frame]
	return ["idle", frame]


## A weapon attack's pose: a fist move's clip, with its wind-up stretched to end on the weapon's
## release frame and its follow-through stretched over the weapon's recovery.
func _weapon_pose(f: Fighter, rules: MatchRules, clip_name: String) -> Array:
	var clip := ArtLibrary.clip(character_id, clip_name)
	var length := int(clip.get("d", 1))
	var strike := int(CLIP_STRIKE[clip_name])
	var m := rules.tuning.moves.get_move(f.move)
	var af := float(f.action_frame)
	var cf := af * strike / maxf(1.0, m.startup)
	if af > m.startup:
		cf = strike + (af - m.startup) * (length - strike) / maxf(1.0, m.total_frames() - m.startup)
	return [clip_name, clampi(int(cf), 0, length - 1)]


## Draws the fighter on `canvas` with its feet at `feet` (screen pixels), `px` screen pixels per
## logical art pixel, facing +1 right or -1 left. Optional move override for menus (e.g. "idle").
func draw(canvas: CanvasItem, feet: Vector2, px: float, facing: int, clip_name: String, frame: int, alpha := 1.0, tint := Color.WHITE) -> void:
	hand_valid = false
	if _rig.is_empty():
		return
	var clip := ArtLibrary.clip(character_id, clip_name)
	if clip.is_empty():
		clip = ArtLibrary.clip(character_id, "idle")
		clip_name = "idle"
	var d := maxi(1, int(clip.get("d", 1)))
	var f := frame % d if bool(clip.get("loop", false)) else clampi(frame, 0, d - 1)
	var base := Transform2D(0.0, Vector2(px * facing, px), 0.0, feet)
	var world: Dictionary = {}
	for e: Array in _plan(clip_name, clip, f):
		var part: String = e[0]
		var pose: Array = e[1]
		var spin := _spring_angle(part) if not _springs.is_empty() else 0.0
		var local: Transform2D = e[2] if spin == 0.0 else Transform2D(pose[2] + spin, Vector2(pose[3], pose[4]), 0.0, Vector2(pose[0], pose[1]))
		var parent: String = e[3]
		var m: Transform2D = ((world[parent] if world.has(parent) else e[4]) * local) if parent != "" else local
		world[part] = m
		if e[5]:
			continue # a spring piece the pack's extras would draw (PACK_EXTRAS)
		if part == "frontHand":
			hand_position = (base * m).origin
			hand_valid = true
		var tex: Texture2D = e[6]
		if tex == null:
			continue
		canvas.draw_set_transform_matrix(base * m)
		canvas.draw_texture_rect(tex, e[7], false, Color(tint.r, tint.g, tint.b, float(e[8]) * alpha))
	canvas.draw_set_transform_matrix(Transform2D.IDENTITY)


## What draw() does for frame f of a clip, worked out once and kept (rooms v1, polish 6: fifteen
## fighters sampled their keyframes from dictionaries on every frame): per part in draw order,
## [part, pose, its local transform without a spring, parent, the parent's own transform for when
## the parent isn't drawn, skipped, texture, rect, opacity]. Everything but the springs depends only
## on the clip and the frame. A frame whose textures haven't all loaded isn't kept.
var _plans: Dictionary = {}
var _planned := 0
const PLAN_LIMIT := 6000


func _plan(clip_name: String, clip: Dictionary, f: int) -> Array:
	var per: Dictionary = _plans.get(clip_name, {})
	if per.has(f):
		return per[f]
	var parts: Dictionary = _rig["parts"]
	var guard := GUARD_CLIPS.has(clip_name)
	var guard_y := 0.0
	if guard:
		guard_y = float(_sample(clip, "head", f, parts.get("head", {}))[1]) + GUARD_BELOW
	var out: Array = []
	var complete := true
	for part: String in _draw_order(clip, f, parts):
		var spec: Dictionary = parts.get(part, {})
		if spec.is_empty():
			continue
		var pose := _sample(clip, part, f, spec)
		if guard and (part == "frontHand" or part == "backHand") and pose[1] < guard_y:
			pose[1] = guard_y + (pose[1] - guard_y) * GUARD_KEEP
		var local := Transform2D(pose[2], Vector2(pose[3], pose[4]), 0.0, Vector2(pose[0], pose[1]))
		var parent := str(spec.get("parent", ""))
		var parent_own := _part_transform(clip, parent, f, parts) if parent != "" else Transform2D.IDENTITY
		var skipped := not PACK_EXTRAS and spec.has("spring")
		var tex: Texture2D = null
		var rect := Rect2()
		if not skipped:
			var slot := _slot(clip, part, f, spec)
			var textures: Dictionary = spec.get("textures", {})
			if part == "head" and not PACK_FACES:
				slot = "" # the head's own face, below
			if not textures.has(slot):
				slot = str(spec.get("texture", "normal"))
			var file: Variant = textures.get(slot, null)
			if file != null:
				tex = ArtLibrary.character_texture(character_id, str(file))
				complete = complete and tex != null
				var w := float(spec.get("width", 32))
				var h := float(spec.get("height", 32))
				var pivot: Dictionary = spec.get("pivot", {"x": 0.5, "y": 0.5})
				var geo: Dictionary = spec.get("textureGeometry", {}).get(slot, {})
				if not geo.is_empty():
					w = float(geo.get("width", w))
					h = float(geo.get("height", h))
					pivot = geo.get("pivot", pivot)
				rect = Rect2(-w * float(pivot["x"]), -h * float(pivot["y"]), w, h)
		out.append([part, pose, local, parent, parent_own, skipped, tex, rect, clampf(pose[5], 0.0, 1.0)])
	if complete:
		if _planned >= PLAN_LIMIT:
			_plans.clear()
			_planned = 0
		per[f] = out
		_plans[clip_name] = per
		_planned += 1
	return out


## [x, y, rotation, scaleX, scaleY, opacity] of a part at a frame: keys eased with the next key's
## easing; rest_offset clips add the rig's rest position; parts without keys stand at rest.
func _sample(clip: Dictionary, part: String, f: int, spec: Dictionary) -> Array:
	var rest: Dictionary = spec.get("rest", {})
	var keys: Array = clip.get("tracks", {}).get(part, [])
	if keys.is_empty():
		return [float(rest.get("x", 0)), float(rest.get("y", 0)), float(rest.get("rotation", 0)),
			float(rest.get("scaleX", 1)), float(rest.get("scaleY", 1)), float(rest.get("opacity", 1))]
	var a: Array = keys[0]
	var b: Array = []
	for k: Array in keys:
		if int(k[0]) <= f:
			a = k
		else:
			b = k
			break
	var v := [float(a[1]), float(a[2]), float(a[3]), float(a[4]), float(a[5]), float(a[6])]
	if not b.is_empty() and int(b[0]) > int(a[0]) and int(a[0]) <= f:
		var t := float(f - int(a[0])) / float(int(b[0]) - int(a[0]))
		var e := _ease(int(b[7]), t)
		for i in 6:
			v[i] = lerpf(float(a[i + 1]), float(b[i + 1]), e)
	if int(clip.get("pm", 0)) == 1:
		v[0] += float(rest.get("x", 0))
		v[1] += float(rest.get("y", 0))
	return v


func _part_transform(clip: Dictionary, part: String, f: int, parts: Dictionary) -> Transform2D:
	var p := _sample(clip, part, f, parts.get(part, {}))
	return Transform2D(p[2], Vector2(p[3], p[4]), 0.0, Vector2(p[0], p[1]))


func _slot(clip: Dictionary, part: String, f: int, spec: Dictionary) -> String:
	var slot := str(spec.get("texture", "normal"))
	for k: Array in clip.get("tex", {}).get(part, []):
		if int(k[0]) <= f:
			slot = str(k[1])
	return slot


func _draw_order(clip: Dictionary, f: int, parts: Dictionary) -> Array:
	var chosen: Array = []
	for o: Array in clip.get("order", []):
		if int(o[0]) <= f:
			chosen = o[1]
	var order: Array = chosen.duplicate()
	# Parts the clip's order doesn't name (CPU-less rigs, extras) go in by their z.
	var rest: Array = []
	for p: String in parts.keys():
		if not order.has(p):
			rest.append(p)
	rest.sort_custom(func(a: String, b: String) -> bool: return float(parts[a].get("z", 0)) < float(parts[b].get("z", 0)))
	if order.is_empty():
		return rest
	for p: String in rest:
		order.append(p)
	return order


static func _ease(i: int, t: float) -> float:
	t = clampf(t, 0.0, 1.0)
	match i:
		1: return 0.0 # step: hold until the key
		2: return t * t
		3: return 1.0 - (1.0 - t) * (1.0 - t)
		4: return t * t * (3.0 - 2.0 * t)
		5:
			var c1 := 1.70158
			var c3 := c1 + 1.0
			return 1.0 + c3 * pow(t - 1.0, 3) + c1 * pow(t - 1.0, 2)
		6:
			if t == 0.0 or t == 1.0:
				return t
			return pow(2.0, -10.0 * t) * sin((t * 10.0 - 0.75) * TAU / 3.0) + 1.0
	return t


## Springs on split pieces: they lean against the fighter's motion and wobble back, stiff or loose
## by the rig's settings (stiffness and bounce, 0 to 1).
func _step_springs() -> void:
	if _spring_parts.is_empty() and not _rig.is_empty():
		_spring_parts = ["-"] # none found yet: marks the list as made
		for part: String in _rig.get("parts", {}).keys():
			if not (_rig["parts"][part].get("spring", {}) as Dictionary).is_empty():
				_spring_parts.append(part)
	for part: String in _spring_parts:
		if part == "-":
			continue
		var spring: Dictionary = _rig["parts"][part]["spring"]
		var s: Array = _springs.get(part, [0.0, 0.0])
		var stiffness := 0.04 + 0.3 * float(spring.get("stiffness", 0.5))
		var damping := 0.04 + 0.3 * (1.0 - float(spring.get("bounce", 0.5)))
		var target := clampf(-_vx * 2.5 + _vy * 1.2, -0.7, 0.7)
		s[1] = s[1] * (1.0 - damping) + (target - s[0]) * stiffness
		s[0] = clampf(s[0] + s[1], -0.9, 0.9)
		_springs[part] = s


func _spring_angle(part: String) -> float:
	return float(_springs.get(part, [0.0, 0.0])[0])
