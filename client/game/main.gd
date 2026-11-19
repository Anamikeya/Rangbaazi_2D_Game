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
## MENU -> HOST / JOIN A ROOM: rooms of up to 15 players whose match the server runs (RoomSession).
##   The lobby (RoomScreen) shows who's in; the host picks 10 to 15 minutes, adds CPUs and starts.
##   In the match the chat button opens the quick-chat phrases. The server returns everyone to the
##   lobby after the result. A page opened with ?arena=CODE joins that room.
## A "Play again" button shows in the middle once a match is over.
## `-- --smoke` runs the headless scene check instead (see SceneSmoke). `-- --touch` forces the
## touch controls on (and lets the mouse act as a finger) for testing them on a desktop.

enum Mode { MENU, PRACTICE, ONLINE, ROOM }

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

# Rooms of up to 15 (the server runs the match).
var _room_session: RoomSession
var _room_screen: RoomScreen
var _room_host := false
var _room_tries := 0
var _settings_screen: SettingsScreen
var _chat_button: Button
var _chat_layer: CanvasLayer
var _end_match_button: Button
# The pickers: the character circle and the arena slider (layer 7, over everything).
var _circle: CharacterCircle
var _creator: CharacterCreator
var _arena_picker: ArenaPicker
var _picker_for := "" # "practice" or "room": what the arena slider's pick is for
var _cpu_character := "akshat"
var _sound: SoundDirector
const CHAT_BUBBLE_TICKS := 180
const HOST_RETRIES := 3

## Auto play: a CPU plays the local fighter (in any mode) until the button is pressed again, so a
## player can step away mid-match. Deliberately only middling, so the others can beat it.
const AUTO_LEVEL := SparringBot.Level.NORMAL
const AUTO_BUTTON := Vector2(250, 62)
var _auto := false
var _auto_button: Button

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
var _menu_backdrop: MatchRules
var _menu_backdrop_for := ""
# The how-to-play card (its own CanvasLayer, over everything in a match).
var _help_layer: CanvasLayer
var _help_controls: Label
var _panel_title: Label
var _panel_note: Label
var _restart_button: Button
# Practice options in the panel, every choice in view: who player 2 is, and how good the CPU is.
var _p2_row: ChoiceRow
var _level_row: ChoiceRow
var _rematch_button: Button


func _ready() -> void:
	# One physics tick = one sim frame.
	Engine.physics_ticks_per_second = Simulation.TICKS_PER_SECOND
	# Android's back button returns to the menu instead of quitting (see _notification).
	get_tree().quit_on_go_back = false
	if SceneSmoke.requested():
		_smoke = SceneSmoke.new() # first, so it counts errors from here on
	InputReader.register()
	GameSettings.load_settings()
	GameSettings.apply()
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
	var arena_code := _url_param("arena")
	if arena_code != "" and MenuScreen.is_valid_code(arena_code):
		_start_room(arena_code, false)


func _build_nodes() -> void:
	_sound = SoundDirector.new()
	_sound.name = "Sound"
	add_child(_sound)
	# Every button in the game clicks when pressed (menus, the lobby, the panels).
	get_tree().node_added.connect(func(node: Node) -> void:
		var button := node as BaseButton
		if button != null:
			button.pressed.connect(func() -> void: _sound.play("click", -6.0)))
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
	_chat_button = _hud_button(_hud, "Chat", _open_chat)
	_chat_button.custom_minimum_size = Vector2(MENU_BUTTON * 1.6, MENU_BUTTON)
	_chat_button.set_anchors_preset(Control.PRESET_TOP_RIGHT)
	_chat_button.offset_left = -(MENU_MARGIN * 2 + MENU_BUTTON + MENU_BUTTON * 1.6)
	_chat_button.offset_right = -(MENU_MARGIN * 2 + MENU_BUTTON)
	_chat_button.offset_top = MENU_MARGIN
	_chat_button.offset_bottom = MENU_MARGIN + MENU_BUTTON

	_auto_button = _hud_button(_hud, "Auto play", func() -> void:
		_auto = _auto_button.button_pressed
		_refresh())
	_auto_button.toggle_mode = true
	_auto_button.tooltip_text = "Let the CPU play your fighter while you're away"
	_auto_button.custom_minimum_size = AUTO_BUTTON
	_auto_button.add_theme_font_size_override("font_size", 26)

	var touch_layer := CanvasLayer.new()
	touch_layer.name = "Touch"
	touch_layer.layer = 2
	add_child(touch_layer)
	_touch = TouchControls.new()
	_touch.visible = false
	touch_layer.add_child(_touch)

	_build_panel()
	_build_help()
	_build_chat()

	var room_layer := CanvasLayer.new()
	room_layer.name = "Room"
	room_layer.layer = 3
	add_child(room_layer)
	_room_screen = RoomScreen.new()
	_room_screen.visible = false
	room_layer.add_child(_room_screen)
	_room_screen.minutes_changed.connect(func(m: int) -> void:
		if _room_session != null: _room_session.set_minutes(m))
	_room_screen.bots_changed.connect(func(n: int) -> void:
		if _room_session != null: _room_session.set_bots(n))
	_room_screen.start_pressed.connect(func() -> void:
		if _room_session != null: _room_session.start_match())
	_room_screen.leave_pressed.connect(_go_to_menu.bind(""))

	var picker_layer := CanvasLayer.new()
	picker_layer.name = "Pickers"
	picker_layer.layer = 7
	add_child(picker_layer)
	_circle = CharacterCircle.new()
	_circle.visible = false
	picker_layer.add_child(_circle)
	_circle.chosen.connect(_on_fighter_chosen)
	_circle.cancelled.connect(func() -> void: _circle.hide())
	_creator = CharacterCreator.new()
	_creator.visible = false
	picker_layer.add_child(_creator)
	_circle.create_requested.connect(func() -> void:
		_circle.hide()
		_creator.open(GameSettings.character))
	_creator.cancelled.connect(func() -> void:
		_creator.hide()
		_circle.open(GameSettings.character))
	_creator.saved.connect(func(id: String) -> void:
		GameSettings.custom = id
		_creator.hide()
		_on_fighter_chosen(id))
	_arena_picker = ArenaPicker.new()
	_arena_picker.visible = false
	picker_layer.add_child(_arena_picker)
	_arena_picker.chosen.connect(_on_arena_chosen)
	_arena_picker.cancelled.connect(func() -> void: _arena_picker.hide())
	_room_screen.fighter_requested.connect(func() -> void: _circle.open(GameSettings.character))
	_room_screen.arena_requested.connect(func() -> void:
		if _room_session != null and _room_session.is_host():
			_picker_for = "room"
			_arena_picker.open(_room_session.stage_id, "Use this arena"))

	var settings_layer := CanvasLayer.new()
	settings_layer.name = "Settings"
	settings_layer.layer = 6
	add_child(settings_layer)
	_settings_screen = SettingsScreen.new()
	_settings_screen.visible = false
	settings_layer.add_child(_settings_screen)

	var menu_layer := CanvasLayer.new()
	menu_layer.name = "Menu"
	menu_layer.layer = 3
	add_child(menu_layer)
	_menu = MenuScreen.new()
	menu_layer.add_child(_menu)
	_menu.practice_requested.connect(func() -> void:
		_picker_for = "practice"
		_arena_picker.open(GameSettings.arena, "Fight here"))
	_menu.fighter_requested.connect(func() -> void: _circle.open(GameSettings.character))
	_menu.create_requested.connect(func() -> void: _start_online(_new_room_code()))
	_menu.host_requested.connect(func() -> void:
		_room_tries = 0
		_start_room(_new_room_code(), true))
	_menu.join_requested.connect(func(code: String) -> void: _start_room(code, false))
	_menu.settings_requested.connect(func() -> void: _settings_screen.open())


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
	column.custom_minimum_size = Vector2(820, 0)
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

	UiTheme.make_primary(_panel_button(column, "Resume", _close_panel))
	_panel_button(column, "How to play", func() -> void:
		_close_panel()
		_open_help())
	_restart_button = _panel_button(column, "Restart", func() -> void:
		_restart_practice()
		_close_panel())
	_p2_row = ChoiceRow.make("Player 2", ["CPU", "A friend"], 0, func(i: int) -> void:
		_p2_cpu = i == 0
		_refresh(), 230.0, 100.0)
	column.add_child(_p2_row)
	_level_row = ChoiceRow.make("CPU skill", LEVEL_NAMES, 1, func(i: int) -> void:
		_cpu_level = i
		_refresh(), 230.0, 100.0)
	column.add_child(_level_row)
	_rematch_button = _panel_button(column, "Rematch", func() -> void:
		_on_rematch()
		_close_panel())
	_end_match_button = _panel_button(column, "End match for everyone", func() -> void:
		if _room_session != null:
			_room_session.end_match()
		_close_panel())
	var leave := _panel_button(column, "Leave match", _go_to_menu.bind(""))
	if UiTheme.can_quit():
		# Leave and Quit side by side, so the panel doesn't grow taller on a phone.
		var bottom := HBoxContainer.new()
		bottom.add_theme_constant_override("separation", 20)
		column.add_child(bottom)
		column.remove_child(leave)
		bottom.add_child(leave)
		leave.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		var quit := UiTheme.quit_button()
		quit.custom_minimum_size = leave.custom_minimum_size
		quit.add_theme_font_size_override("font_size", 40)
		quit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		bottom.add_child(quit)


## The how-to-play card: shown once before the first match (practice waits behind it), and from
## the match menu. Three short lines and the controls for this device.
func _build_help() -> void:
	_help_layer = CanvasLayer.new()
	_help_layer.name = "Help"
	_help_layer.layer = 5
	_help_layer.visible = false
	add_child(_help_layer)
	var root := Control.new()
	root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	root.theme = UiTheme.build()
	_help_layer.add_child(root)
	var backdrop := ColorRect.new()
	backdrop.color = Color(0.05, 0.045, 0.07, 0.72)
	backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	root.add_child(backdrop)
	var center := CenterContainer.new()
	center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	root.add_child(center)
	var box := PanelContainer.new()
	box.add_theme_stylebox_override("panel", UiTheme.panel_box())
	center.add_child(box)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 18)
	column.custom_minimum_size = Vector2(980, 0)
	box.add_child(column)

	var title := Label.new()
	title.text = "HOW TO PLAY"
	title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	title.add_theme_font_size_override("font_size", 56)
	title.add_theme_color_override("font_color", UiTheme.ACCENT)
	column.add_child(title)
	for line: String in [
		"Knock everyone off the stage. The higher their %, the farther they fly.",
		"Crates drop weapons. Touch one to grab it; touch another to swap.",
		"Dodge just as a hit lands to parry it and stun them.",
	]:
		var l := Label.new()
		l.text = line
		l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		l.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		l.add_theme_font_size_override("font_size", 32)
		column.add_child(l)
	_help_controls = Label.new()
	_help_controls.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_help_controls.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_help_controls.add_theme_font_size_override("font_size", 28)
	_help_controls.add_theme_color_override("font_color", UiTheme.TEXT_DIM)
	column.add_child(_help_controls)
	var ok := _hud_button(column, "Got it", _close_help)
	UiTheme.make_primary(ok)
	ok.custom_minimum_size = Vector2(0, 110)
	ok.add_theme_font_size_override("font_size", 44)


func _open_help() -> void:
	_help_controls.text = "Kick: attack, or use your weapon.   Heavy: a big hit (with a direction, a different one).\n" \
		+ "Up + Heavy: leap back to the stage.   Combo: a random flurry of punches.   Throw: hurl your weapon." if _prefer_touch else \
		"A / D move   Space jump (twice)   J attack or use your weapon   K heavy (W + K leaps back to the stage)\n" \
		+ "L dodge   I combo (a random flurry)   U throw your weapon   Esc menu"
	_help_layer.show()
	_update_touch()


func _close_help() -> void:
	_help_layer.hide()
	if not GameSettings.seen_help:
		GameSettings.seen_help = true
		GameSettings.save()
	get_viewport().gui_release_focus()
	_update_touch()


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


## The quick-chat panel: a grid of the phrases (RoomSession.PHRASES); a tap sends one and closes it.
func _build_chat() -> void:
	_chat_layer = CanvasLayer.new()
	_chat_layer.name = "Chat"
	_chat_layer.layer = 5
	_chat_layer.visible = false
	add_child(_chat_layer)
	var root := Control.new()
	root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	root.theme = UiTheme.build()
	_chat_layer.add_child(root)
	var backdrop := ColorRect.new()
	backdrop.color = Color(0.05, 0.045, 0.07, 0.55)
	backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	backdrop.gui_input.connect(func(event: InputEvent) -> void:
		var mouse := event as InputEventMouseButton
		if mouse != null and mouse.pressed and mouse.button_index == MOUSE_BUTTON_LEFT:
			_close_chat())
	root.add_child(backdrop)
	var center := CenterContainer.new()
	center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	root.add_child(center)
	var grid := GridContainer.new()
	grid.columns = 4
	grid.add_theme_constant_override("h_separation", 16)
	grid.add_theme_constant_override("v_separation", 16)
	center.add_child(grid)
	for i in RoomSession.PHRASES.size():
		var b := _hud_button(grid, RoomSession.PHRASES[i], func() -> void:
			if _room_session != null:
				_room_session.say(i)
			_close_chat())
		b.custom_minimum_size = Vector2(380, 96)


func _open_chat() -> void:
	if _mode == Mode.ROOM:
		_chat_layer.show()
		_update_touch()


func _close_chat() -> void:
	_chat_layer.hide()
	_update_touch()


## The touch controls show in a match where wanted, and hide while the panel is open (so a tap on
## the panel's buttons can't also press a fighter's).
func _update_touch() -> void:
	var in_match := _mode != Mode.ROOM or (_room_session != null and _room_session.phase == RoomSession.Phase.PLAYING)
	var want := _mode != Mode.MENU and in_match and _prefer_touch and not _panel_open() and not _chat_layer.visible and not _help_layer.visible
	if _touch.visible != want:
		_touch.visible = want


# ---- modes ----

func _go_to_menu(message: String = "") -> void:
	_close_session()
	_close_room()
	_mode = Mode.MENU
	_state = null
	_auto = false
	_panel_layer.hide()
	_help_layer.hide()
	_touch.hide()
	# Keyboard / pad users get focus on the first button; on touch screens a focus ring is noise.
	_menu.open(message, not _prefer_touch)
	_refresh()


func _start_practice() -> void:
	_close_session()
	_close_room()
	_mode = Mode.PRACTICE
	_restart_practice()
	_enter_game()


## A new practice match with the current CPU and Guts settings.
func _restart_practice() -> void:
	_auto = false
	_rules = MatchRules.new()
	_rules.weapons = true
	_rules.flurry = true
	_rules.hazards = true
	if _smoke == null:
		var arena := StageDefinition.by_id(GameSettings.arena)
		if arena != null:
			_rules.stage = arena
		# The CPU plays a different fighter each match.
		var ids := ArtLibrary.fighters().map(func(f: Dictionary) -> String: return str(f["id"]))
		ids.erase(GameSettings.character)
		if not ids.is_empty():
			_cpu_character = ids[_rng.randi_range(0, ids.size() - 1)]
		# Each fighter's own weapon hits a little harder for them.
		_rules.signatures = PackedInt32Array([Weapons.signature_for(ArtLibrary.look(GameSettings.character)), Weapons.signature_for(_cpu_character)])
	_rules.guts = _guts
	var match_seed: int = SceneSmoke.SEED if _smoke != null else _rng.randi()
	_state = Simulation.create_initial_state(_rules, PLAYERS, match_seed)
	_refresh()


func _start_online(code: String) -> void:
	_close_session()
	_close_room()
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
	_share_link = _make_share_link(code, url)
	_enter_game()


func _on_fighter_chosen(id: String) -> void:
	_sound.cheer(id) # each fighter's own jingle when you pick them
	GameSettings.character = id
	GameSettings.save()
	_circle.hide()
	_menu.refresh_fighter()
	if _room_session != null:
		_room_session.set_character(id)
	if _mode == Mode.PRACTICE:
		_restart_practice()


func _on_arena_chosen(id: String) -> void:
	_arena_picker.hide()
	_sound.play("click")
	if _picker_for == "room":
		if _room_session != null:
			_room_session.set_stage(id)
		return
	GameSettings.arena = id
	GameSettings.save()
	_start_practice()


## Hosts (a new code) or joins a room of up to 15. The lobby shows once the server answers.
func _start_room(code: String, as_host: bool) -> void:
	_close_session()
	_close_room()
	var player_name := GameSettings.player_name if GameSettings.player_name != "" else ("Host" if as_host else "Player")
	_room_session = RoomSession.new(code, as_host, 10, player_name, GameSettings.character)
	_room_host = as_host
	_mode = Mode.ROOM
	_room = code
	_share_link = _make_room_link(code)
	_enter_game()


func _close_room() -> void:
	if _room_session != null:
		_room_session.close()
		_room_session = null
	_room_screen.hide()
	_chat_layer.hide()


## Hides the menu and shows the arena (and touch controls where wanted).
func _enter_game() -> void:
	_menu.hide()
	get_viewport().gui_release_focus() # or Space would press the focused menu button again
	_panel_layer.hide()
	if not GameSettings.seen_help and _smoke == null:
		_open_help() # the first match starts with the how-to-play card
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
		Mode.ROOM:
			_room_step()
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
	if _help_layer.visible:
		return # the first match waits behind the how-to-play card
	# Both fighters' buttons come from the same pre-advance state, the CPU's included.
	for p in PLAYERS:
		if p == 1 and _p2_cpu:
			_inputs[p] = SparringBot.think(_state, _rules, p, _cpu_level)
		elif p == 0 and _auto and _smoke == null:
			_inputs[p] = _auto_buttons()
		else:
			_inputs[p] = _local_buttons(p)
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
	_session.tick(_auto_buttons() if _auto else _local_buttons(0))
	_stalled_ticks = _stalled_ticks + 1 if _session.stalled else 0


func _room_step() -> void:
	if _room_session == null:
		return
	var playing := _room_session.phase == RoomSession.Phase.PLAYING
	if not playing:
		_auto = false # every match starts with you at the controls
	var buttons := 0
	if playing and _auto:
		buttons = _auto_buttons() # keeps fighting while you chat
	elif playing and not _chat_layer.visible:
		buttons = _local_buttons(0)
	_room_session.tick(buttons)
	if _room_session.phase == RoomSession.Phase.ENDED:
		var code := _room_session.end_code
		var message := _room_session.end_message
		if code == "taken" and _room_host and _room_tries < HOST_RETRIES:
			_room_tries += 1
			_start_room(_new_room_code(), true) # that code was in use: try another
			return
		_go_to_menu(message if code != "left" else "")
		return
	_update_touch()


## Which fighter is this device's: 0 in practice, our slot online, our fighter in a room (-1: none).
func _my_fighter() -> int:
	match _mode:
		Mode.PRACTICE:
			return 0
		Mode.ONLINE:
			return int(_session.local_slot) if _session != null else -1
		Mode.ROOM:
			return _room_session.my_fighter if _room_session != null else -1
	return -1


## Auto play's buttons for the local fighter: a SparringBot reading the state on show.
func _auto_buttons() -> int:
	match _mode:
		Mode.PRACTICE:
			return SparringBot.think(_state, _rules, 0, AUTO_LEVEL)
		Mode.ONLINE:
			var slot := _my_fighter()
			if _phase_is("RUNNING") and _session.state != null and slot >= 0:
				return SparringBot.think(_session.state, _session.rules, slot, AUTO_LEVEL)
		Mode.ROOM:
			if _room_session != null:
				return _room_session.autopilot_buttons(AUTO_LEVEL)
	return 0


## Whether there is a fight on that auto play could take over.
func _can_auto() -> bool:
	if _smoke != null or _panel_open() or _help_layer.visible:
		return false
	var shown: GameState = _arena.state
	var mine := _my_fighter()
	if shown == null or shown.match_over or mine < 0 or mine >= shown.fighters.size():
		return false
	match _mode:
		Mode.PRACTICE:
			return _p2_cpu # with two people at one keyboard, both play
		Mode.ONLINE:
			return _phase_is("RUNNING")
		Mode.ROOM:
			return _room_session.phase == RoomSession.Phase.PLAYING
	return false


## The auto play button goes under your own stats box; in a room, beside the clock.
func _place_auto_button() -> void:
	_auto_button.set_pressed_no_signal(_auto)
	_auto_button.text = "Auto play: ON" if _auto else "Auto play"
	_auto_button.modulate = Color("ffd23f") if _auto else Color.WHITE
	if not _auto_button.visible:
		return
	_auto_button.reset_size()
	if _mode == Mode.ROOM:
		var view := get_viewport_rect().size
		_auto_button.position = Vector2(view.x / 2 - 112.0 - _auto_button.size.x, ArenaView.BAR_MARGIN + 4.0)
	else:
		var box := _arena.player_box(_my_fighter())
		_auto_button.position = Vector2(box.position.x, box.end.y + 8.0)


## A person's buttons on this device: P1 is keyboard, first pad and the touch controls. With one
## person here (online, or practice against the CPU) P1 gets every pad.
func _local_buttons(player: int) -> int:
	var one_person := _mode == Mode.ONLINE or _mode == Mode.ROOM or _p2_cpu
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
		if _circle.visible or _creator.visible or _arena_picker.visible:
			return # the pickers handle their own Back
		if _chat_layer.visible:
			_close_chat()
		elif _panel_open():
			_close_panel()
		else:
			_go_to_menu()
	elif _mode == Mode.ROOM:
		return
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
			_close_room()
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
	if _room_session != null:
		_room_session.close()


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


## The value of ?param=VALUE in the page's address, upper-cased (always "" off the web).
func _url_param(param: String) -> String:
	if not OS.has_feature("web"):
		return ""
	var search := str(JavaScriptBridge.eval("window.location.search"))
	for part in search.trim_prefix("?").split("&", false):
		var pair := part.split("=", true, 1)
		if pair.size() == 2 and pair[0] == param:
			return pair[1].uri_decode().strip_edges().to_upper()
	return ""


## The link that opens the web game straight into a room of up to 15: like _make_share_link, with
## ?arena=CODE.
func _make_room_link(code: String) -> String:
	var url := str(RoomSession.url_for_room(code, false, 10, ""))
	return _make_share_link(code, url.get_slice("?", 0)).replace("?room=", "?arena=")


## The link that opens the web game straight into the room: on the web the page's own address
## (origin + "/" at the site root) with ?room=CODE; elsewhere the relay's host (see below).
func _make_share_link(code: String, relay_url: String) -> String:
	if not OS.has_feature("web"):
		# The Android app: the web game is served from the relay's host, so the link opens the room
		# in a browser for a friend without the app. None for a local test relay (ws://).
		if relay_url.begins_with("wss://"):
			return "https://%s/?room=%s" % [relay_url.trim_prefix("wss://").get_slice("/", 0), code]
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
	match _mode:
		Mode.PRACTICE:
			_arena.fighter_characters = PackedStringArray([GameSettings.character, _cpu_character])
		Mode.ONLINE:
			_arena.fighter_characters = PackedStringArray([GameSettings.character, "shahn"]) if _session == null or _session.local_slot != 1 \
				else PackedStringArray(["shahn", GameSettings.character])
		Mode.ROOM:
			_arena.fighter_characters = _room_session.fighter_characters if _room_session != null else PackedStringArray()
		_:
			_arena.fighter_characters = PackedStringArray()
	_arena.room_mode = _mode == Mode.ROOM
	_arena.shake_enabled = GameSettings.effects
	_arena.show_hitboxes = GameSettings.show_hitboxes
	_arena.dynamic_camera = GameSettings.follow_camera
	_chat_button.visible = _mode == Mode.ROOM and _room_session != null \
		and _room_session.phase == RoomSession.Phase.PLAYING and not _panel_open()
	# Each set once per refresh: hiding a button, even for an instant, cancels a press in progress.
	_again_button.visible = not _panel_open() and _can_play_again()
	_copy_button.visible = _mode == Mode.ONLINE and _phase_is("WAITING_FOR_OPPONENT")

	match _mode:
		Mode.MENU:
			_arena.state = null
			_arena.rules = _menu_rules()
		Mode.PRACTICE:
			_refresh_practice()
		Mode.ONLINE:
			_refresh_online()
		Mode.ROOM:
			_refresh_room()
	if not GameSettings.show_debug and _smoke == null:
		_arena.debug_text = ""
	# A phone held upright (and not turned by the fullscreen lock): the arena is tiny that way.
	var view := get_viewport_rect().size
	if _mode != Mode.MENU and _prefer_touch and view.y > view.x:
		var hint := _arena.status_small.duplicate()
		hint.append("Turn your phone sideways for a bigger view")
		_arena.status_small = hint
	# The touch attack button shows the weapon in the local fighter's hand.
	var mine := 0
	if _mode == Mode.ROOM and _room_session != null:
		mine = _room_session.my_fighter
	elif _mode == Mode.ONLINE and _session != null:
		mine = int(_session.local_slot)
	var shown: GameState = _arena.state
	_touch.weapon = shown.fighters[mine].weapon if shown != null and mine >= 0 and mine < shown.fighters.size() else 0
	_auto_button.visible = _can_auto()
	_update_sound()
	_place_auto_button()
	_place_copy_button()
	_place_again_button()
	_refresh_panel()
	_sync_web_copy()
	_arena.queue_redraw()


## Behind the menu: the arena picked for practice, in its art (no match, so it just stands there).
func _menu_rules() -> MatchRules:
	if _menu_backdrop == null or _menu_backdrop_for != GameSettings.arena:
		_menu_backdrop_for = GameSettings.arena
		var stage := StageDefinition.by_id(GameSettings.arena) if _smoke == null else null
		_menu_backdrop = MatchRules.new()
		if stage != null:
			_menu_backdrop.stage = stage
	return _menu_backdrop


## The music and ambience for where we are, and the match's sounds (SoundDirector reads the state).
func _update_sound() -> void:
	if _smoke != null:
		return
	var in_match := _mode != Mode.MENU and _arena.state != null
	if _mode == Mode.ROOM and (_room_session == null or _room_session.phase != RoomSession.Phase.PLAYING):
		in_match = false
	if not in_match:
		_sound.set_scene("menu")
		_sound.observe(null, PackedStringArray(), -1, -1)
		return
	var folder := ArenaStages.art_folder(_arena.rules.stage.id)
	_sound.set_scene(folder if folder != "" else "shahn")
	var you: int = _arena.you_index if _mode == Mode.ROOM else (0 if _mode == Mode.PRACTICE else (_session.local_slot if _session != null else 0))
	var frames_left: int = _room_session.frames_left if _mode == Mode.ROOM and _room_session != null else -1
	_sound.observe(_arena.state, _arena.fighter_characters, you, frames_left)


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
	_p2_row.visible = practice
	_p2_row.select(0 if _p2_cpu else 1)
	_level_row.visible = practice and _p2_cpu
	_level_row.select(_cpu_level)
	_rematch_button.visible = _mode == Mode.ONLINE and _can_rematch()
	_end_match_button.visible = _mode == Mode.ROOM and _room_session != null and _room_session.is_host() \
		and _room_session.phase == RoomSession.Phase.PLAYING


func _refresh_practice() -> void:
	_arena.state = _state
	_arena.rules = _rules
	var names := PackedStringArray()
	var tags := PackedStringArray()
	for i in PLAYERS:
		var cpu := i == 1 and _p2_cpu
		var you := i == 0 and _p2_cpu # with two people at one keyboard, nobody is "you"
		# The fighter's own name on its box; the tag over its head says which is you.
		var who := GameSettings.character if i == 0 else _cpu_character
		names.append(ArtLibrary.fighter_name(who) if _smoke == null and who != "" else "P%d%s" % [i + 1, " CPU" if cpu else " YOU" if you else ""])
		tags.append("CPU" if cpu else ("AUTO" if _auto else "YOU") if you else "")
	_arena.names = names
	_arena.tags = tags
	var nick := Nicknames.for_winner(_state)
	_arena.over_hint = (nick + "   " if nick != "" else "") + _keys_hint("R / F5 / Restart = play again   Esc = menu")
	var checksum := StateHasher.hex(_state.checksum())
	if _smoke != null:
		_arena.debug_text = "SMOKE TEST (bots)   frame %d/%d   checksum %s" % [_state.frame, SceneSmoke.FRAMES, checksum]
	else:
		_arena.debug_text = "frame %d   checksum %s   F2 = P2 %s   F3 = CPU level: %s   F4 = Guts %s   F5 / R = restart   Esc = menu" % [
			_state.frame, checksum, "CPU" if _p2_cpu else "person", LEVEL_NAMES[_cpu_level], "on" if _guts else "off"]


func _refresh_room() -> void:
	var s := _room_session
	if s == null:
		return
	var playing := s.phase == RoomSession.Phase.PLAYING
	_room_screen.visible = s.phase == RoomSession.Phase.LOBBY and not _panel_open()
	if _room_screen.visible:
		_room_screen.show_lobby(s, _share_link)
	_arena.rules = s.rules if s.rules != null else MatchRules.room(StageDefinition.rooftops(), 10)
	_arena.state = s.state if playing else null
	_arena.names = s.fighter_names
	_arena.you_index = s.my_fighter
	_arena.time_left_frames = s.frames_left
	var bubbles: Array = []
	for c: Dictionary in s.chat_log:
		var age: int = s.ticks() - int(c["tick"])
		if age < CHAT_BUBBLE_TICKS and int(c["fighter"]) >= 0:
			bubbles.append({"fighter": c["fighter"], "text": c["text"], "alpha": clampf(1.0 - float(age - 120) / 60.0, 0.0, 1.0)})
	_arena.bubbles = bubbles
	if s.phase == RoomSession.Phase.CONNECTING:
		_arena.status_big = "Connecting..."
		_arena.status_small = PackedStringArray(["Room %s" % s.room])
	elif playing and s.state != null and s.state.match_over:
		var winner := s.state.winner_index
		var nick := Nicknames.for_winner(s.state, s.main_weapons[winner] if winner >= 0 and winner < s.main_weapons.size() else Nicknames.ALL_ROUNDER)
		_arena.over_hint = "%s   Back to the lobby in a moment..." % nick if nick != "" else "Back to the lobby in a moment..."
	elif playing and s.my_fighter < 0:
		_arena.status_small = PackedStringArray(["You're watching: you'll play in the next match."])
	_arena.debug_text = "ROOM %s   %d players   frame %d" % [s.room, s.players.size(), s.state.frame if s.state != null else 0]


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
		var nick := Nicknames.for_winner(state)
		_arena.over_hint = (nick + "   " if nick != "" else "") + _keys_hint("R / F5 / Play again = play again   Esc = menu")
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
	reference_rules.weapons = true
	reference_rules.flurry = true
	reference_rules.hazards = true
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
