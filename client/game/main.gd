extends Node2D
## RANGBAAZI's one scene: the menu, practice against the CPU, and online 1v1. Steps the
## deterministic sim (client/sim) once per physics tick (60 Hz) and draws its state with
## ArenaView. Gameplay lives only in the sim: this script picks each fighter's buttons (keys, pad,
## touch or a SparringBot) and hands them to Simulation.advance or the OnlineSession; it never
## changes a GameState itself.
##
## MENU -> PRACTICE: P1 is you, P2 a SparringBot (Normal). F2 makes P2 a second person on the P2
##   keys and back, F3 cycles the CPU level, F4 turns Guts on / off (restarts), F5 / R restarts,
##   Esc goes back to the menu. For touch, the menu button in the top-right corner opens a panel
##   (Resume, Restart, CPU level, Leave) and pauses practice while it is open.
## MENU -> ONLINE: create a room (random code) or join one by code, or open the page with
##   ?room=CODE on the web. An OnlineSession (client/net) runs the match over the relay; this
##   draws session.state and the connection status. R / F5 / Play again / Rematch plays again
##   once a match is over (someone won, or the session ended); Esc or the panel's Leave leaves.
##   The panel can't pause an online match: it says so.
## A "Play again" button shows in the middle once a match is over.
## `-- --smoke` runs the headless scene check instead (see SceneSmoke). `-- --touch` forces the
## touch controls on (and lets the mouse act as a finger) for testing them on a desktop.

enum Mode { MENU, PRACTICE, ONLINE }

const ONLINE_SESSION_PATH := "res://net/online_session.gd"
## What this scene calls on an OnlineSession; a script without them counts as missing.
const SESSION_METHODS := ["relay_url_for_room", "tick", "rematch", "close"]
const SESSION_PHASES := ["CONNECTING", "WAITING_FOR_OPPONENT", "STARTING", "RUNNING", "ENDED"]
const ROOM_ALPHABET := "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
const ROOM_CODE_LENGTH := 5
## Ticks the session must be stalled before "Waiting for your opponent..." shows.
const STALL_NOTICE_TICKS := 20
const COPIED_MSEC := 1500 # how long the copy button says "Copied!"
## The square menu button in the top-right corner, and its distance from the screen edges.
const MENU_BUTTON := 104.0
const MENU_MARGIN := 20.0
const LEVEL_NAMES := ["Easy", "Normal", "Hard"] # by SparringBot.Level
const PLAYERS := 2

var _mode: int = Mode.MENU

# Practice: settings are kept across restarts.
var _rules: MatchRules = MatchRules.new()
var _state: GameState
var _p2_cpu := true
var _cpu_level: int = SparringBot.Level.NORMAL
var _guts := true
var _inputs := PackedInt32Array([0, 0])
var _rng := RandomNumberGenerator.new() # room codes and practice seeds: never sim state

# Online. The session is untyped and its script loaded at runtime, so this scene still parses
# and runs PRACTICE in a build without client/net.
var _session_script: GDScript
var _session = null # OnlineSession
var _phases: Dictionary = {} # OnlineSession.Phase names -> values
var _stalled_ticks := 0
var _room := ""
var _share_link := ""
var _copied_until_msec := 0 # the copy button says "Copied!" until then
var _web_copy_target := "" # what _sync_web_copy last told the page ("" = button hidden)

var _smoke: SceneSmoke
var _smoke_done := false

## Whether to show the touch controls in a match: on where TouchControls.wanted(); on a desktop
## with a touchscreen, off once keys or a pad are used and on again at the next real touch.
var _prefer_touch := false

var _arena: ArenaView
var _menu: MenuScreen
var _touch: TouchControls
var _hud: Control
var _menu_button: Button # top-right: opens the panel
var _again_button: Button # middle: "Play again" once a match is over
var _copy_button: Button

# The panel the menu button opens (its own CanvasLayer, over the touch controls).
var _panel_layer: CanvasLayer
var _panel_title: Label
var _panel_note: Label
var _restart_button: Button
var _level_button: Button
var _rematch_button: Button


func _ready() -> void:
	# One physics tick = one sim frame.
	Engine.physics_ticks_per_second = Simulation.TICKS_PER_SECOND
	# Android's back button returns to the menu instead of quitting (see _notification).
	get_tree().quit_on_go_back = false
	if SceneSmoke.requested():
		_smoke = SceneSmoke.new() # first, so it counts errors from here on
	InputReader.register()
	_rng.randomize()
	_prefer_touch = TouchControls.wanted() and _smoke == null
	if TouchControls.forced():
		Input.emulate_touch_from_mouse = true # test the touch controls with a mouse
	_build_nodes()

	if _smoke != null:
		_begin_smoke()
		return
	if OS.has_feature("web") and TouchControls.handheld():
		JavaScriptBridge.eval(WEB_LANDSCAPE_JS)
	_go_to_menu()
	var code := _room_from_url()
	if code != "":
		_start_online(code)


func _build_nodes() -> void:
	_arena = ArenaView.new()
	_arena.name = "Arena"
	add_child(_arena)

	var hud_layer := CanvasLayer.new()
	hud_layer.name = "Hud"
	hud_layer.layer = 1
	add_child(hud_layer)
	_hud = Control.new()
	_hud.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_hud.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_hud.theme = UiTheme.build()
	hud_layer.add_child(_hud)

	_menu_button = _hud_button(_hud, "", _open_panel)
	_menu_button.icon = _menu_icon()
	_menu_button.icon_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_menu_button.tooltip_text = "Menu"
	_menu_button.custom_minimum_size = Vector2(MENU_BUTTON, MENU_BUTTON)
	_menu_button.set_anchors_preset(Control.PRESET_TOP_RIGHT)
	_menu_button.offset_left = -(MENU_MARGIN + MENU_BUTTON)
	_menu_button.offset_right = -MENU_MARGIN
	_menu_button.offset_top = MENU_MARGIN
	_menu_button.offset_bottom = MENU_MARGIN + MENU_BUTTON
	_again_button = _hud_button(_hud, "Play again", _on_play_again)
	_again_button.custom_minimum_size = Vector2(380, 110)
	_again_button.add_theme_font_size_override("font_size", 40)
	_copy_button = _hud_button(_hud, "Copy link", _on_copy)

	var touch_layer := CanvasLayer.new()
	touch_layer.name = "Touch"
	touch_layer.layer = 2
	add_child(touch_layer)
	_touch = TouchControls.new()
	_touch.visible = false
	touch_layer.add_child(_touch)

	_build_panel()

	var menu_layer := CanvasLayer.new()
	menu_layer.name = "Menu"
	menu_layer.layer = 3
	add_child(menu_layer)
	_menu = MenuScreen.new()
	menu_layer.add_child(_menu)
	_menu.practice_requested.connect(_start_practice)
	_menu.create_requested.connect(func() -> void: _start_online(_new_room_code()))
	_menu.join_requested.connect(_start_online)


## A HUD button: never takes keyboard focus, so Space and the arrows stay with the fighters.
func _hud_button(parent: Control, text: String, on_pressed: Callable) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.custom_minimum_size = Vector2(0, 84)
	b.add_theme_font_size_override("font_size", 30)
	b.pressed.connect(on_pressed)
	parent.add_child(b)
	return b


## Three white bars, the usual "menu" sign (drawn here: the default font has no such glyph).
static func _menu_icon() -> Texture2D:
	var image := Image.create(44, 36, false, Image.FORMAT_RGBA8)
	image.fill(Color(0, 0, 0, 0))
	for i in 3:
		image.fill_rect(Rect2i(0, i * 15, 44, 6), Color.WHITE)
	return ImageTexture.create_from_image(image)


## The panel the menu button opens: a dimmed screen (a tap on it closes the panel) and a column
## of big buttons. Which buttons show depends on the mode (see _refresh_panel).
func _build_panel() -> void:
	_panel_layer = CanvasLayer.new()
	_panel_layer.name = "Panel"
	_panel_layer.layer = 4
	_panel_layer.visible = false
	add_child(_panel_layer)
	var root := Control.new()
	root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	root.theme = UiTheme.build()
	_panel_layer.add_child(root)

	var backdrop := ColorRect.new()
	backdrop.color = Color(0.05, 0.045, 0.07, 0.6)
	backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	backdrop.gui_input.connect(func(event: InputEvent) -> void:
		# A click or tap closes the panel; a mouse wheel or trackpad scroll doesn't.
		var mouse := event as InputEventMouseButton
		if mouse != null and mouse.pressed and mouse.button_index == MOUSE_BUTTON_LEFT:
			_close_panel())
	root.add_child(backdrop)

	var center := CenterContainer.new()
	center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	root.add_child(center)
	var box := PanelContainer.new()
	box.add_theme_stylebox_override("panel", UiTheme.panel_box())
	center.add_child(box)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 20)
	column.custom_minimum_size = Vector2(560, 0)
	box.add_child(column)

	_panel_title = Label.new()
	_panel_title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_panel_title.add_theme_font_size_override("font_size", 52)
	_panel_title.add_theme_color_override("font_color", UiTheme.ACCENT)
	column.add_child(_panel_title)
	_panel_note = Label.new()
	_panel_note.text = "The match keeps going while this is open."
	_panel_note.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_panel_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_panel_note.add_theme_font_size_override("font_size", 26)
	_panel_note.add_theme_color_override("font_color", UiTheme.TEXT_DIM)
	column.add_child(_panel_note)

	_panel_button(column, "Resume", _close_panel)
	_restart_button = _panel_button(column, "Restart", func() -> void:
		_restart_practice()
		_close_panel())
	_level_button = _panel_button(column, "CPU: Normal", _on_level_pressed)
	_rematch_button = _panel_button(column, "Rematch", func() -> void:
		_on_rematch()
		_close_panel())
	_panel_button(column, "Leave match", _go_to_menu.bind(""))


func _panel_button(parent: Control, text: String, on_pressed: Callable) -> Button:
	var b := _hud_button(parent, text, on_pressed)
	b.custom_minimum_size = Vector2(0, 110)
	b.add_theme_font_size_override("font_size", 40)
	return b


func _panel_open() -> bool:
	return _panel_layer.visible


func _open_panel() -> void:
	if _mode == Mode.MENU:
		return
	_panel_layer.show()
	_update_touch()
	_refresh()


func _close_panel() -> void:
	if not _panel_layer.visible:
		return
	_panel_layer.hide()
	_update_touch()
	_refresh()


## The touch controls show in a match where wanted, and hide while the panel is open (so a tap on
## the panel's buttons can't also press a fighter's).
func _update_touch() -> void:
	var want := _mode != Mode.MENU and _prefer_touch and not _panel_open()
	if _touch.visible != want:
		_touch.visible = want


# ---- modes ----

func _go_to_menu(message: String = "") -> void:
	_close_session()
	_mode = Mode.MENU
	_state = null
	_panel_layer.hide()
	_touch.hide()
	# Keyboard / pad users get focus on the first button; on touch screens a focus ring is noise.
	_menu.open(message, not _prefer_touch)
	_refresh()


func _start_practice() -> void:
	_close_session()
	_mode = Mode.PRACTICE
	_restart_practice()
	_enter_game()


## A new practice match with the current CPU and Guts settings.
func _restart_practice() -> void:
	_rules = MatchRules.new()
	_rules.guts = _guts
	var match_seed: int = SceneSmoke.SEED if _smoke != null else _rng.randi()
	_state = Simulation.create_initial_state(_rules, PLAYERS, match_seed)
	_refresh()


func _start_online(code: String) -> void:
	_close_session()
	var script := _online_script()
	if script == null:
		_go_to_menu("Online play isn't available in this build.")
		return
	_install_web_copy()
	var url := str(script.call("relay_url_for_room", code))
	_session = script.new(url, code)
	if _session == null:
		_go_to_menu("Online play couldn't start.")
		return
	_mode = Mode.ONLINE
	_room = code
	_stalled_ticks = 0
	_copied_until_msec = 0
	_share_link = _make_share_link(code)
	_enter_game()


## Hides the menu and shows the arena (and touch controls where wanted).
func _enter_game() -> void:
	_menu.hide()
	get_viewport().gui_release_focus() # or Space would press the focused menu button again
	_panel_layer.hide()
	_update_touch()
	if OS.has_feature("web") and TouchControls.handheld():
		JavaScriptBridge.eval("window.rangbaaziLandscape && window.rangbaaziLandscape.request()")
	_refresh()


## The OnlineSession script, loaded on first use; null if client/net is missing, doesn't compile
## or lacks what this scene calls (then MENU and PRACTICE still work).
func _online_script() -> GDScript:
	if _session_script != null or not ResourceLoader.exists(ONLINE_SESSION_PATH):
		return _session_script
	var script := load(ONLINE_SESSION_PATH) as GDScript
	if script == null or not script.can_instantiate():
		return null
	var methods := script.get_script_method_list().map(func(m: Dictionary) -> String: return m["name"])
	var phases: Variant = script.get_script_constant_map().get("Phase")
	if not phases is Dictionary:
		return null
	for m: String in SESSION_METHODS:
		if not methods.has(m):
			return null
	for p: String in SESSION_PHASES:
		if not (phases as Dictionary).has(p):
			return null
	_phases = phases
	_session_script = script
	return script


func _close_session() -> void:
	if _session != null:
		_session.close()
		_session = null


func _phase_is(phase_name: String) -> bool:
	return _session != null and _session.phase == _phases.get(phase_name, -1)


## Online, R / F5 / Rematch plays again once the session has ended or the match is won: the
## session keeps RUNNING after a win until someone asks for a rematch or leaves.
func _can_rematch() -> bool:
	if _phase_is("ENDED"):
		return true
	if not _phase_is("RUNNING"):
		return false
	var state: GameState = _session.state
	return state != null and state.match_over


func _new_room_code() -> String:
	var code := ""
	for i in ROOM_CODE_LENGTH:
		code += ROOM_ALPHABET[_rng.randi_range(0, ROOM_ALPHABET.length() - 1)]
	return code


# ---- ticking ----

func _physics_process(_delta: float) -> void:
	match _mode:
		Mode.PRACTICE:
			if not _panel_open(): # practice pauses while the panel is open
				_practice_step()
		Mode.ONLINE:
			_online_step()
	_refresh()


func _practice_step() -> void:
	if _smoke != null:
		if _smoke_done:
			return
		if _state.frame == SceneSmoke.TOUCH_FROM_FRAME:
			_touch.show() # draw the touch layout for the rest of the run
		if _state.frame == SceneSmoke.TOUCH_TEST_FRAME:
			var touch_problem := _smoke.touch_test(_touch, get_viewport())
			if touch_problem != "":
				_end_smoke(false, touch_problem)
				return
		_smoke.press_keys(_state, _rules)
	# Both fighters' buttons come from the same pre-advance state, the CPU's included.
	for p in PLAYERS:
		_inputs[p] = SparringBot.think(_state, _rules, p, _cpu_level) if p == 1 and _p2_cpu else _local_buttons(p)
	if _smoke != null:
		var problem := _smoke.check_read(_state.frame, _inputs[0])
		if problem != "":
			_end_smoke(false, problem)
			return
	Simulation.advance(_state, _rules, _inputs)
	if _smoke != null and _state.frame >= SceneSmoke.FRAMES:
		_finish_smoke()


func _online_step() -> void:
	if _session == null:
		return
	_session.tick(_local_buttons(0))
	_stalled_ticks = _stalled_ticks + 1 if _session.stalled else 0


## A person's buttons on this device: P1 is keyboard, first pad and the touch controls. With one
## person here (online, or practice against the CPU) P1 gets every pad.
func _local_buttons(player: int) -> int:
	var one_person := _mode == Mode.ONLINE or _p2_cpu
	var b := InputReader.read(player, one_person)
	if player == 0:
		b |= _touch.buttons()
	return b & Buttons.KNOWN


# ---- input ----

## On a desktop with a touchscreen the touch controls get out of the way while keys or a pad are
## used, and come back at the next real touch (mouse clicks don't count: project.godot turns
## emulate_touch_from_mouse off). Phones and tablets keep them on.
func _input(event: InputEvent) -> void:
	if _smoke != null or not event.is_pressed():
		return
	if event is InputEventScreenTouch:
		_prefer_touch = true
	elif (event is InputEventKey or event is InputEventJoypadButton) \
			and not TouchControls.handheld() and not TouchControls.forced():
		_prefer_touch = false
	else:
		return
	var were_shown := _touch.visible
	_update_touch()
	# The controls ignored this touch while hidden (they see input before this node does): hand
	# it over, so the touch that brings them back also presses what it landed on.
	if event is InputEventScreenTouch and _touch.visible and not were_shown:
		_touch.handle(event)


func _unhandled_input(event: InputEvent) -> void:
	if _smoke != null or _mode == Mode.MENU:
		return
	if event.is_action_pressed(InputReader.BACK):
		if _panel_open():
			_close_panel()
		else:
			_go_to_menu()
	elif _mode == Mode.ONLINE:
		if event.is_action_pressed(InputReader.RESTART):
			_on_rematch()
		else:
			return
	elif event.is_action_pressed(InputReader.RESTART):
		_restart_practice()
	elif event.is_action_pressed(InputReader.TOGGLE_CPU):
		_p2_cpu = not _p2_cpu
	elif event.is_action_pressed(InputReader.CPU_LEVEL):
		_cpu_level = (_cpu_level + 1) % LEVEL_NAMES.size()
	elif event.is_action_pressed(InputReader.TOGGLE_GUTS):
		_guts = not _guts # a rule of the match, so it starts a new one
		_restart_practice()
	else:
		return
	get_viewport().set_input_as_handled()
	_refresh()


## "Play again" in the middle once a match is over: a new practice match, or a rematch online.
func _on_play_again() -> void:
	if _mode == Mode.PRACTICE:
		_restart_practice()
	else:
		_on_rematch()


## The panel's CPU button: turns the CPU back on if P2 was a person, else cycles its level.
func _on_level_pressed() -> void:
	if _p2_cpu:
		_cpu_level = (_cpu_level + 1) % LEVEL_NAMES.size()
	else:
		_p2_cpu = true
	_refresh()


func _on_rematch() -> void:
	if _can_rematch():
		_session.rematch()
		_stalled_ticks = 0
		_refresh()


## The copy button's "pressed". Off the web it copies here. On the web the page's own listener has
## normally copied already, inside the tap (see WEB_COPY_JS); this only covers a tap the listener
## missed, which still works where the browser counts the tap as recent (Chrome, Firefox).
func _on_copy() -> void:
	if OS.has_feature("web"):
		JavaScriptBridge.eval("window.rangbaaziCopy && window.rangbaaziCopy.pressed()")
	else:
		DisplayServer.clipboard_set(_copy_text())
		_copied_until_msec = Time.get_ticks_msec() + COPIED_MSEC


func _copy_label() -> String:
	if Time.get_ticks_msec() < _copied_until_msec:
		return "Copied!"
	return "Copy link" if _share_link != "" else "Copy code"


func _copy_text() -> String:
	return _share_link if _share_link != "" else _room


## Web: tells the page where the copy button is (in canvas pixels) and what it copies, or that it's
## hidden, whenever that changes; then picks up a copy the page finished ("Copied!" shows only then).
func _sync_web_copy() -> void:
	if not OS.has_feature("web"):
		return
	var target := ""
	if _copy_button.visible:
		var r := get_viewport().get_final_transform() * _copy_button.get_global_rect()
		target = "%s, %d, %d, %d, %d" % [JSON.stringify(_copy_text()),
			floori(r.position.x), floori(r.position.y), ceili(r.size.x), ceili(r.size.y)]
	if target != _web_copy_target:
		_web_copy_target = target
		JavaScriptBridge.eval("window.rangbaaziCopy && window.rangbaaziCopy.target(%s)" % (target if target != "" else "null"))
	if _copy_button.visible and str(JavaScriptBridge.eval("window.rangbaaziCopy ? window.rangbaaziCopy.take() : ''")) == "copied":
		_copied_until_msec = Time.get_ticks_msec() + COPIED_MSEC


## Browsers only let a page write the clipboard inside a click or tap's own event handler (Safari
## strictly; Chrome and Firefox for a few seconds after it), and Godot reacts to input in its own
## frame, outside it. So the page watches for a press and release on the copy button's rectangle
## (sent by _sync_web_copy) and copies right there, in the release event. Falls back to a hidden
## textarea + execCommand, then to a prompt that shows the link to copy by hand.
const WEB_COPY_JS := """
(function () {
	if (window.rangbaaziCopy) return;
	var target = null; // the copy button {text, x, y, w, h} in canvas pixels, or null while hidden
	var pressStarted = false; // the current press began on the button
	var lastCopy = -1e9; // performance.now() of the last copy started
	var lastTouch = -1e9; // mouse events a browser makes up after a touch are ignored
	var outcome = ''; // 'copied' once a copy succeeded, until Godot takes it
	function canvas() {
		return document.getElementById('canvas') || document.querySelector('canvas');
	}
	function onButton(clientX, clientY) {
		var c = canvas();
		if (target === null || !c) return false;
		var r = c.getBoundingClientRect();
		if (!r.width || !r.height) return false;
		var x = (clientX - r.left) * c.width / r.width;
		var y = (clientY - r.top) * c.height / r.height;
		return x >= target.x && y >= target.y && x < target.x + target.w && y < target.y + target.h;
	}
	function viaTextarea(text) {
		var ta = document.createElement('textarea');
		ta.value = text;
		ta.setAttribute('readonly', '');
		ta.style.position = 'fixed';
		ta.style.top = '0';
		ta.style.left = '0';
		ta.style.opacity = '0';
		document.body.appendChild(ta);
		ta.focus();
		ta.select();
		ta.setSelectionRange(0, text.length);
		var ok = false;
		try { ok = document.execCommand('copy'); } catch (e) {}
		document.body.removeChild(ta);
		var canvas = document.getElementById('canvas');
		if (canvas) canvas.focus();
		return ok;
	}
	function byHand(text) {
		window.prompt('Copy this link and send it to your opponent:', text);
	}
	function copy(text) {
		lastCopy = performance.now();
		function fallback() {
			if (viaTextarea(text)) outcome = 'copied';
			else byHand(text);
		}
		if (navigator.clipboard && navigator.clipboard.writeText) {
			navigator.clipboard.writeText(text).then(function () { outcome = 'copied'; }, fallback);
		} else {
			fallback();
		}
	}
	function down(clientX, clientY) {
		pressStarted = onButton(clientX, clientY);
	}
	function up(clientX, clientY) {
		var hit = pressStarted && onButton(clientX, clientY);
		pressStarted = false;
		if (hit) copy(target.text);
	}
	window.addEventListener('touchstart', function (e) {
		lastTouch = performance.now();
		var t = e.changedTouches[0];
		if (t) down(t.clientX, t.clientY);
	}, true);
	window.addEventListener('touchend', function (e) {
		lastTouch = performance.now();
		var t = e.changedTouches[0];
		if (t) up(t.clientX, t.clientY);
	}, true);
	window.addEventListener('touchcancel', function () { pressStarted = false; }, true);
	window.addEventListener('mousedown', function (e) {
		if (e.button === 0 && performance.now() - lastTouch > 1000) down(e.clientX, e.clientY);
	}, true);
	window.addEventListener('mouseup', function (e) {
		if (e.button === 0 && performance.now() - lastTouch > 1000) up(e.clientX, e.clientY);
	}, true);
	window.rangbaaziCopy = {
		target: function (text, x, y, w, h) {
			target = text === null ? null : { text: String(text), x: x, y: y, w: w, h: h };
			if (target === null) pressStarted = false;
		},
		// Godot's button fired: copy, unless this tap was already copied above.
		pressed: function () {
			if (target !== null && performance.now() - lastCopy > 1000) copy(target.text);
		},
		take: function () {
			var o = outcome;
			outcome = '';
			return o;
		}
	};
})();
"""


## Phones and tablets on the web: the game is played in landscape, but a browser follows how the
## phone is held, and many people keep auto-rotate off. So landscape is the default: the first tap
## anywhere (the menu included) goes fullscreen and locks the screen to landscape, and so does the
## next tap after leaving fullscreen (Android Chrome). Browsers allow fullscreen only during or
## just after a tap, which is why it waits for one. iPhone Safari has no fullscreen for pages, so
## there the menu and the match ask the player to turn the phone instead.
const WEB_LANDSCAPE_JS := """
(function () {
	if (window.rangbaaziLandscape) return;
	function enter() {
		var page = document.documentElement;
		if (document.fullscreenElement || !page.requestFullscreen) return;
		try {
			page.requestFullscreen({ navigationUI: 'hide' }).then(function () {
				if (screen.orientation && screen.orientation.lock) {
					screen.orientation.lock('landscape').catch(function () {});
				}
			}, function () {});
		} catch (e) {}
	}
	window.addEventListener('touchend', enter, true);
	window.rangbaaziLandscape = {
		// Starting a match: in case that came from something other than a touch.
		request: function () {
			if (!navigator.userActivation || navigator.userActivation.isActive) enter();
		}
	};
})();
"""


func _install_web_copy() -> void:
	if OS.has_feature("web"):
		JavaScriptBridge.eval(WEB_COPY_JS)


func _notification(what: int) -> void:
	match what:
		NOTIFICATION_WM_CLOSE_REQUEST:
			_close_session()
		NOTIFICATION_APPLICATION_FOCUS_OUT, NOTIFICATION_WM_WINDOW_FOCUS_OUT, NOTIFICATION_APPLICATION_PAUSED:
			# A key held while the window loses focus never sends its release: let go of every key
			# so the fighter doesn't keep running. (The touch controls do the same for fingers.)
			if _smoke == null:
				InputReader.release_all()
		NOTIFICATION_WM_GO_BACK_REQUEST:
			if _mode == Mode.MENU:
				get_tree().quit()
			elif _smoke == null:
				if _panel_open():
					_close_panel()
				else:
					_go_to_menu()


func _exit_tree() -> void:
	_close_session()


# ---- web page address ----

## The room in the page's ?room=CODE, or "" (always "" off the web).
func _room_from_url() -> String:
	if not OS.has_feature("web"):
		return ""
	var search := str(JavaScriptBridge.eval("window.location.search"))
	for part in search.trim_prefix("?").split("&", false):
		var pair := part.split("=", true, 1)
		if pair.size() == 2 and pair[0] == "room":
			var code := pair[1].uri_decode().strip_edges().to_upper()
			if MenuScreen.is_valid_code(code):
				return code
			_menu.show_error("That room link isn't valid: codes are 4 to 8 letters or digits.")
	return ""


## The link that opens this page straight into the room: the page's own address (origin + "/" at
## the site root) with ?room=CODE. "" off the web.
func _make_share_link(code: String) -> String:
	if not OS.has_feature("web"):
		return ""
	var origin := str(JavaScriptBridge.eval("window.location.origin"))
	var path := str(JavaScriptBridge.eval("window.location.pathname"))
	if not path.begins_with("/"):
		path = "/"
	return "%s%s?room=%s" % [origin, path, code]


# ---- view ----

## Hands the arena what to draw and shows the HUD buttons that apply. Reads state only.
func _refresh() -> void:
	_arena.names = PackedStringArray()
	_arena.tags = PackedStringArray()
	_arena.status_big = ""
	_arena.status_small = PackedStringArray()
	_arena.over_hint = ""
	_arena.debug_text = ""
	# Keep the arena's touch layout while the panel hides the controls, so it doesn't jump.
	_arena.touch_layout = _touch.visible or (_panel_open() and _prefer_touch)
	_arena.hud_right_inset = MENU_BUTTON + MENU_MARGIN
	_hud.visible = _mode != Mode.MENU
	# Each set once per refresh: hiding a button, even for an instant, cancels a press in progress.
	_again_button.visible = not _panel_open() and _can_play_again()
	_copy_button.visible = _mode == Mode.ONLINE and _phase_is("WAITING_FOR_OPPONENT")

	match _mode:
		Mode.MENU:
			_arena.state = null
			_arena.rules = _rules
		Mode.PRACTICE:
			_refresh_practice()
		Mode.ONLINE:
			_refresh_online()
	# A phone held upright (and not turned by the fullscreen lock): the arena is tiny that way.
	var view := get_viewport_rect().size
	if _mode != Mode.MENU and _prefer_touch and view.y > view.x:
		var hint := _arena.status_small.duplicate()
		hint.append("Turn your phone sideways for a bigger view")
		_arena.status_small = hint
	_place_copy_button()
	_place_again_button()
	_refresh_panel()
	_sync_web_copy()
	_arena.queue_redraw()


## A keyboard hint, or "" where the touch controls are in use (the buttons say it there).
func _keys_hint(hint: String) -> String:
	return "" if _prefer_touch else hint


## Whether "Play again" applies: a practice match is over, or online a rematch can start.
func _can_play_again() -> bool:
	match _mode:
		Mode.PRACTICE:
			return _state != null and _state.match_over
		Mode.ONLINE:
			return _can_rematch()
	return false


## The copy button goes under the status lines (online, while waiting for an opponent).
func _place_copy_button() -> void:
	if not _copy_button.visible:
		return
	_copy_button.text = _copy_label()
	var view := get_viewport_rect().size
	_copy_button.reset_size()
	_copy_button.position = Vector2((view.x - _copy_button.size.x) / 2, _arena.status_bottom() + 10.0)


## "Play again" goes under "P1 WINS!" and its hint, or under the status lines (online, when the
## session ended without a winner).
func _place_again_button() -> void:
	if not _again_button.visible:
		return
	var view := get_viewport_rect().size
	var shown: GameState = _arena.state
	var y := view.y * ArenaView.WINNER_Y + 90.0 if shown != null and shown.match_over else _arena.status_bottom() + 10.0
	_again_button.reset_size()
	_again_button.position = Vector2((view.x - _again_button.size.x) / 2, y)


## The panel's title and the buttons that apply now.
func _refresh_panel() -> void:
	if not _panel_open():
		return
	var practice := _mode == Mode.PRACTICE
	_panel_title.text = "Paused" if practice else "Menu"
	_panel_note.visible = not practice
	_restart_button.visible = practice
	_level_button.visible = practice
	_level_button.text = "CPU: %s" % LEVEL_NAMES[_cpu_level] if _p2_cpu else "P2: person (tap for CPU)"
	_rematch_button.visible = _mode == Mode.ONLINE and _can_rematch()


func _refresh_practice() -> void:
	_arena.state = _state
	_arena.rules = _rules
	var names := PackedStringArray()
	var tags := PackedStringArray()
	for i in PLAYERS:
		var cpu := i == 1 and _p2_cpu
		var you := i == 0 and _p2_cpu # with two people at one keyboard, nobody is "you"
		names.append("P%d%s" % [i + 1, " CPU" if cpu else " YOU" if you else ""])
		tags.append("CPU" if cpu else "YOU" if you else "")
	_arena.names = names
	_arena.tags = tags
	_arena.over_hint = _keys_hint("R / F5 / Restart = play again   Esc = menu")
	var checksum := StateHasher.hex(_state.checksum())
	if _smoke != null:
		_arena.debug_text = "SMOKE TEST (bots)   frame %d/%d   checksum %s" % [_state.frame, SceneSmoke.FRAMES, checksum]
	else:
		_arena.debug_text = "frame %d   checksum %s   F2 = P2 %s   F3 = CPU level: %s   F4 = Guts %s   F5 / R = restart   Esc = menu" % [
			_state.frame, checksum, "CPU" if _p2_cpu else "person", LEVEL_NAMES[_cpu_level], "on" if _guts else "off"]


func _refresh_online() -> void:
	if _session == null:
		return
	var s = _session # OnlineSession
	var state: GameState = s.state
	var slot: int = s.local_slot
	_arena.state = state
	_arena.rules = s.rules if s.rules != null else _rules
	var names := PackedStringArray()
	var tags := PackedStringArray()
	for i in PLAYERS:
		names.append("P%d YOU" % (i + 1) if i == slot else "P%d" % (i + 1))
		tags.append("YOU" if i == slot else "")
	_arena.names = names
	_arena.tags = tags

	# Connection status over the arena.
	var big := ""
	var small := PackedStringArray()
	if _phase_is("CONNECTING"):
		big = "Connecting..."
		small.append("Room %s" % _room)
	elif _phase_is("WAITING_FOR_OPPONENT"):
		big = "Waiting for your opponent..."
		small.append("Room %s" % _room)
		if _share_link != "":
			small.append("Share this link: %s" % _share_link)
	elif _phase_is("STARTING"):
		big = "Starting..."
	elif _phase_is("RUNNING"):
		if s.interrupted:
			big = "Connection interrupted..."
		elif _stalled_ticks > STALL_NOTICE_TICKS:
			small.append("Waiting for your opponent...")
	elif _phase_is("ENDED"):
		big = s.end_message
		if not _prefer_touch:
			small.append("R / F5 / Play again = play again, Esc = menu")
	if _phase_is("RUNNING") and state != null and state.match_over:
		_arena.over_hint = _keys_hint("R / F5 / Play again = play again   Esc = menu")
	_arena.status_big = big
	_arena.status_small = small

	_arena.debug_text = "ONLINE   room %s   %s   frame %d   ping %d ms   rollback last %d max %d median %d   behind %d   verified %d" % [
		_room, "P%d" % (slot + 1) if slot >= 0 else "P?", state.frame if state != null else 0, s.ping_ms,
		s.rollback_last, s.rollback_max, s.rollback_median, s.frames_behind, s.checksums_verified]


# ---- scene smoke test ----

func _begin_smoke() -> void:
	Engine.physics_ticks_per_second = SceneSmoke.TICKS_PER_SECOND
	Engine.max_physics_steps_per_frame = 16
	_p2_cpu = true
	_cpu_level = SceneSmoke.P2_LEVEL
	_guts = true
	print("SCENE SMOKE: practice, P1 = SparringBot Hard on P1's keys, P2 = CPU Normal, %d frames" % SceneSmoke.FRAMES)
	_go_to_menu() # through the menu, as a person would
	_start_practice()


func _process(_delta: float) -> void:
	# Watchdog: a run that stops advancing (or never starts) still ends, with exit code 1.
	if _smoke != null and not _smoke_done and _smoke.timed_out():
		_end_smoke(false, "timed out after %d ms at frame %d%s" % [
			SceneSmoke.TIMEOUT_MSEC, _state.frame if _state != null else -1, _smoke.error_note()])


func _finish_smoke() -> void:
	var reference_rules := MatchRules.new()
	if reference_rules.fingerprint() != _rules.fingerprint():
		_end_smoke(false, "the practice rules differ from the defaults the reference run uses")
		return
	var expected := SceneSmoke.reference_checksum(reference_rules)
	var actual := _state.checksum()
	if actual != expected:
		_end_smoke(false, "frames=%d checksum=%s but the pure sim run gives %s%s" % [
			_state.frame, StateHasher.hex(actual), StateHasher.hex(expected), _smoke.error_note()])
		return
	var problem := _smoke.final_problem(_state, _rules, _arena.match_draws)
	if problem != "":
		_end_smoke(false, problem)
		return
	print("SCENE SMOKE: P1 held buttons on %d frames, arena drew the match %d times, touch self-test passed" % [
		_smoke.pressed_frames, _arena.match_draws])
	_end_smoke(true, "frames=%d checksum=%s" % [_state.frame, StateHasher.hex(actual)])


func _end_smoke(passed: bool, detail: String) -> void:
	if _smoke_done:
		return
	_smoke_done = true
	_smoke.stop()
	if passed:
		print("SCENE SMOKE PASS %s" % detail)
	else:
		print("SCENE SMOKE FAIL %s" % detail)
	print("SCENE SMOKE took %d ms" % _smoke.elapsed_msec())
	set_physics_process(false)
	set_process(false)
	get_tree().quit(0 if passed else 1)
