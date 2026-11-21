class_name SettingsScreen
extends Control
## Settings: the two volume sliders, then one row per option with every choice in view (ChoiceRow):
## the camera, the frame rate, hit shake, the touch buttons' size and opacity, and in debug builds
## only, the hitboxes and the debug line (rooms v1, fix 20). A tap picks and saves at once
## (GameSettings); nothing cycles and nothing opens. Android's back and Esc close it through the
## scene's one back chain (main.gd _close_top_overlay, which calls close()).

signal closed

var _camera: ChoiceRow
var _fps: ChoiceRow
var _effects: ChoiceRow
var _touch_size: ChoiceRow
var _touch_alpha: ChoiceRow
var _hitboxes: ChoiceRow
var _debug: ChoiceRow

const ON_OFF := ["On", "Off"]
const ROW_HEIGHT := 80.0


## Whether the developer rows (hitboxes, debug info) show: in debug builds only, never to players.
static func developer_rows() -> bool:
	return OS.is_debug_build()


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()
	var backdrop := ColorRect.new()
	backdrop.color = Color(0.05, 0.045, 0.07, 1.0) # opaque: the screen under it never ghosts through
	backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(backdrop)
	var center := CenterContainer.new()
	center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(center)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 12)
	column.custom_minimum_size = Vector2(1240, 0)
	center.add_child(column)

	# The pickers' heading style (CHOOSE THE ARENA), the same on every screen.
	column.add_child(UiTheme.heading("SETTINGS"))

	_volume_slider(column, "Music", func() -> float: return GameSettings.music_volume,
		func(v: float) -> void: GameSettings.music_volume = v)
	_volume_slider(column, "Effects", func() -> float: return GameSettings.effects_volume,
		func(v: float) -> void: GameSettings.effects_volume = v)

	_camera = _row(column, "Camera", ["Follows the fight (not in rooms)", "Whole arena"], func(i: int) -> void:
		GameSettings.follow_camera = i == 0)
	_fps = _row(column, "Frame rate", ["60", "30 (saves battery)"], func(i: int) -> void:
		GameSettings.max_fps = GameSettings.FPS_CHOICES[i])
	_effects = _row(column, "Hit shake", ON_OFF, func(i: int) -> void: GameSettings.effects = i == 0)
	_touch_size = _row(column, "Touch buttons", ["Small", "Medium", "Large"], func(i: int) -> void:
		GameSettings.touch_scale = GameSettings.TOUCH_SCALES[i])
	_touch_alpha = _row(column, "Opacity", ["Low", "Medium", "High"], func(i: int) -> void:
		GameSettings.touch_alpha = GameSettings.TOUCH_ALPHAS[i])
	if developer_rows():
		_hitboxes = _row(column, "Show hitboxes", ON_OFF, func(i: int) -> void: GameSettings.show_hitboxes = i == 0)
		_debug = _row(column, "Debug info", ON_OFF, func(i: int) -> void: GameSettings.show_debug = i == 0)

	var back := Button.new()
	back.text = "Done"
	back.focus_mode = Control.FOCUS_NONE
	back.custom_minimum_size = Vector2(0, 100)
	back.add_theme_font_size_override("font_size", 44)
	UiTheme.make_primary(back)
	back.pressed.connect(close)
	column.add_child(back)
	_refresh()


func open() -> void:
	_refresh()
	show()


## Hides the screen (Done, Android's back, Esc).
func close() -> void:
	if not visible:
		return
	hide()
	closed.emit()


## The rows shown, for tests: [title, ...].
func row_titles() -> PackedStringArray:
	var out := PackedStringArray()
	for row in [_camera, _fps, _effects, _touch_size, _touch_alpha, _hitboxes, _debug]:
		if row != null:
			out.append(((row as ChoiceRow).get_child(0) as Label).text)
	return out


## One option: its name, and a chip for each choice. Picking saves and applies at once.
func _row(parent: Control, title: String, options: Array, change: Callable) -> ChoiceRow:
	var row := ChoiceRow.make(title, options, 0, func(i: int) -> void:
		change.call(i)
		GameSettings.save()
		GameSettings.apply(), 380.0, ROW_HEIGHT)
	parent.add_child(row)
	return row


## A labelled volume slider, 0 to 100%; it saves and takes effect as it moves.
func _volume_slider(parent: Control, title: String, getter: Callable, setter: Callable) -> void:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 14)
	parent.add_child(row)
	var label := Label.new()
	label.custom_minimum_size = Vector2(380, 0)
	label.add_theme_font_size_override("font_size", 36)
	row.add_child(label)
	var slider := HSlider.new()
	slider.min_value = 0
	slider.max_value = 100
	slider.step = 5
	slider.value = roundf(float(getter.call()) * 100.0)
	slider.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	slider.custom_minimum_size = Vector2(0, 88)
	slider.focus_mode = Control.FOCUS_NONE
	label.text = "%s  %d%%" % [title, int(slider.value)]
	slider.value_changed.connect(func(v: float) -> void:
		setter.call(v / 100.0)
		label.text = "%s  %d%%" % [title, int(v)]
		GameSettings.apply_audio()
		GameSettings.save())
	row.add_child(slider)


func _refresh() -> void:
	if _fps == null:
		return
	_camera.select(0 if GameSettings.follow_camera else 1)
	_fps.select(maxi(0, GameSettings.FPS_CHOICES.find(GameSettings.max_fps)))
	_effects.select(0 if GameSettings.effects else 1)
	_touch_size.select(_index_of(GameSettings.touch_scale, GameSettings.TOUCH_SCALES))
	_touch_alpha.select(_index_of(GameSettings.touch_alpha, GameSettings.TOUCH_ALPHAS))
	if _hitboxes != null:
		_hitboxes.select(0 if GameSettings.show_hitboxes else 1)
		_debug.select(0 if GameSettings.show_debug else 1)


static func _index_of(value: float, choices: Array) -> int:
	for i in choices.size():
		if is_equal_approx(float(choices[i]), value):
			return i
	return 1
