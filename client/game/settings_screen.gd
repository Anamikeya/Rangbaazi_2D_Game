class_name SettingsScreen
extends Control
## Settings: frame rate, effects, hitboxes and the debug line. Each button toggles one option and
## saves at once (GameSettings).

signal closed

var _fps: Button
var _effects: Button
var _hitboxes: Button
var _camera: Button
var _debug: Button


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()
	var backdrop := ColorRect.new()
	backdrop.color = Color(0.05, 0.045, 0.07, 0.92)
	backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(backdrop)
	var center := CenterContainer.new()
	center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(center)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 22)
	column.custom_minimum_size = Vector2(760, 0)
	center.add_child(column)

	var title := Label.new()
	title.text = "Settings"
	title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	title.add_theme_font_size_override("font_size", 72)
	title.add_theme_color_override("font_color", UiTheme.ACCENT)
	column.add_child(title)

	_volume_slider(column, "Music", func() -> float: return GameSettings.music_volume,
		func(v: float) -> void: GameSettings.music_volume = v)
	_volume_slider(column, "Effects", func() -> float: return GameSettings.effects_volume,
		func(v: float) -> void: GameSettings.effects_volume = v)

	_fps = _toggle(column, func() -> void:
		var i := GameSettings.FPS_CHOICES.find(GameSettings.max_fps)
		GameSettings.max_fps = GameSettings.FPS_CHOICES[(i + 1) % GameSettings.FPS_CHOICES.size()])
	_camera = _toggle(column, func() -> void: GameSettings.follow_camera = not GameSettings.follow_camera)
	_effects = _toggle(column, func() -> void: GameSettings.effects = not GameSettings.effects)
	_hitboxes = _toggle(column, func() -> void: GameSettings.show_hitboxes = not GameSettings.show_hitboxes)
	_debug = _toggle(column, func() -> void: GameSettings.show_debug = not GameSettings.show_debug)

	var note := Label.new()
	note.text = "The match always runs at 60 frames a second; 30 only draws fewer, to save battery."
	note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	note.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	note.add_theme_font_size_override("font_size", 24)
	note.add_theme_color_override("font_color", UiTheme.TEXT_DIM)
	column.add_child(note)

	var back := Button.new()
	back.text = "Back"
	back.custom_minimum_size = Vector2(0, 110)
	back.add_theme_font_size_override("font_size", 44)
	back.pressed.connect(func() -> void:
		hide()
		closed.emit())
	column.add_child(back)
	_refresh()


func open() -> void:
	_refresh()
	show()


## A labelled volume slider, 0 to 100%; it saves and takes effect as it moves.
func _volume_slider(parent: Control, title: String, getter: Callable, setter: Callable) -> void:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 24)
	parent.add_child(row)
	var label := Label.new()
	label.custom_minimum_size = Vector2(300, 0)
	label.add_theme_font_size_override("font_size", 38)
	row.add_child(label)
	var slider := HSlider.new()
	slider.min_value = 0
	slider.max_value = 100
	slider.step = 5
	slider.value = roundf(float(getter.call()) * 100.0)
	slider.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	slider.custom_minimum_size = Vector2(0, 80)
	slider.focus_mode = Control.FOCUS_NONE
	label.text = "%s  %d%%" % [title, int(slider.value)]
	slider.value_changed.connect(func(v: float) -> void:
		setter.call(v / 100.0)
		label.text = "%s  %d%%" % [title, int(v)]
		GameSettings.apply_audio()
		GameSettings.save())
	row.add_child(slider)


func _toggle(parent: Control, change: Callable) -> Button:
	var b := Button.new()
	b.custom_minimum_size = Vector2(0, 110)
	b.add_theme_font_size_override("font_size", 40)
	b.pressed.connect(func() -> void:
		change.call()
		GameSettings.save()
		GameSettings.apply()
		_refresh())
	parent.add_child(b)
	return b


func _refresh() -> void:
	if _fps == null:
		return
	_fps.text = "Frame rate: %d fps" % GameSettings.max_fps
	_camera.text = "Camera: %s" % ("follows the fight" if GameSettings.follow_camera else "whole arena")
	_effects.text = "Effects (hit shake): %s" % ("on" if GameSettings.effects else "off")
	_hitboxes.text = "Show hitboxes: %s" % ("on" if GameSettings.show_hitboxes else "off")
	_debug.text = "Debug info: %s" % ("on" if GameSettings.show_debug else "off")
