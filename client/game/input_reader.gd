class_name InputReader
## Turns keys and gamepads into the sim's Buttons (port of Controls/InputBindings.cs). The key
## actions are registered in code instead of project.godot, so every binding lives here and
## remapping can be added in one place later.
##
##   P1 keyboard: A / D move, W up, S down, Space jump, J light, K heavy, L dodge, U throw weapon
##   P2 keyboard: arrows move and aim, Numpad 0 jump, 1 light, 2 heavy, 3 dodge, 4 throw weapon
##   Gamepads: left stick or d-pad, A jump, X light, Y heavy, RB dodge, LB throw weapon. P1 has the first
##     connected pad and P2 the second; with one person on this device (online, or practice
##     against the CPU) P1 has every pad, so a pad still works when the OS lists a non-pad
##     device first (some Android phones report their fingerprint sensor as a joypad).
##   Menu and practice keys: F2 P2 CPU / human, F3 CPU level, F4 Guts on / off,
##     F5 or R restart (rematch online), Esc back to the menu. Pad: Start restart, Back menu.
##
## Reading only: nothing here touches GameState. The touch controls (TouchControls) are read
## separately and OR'ed into P1's buttons by the scene.

const PLAYERS := 2

const RESTART := &"rb_restart"
const TOGGLE_CPU := &"rb_toggle_cpu"
const CPU_LEVEL := &"rb_cpu_level"
const TOGGLE_GUTS := &"rb_toggle_guts"
const BACK := &"rb_back"

## Sim buttons and their action-name suffixes. KEYS and PAD_BUTTONS use the same order.
const BUTTONS := [
	Buttons.LEFT, Buttons.RIGHT, Buttons.UP, Buttons.DOWN,
	Buttons.JUMP, Buttons.LIGHT, Buttons.HEAVY, Buttons.DODGE, Buttons.DROP_WEAPON,
]
const SUFFIXES := ["left", "right", "up", "down", "jump", "light", "heavy", "dodge", "throw"]

## Default physical keys (layout-independent) for P1 and P2, in BUTTONS order.
const KEYS := [
	[KEY_A, KEY_D, KEY_W, KEY_S, KEY_SPACE, KEY_J, KEY_K, KEY_L, KEY_U],
	[KEY_LEFT, KEY_RIGHT, KEY_UP, KEY_DOWN, KEY_KP_0, KEY_KP_1, KEY_KP_2, KEY_KP_3, KEY_KP_4],
]

## Pad buttons in BUTTONS order (the d-pad for the four directions).
const PAD_BUTTONS := [
	JOY_BUTTON_DPAD_LEFT, JOY_BUTTON_DPAD_RIGHT, JOY_BUTTON_DPAD_UP, JOY_BUTTON_DPAD_DOWN,
	JOY_BUTTON_A, JOY_BUTTON_X, JOY_BUTTON_Y, JOY_BUTTON_RIGHT_SHOULDER, JOY_BUTTON_LEFT_SHOULDER,
]

## Stick past this counts as held (same dead zone as the C# bindings).
const DEAD_ZONE := 0.4

## _actions[player][i] is the key action for BUTTONS[i]; filled by register().
static var _actions: Array = []


## Adds the actions and their default keys to the InputMap. Safe to call more than once.
static func register() -> void:
	if not _actions.is_empty():
		return
	for p in PLAYERS:
		var names: Array[StringName] = []
		for i in BUTTONS.size():
			var action := StringName("p%d_%s" % [p + 1, SUFFIXES[i]])
			names.append(action)
			if not InputMap.has_action(action):
				InputMap.add_action(action)
			InputMap.action_add_event(action, _key_event(KEYS[p][i]))
		_actions.append(names)

	_add_global(RESTART, [KEY_F5, KEY_R], JOY_BUTTON_START)
	_add_global(TOGGLE_CPU, [KEY_F2])
	_add_global(CPU_LEVEL, [KEY_F3])
	_add_global(TOGGLE_GUTS, [KEY_F4])
	_add_global(BACK, [KEY_ESCAPE], JOY_BUTTON_BACK)


## This player's held keys and pad buttons as Buttons flags (unknown bits masked off).
## every_pad: P1 reads every connected pad (only one person plays on this device).
static func read(player: int, every_pad := false) -> int:
	if player < 0 or player >= PLAYERS:
		return Buttons.NONE
	register()
	var names: Array[StringName] = _actions[player]
	var b := Buttons.NONE
	for i in BUTTONS.size():
		if Input.is_action_pressed(names[i]):
			b |= BUTTONS[i]
	var pads := Input.get_connected_joypads()
	pads.sort()
	if every_pad and player == 0:
		for device: int in pads:
			b |= _read_pad(device)
	elif player < pads.size():
		b |= _read_pad(pads[player])
	return b & Buttons.KNOWN


## Lets go of every fighter key, e.g. when the window loses focus while a key is held and its
## release would never arrive. The next real press presses it again.
static func release_all() -> void:
	for names: Array[StringName] in _actions:
		for action in names:
			Input.action_release(action)


## The default physical key for BUTTONS[index] of a player (the scene smoke test presses these).
static func key_for(player: int, index: int) -> Key:
	return KEYS[player][index]


## One pad's buttons: left stick or d-pad, A / X / Y / RB.
static func _read_pad(device: int) -> int:
	var b := Buttons.NONE
	for i in BUTTONS.size():
		if Input.is_joy_button_pressed(device, PAD_BUTTONS[i]):
			b |= BUTTONS[i]
	var x := Input.get_joy_axis(device, JOY_AXIS_LEFT_X)
	var y := Input.get_joy_axis(device, JOY_AXIS_LEFT_Y) # down is positive on pads
	if x < -DEAD_ZONE:
		b |= Buttons.LEFT
	elif x > DEAD_ZONE:
		b |= Buttons.RIGHT
	if y < -DEAD_ZONE:
		b |= Buttons.UP
	elif y > DEAD_ZONE:
		b |= Buttons.DOWN
	return b


## A binding for a physical key from any keyboard (device -1 matches every device).
static func _key_event(key: Key) -> InputEventKey:
	var e := InputEventKey.new()
	e.device = -1
	e.physical_keycode = key
	return e


## A menu / debug action on these keys and, optionally, a button on any pad.
static func _add_global(action: StringName, keys: Array, pad_button: int = -1) -> void:
	if not InputMap.has_action(action):
		InputMap.add_action(action)
	for k: Key in keys:
		InputMap.action_add_event(action, _key_event(k))
	if pad_button >= 0:
		var b := InputEventJoypadButton.new()
		b.device = -1 # all devices
		b.button_index = pad_button as JoyButton
		InputMap.action_add_event(action, b)
