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
##   (Resume, Restart, CPU level, Auto play Off / On, Leave) and pauses practice while it is open.
## MENU -> ONLINE: create a room (random code) or join one by code, or open the page with
##   ?room=CODE on the web. An OnlineSession (client/net) runs the match over the relay; this
##   draws session.state and the connection status. R / F5 / Play again / Rematch plays again
##   once a match is over (someone won, or the session ended); Esc or the panel's Leave leaves.
##   The panel can't pause an online match: it says so.
## MENU -> HOST / JOIN A ROOM: rooms of up to 15 players whose match the server runs (RoomSession).
##   The lobby (RoomScreen) shows who's in; the host picks 10 to 15 minutes, adds CPUs and starts.
##   In the match the chat button opens the quick-chat phrases. The server returns everyone to the
##   lobby after the result. A page opened with ?arena=CODE joins that room.
## Once a practice or room match is over the result panel (ResultScreen) shows, with practice's Play
## again, Change fighter, Change arena and Menu, and a room's countdown to the lobby; a duel keeps
## its "P1 WINS!" banner and a "Play again" button. The touch controls show only while a fight of
## ours is on (_fighting).
## Android's back and Esc (rooms v1, fixes 3 and 4) go through one chain, _on_back: the top overlay
## closes first (_close_top_overlay: the creator back to the circle, the circle, the arena slider,
## Settings, How to play, quick chat, the match panel, a question being asked); with none open, a
## match opens its panel (practice pauses), the lobby asks "Leave the room?  Stay / Leave", a wait
## for a connection or an opponent is cancelled, and only the bare menu asks "Quit?  No / Yes".
## Nothing quits the game or leaves a room without asking.
## While a duel connects or waits, and while a room connects, the chosen arena stands behind a
## panel with the code, Copy link, Cancel and moving dots (_refresh_wait, fixes 10 and 23). Errors
## land on the menu's banner with a button for the next step (MenuScreen.error_actions, fix 22).
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
## The room HUD strip stops this far before Chat's left edge (more and a four-team room of 15
## collapses at 1920 px; test_review_fixes checks it).
const HUD_GAP := 12.0

# The winner's nickname for the match on screen (_winner_nick).
var _nick := Nicknames.Kept.new()
const LEVEL_NAMES := ["Easy", "Normal", "Hard"] # by SparringBot.Level
const PLAYERS := 2
## Where an out-of-date Android app is sent to update (the menu banner's "Update").
## How often the web page's own timer sends the room's keepalive (_install_web_keepalive): well
## inside the relay's 60 s idle limit even when a hidden tab's timers run late.
const WEB_KEEPALIVE_MSEC := 15000
const STORE_URL := "https://play.google.com/store/apps/details?id=com.artificialinteractive.rangbazi"

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
## The code of the room this device was last the host of, seen in its lobby: a Rejoin that finds
## it gone says so and offers a new room (MenuScreen.error_actions).
var _hosted_room := ""
## The page timer's callback (_install_web_keepalive), kept so it isn't collected.
var _web_keepalive_cb: JavaScriptObject
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

## Auto play: a CPU plays the local fighter (in any mode) until it is turned off again, so a player
## can step away mid-match. Deliberately only middling, so the others can beat it. Turned on and
## off with the "Auto play: Off / On" chips in the match panel (rooms v1, fix 12); an AUTO badge on
## your HUD chip or player box says it is on.
const AUTO_LEVEL := SparringBot.Level.NORMAL
var _auto := false
var _auto_row: ChoiceRow

## The result panel (rooms v1, 3.3), over the arena once a room or practice match is over.
var _result_layer: CanvasLayer
var _result: ResultScreen

var _smoke: SceneSmoke
var _smoke_done := false
# After the practice run: the result panel and touch checks (SMOKE_TAIL), one step every few frames.
var _smoke_tail := -1
var _smoke_wait := 0
var _smoke_draws := 0

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
# Practice options in the panel, every choice in view: who player 2 is, how good the CPU is, and
# whether Guts is on (the F2 / F3 / F4 keys are hidden extras for the same).
var _p2_row: ChoiceRow
var _level_row: ChoiceRow
var _guts_row: ChoiceRow
var _practice_row: HBoxContainer # Restart, Change fighter, Change arena
var _rematch_button: Button
var _end_confirm: HBoxContainer
var _leave_button: Button
var _leave_confirm: HBoxContainer # "Leave the room?  Stay / Leave"
# How to play, on touch: the (dimmed, asleep) controls under it, each with a callout.
var _help_box: PanelContainer
var _help_center: CenterContainer
var _help_backdrop: ColorRect
var _help_callouts: HelpCallouts
# Quick chat: a strip along the top (the match goes on under it).
var _chat_strip: PanelContainer
# The wait panel: a duel connecting or waiting for its opponent, a room connecting.
var _wait_panel: CenterContainer
var _wait_title: Label
var _wait_note: Label
var _wait_code: Label
var _wait_code_caption: Label
var _wait_cancel: Button
# The frame the last back was handled on (Android can deliver it twice).
var _back_frame := -1
# Polish (rooms v1, section 4): the host's remembered team chip waits to be sent until the lobby
# says we are the host; the team we were last seen on, and when we last asked to change it (the
# team sound plays for our own pick); the frame-time meter for rooms (its line goes to the log).
var _room_teams_pending := false
var _seen_team := -1
var _team_asked_msec := -100000
var _meter := FrameMeter.new()


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
	_install_web_keepalive()
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
	_build_wait_panel()
	_chat_button = _hud_button(_hud, "Chat", _open_chat)
	_chat_button.custom_minimum_size = Vector2(MENU_BUTTON * 1.6, MENU_BUTTON)
	_chat_button.set_anchors_preset(Control.PRESET_TOP_RIGHT)
	_chat_button.offset_left = -(MENU_MARGIN * 2 + MENU_BUTTON + MENU_BUTTON * 1.6)
	_chat_button.offset_right = -(MENU_MARGIN * 2 + MENU_BUTTON)
	_chat_button.offset_top = MENU_MARGIN
	_chat_button.offset_bottom = MENU_MARGIN + MENU_BUTTON

	var touch_layer := CanvasLayer.new()
	touch_layer.name = "Touch"
	touch_layer.layer = 2
	add_child(touch_layer)
	_touch = TouchControls.new()
	_touch.visible = false
	touch_layer.add_child(_touch)

	_result_layer = CanvasLayer.new()
	_result_layer.name = "Result"
	_result_layer.layer = 3
	add_child(_result_layer)
	_result = ResultScreen.new()
	_result.visible = false
	_result_layer.add_child(_result)
	_result.play_again_pressed.connect(func() -> void:
		if _mode == Mode.PRACTICE:
			_restart_practice())
	_result.change_fighter_pressed.connect(func() -> void: _circle.open(GameSettings.character))
	_result.change_arena_pressed.connect(_open_practice_picker)
	_result.menu_pressed.connect(_go_to_menu.bind(""))

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
	# The host's mode chip and minutes are remembered for the next room they host (polish 4).
	_room_screen.minutes_changed.connect(func(m: int) -> void:
		if _smoke == null:
			GameSettings.remember_room(GameSettings.room_teams, m)
		if _room_session != null: _room_session.set_minutes(m))
	_room_screen.bots_changed.connect(func(n: int) -> void:
		if _room_session != null: _room_session.set_bots(n))
	_room_screen.teams_changed.connect(func(count: int) -> void:
		if _smoke == null:
			GameSettings.remember_room(count, GameSettings.room_minutes)
		_room_teams_pending = false
		if _room_session != null: _room_session.set_teams(count))
	_room_screen.team_picked.connect(func(team: int) -> void:
		_team_asked_msec = Time.get_ticks_msec()
		if _room_session != null: _room_session.pick_team(team))
	_room_screen.start_pressed.connect(func() -> void:
		if _room_session != null: _room_session.start_match())
	_room_screen.leave_pressed.connect(_go_to_menu.bind(""))
	_room_screen.copy_requested.connect(_on_copy)

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
	_arena_picker.skill_picked.connect(func(level: int) -> void:
		_cpu_level = level
		_refresh())
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
	_menu.practice_requested.connect(_open_practice_picker)
	_menu.fighter_requested.connect(func() -> void: _circle.open(GameSettings.character))
	_menu.create_requested.connect(func() -> void: _start_online(_new_room_code()))
	_menu.host_requested.connect(_host_room)
	_menu.join_requested.connect(func(code: String) -> void: _start_room(code, false))
	_menu.settings_requested.connect(func() -> void: _settings_screen.open())
	_menu.error_action.connect(_on_error_action)


## The arena slider for practice, with the CPU skill chips (rooms v1, fix 28).
func _open_practice_picker() -> void:
	_picker_for = "practice"
	_arena_picker.open(GameSettings.arena, "Fight here", _cpu_level)


func _host_room() -> void:
	_room_tries = 0
	_start_room(_new_room_code(), true)


## A button on the menu's error banner (MenuScreen.error_actions): the next step after an error.
func _on_error_action(action: String, room: String) -> void:
	match action:
		"host":
			_host_room()
		"rejoin":
			if MenuScreen.is_valid_code(room):
				_start_room(room, false)
		"refresh":
			if OS.has_feature("web"):
				JavaScriptBridge.eval("window.location.reload()")
		"update":
			OS.shell_open(STORE_URL)


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
	column.add_theme_constant_override("separation", 14)
	column.custom_minimum_size = Vector2(1080, 0)
	box.add_child(column)

	# The pickers' and Settings' heading style here too (polish 5).
	_panel_title = UiTheme.heading("")
	column.add_child(_panel_title)
	_panel_note = Label.new()
	_panel_note.text = "The match keeps going while this is open."
	_panel_note.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_panel_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_panel_note.add_theme_font_size_override("font_size", 26)
	_panel_note.add_theme_color_override("font_color", UiTheme.TEXT_DIM)
	column.add_child(_panel_note)

	UiTheme.make_primary(_panel_button(column, "Resume", _close_panel))
	# How to play and Settings side by side (Settings opens over the panel, fix 19).
	var help_row := HBoxContainer.new()
	help_row.add_theme_constant_override("separation", 14)
	column.add_child(help_row)
	_panel_button(help_row, "How to play", func() -> void:
		_close_panel()
		_open_help())
	_panel_button(help_row, "Settings", func() -> void: _settings_screen.open())
	for b in help_row.get_children():
		(b as Control).size_flags_horizontal = Control.SIZE_EXPAND_FILL
	# Practice: start over, or with another fighter or arena, without going back to the menu.
	_practice_row = HBoxContainer.new()
	_practice_row.add_theme_constant_override("separation", 14)
	column.add_child(_practice_row)
	_restart_button = _panel_button(_practice_row, "Restart", func() -> void:
		_restart_practice()
		_close_panel())
	_panel_button(_practice_row, "Change fighter", func() -> void: _circle.open(GameSettings.character))
	_panel_button(_practice_row, "Change arena", _open_practice_picker)
	for b in _practice_row.get_children():
		(b as Control).size_flags_horizontal = Control.SIZE_EXPAND_FILL
		(b as Control).add_theme_font_size_override("font_size", 36)
	_p2_row = ChoiceRow.make("Player 2", ["CPU", "A friend"], 0, func(i: int) -> void:
		_p2_cpu = i == 0
		_refresh(), 260.0, 88.0)
	column.add_child(_p2_row)
	_level_row = ChoiceRow.make("CPU skill", LEVEL_NAMES, 1, func(i: int) -> void:
		_cpu_level = i
		_refresh(), 260.0, 88.0)
	column.add_child(_level_row)
	# Guts as chips next to the CPU's skill (it was only on F4): a rule, so it restarts the match.
	_guts_row = ChoiceRow.make("Guts", ["Off", "On"], 1, func(i: int) -> void:
		if _guts != (i == 1):
			_guts = i == 1
			_restart_practice()
		_refresh(), 260.0, 88.0)
	_guts_row.tooltip_text = "The comeback meter: hits taken fill it, then a free big hit"
	column.add_child(_guts_row)
	# Auto play as two chips, the current one lit (never a button whose label is its state).
	_auto_row = ChoiceRow.make("Auto play", ["Off", "On"], 0, func(i: int) -> void:
		_auto = i == 1
		_refresh(), 260.0, 96.0)
	_auto_row.tooltip_text = "Let the CPU play your fighter while you're away"
	column.add_child(_auto_row)
	_rematch_button = _panel_button(column, "Rematch", func() -> void:
		_on_rematch()
		_close_panel())
	# "End match for everyone" asks first: "End the match?  No / Yes, end it".
	_end_match_button = _panel_button(column, "End match for everyone", _ask_end_match)
	_end_confirm = UiTheme.confirm_row("End the match?", "No", "Yes, end it", func() -> void:
		_end_confirm.hide()
		_end_match_button.show(), func() -> void:
		_end_confirm.hide()
		if _room_session != null:
			_room_session.end_match()
		_close_panel(), 100.0, 38)
	_end_confirm.visible = false
	column.add_child(_end_confirm)
	# Leaving a room leaves the room, so the button says so, and asks first (fix 4). Quitting the
	# game is on the menu, not here next to it (fix 9).
	_leave_button = _panel_button(column, "Leave match", _on_leave_pressed)
	_leave_confirm = UiTheme.confirm_row("Leave the room?", "Stay", "Leave", func() -> void:
		_leave_confirm.hide()
		_leave_button.show(), func() -> void: _go_to_menu(), 100.0, 38)
	_leave_confirm.visible = false
	column.add_child(_leave_confirm)


## The panel's leave button: a room asks "Leave the room?  Stay / Leave"; practice and a duel leave.
func _on_leave_pressed() -> void:
	if _mode == Mode.ROOM:
		_leave_button.hide()
		_leave_confirm.show()
	else:
		_go_to_menu()


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
	_help_backdrop = backdrop
	# On touch: what each of the (dimmed) buttons does, next to it (fix 27).
	_help_callouts = HelpCallouts.new()
	_help_callouts.touch = _touch
	_help_callouts.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_help_callouts.mouse_filter = Control.MOUSE_FILTER_IGNORE
	root.add_child(_help_callouts)
	_help_center = CenterContainer.new()
	_help_center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_help_center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	root.add_child(_help_center)
	var box := PanelContainer.new()
	box.add_theme_stylebox_override("panel", UiTheme.panel_box())
	_help_center.add_child(box)
	_help_box = box
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 18)
	column.custom_minimum_size = Vector2(980, 0)
	box.add_child(column)

	column.add_child(UiTheme.heading("HOW TO PLAY"))
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
	_help_controls.add_theme_font_size_override("font_size", 32)
	_help_controls.add_theme_color_override("font_color", UiTheme.TEXT)
	column.add_child(_help_controls)
	var ok := _hud_button(column, "Got it", _close_help)
	UiTheme.make_primary(ok)
	ok.custom_minimum_size = Vector2(0, 110)
	ok.add_theme_font_size_override("font_size", 44)


## How to play. On touch the card moves to the top, the controls stay on screen under it (dimmed,
## pressing nothing) and each button has a callout naming what it does; elsewhere the card lists
## the keys.
func _open_help() -> void:
	var touch := _prefer_touch and _mode != Mode.MENU
	_help_controls.visible = not touch
	_help_controls.text = "A / D move   Space jump (twice)   J Kick: attack or use your weapon   K Heavy (W + K leaps back)\n" \
		+ "L Dodge   I Combo (a flurry)   U Throw your weapon   Esc pause"
	_help_callouts.visible = touch
	# The card in the top part of the screen on touch, so the buttons and callouts show below it.
	_help_callouts.size = get_viewport_rect().size
	var bottom := _help_callouts.card_bottom() / maxf(1.0, _help_callouts.size.y)
	_help_center.anchor_bottom = bottom if touch else 1.0
	_help_center.offset_bottom = 0
	_help_backdrop.color = Color(0.05, 0.045, 0.07, 0.5 if touch else 0.72)
	_help_layer.show()
	_update_touch()
	_help_callouts.queue_redraw()


func _close_help() -> void:
	_help_layer.hide()
	if not GameSettings.seen_help:
		GameSettings.seen_help = true
		GameSettings.save()
	get_viewport().gui_release_focus()
	_update_touch()


func _panel_button(parent: Control, text: String, on_pressed: Callable) -> Button:
	var b := _hud_button(parent, text, on_pressed)
	b.custom_minimum_size = Vector2(0, 96)
	b.add_theme_font_size_override("font_size", 40)
	return b


func _panel_open() -> bool:
	return _panel_layer.visible


func _open_panel() -> void:
	if _mode == Mode.MENU:
		return
	_chat_layer.hide()
	_panel_layer.show()
	_update_touch()
	_refresh()


## The panel's "End match for everyone": swaps the button for the "End the match? No / Yes" chips.
func _ask_end_match() -> void:
	_end_match_button.hide()
	_end_confirm.show()


func _close_panel() -> void:
	if not _panel_layer.visible:
		return
	_end_confirm.hide() # the questions are asked again next time
	_leave_confirm.hide()
	_leave_button.show()
	_panel_layer.hide()
	_update_touch()
	_refresh()


## Quick chat (rooms v1, fix 21): a strip along the top, under the HUD band and the top-right
## buttons, titled "Quick chat", with the phrases (RoomSession.PHRASES) in a row that slides (about
## six in view) and a Close chip. A tap sends one and closes it. The match goes on under it: your
## fighter keeps its keys and touch controls while it is open.
func _build_chat() -> void:
	_chat_layer = CanvasLayer.new()
	_chat_layer.name = "Chat"
	_chat_layer.layer = 5
	_chat_layer.visible = false
	add_child(_chat_layer)
	var root := Control.new()
	root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	root.theme = UiTheme.build()
	_chat_layer.add_child(root)
	_chat_strip = PanelContainer.new()
	var box := UiTheme.panel_box()
	box.set_content_margin_all(14)
	_chat_strip.add_theme_stylebox_override("panel", box)
	_chat_strip.set_anchors_and_offsets_preset(Control.PRESET_TOP_WIDE)
	_chat_strip.offset_left = MENU_MARGIN
	_chat_strip.offset_right = -MENU_MARGIN
	_chat_strip.offset_top = MENU_MARGIN + MENU_BUTTON + 10.0
	root.add_child(_chat_strip)
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 14)
	_chat_strip.add_child(row)
	var title := Label.new()
	# The heading's colour and case; smaller, as the strip is one row (polish 5).
	title.text = "QUICK CHAT"
	title.add_theme_font_size_override("font_size", 34)
	title.add_theme_color_override("font_color", UiTheme.TEXT_DIM)
	title.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	row.add_child(title)
	var scroll := ScrollContainer.new()
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.vertical_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_SHOW_NEVER
	scroll.custom_minimum_size = Vector2(0, 88)
	row.add_child(scroll)
	var phrases := HBoxContainer.new()
	phrases.add_theme_constant_override("separation", 12)
	scroll.add_child(phrases)
	for i in RoomSession.PHRASES.size():
		var b := _hud_button(phrases, RoomSession.PHRASES[i], func() -> void:
			if _room_session != null:
				_room_session.say(i)
			_close_chat())
		b.custom_minimum_size = Vector2(0, 84)
	var close := _hud_button(row, "Close", _close_chat)
	close.custom_minimum_size = Vector2(150, 84)


func _open_chat() -> void:
	if _mode == Mode.ROOM:
		_chat_layer.show()
		_update_touch()


func _close_chat() -> void:
	_chat_layer.hide()
	_update_touch()


## The touch controls show only while they do something (rooms v1, fix 11): where wanted, with a
## fighter of ours in a match that is running and not over (not while watching, waiting, connecting
## or on the result), and hide while the panel is open (so a tap on it can't also press a fighter's
## buttons). Quick chat leaves them live (fix 21); How to play shows them asleep under its callouts
## (fix 27).
func _update_touch() -> void:
	var want := _prefer_touch and _fighting() and not _panel_open()
	_touch.frozen = _help_layer.visible
	if _touch.visible != want:
		_touch.visible = want


## Whether a fight is on that this device's fighter is in: practice not over; online running (the
## opponent here) and not over; in a room playing, with a fighter of ours, and not over.
func _fighting() -> bool:
	if _my_fighter() < 0:
		return false
	match _mode:
		Mode.PRACTICE:
			return _state != null and not _state.match_over
		Mode.ONLINE:
			return _phase_is("RUNNING") and _session.state != null and not (_session.state as GameState).match_over
		Mode.ROOM:
			return _room_session != null and _room_session.phase == RoomSession.Phase.PLAYING \
				and _room_session.state != null and not _room_session.state.match_over
	return false


# ---- modes ----

## Back to the menu. message, code and room say why a session ended (the error banner's text and
## its next-step buttons, MenuScreen.error_actions).
func _go_to_menu(message: String = "", code := "", room := "", hosting := false) -> void:
	_close_session()
	_close_room()
	_mode = Mode.MENU
	_state = null
	_auto = false
	_close_panel()
	_panel_layer.hide()
	_help_layer.hide()
	_touch.hide()
	# Keyboard / pad users get focus on the first button; on touch screens a focus ring is noise.
	_menu.open(message, not _prefer_touch, code, room, hosting)
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
		_close_panel() # picked from the paused panel: straight into the new match


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
	_install_web_copy() # the lobby's Copy link copies inside the tap on the web
	_copied_until_msec = 0
	# A host's room starts with their last match length and, once the lobby says they are the host,
	# their last mode chip (GameSettings.room_minutes / room_teams, polish 4).
	_room_session = RoomSession.new(code, as_host, GameSettings.room_minutes if as_host else GameSettings.ROOM_MINUTES_MIN,
		player_name, GameSettings.character)
	_room_teams_pending = as_host and GameSettings.room_teams > 0
	_seen_team = -1
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
		if _smoke_done or _smoke_tail >= 0:
			return
		if _state.frame == SceneSmoke.TOUCH_FROM_FRAME:
			_touch.show() # draw the touch layout for the rest of the run
		if _state.frame == SceneSmoke.TOUCH_TEST_FRAME:
			var touch_problem := _smoke.touch_test(_touch, get_viewport())
			if touch_problem != "":
				_end_smoke(false, touch_problem)
				return
		if _smoke.all_weapons:
			SceneSmoke.arm(_state) # the next weapon in turn, before anyone's buttons are worked out
		_smoke.press_keys(_state, _rules)
	if _help_layer.visible:
		return # the first match waits behind the how-to-play card
	if _state.match_over and _smoke == null:
		return # the result stands still behind its panel (the view keeps the winner moving)
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
	if _smoke != null and _state.frame >= _smoke.frames():
		_finish_smoke()


func _online_step() -> void:
	if _session == null:
		return
	_session.tick(_auto_buttons() if _auto else _local_buttons(0))
	_stalled_ticks = _stalled_ticks + 1 if _session.stalled else 0


func _room_step() -> void:
	if _room_session == null or _smoke_tail >= 0:
		return # the smoke test's room has no server: it only draws
	var playing := _room_session.phase == RoomSession.Phase.PLAYING
	if not playing:
		_auto = false # every match starts with you at the controls
	var buttons := 0
	if playing and _auto:
		buttons = _auto_buttons()
	elif playing:
		buttons = _local_buttons(0) # quick chat's strip leaves the controls live
	if not playing and _chat_layer.visible:
		_close_chat()
	_room_session.tick(buttons)
	if _room_session.phase == RoomSession.Phase.ENDED:
		var code := _room_session.end_code
		var message := _room_session.end_message
		if code == "taken" and _room_host and _room_tries < HOST_RETRIES:
			_room_tries += 1
			_start_room(_new_room_code(), true) # that code was in use: try another
			return
		# A host whose room the server never opened tries again by hosting, not by joining a code
		# that was never made; a host whose room has since closed (EmptyRoomGrace ran out) is told
		# so, and offered a new one (final round).
		var hosting := _room_host and _room_session.my_id < 0
		if code == "no_room" and _room != "" and _room == _hosted_room:
			message = "Your room %s has closed. Host a new one to play on." % _room
			hosting = true
		_go_to_menu(message if code != "left" else "", code, _room, hosting)
		return
	if _room_session.is_host():
		_hosted_room = _room
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


## Whether there is a fight on that auto play could take over (its chips show in the panel then).
func _can_auto() -> bool:
	if _smoke != null or not _fighting():
		return false
	if _mode == Mode.PRACTICE:
		return _p2_cpu # with two people at one keyboard, both play
	return true


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
	if _smoke != null:
		return
	if event.is_action_pressed(InputReader.BACK):
		_on_back() # Esc and a pad's Back: the same chain as Android's back button
	elif _mode == Mode.MENU:
		return
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


## Android's back, Esc and a pad's Back (rooms v1, fixes 3 and 4): close the top overlay; with none
## open, a match opens its panel (practice pauses), the lobby asks "Leave the room?", a wait for a
## connection or an opponent is cancelled, a finished practice or duel goes to the menu, and the bare
## menu asks "Quit?  No / Yes". Nothing here quits the game or leaves a room without asking.
func _on_back() -> void:
	var frame := Engine.get_process_frames()
	if frame == _back_frame:
		return # the same press delivered twice
	_back_frame = frame
	if _close_top_overlay():
		_refresh()
		return
	match _mode:
		Mode.MENU:
			if UiTheme.can_quit():
				_menu.ask_quit()
		Mode.PRACTICE:
			if _state != null and _state.match_over:
				_go_to_menu()
			else:
				_open_panel()
		Mode.ONLINE:
			if _phase_is("RUNNING") and not _can_rematch():
				_open_panel()
			else:
				_go_to_menu() # cancel the wait, or leave a duel that is over
		Mode.ROOM:
			if _room_session == null or _room_session.phase == RoomSession.Phase.CONNECTING:
				_go_to_menu() # not in the room yet: Cancel
			elif _room_session.phase == RoomSession.Phase.LOBBY:
				_room_screen.ask_leave()
			else:
				_open_panel()
	_refresh()


## Closes whatever is on top, in this order: the creator (back to the circle), the circle, the arena
## slider, Settings, How to play, quick chat, the match panel, then a question being asked (the
## lobby's "Leave the room?", the menu's "Quit?"). False if nothing was open.
func _close_top_overlay() -> bool:
	if _creator.visible:
		_creator.hide()
		_circle.open(GameSettings.character)
	elif _circle.visible:
		_circle.hide()
	elif _arena_picker.visible:
		_arena_picker.hide()
	elif _settings_screen.visible:
		_settings_screen.close()
	elif _help_layer.visible:
		_close_help()
	elif _chat_layer.visible:
		_close_chat()
	elif _panel_open():
		_close_panel()
	elif _room_screen.visible and _room_screen.leave_asked():
		_room_screen.cancel_leave()
	elif _menu.visible and _menu.quit_asked():
		_menu.cancel_quit()
	else:
		return false
	return true


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
		_sound.copied()


func _copy_label() -> String:
	if Time.get_ticks_msec() < _copied_until_msec:
		return "Copied!"
	return "Copy link" if _share_link != "" else "Copy code"


func _copy_text() -> String:
	return _share_link if _share_link != "" else _room


## The copy button on screen now: the lobby's Copy link, the duel's on the wait panel, or null.
## (Android has no share sheet the engine can open, so there too it copies: rooms v1, fix 7.)
func _active_copy_button() -> Button:
	if _room_screen.visible and not _room_screen.leave_asked():
		return _room_screen.copy_button()
	if _copy_button.is_visible_in_tree():
		return _copy_button
	return null


## Web: tells the page where the copy button is (in canvas pixels) and what it copies, or that it's
## hidden, whenever that changes; then picks up a copy the page finished ("Copied!" shows only then).
func _sync_web_copy() -> void:
	if not OS.has_feature("web"):
		return
	var target := ""
	var button := _active_copy_button()
	if button != null:
		var r := get_viewport().get_final_transform() * button.get_global_rect()
		target = "%s, %d, %d, %d, %d" % [JSON.stringify(_copy_text()),
			floori(r.position.x), floori(r.position.y), ceili(r.size.x), ceili(r.size.y)]
	if target != _web_copy_target:
		_web_copy_target = target
		JavaScriptBridge.eval("window.rangbaaziCopy && window.rangbaaziCopy.target(%s)" % (target if target != "" else "null"))
	if button != null and str(JavaScriptBridge.eval("window.rangbaaziCopy ? window.rangbaaziCopy.take() : ''")) == "copied":
		_copied_until_msec = Time.get_ticks_msec() + COPIED_MSEC
		_sound.copied()


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
		window.prompt('Copy this link and send it to your friends:', text);
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


## On the web a hidden tab gets no frames, so the room's keepalive (RoomSession.tick) stops and the
## relay would close the connection after 60 s of silence: a host who switches apps to share the
## link loses the room. A page timer, which browsers still run (about once a second at most) in a
## hidden tab, sends it instead (final round).
func _install_web_keepalive() -> void:
	if not OS.has_feature("web") or _web_keepalive_cb != null:
		return
	_web_keepalive_cb = JavaScriptBridge.create_callback(_on_web_keepalive)
	var window := JavaScriptBridge.get_interface("window")
	if window != null:
		window.setInterval(_web_keepalive_cb, WEB_KEEPALIVE_MSEC)


func _on_web_keepalive(_args: Array) -> void:
	if _room_session != null:
		_room_session.keepalive()


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
			# Android's back button (quit_on_go_back is off): never quits from here.
			if _smoke == null:
				_on_back()


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
	# A local test page's ?relay= (OnlineSession.relay_url_for_room) goes along, so a second tab
	# opened from the link finds the same relay.
	var relay := str(JavaScriptBridge.eval("new URLSearchParams(window.location.search).get('relay') || ''"))
	return share_link_for(origin, path, code, relay)


## The web share link: the page's address with ?room=CODE, plus ?relay= when the page has a local
## test relay's address (ws://127.0.0.1 or localhost; anything else is dropped).
static func share_link_for(origin: String, path: String, code: String, relay := "") -> String:
	var link := "%s%s?room=%s" % [origin, path, code]
	if relay.begins_with("ws://127.0.0.1:") or relay.begins_with("ws://localhost:"):
		link += "&relay=" + relay.uri_encode()
	return link


# ---- view ----

## Hands the arena what to draw and shows the HUD buttons that apply. Reads state only.
func _refresh() -> void:
	if _smoke == null:
		_update_touch() # the controls go as soon as they would do nothing (the match ended, say)
	_arena.names = PackedStringArray()
	_arena.tags = PackedStringArray()
	_arena.status_big = ""
	_arena.status_small = PackedStringArray()
	_arena.over_hint = ""
	_arena.debug_text = ""
	# Keep the arena's touch layout while the panel hides the controls or the result is up, so the
	# arena doesn't jump. Watching, it uses the whole screen.
	var over := _arena.state != null and _arena.state.match_over
	_arena.touch_layout = _touch.visible or (_prefer_touch and _my_fighter() >= 0 and (_panel_open() or over) and _mode != Mode.MENU)
	# Text sizes follow the device, not the layout: a watcher on a phone has the whole screen but
	# still reads the touch sizes (rooms v1, 2.6).
	_arena.touch_text = _prefer_touch or TouchControls.handheld()
	# The HUD keeps clear of the top-right buttons: the menu, and in a room Chat too (rooms v1, 3.1),
	# with a gap before Chat's edge so the last chip and Chat aren't one tap target.
	_arena.hud_right_inset = 2.0 * MENU_MARGIN + 2.6 * MENU_BUTTON + HUD_GAP if _mode == Mode.ROOM else MENU_BUTTON + MENU_MARGIN
	_arena.hud_top_inset = MENU_MARGIN + MENU_BUTTON + 10.0
	_arena.auto_on = _auto
	_arena.show_banner = _mode == Mode.ONLINE # practice and rooms have the result panel
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
	_arena.show_hitboxes = GameSettings.show_hitboxes and SettingsScreen.developer_rows()
	_arena.dynamic_camera = GameSettings.follow_camera
	_chat_button.visible = _mode == Mode.ROOM and _room_session != null \
		and _room_session.phase == RoomSession.Phase.PLAYING and not _panel_open()
	# Each set once per refresh: hiding a button, even for an instant, cancels a press in progress.
	_again_button.visible = not _panel_open() and _mode == Mode.ONLINE and _can_play_again()

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
	# The lobby has its own Leave room and back question: the match menu button would only ghost
	# through its backdrop.
	_menu_button.visible = not _room_screen.visible
	if not (GameSettings.show_debug and SettingsScreen.developer_rows()) and _smoke == null:
		_arena.debug_text = ""
	# The wait panel says it all while waiting: no status lines over the arena behind it.
	if _refresh_wait():
		_arena.status_big = ""
		_arena.status_small = PackedStringArray()
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
	_update_sound()
	_refresh_result()
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
	_sound.arena_half = _arena.half_width() + 1.0 # 17 m, or 25 m on the big level
	# Two people at one keyboard: nobody is "you", so both hear the winner's jingle.
	if _mode == Mode.PRACTICE and not _p2_cpu:
		you = -1
	_sound.observe(_arena.state, _arena.fighter_characters, you, frames_left, _arena.rules)


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


## The wait panel (rooms v1, fixes 10 and 23): centred over the player's own arena while a duel
## connects or waits for its opponent and while a room connects. A title with moving dots, a line
## saying what is happening, the code in large type, Cancel, and (a duel waiting) Copy link.
func _build_wait_panel() -> void:
	_wait_panel = CenterContainer.new()
	_wait_panel.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_wait_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_wait_panel.visible = false
	_hud.add_child(_wait_panel)
	var box := PanelContainer.new()
	box.add_theme_stylebox_override("panel", UiTheme.panel_box())
	_wait_panel.add_child(box)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 14)
	column.custom_minimum_size = Vector2(900, 0)
	box.add_child(column)
	_wait_title = _wait_label(column, 56, UiTheme.ACCENT)
	_wait_note = _wait_label(column, 32, UiTheme.TEXT)
	_wait_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_wait_code_caption = _wait_label(column, 28, UiTheme.TEXT_DIM)
	_wait_code_caption.text = "ROOM CODE"
	_wait_code = _wait_label(column, 110, UiTheme.TEXT)
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 20)
	column.add_child(row)
	_wait_cancel = _hud_button(row, "Cancel", _go_to_menu.bind(""))
	_wait_cancel.custom_minimum_size = Vector2(0, 104)
	_wait_cancel.add_theme_font_size_override("font_size", 40)
	_wait_cancel.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_copy_button = _hud_button(row, "Copy link", _on_copy)
	_copy_button.custom_minimum_size = Vector2(0, 104)
	_copy_button.add_theme_font_size_override("font_size", 40)
	_copy_button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	UiTheme.make_primary(_copy_button)


func _wait_label(parent: Control, font_size: int, colour: Color) -> Label:
	var l := Label.new()
	l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	l.add_theme_font_size_override("font_size", font_size)
	l.add_theme_color_override("font_color", colour)
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	parent.add_child(l)
	return l


## What the wait panel says now, or hides it (see _build_wait_panel). Returns whether it shows.
func _refresh_wait() -> bool:
	var title := ""
	var note := ""
	var copy := false
	if _mode == Mode.ONLINE and _session != null:
		if _phase_is("CONNECTING"):
			title = "Connecting"
			note = "Opening your duel room."
		elif _phase_is("WAITING_FOR_OPPONENT"):
			title = "Waiting for your opponent"
			note = "Send them the link, or they type the code under Join on the menu." if _share_link != "" \
				else "They type the code under Join on the menu."
			copy = true
		elif _phase_is("STARTING"):
			title = "Starting"
			note = "Your opponent is here."
	elif _mode == Mode.ROOM and _room_session != null and _room_session.phase == RoomSession.Phase.CONNECTING:
		title = "Connecting"
		note = "Opening your room." if _room_host else "Joining the room."
	var show := title != "" and not _panel_open()
	_wait_panel.visible = show
	if show:
		_wait_title.text = title + UiTheme.dots()
		_wait_note.text = note
		_wait_code.text = _room
		_copy_button.visible = copy
		_copy_button.text = _copy_label()
	return show


## The wait panel's Cancel, for tests.
func wait_cancel_button() -> Button:
	return _wait_cancel


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
	_panel_title.text = "PAUSED" if practice else "MATCH MENU"
	_panel_note.visible = not practice
	_practice_row.visible = practice
	# A second person needs keys or a pad: on a phone with no pad, "A friend" would have no controls.
	_p2_row.visible = practice and (not _prefer_touch or not Input.get_connected_joypads().is_empty() or not _p2_cpu)
	_p2_row.select(0 if _p2_cpu else 1)
	_level_row.visible = practice and _p2_cpu
	_level_row.select(_cpu_level)
	_guts_row.visible = practice
	_guts_row.select(1 if _guts else 0)
	_rematch_button.visible = _mode == Mode.ONLINE and _can_rematch()
	var can_end := _mode == Mode.ROOM and _room_session != null and _room_session.is_host() \
		and _room_session.phase == RoomSession.Phase.PLAYING
	_end_confirm.visible = can_end and _end_confirm.visible
	_end_match_button.visible = can_end and not _end_confirm.visible
	_auto_row.visible = _can_auto()
	_auto_row.select(1 if _auto else 0)
	_leave_button.text = "Leave room" if _mode == Mode.ROOM else "Leave match"
	_leave_button.visible = not _leave_confirm.visible


## The winner's nickname for the match on screen, the same on every tick it stays over
## (Nicknames.Kept).
func _winner_nick(s: GameState, code := -1) -> String:
	return _nick.for_match(s, code)


## The result panel: shown once a practice match or a room's match is over (with the touch
## controls hidden, _fighting), hidden otherwise. A duel keeps its banner and Play again.
func _refresh_result() -> void:
	var shown: GameState = _arena.state
	var want := shown != null and shown.match_over and (_mode == Mode.PRACTICE or _mode == Mode.ROOM)
	_arena.hide_tags = want
	if not want:
		if _result.visible:
			_result.hide()
		return
	if _mode == Mode.PRACTICE:
		var you := 0 if _p2_cpu else -1
		_result.show_practice(shown, _arena.rules, _arena.names, _arena.fighter_characters, you, _winner_nick(shown),
			_keys_hint("R = play again   Esc = menu"))
	else:
		var s := _room_session
		var winner := shown.winner_index
		var nick := _winner_nick(shown, s.main_weapons[winner] if winner >= 0 and winner < s.main_weapons.size() else Nicknames.ALL_ROUNDER)
		var left := s.result_left()
		_result.show_room(shown, _arena.rules, _arena.names, _arena.fighter_characters, s.my_fighter, nick,
			left, ceili(maxf(left, 0.0) * RoomSession.RESULT_TICKS / 60.0))


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
	var nick := _winner_nick(_state)
	_arena.over_hint = (nick + "   " if nick != "" else "") + _keys_hint("R / F5 / Restart = play again   Esc = menu")
	var checksum := StateHasher.hex(_state.checksum())
	if _smoke != null:
		_arena.debug_text = "SMOKE TEST (bots)   frame %d/%d   checksum %s" % [_state.frame, _smoke.frames(), checksum]
	else:
		_arena.debug_text = "frame %d   checksum %s   F2 = P2 %s   F3 = CPU level: %s   F4 = Guts %s   F5 / R = restart   Esc = menu" % [
			_state.frame, checksum, "CPU" if _p2_cpu else "person", LEVEL_NAMES[_cpu_level], "on" if _guts else "off"]


func _refresh_room() -> void:
	var s := _room_session
	if s == null:
		return
	var playing := s.phase == RoomSession.Phase.PLAYING
	_room_screen.visible = s.phase == RoomSession.Phase.LOBBY and not _panel_open()
	if s.phase == RoomSession.Phase.LOBBY and s.room_phase == "lobby":
		if _room_teams_pending and s.is_host():
			_room_teams_pending = false
			if s.team_count != GameSettings.room_teams:
				s.set_teams(GameSettings.room_teams)
		# Your own pick of a team landed: a small confirmation (polish 2).
		var team := s.my_team()
		if _seen_team >= 0 and team > 0 and team != _seen_team and Time.get_ticks_msec() - _team_asked_msec < 3000:
			_sound.team_joined()
		_seen_team = team
	if _room_screen.visible:
		_room_screen.show_lobby(s, _share_link)
		_room_screen.set_copy_label(_copy_label())
	elif _room_screen.leave_asked():
		_room_screen.cancel_leave() # the match started: the question is moot
	_arena.rules = s.rules if s.rules != null else MatchRules.room(StageDefinition.rooftops(), 10)
	if s.phase == RoomSession.Phase.CONNECTING:
		_arena.rules = _menu_rules() # your own arena behind the wait panel
	_arena.state = s.state if playing else null
	_arena.names = s.fighter_names
	# Someone who left mid-match: the CPU plays their fighter now, and its tag says so.
	var tags := PackedStringArray()
	for i in s.fighter_names.size():
		tags.append("CPU" if playing and s.fighter_left(i) else "")
	_arena.tags = tags
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
		var nick := _winner_nick(s.state, s.main_weapons[winner] if winner >= 0 and winner < s.main_weapons.size() else Nicknames.ALL_ROUNDER)
		_arena.over_hint = "%s   Back to the lobby in a moment..." % nick if nick != "" else "Back to the lobby in a moment..."
	elif playing and s.my_fighter < 0:
		_arena.status_small = PackedStringArray(["You're watching: you'll get a team in the next match." if s.team_count > 0
			else "You're watching: you'll play in the next match."])
	_arena.debug_text = "ROOM %s   %d players   frame %d" % [s.room, s.players.size(), s.state.frame if s.state != null else 0]


func _refresh_online() -> void:
	if _session == null:
		return
	var s = _session # OnlineSession
	var state: GameState = s.state
	var slot: int = s.local_slot
	# Before the match: the player's own arena stands behind the wait panel (fix 10), not a graybox.
	var waiting := _phase_is("CONNECTING") or _phase_is("WAITING_FOR_OPPONENT") or _phase_is("STARTING")
	_arena.state = null if waiting else state
	_arena.rules = _menu_rules() if waiting else (s.rules if s.rules != null else _rules)
	var names := PackedStringArray()
	var tags := PackedStringArray()
	for i in PLAYERS:
		names.append("P%d YOU" % (i + 1) if i == slot else "P%d" % (i + 1))
		tags.append("YOU" if i == slot else "")
	_arena.names = names
	_arena.tags = tags

	# Connection status over the arena (before the match, the wait panel says it: _refresh_wait).
	var big := ""
	var small := PackedStringArray()
	if _phase_is("RUNNING"):
		if s.interrupted:
			big = "Connection interrupted..."
		elif _stalled_ticks > STALL_NOTICE_TICKS:
			small.append("Waiting for your opponent...")
	elif _phase_is("ENDED"):
		big = s.end_message
		if not _prefer_touch:
			small.append("R / F5 / Play again = play again, Esc = menu")
	if _phase_is("RUNNING") and state != null and state.match_over:
		var nick := _winner_nick(state)
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
	print("SCENE SMOKE: practice, P1 = SparringBot Hard on P1's keys, P2 = CPU Normal, %d frames%s" % [_smoke.frames(),
		", every crate weapon in turn (%d of them)" % Weapons.crate_list().size() if _smoke.all_weapons else ""])
	_go_to_menu() # through the menu, as a person would
	_start_practice()


func _process(_delta: float) -> void:
	_measure_frame()
	if _smoke != null and not _smoke_done and _smoke_tail >= 0:
		_smoke_tail_tick()
	# Watchdog: a run that stops advancing (or never starts) still ends, with exit code 1.
	if _smoke != null and not _smoke_done and _smoke.timed_out():
		_end_smoke(false, "timed out after %d ms at frame %d%s" % [
			SceneSmoke.TIMEOUT_MSEC, _state.frame if _state != null else -1, _smoke.error_note()])


## While a room's match is on, every frame goes to the FrameMeter, and every 5 s its line (average
## frame time and rate, the slowest frame, the arena's drawing time) is printed to the log: on
## Android, `adb logcat -s godot:I` shows it; on the web, the browser's console (polish 6). Nothing
## shows on screen, so release builds carry it too.
func _measure_frame() -> void:
	var s := _room_session
	if _mode != Mode.ROOM or s == null or s.phase != RoomSession.Phase.PLAYING or _arena.state == null or _smoke != null:
		_meter.reset()
		return
	if not _meter.frame(Time.get_ticks_usec(), _arena.last_draw_usec):
		return
	var view := get_viewport().get_visible_rect().size
	var screen := DisplayServer.window_get_size()
	print(_meter.line("room of %d fighters, %s level, %dx%d window (%dx%d logical), %s" % [_arena.state.fighters.size(),
		"big" if _arena.rules.stage.size == StageDefinition.SIZE_BIG else "normal", screen.x, screen.y, view.x, view.y,
		"web" if OS.has_feature("web") else OS.get_name()]))


func _finish_smoke() -> void:
	var reference_rules := MatchRules.new()
	reference_rules.weapons = true
	reference_rules.flurry = true
	reference_rules.hazards = true
	if reference_rules.fingerprint() != _rules.fingerprint():
		_end_smoke(false, "the practice rules differ from the defaults the reference run uses")
		return
	var expected := SceneSmoke.reference_checksum(reference_rules, _smoke.all_weapons)
	var actual := _state.checksum()
	if actual != expected:
		_end_smoke(false, "frames=%d checksum=%s but the pure sim run gives %s%s" % [
			_state.frame, StateHasher.hex(actual), StateHasher.hex(expected), _smoke.error_note()])
		return
	var problem := _smoke.final_problem(_state, _rules, _arena.match_draws)
	if problem != "":
		_end_smoke(false, problem)
		return
	var view_problem := SceneSmoke.whole_level_problem()
	if view_problem != "":
		_end_smoke(false, view_problem)
		return
	print("SCENE SMOKE: P1 held buttons on %d frames, arena drew the match %d times, touch self-test passed, whole level visible in rooms at %d sizes" % [
		_smoke.pressed_frames, _arena.match_draws, 2 * SceneSmoke.VIEW_WINDOWS.size()])
	_smoke_pass_detail = "frames=%d checksum=%s" % [_state.frame, StateHasher.hex(actual)]
	_smoke_tail = 0
	_smoke_wait = 0


## The smoke test's last part (rooms v1, 6.4): the result panel shows with the touch controls
## hidden, in practice and in a room of 15 on four teams (drawn, with the HUD strip, for a few
## frames each, so a drawing error fails the run); the controls are on while a room's match runs and
## off while watching. Each step sets the scene up, then is checked SMOKE_SETTLE frames later.
const SMOKE_SETTLE := 3
var _smoke_pass_detail := ""


func _smoke_tail_tick() -> void:
	if _smoke_wait > 0:
		_smoke_wait -= 1
		_refresh()
		return
	var problem := ""
	match _smoke_tail:
		0: # practice over, on a phone: the result, and no touch controls over it
			_prefer_touch = true
			_touch.show()
			_state.match_over = true
			_state.winner_index = 0
			_refresh()
			_update_touch()
		1:
			problem = _smoke_result_problem("practice", true)
			if problem == "":
				_smoke_room(false)
		2: # a room of 15 playing: the controls are on, the result isn't
			if not _touch.visible:
				problem = "touch: the controls are hidden while a room's match runs"
			elif _result.visible:
				problem = "result: the panel shows while the match runs"
			_smoke_draws = _arena.match_draws
			if problem == "":
				_room_session.state.match_over = true
				_room_session.state.winner_index = 0
				_room_session.note_over()
				_refresh()
				_update_touch()
		3:
			problem = _smoke_result_problem("room", false)
			if problem == "" and _arena.match_draws <= _smoke_draws:
				problem = "result: the room's arena stopped drawing behind the result panel"
			if problem == "": # watching: no fighter of ours, so no controls
				_room_session.state.match_over = false
				_room_session.my_fighter = -1
				_refresh()
				_update_touch()
		4:
			if _touch.visible:
				problem = "touch: the controls show while watching"
			elif _result.visible:
				problem = "result: the panel shows while watching a match that runs"
			if problem == "":
				problem = _smoke.errors_problem()
			if problem == "":
				print("SCENE SMOKE: the result panel showed with the touch controls hidden (practice, and a room of 15 in four teams); the controls were on in the room's match and off while watching")
				# Back in a match (watching it): the panel opens, the room stays.
				problem = _smoke_back_in_match() # ends on the menu, for the screens' back checks
		5:
			problem = _smoke_back_problem()
			if problem == "":
				_smoke_lobby()
		6:
			problem = _smoke_lobby_problem()
			if problem == "":
				problem = _smoke.errors_problem()
			if problem == "":
				print("SCENE SMOKE: Android back closed Settings, the circle, the creator and the arena slider and never quit; in a room it opened the panel or asked to leave; the 15-member lobby kept Leave room and START on screen at %d sizes" % SceneSmoke.VIEW_WINDOWS.size())
				_end_smoke(true, _smoke_pass_detail)
				return
	if problem != "":
		_end_smoke(false, problem)
		return
	_smoke_tail += 1
	_smoke_wait = SMOKE_SETTLE


## "" if the result panel is up with the touch controls hidden, inside the screen under the HUD band
## at every smoke window size (and, for practice, its buttons on screen), else what is wrong.
func _smoke_result_problem(what: String, buttons: bool) -> String:
	_update_touch()
	if not _result.visible:
		return "result: no result panel once the %s match was over" % what
	if _touch.visible:
		return "result: the touch controls cover the %s result" % what
	var view := get_viewport_rect()
	if buttons:
		for b: Node in _result._buttons.get_children():
			var r := (b as Control).get_global_rect()
			if not view.encloses(r):
				return "result: the %s button %s at %s is off the %s screen" % [what, (b as Button).text, r, view.size]
	for window: Vector2 in SceneSmoke.VIEW_WINDOWS:
		var size := SceneSmoke.logical_size(window)
		var panel := _result.panel_rect(size)
		if not Rect2(Vector2(0, 90), size - Vector2(0, 90)).encloses(panel):
			return "result: the %s panel %s isn't inside %s under the HUD band" % [what, panel, size]
	return ""


## A room of 15 on four teams with no server behind it, on the big level, playing (frame 0), for
## the smoke test to draw.
func _smoke_room(watching: bool) -> void:
	var n := 15
	var s := RoomSession.new("SMOKE", true, 10, "Smoke", "shahn", false)
	s.my_id = 1
	s.host_id = 1
	s.team_count = 4
	var teams := PackedInt32Array()
	var ids: Array = ArtLibrary.fighters().map(func(f: Dictionary) -> String: return str(f["id"]))
	for i in n:
		teams.append(1 + i * 4 / n)
		s.fighter_names.append("P%d" % (i + 1))
		s.fighter_characters.append(str(ids[i % ids.size()]) if not ids.is_empty() else "shahn")
		s.fighter_cpu.append(i > 0)
		s.fighter_ids.append(i + 1)
	s.fighter_teams = teams
	s.rules = MatchRules.room(StageDefinition.by_id("rooftops", StageDefinition.size_for(n)), 10, PackedInt32Array(), teams)
	s.state = Simulation.create_initial_state(s.rules, n, SceneSmoke.SEED)
	s.main_weapons.resize(n)
	s.my_fighter = -1 if watching else 0
	s.frames_left = s.rules.time_limit_frames
	s.phase = RoomSession.Phase.PLAYING
	_room_session = s
	_mode = Mode.ROOM
	_refresh()
	_update_touch()


## One press of Android's back, as the smoke test makes it (the once-a-frame guard reset first).
func _smoke_back() -> void:
	_back_frame = -1
	_on_back()


## Back in a room's match (rooms v1, fix 4): the panel opens and the room stays; back again closes it.
func _smoke_back_in_match() -> String:
	_close_panel()
	_smoke_back()
	if _mode != Mode.ROOM or _room_session == null:
		return "back: a press in a room's match left the room"
	if not _panel_open():
		return "back: a press in a room's match didn't open the match panel"
	_smoke_back()
	if _panel_open() or _mode != Mode.ROOM:
		return "back: a second press didn't just close the match panel"
	_go_to_menu()
	return ""


## Back on every screen that used to quit the game (fix 3): each closes and the game stays; the bare
## menu asks "Quit?" and a second press only takes the question away.
func _smoke_back_problem() -> String:
	if _mode != Mode.MENU or not _menu.visible:
		return "back: not on the menu to start with"
	var screens: Array = [
		["Settings", func() -> void: _settings_screen.open(), func() -> bool: return _settings_screen.visible],
		["the fighter circle", func() -> void: _circle.open(GameSettings.character), func() -> bool: return _circle.visible],
		["the arena slider", _open_practice_picker, func() -> bool: return _arena_picker.visible],
	]
	for s: Array in screens:
		(s[1] as Callable).call()
		if not (s[2] as Callable).call():
			return "back: %s didn't open" % s[0]
		_smoke_back()
		if (s[2] as Callable).call():
			return "back: a press didn't close %s" % s[0]
		if _mode != Mode.MENU or not _menu.visible or _menu.quit_asked():
			return "back: closing %s did more than close it" % s[0]
	# The creator goes back to the circle, then the circle closes.
	_creator.open(GameSettings.character)
	_smoke_back()
	if _creator.visible or not _circle.visible:
		return "back: the creator didn't go back to the fighter circle"
	_smoke_back()
	if _circle.visible:
		return "back: the circle (after the creator) didn't close"
	if UiTheme.can_quit():
		_smoke_back()
		if not _menu.quit_asked():
			return "back: the bare menu didn't ask \"Quit?\""
		_smoke_back()
		if _menu.quit_asked() or not _menu.visible:
			return "back: a second press on \"Quit?\" didn't just take the question away"
	return ""


## A lobby of 15 in four teams (host, no server behind it), shown by the scene; and the same lobby,
## in teams and as a free-for-all, laid out at every smoke window's logical size.
var _smoke_lobbies: Array = [] # [[holder, RoomScreen, size, what]]


func _smoke_lobby_session(teams: int) -> RoomSession:
	# The widest code the alphabet makes, so the lobby check sees the top bar at its tightest.
	var s := RoomSession.new("MWMWM", true, 10, "Smoke", "shahn", false)
	s.my_id = 1
	s.host_id = 1
	s.team_count = teams
	s.phase = RoomSession.Phase.LOBBY
	var ids: Array = ArtLibrary.fighters().map(func(f: Dictionary) -> String: return str(f["id"]))
	for i in 15:
		s.players.append({"id": i + 1, "name": "Player%d" % (i + 1) if i < 4 else "CPU %d" % (i + 1), "cpu": i >= 4,
			"character": str(ids[i % ids.size()]) if not ids.is_empty() else "shahn",
			"team": 1 + i % 4 if teams > 0 else 0, "left": false})
	return s


func _smoke_lobby() -> void:
	_room_session = _smoke_lobby_session(4)
	_mode = Mode.ROOM
	_room = "SMOKE"
	_menu.hide()
	_refresh()
	var layer := CanvasLayer.new()
	layer.layer = 9
	layer.name = "SmokeLobbies"
	add_child(layer)
	for window: Vector2 in SceneSmoke.VIEW_WINDOWS:
		var size := SceneSmoke.logical_size(window)
		for teams: int in [4, 0]:
			var holder := Control.new()
			holder.position = Vector2.ZERO
			holder.size = size
			holder.visible = false
			layer.add_child(holder)
			var screen := RoomScreen.new()
			holder.add_child(screen)
			holder.visible = true
			screen.show_lobby(_smoke_lobby_session(teams), "https://example.test/?arena=SMOKE")
			_smoke_lobbies.append([holder, screen, size, "%s, %s" % [size, "four teams" if teams > 0 else "free-for-all"]])


## The lobby checks: back asks "Leave the room?" (and back again takes the question away) without
## leaving; every laid-out lobby of 15 has Leave room, Copy link and START on its screen.
func _smoke_lobby_problem() -> String:
	if not _room_screen.visible:
		return "lobby: the room screen isn't showing the smoke lobby"
	_smoke_back()
	if _mode != Mode.ROOM or not _room_screen.visible:
		return "back: a press in the lobby left the room"
	if not _room_screen.leave_asked():
		return "back: a press in the lobby didn't ask \"Leave the room?\""
	_smoke_back()
	if _room_screen.leave_asked() or _mode != Mode.ROOM:
		return "back: a second press didn't just take \"Leave the room?\" away"
	var problem := ""
	for l: Array in _smoke_lobbies:
		var screen: RoomScreen = l[1]
		var screen_rect := Rect2(Vector2.ZERO, l[2] as Vector2).grow(0.5)
		for b: Array in [["Leave room", screen.leave_button()], ["Copy link", screen.copy_button()], ["START", screen.start_button()]]:
			var button: Button = b[1]
			if not button.is_visible_in_tree():
				problem = "lobby: %s isn't shown in the lobby of 15 (%s)" % [b[0], l[3]]
			elif not screen_rect.encloses(button.get_global_rect()):
				problem = "lobby: %s at %s is off the %s screen" % [b[0], button.get_global_rect(), l[3]]
			if problem != "":
				break
		if problem != "":
			break
		# The room code is never cut: its type shrinks to fit between Leave room and Copy link.
		var title := screen.title_label()
		var font := title.get_theme_font("font")
		var width := font.get_string_size(title.text, HORIZONTAL_ALIGNMENT_LEFT, -1, title.get_theme_font_size("font_size")).x
		if width > title.size.x + 0.5:
			problem = "lobby: the code %s is %.0f px wide in a %.0f px box (%s)" % [title.text, width, title.size.x, l[3]]
			break
	for l: Array in _smoke_lobbies:
		(l[0] as Node).get_parent().queue_free()
		break
	_smoke_lobbies.clear()
	return problem


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
