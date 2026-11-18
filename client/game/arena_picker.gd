class_name ArenaPicker
extends Control
## Arena select: a slider of painted arena cards. Swipe or drag the row (it snaps to a card), tap a
## card, drag the slider bar underneath, or use the arrows; the card in the middle is the pick.
## No drop-down lists: you see every arena as you slide past it.

signal chosen(stage_id: String)
signal cancelled

const CARD := Vector2(800, 450)
const GAP := 60.0

var _arenas: Array = []
var _offset := 0.0 # cards scrolled; card i is centred when offset = i
var _target := 0.0
var _dragging := false
var _moved := 0.0
var _slider: HSlider
var _title: Label
var _pick: Button
var _syncing := false
var _confirm_text := "Fight here"


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()
	mouse_filter = Control.MOUSE_FILTER_STOP
	_arenas = ArtLibrary.arenas()

	var heading := Label.new()
	heading.text = "CHOOSE THE ARENA"
	heading.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	heading.add_theme_font_size_override("font_size", 44)
	heading.add_theme_color_override("font_color", UiTheme.TEXT_DIM)
	heading.set_anchors_and_offsets_preset(Control.PRESET_TOP_WIDE)
	heading.offset_top = 30
	heading.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(heading)
	_title = Label.new()
	_title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_title.add_theme_font_size_override("font_size", 80)
	_title.add_theme_color_override("font_color", UiTheme.ACCENT)
	_title.set_anchors_and_offsets_preset(Control.PRESET_TOP_WIDE)
	_title.offset_top = 90
	_title.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_title)

	_slider = HSlider.new()
	_slider.min_value = 0
	_slider.max_value = maxi(0, _arenas.size() - 1)
	_slider.step = 1
	_slider.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_WIDE)
	_slider.offset_left = 200
	_slider.offset_right = -200
	_slider.offset_top = -240
	_slider.offset_bottom = -190
	_slider.focus_mode = Control.FOCUS_NONE
	_slider.value_changed.connect(func(v: float) -> void:
		if not _syncing:
			_target = v
			_refresh())
	add_child(_slider)

	var bottom := HBoxContainer.new()
	bottom.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_WIDE)
	bottom.offset_top = -150
	bottom.offset_bottom = -30
	bottom.alignment = BoxContainer.ALIGNMENT_CENTER
	bottom.add_theme_constant_override("separation", 30)
	add_child(bottom)
	var back := _button("Back", 220)
	back.pressed.connect(func() -> void: cancelled.emit())
	bottom.add_child(back)
	var left := _button("<", 130)
	left.pressed.connect(func() -> void: step(-1))
	bottom.add_child(left)
	_pick = _button(_confirm_text, 460)
	_pick.pressed.connect(func() -> void: chosen.emit(current_id()))
	bottom.add_child(_pick)
	var right := _button(">", 130)
	right.pressed.connect(func() -> void: step(1))
	bottom.add_child(right)


## Opens on this arena; confirm_text labels the pick button ("Fight here", "Use this arena").
func open(stage_id: String, confirm_text := "Fight here") -> void:
	_confirm_text = confirm_text
	var i := 0
	for k in _arenas.size():
		if _arenas[k]["id"] == stage_id:
			i = k
	_target = i
	_offset = i
	show()
	_refresh()


func current_id() -> String:
	if _arenas.is_empty():
		return "rooftops"
	return str(_arenas[clampi(roundi(_target), 0, _arenas.size() - 1)]["id"])


func step(direction: int) -> void:
	_target = clampf(roundf(_target) + direction, 0, _arenas.size() - 1)
	_refresh()


func _process(delta: float) -> void:
	if not visible:
		return
	if not _dragging:
		_offset = lerpf(_offset, _target, minf(1.0, delta * 10.0))
	queue_redraw()


func _gui_input(event: InputEvent) -> void:
	var button := event as InputEventMouseButton
	if button != null and button.button_index == MOUSE_BUTTON_LEFT:
		if button.pressed:
			_dragging = true
			_moved = 0.0
		else:
			_dragging = false
			if _moved < 12.0:
				_tap(button.position)
			else:
				_target = clampf(roundf(_offset), 0, _arenas.size() - 1)
			_refresh()
		accept_event()
	var motion := event as InputEventMouseMotion
	if motion != null and _dragging:
		_moved += absf(motion.relative.x)
		_offset = clampf(_offset - motion.relative.x / (CARD.x + GAP), -0.4, _arenas.size() - 0.6)
		_target = clampf(roundf(_offset), 0, _arenas.size() - 1)
		_refresh()
		accept_event()


func _unhandled_key_input(event: InputEvent) -> void:
	if not visible or not event.is_pressed():
		return
	if event.is_action_pressed("ui_left"):
		step(-1)
	elif event.is_action_pressed("ui_right"):
		step(1)
	elif event.is_action_pressed("ui_accept"):
		chosen.emit(current_id())
	elif event.is_action_pressed("ui_cancel"):
		cancelled.emit()
	else:
		return
	get_viewport().set_input_as_handled()


func _tap(pos: Vector2) -> void:
	for i in _arenas.size():
		if _card_rect(i).has_point(pos):
			if i == roundi(_target):
				chosen.emit(current_id())
			else:
				_target = i
			return


func _card_rect(i: int) -> Rect2:
	var s := size
	var d := i - _offset
	var scale := lerpf(1.0, 0.72, clampf(absf(d), 0.0, 1.0))
	var c := Vector2(s.x * 0.5 + d * (CARD.x + GAP), s.y * 0.5 + 10.0)
	var size_now := CARD * scale
	return Rect2(c - size_now / 2.0, size_now)


func _draw() -> void:
	draw_rect(Rect2(Vector2.ZERO, size), Color(0.07, 0.06, 0.1))
	var order: Array = []
	for i in _arenas.size():
		if absf(i - _offset) < 3.5:
			order.append(i)
	order.sort_custom(func(a: int, b: int) -> bool: return absf(a - _offset) > absf(b - _offset))
	for i: int in order:
		var r := _card_rect(i)
		var id := str(_arenas[i]["id"])
		var arena := ArtLibrary.arena(id)
		draw_rect(r.grow(6), Color(0, 0, 0, 0.5))
		draw_rect(r, Color(str(arena.get("sky", "#1a1626"))))
		for layer: Dictionary in arena.get("layers", []):
			if float(layer.get("z", 0)) > 0.0:
				continue
			var tex := ArtLibrary.arena_texture(id, str(layer["file"]))
			if tex != null:
				draw_texture_rect(tex, r, false)
		var focus := i == roundi(_target)
		draw_rect(r, UiTheme.ACCENT if focus else Color(1, 1, 1, 0.25), false, 8.0 if focus else 3.0)
		if not focus:
			draw_rect(r, Color(0, 0, 0, 0.35))


func _refresh() -> void:
	_syncing = true
	_slider.value = roundf(_target)
	_syncing = false
	var id := current_id()
	_title.text = ArtLibrary.arena_title(id)
	_pick.text = _confirm_text


func _button(text: String, width: float) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.custom_minimum_size = Vector2(width, 110)
	b.add_theme_font_size_override("font_size", 42)
	return b
