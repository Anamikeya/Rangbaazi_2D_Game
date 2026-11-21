class_name MenuScreen
extends Control
## The title menu. Left: your fighter, big, with your name. Right: three ways to play, each a
## large card that says what it is (host a room for up to 15, practice against the CPU, a 1v1
## duel), and a box to join a room by its code. Settings sits top right. Built from code; the
## scene (main.gd) listens to the signals.
##
## On a phone the on-screen keyboard covers the lower half of the screen, where the room-code box
## sits. While the box is being typed in there, the menu shows just the join row and the message,
## in the top part of the screen. A tap outside the box puts the keyboard away.

signal practice_requested
signal host_requested
signal create_requested # a 1v1 duel room (OnlineSession)
signal join_requested(code: String)
signal settings_requested
signal fighter_requested
## A button on the error banner: "host" (host a room), "rejoin" (the room in `arg`), "refresh" (the
## web page), "update" (the app's store page) or "dismiss".
signal error_action(action: String, arg: String)

const MIN_CODE := 4
const MAX_CODE := 8
## While typing a code on a phone, the menu fits in this top fraction of the screen.
const COMPACT_HEIGHT := 0.42

var _code_edit: LineEdit
var _name_edit: LineEdit
var _fighter: ArtPreview
var _fighter_button: Button
var _fighter_name: Label
var _fighter_role: Label
var _join_button: Button
var _practice_button: Button
## The error banner under the join row (rooms v1, fix 22): the message and a button for each next
## step (error_actions).
var _error_box: PanelContainer
var _error_label: Label
var _error_buttons: HBoxContainer
var _error_room := ""
## "Quit game", and in its place while asking, "Quit?  No / Yes" (rooms v1, fix 26).
var _quit_button: Button
var _quit_row: HBoxContainer
## Shown on a phone or tablet held upright: the game is played in landscape (main.gd turns it on
## the first tap where the browser allows; an iPhone has to be turned by hand).
var _sideways_hint: Label
var _center: MarginContainer
## Hidden while typing a code on a phone (see COMPACT_HEIGHT).
var _full_only: Array[Control] = []
var _compact := false


## Upper-cased, letters and digits only, at most MAX_CODE characters.
static func clean_code(text: String) -> String:
	var out := ""
	for ch in text.to_upper():
		if out.length() >= MAX_CODE:
			break
		if (ch >= "A" and ch <= "Z") or (ch >= "0" and ch <= "9"):
			out += ch
	return out


static func is_valid_code(code: String) -> bool:
	return code.length() >= MIN_CODE and code.length() <= MAX_CODE and clean_code(code) == code


## The next steps the error banner offers for a session's end code: [[button label, action], ...]
## (rooms v1, fix 22). `room` is the room that was lost, `web` whether this is the browser game.
## Every message gets at least one button, so no error is a dead end. `hosting` is true when the
## session was hosting a room the server never opened (refused as busy, or never reached): trying
## again hosts again, since joining that code would only find no room (final round).
static func error_actions(code: String, room: String, web: bool, hosting := false) -> Array:
	match code:
		"full", "taken":
			return [["Host a room", "host"], ["OK", "dismiss"]]
		"no_room":
			return [["Host a new room" if hosting else "Host a room", "host"], ["OK", "dismiss"]]
		"old_client", "size", "stage":
			return [["Refresh", "refresh"] if web else ["Update", "update"], ["OK", "dismiss"]]
		"closed":
			if hosting:
				return [["Try again", "host"], ["OK", "dismiss"]]
			if room != "":
				return [["Rejoin %s" % room, "rejoin"], ["OK", "dismiss"]]
		"connect", "timeout", "busy":
			if hosting:
				return [["Try again", "host"], ["OK", "dismiss"]]
			if room != "":
				return [["Try again", "rejoin"], ["OK", "dismiss"]]
	return [["OK", "dismiss"]]

func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()

	# The chosen arena shows through, dimmed (main.gd draws it behind the menu).
	var backdrop := ColorRect.new()
	backdrop.color = Color(0.05, 0.045, 0.07, 0.74)
	backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(backdrop)

	_center = MarginContainer.new()
	_center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	for side in ["left", "right"]:
		_center.add_theme_constant_override("margin_" + side, 70)
	_center.add_theme_constant_override("margin_top", 36)
	_center.add_theme_constant_override("margin_bottom", 36)
	add_child(_center)
	var page := VBoxContainer.new()
	page.add_theme_constant_override("separation", 18)
	_center.add_child(page)

	# Top: the title on the left, Settings on the right.
	var top := HBoxContainer.new()
	page.add_child(top)
	var heading := VBoxContainer.new()
	heading.add_theme_constant_override("separation", 0)
	heading.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	top.add_child(heading)
	var title := _label("RANGBAAZI", 104, UiTheme.ACCENT, HORIZONTAL_ALIGNMENT_LEFT)
	title.add_theme_constant_override("outline_size", 10)
	title.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.6))
	heading.add_child(title)
	heading.add_child(_label("Rooftop brawls for up to 15. Knock them off the edge.", 30, UiTheme.TEXT_DIM, HORIZONTAL_ALIGNMENT_LEFT))
	var sideways := "Turn your phone sideways to play." if OS.has_feature("web_ios") else "Tap anywhere to play in landscape."
	_sideways_hint = _label(sideways, 34, UiTheme.ACCENT, HORIZONTAL_ALIGNMENT_LEFT)
	_sideways_hint.visible = false
	heading.add_child(_sideways_hint)
	var settings := _button("Settings")
	settings.custom_minimum_size = Vector2(300, 96)
	settings.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	settings.pressed.connect(func() -> void: settings_requested.emit())
	top.add_child(settings)
	if UiTheme.can_quit():
		# Quit as big as Settings; a tap asks "Quit?  No / Yes" in its place (so does Android's back
		# on this screen), so a stray tap never closes the game.
		top.add_theme_constant_override("separation", 20)
		_quit_button = _button("Quit game")
		_quit_button.custom_minimum_size = Vector2(300, 96)
		_quit_button.size_flags_vertical = Control.SIZE_SHRINK_CENTER
		_quit_button.focus_mode = Control.FOCUS_NONE
		_quit_button.pressed.connect(ask_quit)
		top.add_child(_quit_button)
		_quit_row = UiTheme.confirm_row("Quit?", "No", "Yes", cancel_quit, func() -> void: get_tree().quit(), 96.0, 44)
		_quit_row.size_flags_vertical = Control.SIZE_SHRINK_CENTER
		_quit_row.visible = false
		top.add_child(_quit_row)

	var main := HBoxContainer.new()
	main.add_theme_constant_override("separation", 50)
	main.size_flags_vertical = Control.SIZE_EXPAND_FILL
	page.add_child(main)

	# Left: who you are. The fighter, big; tap it (or the button) for the character circle.
	var you_box := PanelContainer.new()
	you_box.add_theme_stylebox_override("panel", UiTheme.card_box())
	you_box.custom_minimum_size = Vector2(560, 0)
	main.add_child(you_box)
	var you := VBoxContainer.new()
	you.add_theme_constant_override("separation", 10)
	you_box.add_child(you)
	you.add_child(_label("YOUR FIGHTER", 30, UiTheme.TEXT_DIM))
	_fighter = ArtPreview.new()
	_fighter.custom_minimum_size = Vector2(0, 280)
	_fighter.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_fighter.fighter_id = GameSettings.character
	_fighter.clip = "select_pose"
	_fighter.framed = false
	_fighter.pressed.connect(func() -> void: fighter_requested.emit())
	you.add_child(_fighter)
	_fighter_name = _label("", 52, UiTheme.ACCENT)
	you.add_child(_fighter_name)
	_fighter_role = _label("", 30, UiTheme.TEXT_DIM)
	you.add_child(_fighter_role)
	_fighter_button = _button("Change fighter")
	_fighter_button.custom_minimum_size = Vector2(0, 92)
	_fighter_button.add_theme_font_size_override("font_size", 36)
	_fighter_button.pressed.connect(func() -> void: fighter_requested.emit())
	you.add_child(_fighter_button)
	# The name box says what it is above it, not only while empty (rooms v1, fix 28).
	var name_caption := _label("YOUR NAME", 30, UiTheme.TEXT_DIM)
	you.add_child(name_caption)
	_name_edit = LineEdit.new()
	_name_edit.placeholder_text = "Type your name"
	_name_edit.max_length = 12
	_name_edit.alignment = HORIZONTAL_ALIGNMENT_CENTER
	_name_edit.custom_minimum_size = Vector2(0, 88)
	_name_edit.add_theme_font_size_override("font_size", 38)
	_name_edit.text = GameSettings.player_name
	_name_edit.text_changed.connect(func(text: String) -> void:
		GameSettings.player_name = GameSettings.clean_name(text)
		GameSettings.save())
	you.add_child(_name_edit)
	refresh_fighter()

	# Right: three ways to play, each saying what it is.
	var play := VBoxContainer.new()
	play.add_theme_constant_override("separation", 20)
	play.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	main.add_child(play)

	var host := _card("PLAY WITH FRIENDS", "Host a room for up to 15 players and share its code", true)
	host.pressed.connect(func() -> void: host_requested.emit())
	play.add_child(host)

	var join_row := HBoxContainer.new()
	join_row.add_theme_constant_override("separation", 20)
	play.add_child(join_row)
	_code_edit = LineEdit.new()
	_code_edit.placeholder_text = "GOT A ROOM CODE? TYPE IT HERE"
	_code_edit.max_length = MAX_CODE
	_code_edit.alignment = HORIZONTAL_ALIGNMENT_CENTER
	_code_edit.custom_minimum_size = Vector2(0, 116)
	_code_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_code_edit.add_theme_font_size_override("font_size", 40)
	_code_edit.virtual_keyboard_type = LineEdit.KEYBOARD_TYPE_DEFAULT
	_code_edit.text_changed.connect(_on_code_changed)
	_code_edit.text_submitted.connect(func(_text: String) -> void: _try_join())
	join_row.add_child(_code_edit)
	_join_button = _button("Join")
	_join_button.custom_minimum_size = Vector2(300, 116)
	_join_button.disabled = true
	_join_button.pressed.connect(_try_join)
	join_row.add_child(_join_button)

	# The error banner, right under the join row it is usually about.
	_error_box = PanelContainer.new()
	var banner := UiTheme.card_box()
	banner.border_color = UiTheme.ERROR
	banner.set_border_width_all(3)
	banner.bg_color = Color(0.16, 0.07, 0.08, 0.96)
	banner.set_content_margin_all(18)
	_error_box.add_theme_stylebox_override("panel", banner)
	_error_box.visible = false
	play.add_child(_error_box)
	var banner_row := HBoxContainer.new()
	banner_row.add_theme_constant_override("separation", 16)
	_error_box.add_child(banner_row)
	_error_label = _label("", 32, UiTheme.TEXT, HORIZONTAL_ALIGNMENT_LEFT)
	_error_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_error_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_error_label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	banner_row.add_child(_error_label)
	_error_buttons = HBoxContainer.new()
	_error_buttons.add_theme_constant_override("separation", 12)
	_error_buttons.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	banner_row.add_child(_error_buttons)

	var more := HBoxContainer.new()
	more.add_theme_constant_override("separation", 20)
	more.size_flags_vertical = Control.SIZE_EXPAND_FILL
	play.add_child(more)
	_practice_button = _card("PRACTICE", "Fight the CPU. Works offline", false)
	_practice_button.pressed.connect(func() -> void: practice_requested.emit())
	more.add_child(_practice_button)
	var duel := _card("1v1 DUEL", "One friend, one link", false)
	duel.pressed.connect(func() -> void: create_requested.emit())
	more.add_child(duel)

	var keys := _label("How to play is in the match menu. Keyboard, gamepad and touch all work.", 30, UiTheme.TEXT_DIM)
	keys.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	play.add_child(keys)
	_full_only = [top, you_box, host, more, keys]


func _process(_delta: float) -> void:
	_set_compact(_typing_on_phone())
	var upright := size.y > size.x
	_sideways_hint.visible = upright and TouchControls.handheld() and not _compact


## A tap or click outside the room-code box puts the on-screen keyboard away (clicks on the
## containers come up to here; the buttons and the box take their own).
func _gui_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.is_pressed():
		if _code_edit.has_focus():
			_code_edit.release_focus()
		if _name_edit.has_focus():
			_name_edit.release_focus()


## The room-code box has the focus on a phone or tablet, so the on-screen keyboard is likely up.
## The Android app knows the keyboard's height; a mobile browser doesn't say, so there it counts
## while the box has the focus.
func _typing_on_phone() -> bool:
	if not is_visible_in_tree() or not _code_edit.has_focus():
		return false
	if OS.has_feature("mobile") and DisplayServer.has_feature(DisplayServer.FEATURE_VIRTUAL_KEYBOARD):
		return DisplayServer.virtual_keyboard_get_height() > 0
	return OS.has_feature("web_android") or OS.has_feature("web_ios")


func _set_compact(on: bool) -> void:
	if on == _compact:
		return
	_compact = on
	for c in _full_only:
		c.visible = not on
	_center.set_anchor(SIDE_BOTTOM, COMPACT_HEIGHT if on else 1.0, true) # keep the offsets (0): resize, not move


## Shows the menu, optionally with a message (why online play ended or failed to start), the
## session's end code and the room it was in (for the banner's buttons, error_actions; `hosting`:
## hosting a room the server never opened).
func open(message: String = "", focus := true, code := "", room := "", hosting := false) -> void:
	show_error(message, code, room, hosting)
	cancel_quit()
	show()
	if focus:
		_practice_button.grab_focus()


## Android's back (or Esc) on the bare menu: asks "Quit?  No / Yes" instead of quitting.
func ask_quit() -> void:
	if _quit_row == null:
		return
	_quit_button.hide()
	_quit_row.show()


func cancel_quit() -> void:
	if _quit_row == null:
		return
	_quit_row.hide()
	_quit_button.show()


func quit_asked() -> bool:
	return _quit_row != null and _quit_row.visible


## The banner's buttons, for tests.
func error_buttons() -> Array[Button]:
	var out: Array[Button] = []
	for c in _error_buttons.get_children():
		if c is Button and not c.is_queued_for_deletion():
			out.append(c)
	return out


func error_banner() -> Control:
	return _error_box


## Shows the fighter picked in the character circle.
func refresh_fighter() -> void:
	_fighter.fighter_id = GameSettings.character
	_fighter_name.text = ArtLibrary.fighter_name(GameSettings.character)
	_fighter_role.text = ArtLibrary.fighter_role(GameSettings.character)


## The error banner: the message, and a button for each next step for this code (none: hidden).
func show_error(message: String, code := "", room := "", hosting := false) -> void:
	_error_label.text = message
	_error_room = room
	_error_box.visible = message != ""
	for c in _error_buttons.get_children():
		_error_buttons.remove_child(c)
		c.queue_free()
	if message == "":
		return
	var first := true
	for a: Array in error_actions(code, room, OS.has_feature("web"), hosting):
		var b := Button.new()
		b.text = str(a[0])
		b.focus_mode = Control.FOCUS_NONE
		b.custom_minimum_size = Vector2(150, 96)
		b.add_theme_font_size_override("font_size", 34)
		if first and a[1] != "dismiss":
			UiTheme.make_primary(b)
		first = false
		var action := str(a[1])
		b.pressed.connect(func() -> void:
			if action == "dismiss":
				show_error("")
			else:
				error_action.emit(action, _error_room))
		_error_buttons.add_child(b)


func _on_code_changed(text: String) -> void:
	var clean := clean_code(text)
	if clean != text:
		var caret := _code_edit.caret_column
		_code_edit.text = clean
		_code_edit.caret_column = mini(caret, clean.length())
	_join_button.disabled = not is_valid_code(clean)


func _try_join() -> void:
	var code := clean_code(_code_edit.text)
	if is_valid_code(code):
		join_requested.emit(code)
	else:
		show_error("Room codes are %d to %d letters or digits." % [MIN_CODE, MAX_CODE])


func _button(text: String) -> Button:
	var b := Button.new()
	b.text = text
	b.custom_minimum_size = Vector2(0, 120)
	b.add_theme_font_size_override("font_size", 44)
	return b


## A big button that says what it does: a title and a line under it. The primary one is filled
## with the accent colour, so the eye lands on it first.
func _card(title: String, subtitle: String, primary: bool) -> Button:
	var b := Button.new()
	b.custom_minimum_size = Vector2(0, 170)
	b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	b.size_flags_vertical = Control.SIZE_EXPAND_FILL
	if primary:
		b.add_theme_stylebox_override("normal", UiTheme.primary_box(0.0))
		b.add_theme_stylebox_override("hover", UiTheme.primary_box(0.12))
		b.add_theme_stylebox_override("pressed", UiTheme.primary_box(-0.15))
	var lines := VBoxContainer.new()
	lines.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	lines.alignment = BoxContainer.ALIGNMENT_CENTER
	lines.add_theme_constant_override("separation", 4)
	lines.mouse_filter = Control.MOUSE_FILTER_IGNORE
	b.add_child(lines)
	var dark := Color("1b1a22")
	var t := _label(title, 54 if primary else 46, dark if primary else UiTheme.TEXT)
	t.mouse_filter = Control.MOUSE_FILTER_IGNORE
	lines.add_child(t)
	var sub := _label(subtitle, 26, Color(dark, 0.8) if primary else UiTheme.TEXT_DIM)
	sub.mouse_filter = Control.MOUSE_FILTER_IGNORE
	sub.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	lines.add_child(sub)
	return b


func _label(text: String, font_size: int, color: Color, align := HORIZONTAL_ALIGNMENT_CENTER) -> Label:
	var l := Label.new()
	l.text = text
	l.horizontal_alignment = align
	l.add_theme_font_size_override("font_size", font_size)
	l.add_theme_color_override("font_color", color)
	return l


func _spacer(height: float) -> Control:
	var c := Control.new()
	c.custom_minimum_size = Vector2(0, height)
	return c
