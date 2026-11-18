class_name SceneSmoke
extends RefCounted
## Headless check of the real scene (run with `-- --smoke`):
##   godot --headless --path client -- --smoke
## The scene opens the menu, then plays PRACTICE for FRAMES frames with both fighters driven by
## SparringBot: P1 (Hard) through P1's keyboard path (this presses and releases P1's keys as key
## events, and the scene reads them back through InputReader like a person's keys), P2 (Normal)
## as the practice CPU. From TOUCH_FROM_FRAME the touch controls are on screen too (untouched, so
## they add no buttons), and on TOUCH_TEST_FRAME a multi-touch self-test presses, slides, lifts
## and cancels fingers on them, between two reads, so the match never sees those buttons.
##
## Passes (exit code 0) only if:
##   - every frame's P1 read equals the bot's buttons,
##   - the final checksum equals a pure Simulation run with the same bots, rules and seed,
##   - the touch self-test read back what each finger was holding,
##   - the match did something (P1 pressed buttons, someone was hit) and was drawn, so a broken
##     input path can't pass by pressing nothing,
##   - the engine logged no errors (e.g. a script error while drawing) during the run.
## Fails with exit code 1 otherwise, or after TIMEOUT_MSEC.

const FRAMES := 900
const SEED := 20261108
const TIMEOUT_MSEC := 60000
const P1_LEVEL := SparringBot.Level.HARD
const P2_LEVEL := SparringBot.Level.NORMAL
## Physics ticks per second while testing: one tick is still one sim frame, the ticks just come
## faster than real time (the sim doesn't care), so the check takes about 2 s instead of 15.
const TICKS_PER_SECOND := 480
const TOUCH_FROM_FRAME := 450
const TOUCH_TEST_FRAME := 600
## Fewer frames with P1 buttons held than this means the bots (or the key path) did nothing.
const MIN_PRESSED_FRAMES := 100
## Fewer match draws than this means the arena wasn't drawing the match.
const MIN_DRAWS := 30

var pressed_frames := 0 # frames on which P1 held at least one button

var _started_msec := Time.get_ticks_msec()
var _held := Buttons.NONE   # P1's buttons whose keys are down right now
var _wanted := Buttons.NONE # what the bot asked for this frame
var _errors := ErrorCounter.new()


## Counts the errors the engine logs (script errors, push_error) while the smoke test runs.
class ErrorCounter extends Logger:
	var count := 0
	var first := ""
	var _lock := Mutex.new()

	func _log_error(_function: String, file: String, line: int, code: String, rationale: String,
			_editor_notify: bool, error_type: int, _script_backtraces: Array[ScriptBacktrace]) -> void:
		if error_type == ERROR_TYPE_WARNING:
			return
		_lock.lock() # loggers can be called from any thread
		count += 1
		if first == "":
			first = "%s (%s:%d)" % [rationale if rationale != "" else code, file, line]
		_lock.unlock()


static func requested() -> bool:
	return OS.get_cmdline_user_args().has("--smoke") or OS.get_cmdline_args().has("--smoke")


func _init() -> void:
	OS.add_logger(_errors)


## Stops counting errors (call once the result is known).
func stop() -> void:
	OS.remove_logger(_errors)


## Before the scene reads its inputs: presses and releases P1's keys so that they spell this
## frame's bot buttons. Events are flushed at once so the read sees them this frame.
func press_keys(state: GameState, rules: MatchRules) -> void:
	_wanted = SparringBot.think(state, rules, 0, P1_LEVEL)
	for i in InputReader.BUTTONS.size():
		var bit: int = InputReader.BUTTONS[i]
		# A key is down when every button it stands for is wanted (the combo key is two at once).
		var down := (_wanted & bit) == bit
		if down == ((_held & bit) == bit):
			continue
		var e := InputEventKey.new()
		e.physical_keycode = InputReader.key_for(0, i)
		e.pressed = down
		Input.parse_input_event(e)
	Input.flush_buffered_events()
	_held = _wanted


## After the read: "" if P1's buttons came through the keyboard path unchanged, else why not.
func check_read(frame: int, p1_buttons: int) -> String:
	if p1_buttons != _wanted:
		return "frame %d: P1 read buttons %d, but the bot pressed %d" % [frame, p1_buttons, _wanted]
	if p1_buttons != Buttons.NONE:
		pressed_frames += 1
	return ""


## Presses, slides, lifts and cancels fingers on the (visible) touch controls and checks what
## they hold after each step; ends with every finger up. "" if all is well, else what went wrong.
func touch_test(touch: TouchControls, viewport: Viewport) -> String:
	if not touch.is_visible_in_tree():
		return "touch: the controls aren't on screen"
	var steps := [
		# [what, finger (touch index), button it is on, buttons held afterwards]
		["press", 0, Buttons.RIGHT, Buttons.RIGHT],
		["press", 1, Buttons.HEAVY, Buttons.RIGHT | Buttons.HEAVY],
		["drag", 0, Buttons.UP, Buttons.UP | Buttons.HEAVY],
		["drag", 1, Buttons.LIGHT, Buttons.UP | Buttons.LIGHT],
		["release", 0, Buttons.UP, Buttons.LIGHT],
		["press", 2, Buttons.JUMP, Buttons.LIGHT | Buttons.JUMP],
		["release", 1, Buttons.LIGHT, Buttons.JUMP],
		["cancel", 2, Buttons.JUMP, Buttons.NONE],
		["press", 3, Buttons.DODGE, Buttons.DODGE],
		["focus_out", 3, Buttons.DODGE, Buttons.NONE], # the app loses focus mid-touch
		["release", 3, Buttons.DODGE, Buttons.NONE],
		["press", 4, Buttons.LEFT, Buttons.LEFT],
		["drag", 4, Buttons.DOWN, Buttons.DOWN], # slid down below the pad, to the middle
		["release", 4, Buttons.DOWN, Buttons.NONE],
		["press", 5, Buttons.UP, Buttons.UP], # a tap straight on the Up arrow
		["release", 5, Buttons.UP, Buttons.NONE],
	]
	# Events arrive in window pixels; the controls work in viewport units (the stretch transform).
	var to_window := viewport.get_final_transform()
	for step: Array in steps:
		var what: String = step[0]
		var finger: int = step[1]
		var at: Vector2 = to_window * touch.point_for(step[2])
		match what:
			"press", "release", "cancel":
				var t := InputEventScreenTouch.new()
				t.index = finger
				t.position = at
				t.pressed = what == "press"
				t.canceled = what == "cancel"
				Input.parse_input_event(t)
			"drag":
				var d := InputEventScreenDrag.new()
				d.index = finger
				d.position = at
				Input.parse_input_event(d)
			"focus_out":
				touch.notification(Node.NOTIFICATION_APPLICATION_FOCUS_OUT)
		Input.flush_buffered_events()
		var held := touch.buttons()
		if held != step[3]:
			return "touch: after %s of finger %d the controls hold buttons %d, expected %d" % [what, finger, held, step[3]]
	return ""


## After the last frame: "" if the run did something and logged no errors, else why it doesn't count.
func final_problem(state: GameState, rules: MatchRules, match_draws: int) -> String:
	if _errors.count > 0:
		return "the engine logged %d error(s), the first: %s" % [_errors.count, _errors.first]
	if pressed_frames < MIN_PRESSED_FRAMES:
		return "P1 held buttons on only %d frames: the bots or the key path did nothing" % pressed_frames
	var hit := state.match_over
	for f in state.fighters:
		hit = hit or f.josh > 0 or f.stocks < rules.stocks
	if not hit:
		return "nobody was hit in %d frames" % state.frame
	if match_draws < MIN_DRAWS:
		return "the arena drew the match only %d times" % match_draws
	return ""


## The errors logged so far, for a failure caused by something else (e.g. a timeout).
func error_note() -> String:
	return "" if _errors.count == 0 else " (and %d engine error(s), the first: %s)" % [_errors.count, _errors.first]


func timed_out() -> bool:
	return Time.get_ticks_msec() - _started_msec > TIMEOUT_MSEC


func elapsed_msec() -> int:
	return Time.get_ticks_msec() - _started_msec


## The checksum after FRAMES frames of the same match run straight through the sim.
static func reference_checksum(rules: MatchRules) -> int:
	var state := Simulation.create_initial_state(rules, 2, SEED)
	var inputs := PackedInt32Array([0, 0])
	for frame in FRAMES:
		inputs[0] = SparringBot.think(state, rules, 0, P1_LEVEL)
		inputs[1] = SparringBot.think(state, rules, 1, P2_LEVEL)
		Simulation.advance(state, rules, inputs)
	return state.checksum()
