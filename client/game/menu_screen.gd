class_name MenuScreen
extends Control
## The title menu: practice against the CPU, create an online room, or join one by its code.
## Built from code; large, touch-friendly buttons. The scene (main.gd) listens to the signals.
##
## On a phone the on-screen keyboard covers the lower half of the screen, where the room-code box
## sits. While the box is being typed in there, the menu shows just the join row and the message,
## in the top part of the screen. A tap outside the box puts the keyboard away.

signal practice_requested
signal create_requested
signal join_requested(code: String)

const MIN_CODE := 4
const MAX_CODE := 8
## While typing a code on a phone, the menu fits in this top fraction of the screen.
const COMPACT_HEIGHT := 0.42

var _code_edit: LineEdit
var _join_button: Button
var _practice_button: Button
var _error_label: Label
## Shown on a phone or tablet held upright: the game is played in landscape (main.gd turns it on
## the first tap where the browser allows; an iPhone has to be turned by hand).
var _sideways_hint: Label
var _center: CenterContainer
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


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()

	var backdrop := ColorRect.new()
	backdrop.color = Color(0.05, 0.045, 0.07, 0.82)
	backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(backdrop)

	_center = CenterContainer.new()
	_center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(_center)

	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 26)
	column.custom_minimum_size = Vector2(760, 0)
	_center.add_child(column)

	var title := _label("RANGBAAZI", 150, UiTheme.ACCENT)
	title.add_theme_constant_override("outline_size", 10)
	title.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.6))
	column.add_child(title)
	var tagline := _label("Rooftop brawls. Knock them off the edge, keep your stocks.", 30, UiTheme.TEXT_DIM)
	column.add_child(tagline)
	var sideways := "Turn your phone sideways to play." if OS.has_feature("web_ios") else "Tap anywhere to play in landscape."
	_sideways_hint = _label(sideways, 34, UiTheme.ACCENT)
	_sideways_hint.visible = false
	column.add_child(_sideways_hint)
	var spacer := _spacer(18)
	column.add_child(spacer)

	_practice_button = _button("Practice vs CPU")
	_practice_button.pressed.connect(func() -> void: practice_requested.emit())
	column.add_child(_practice_button)

	var create := _button("Create online room")
	create.pressed.connect(func() -> void: create_requested.emit())
	column.add_child(create)

	var join_row := HBoxContainer.new()
	join_row.add_theme_constant_override("separation", 20)
	column.add_child(join_row)
	_code_edit = LineEdit.new()
	_code_edit.placeholder_text = "ROOM CODE"
	_code_edit.max_length = MAX_CODE
	_code_edit.alignment = HORIZONTAL_ALIGNMENT_CENTER
	_code_edit.custom_minimum_size = Vector2(0, 120)
	_code_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_code_edit.add_theme_font_size_override("font_size", 44)
	_code_edit.virtual_keyboard_type = LineEdit.KEYBOARD_TYPE_DEFAULT
	_code_edit.text_changed.connect(_on_code_changed)
	_code_edit.text_submitted.connect(func(_text: String) -> void: _try_join())
	join_row.add_child(_code_edit)
	_join_button = _button("Join room")
	_join_button.custom_minimum_size = Vector2(320, 120)
	_join_button.disabled = true
	_join_button.pressed.connect(_try_join)
	join_row.add_child(_join_button)

	_error_label = _label("", 28, UiTheme.ERROR)
	_error_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	column.add_child(_error_label)

	var keys := _label("Keys: A / D move, W / S aim, Space jump, J light, K heavy, L dodge. Pads and touch work too.", 22, UiTheme.TEXT_DIM)
	keys.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	column.add_child(keys)
	_full_only = [title, tagline, spacer, _practice_button, create, keys]


func _process(_delta: float) -> void:
	_set_compact(_typing_on_phone())
	var upright := size.y > size.x
	_sideways_hint.visible = upright and TouchControls.handheld() and not _compact


## A tap or click outside the room-code box puts the on-screen keyboard away (clicks on the
## containers come up to here; the buttons and the box take their own).
func _gui_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.is_pressed() and _code_edit.has_focus():
		_code_edit.release_focus()


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


## Shows the menu, optionally with a message (why online play ended or failed to start).
func open(message: String = "", focus := true) -> void:
	show_error(message)
	show()
	if focus:
		_practice_button.grab_focus()


func show_error(message: String) -> void:
	_error_label.text = message


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


func _label(text: String, font_size: int, color: Color) -> Label:
	var l := Label.new()
	l.text = text
	l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	l.add_theme_font_size_override("font_size", font_size)
	l.add_theme_color_override("font_color", color)
	return l


func _spacer(height: float) -> Control:
	var c := Control.new()
	c.custom_minimum_size = Vector2(0, height)
	return c
