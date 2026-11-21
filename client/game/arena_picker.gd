class_name ArenaPicker
extends Control
## Arena select: a slider of painted arena cards. Swipe or drag the row (it snaps to a card), tap a
## card, drag the slider bar underneath, or use the arrows; the card in the middle is the pick.
## No drop-down lists: you see every arena as you slide past it. Each card shows the painting with
## the arena's platforms and hazards drawn over it to scale, and "n / 31" says where you are (rooms
## v1, fix 29). Opened for practice it also shows the CPU's skill as chips (Easy / Normal / Hard,
## fix 28). Android's back and Esc close it through main.gd's back chain.

signal chosen(stage_id: String)
signal cancelled
## A CPU skill chip was tapped (only shown when opened for practice): SparringBot.Level.
signal skill_picked(level: int)

const CARD := Vector2(720, 405)
const GAP := 60.0
const SLIDER_HEIGHT := 96.0
## The painted art's frame in metres (every arena: 32 m wide, from 1.75 m below the floor's top to
## 16.25 m above it), for drawing the stage over the painting.
const ART_HALF_WIDTH := 16.0
const ART_TOP := 16.25
const ART_BOTTOM := -1.75
const HAZARD_COLOUR := Color(1.0, 0.35, 0.3, 0.55)
const LEVEL_NAMES := ["Easy", "Normal", "Hard"]

var _arenas: Array = []
var _offset := 0.0 # cards scrolled; card i is centred when offset = i
var _target := 0.0
var _dragging := false
var _moved := 0.0
var _slider: HSlider
var _title: Label
var _owner: Label
var _count: Label
var _pick: Button
var _skill_row: ChoiceRow
var _syncing := false
var _confirm_text := "Fight here"


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()
	mouse_filter = Control.MOUSE_FILTER_STOP
	_arenas = ArtLibrary.arenas()

	var heading := UiTheme.heading("CHOOSE THE ARENA")
	heading.set_anchors_and_offsets_preset(Control.PRESET_TOP_WIDE)
	heading.offset_top = 30
	add_child(heading)
	_title = Label.new()
	_title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_title.add_theme_font_size_override("font_size", 80)
	_title.add_theme_color_override("font_color", UiTheme.ACCENT)
	_title.set_anchors_and_offsets_preset(Control.PRESET_TOP_WIDE)
	_title.offset_top = 84
	_title.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_title)
	_owner = Label.new()
	_owner.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_owner.add_theme_font_size_override("font_size", 36)
	_owner.add_theme_color_override("font_color", UiTheme.TEXT_DIM)
	_owner.set_anchors_and_offsets_preset(Control.PRESET_TOP_WIDE)
	_owner.offset_top = 184
	_owner.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_owner)

	# The slider (96 tall, a thumb-sized handle), with "n / 31" at its right end.
	_slider = HSlider.new()
	_slider.min_value = 0
	_slider.max_value = maxi(0, _arenas.size() - 1)
	_slider.step = 1
	_slider.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_WIDE)
	_slider.offset_left = 260
	_slider.offset_right = -260
	_slider.offset_top = -244 - SLIDER_HEIGHT
	_slider.offset_bottom = -244
	_slider.focus_mode = Control.FOCUS_NONE
	_slider.value_changed.connect(func(v: float) -> void:
		if not _syncing:
			_target = v
			_refresh())
	add_child(_slider)
	_count = Label.new()
	_count.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	_count.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	_count.add_theme_font_size_override("font_size", 40)
	_count.add_theme_color_override("font_color", UiTheme.TEXT)
	_count.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_RIGHT)
	_count.offset_left = -236
	_count.offset_right = -40
	_count.offset_top = -244 - SLIDER_HEIGHT
	_count.offset_bottom = -244
	_count.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_count)

	# Practice only: how good the CPU is, every choice in view.
	var skill_center := CenterContainer.new()
	skill_center.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_WIDE)
	skill_center.offset_top = -236
	skill_center.offset_bottom = -148
	skill_center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(skill_center)
	_skill_row = ChoiceRow.make("CPU skill", LEVEL_NAMES, 1, func(i: int) -> void: skill_picked.emit(i), 220.0, 84.0)
	_skill_row.custom_minimum_size = Vector2(900, 0)
	_skill_row.visible = false
	skill_center.add_child(_skill_row)

	var bottom := HBoxContainer.new()
	bottom.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_WIDE)
	bottom.offset_top = -138
	bottom.offset_bottom = -28
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
	UiTheme.make_primary(_pick)
	_pick.pressed.connect(func() -> void: chosen.emit(current_id()))
	bottom.add_child(_pick)
	var right := _button(">", 130)
	right.pressed.connect(func() -> void: step(1))
	bottom.add_child(right)


## Opens on this arena; confirm_text labels the pick button ("Fight here", "Use this arena").
## skill >= 0 (practice) shows the CPU skill chips with that level lit.
func open(stage_id: String, confirm_text := "Fight here", skill := -1) -> void:
	_confirm_text = confirm_text
	var i := 0
	for k in _arenas.size():
		if _arenas[k]["id"] == stage_id:
			i = k
	_target = i
	_offset = i
	_skill_row.visible = skill >= 0
	if skill >= 0:
		_skill_row.select(skill)
	show()
	_refresh()


func current_id() -> String:
	if _arenas.is_empty():
		return "rooftops"
	return str(_arenas[clampi(roundi(_target), 0, _arenas.size() - 1)]["id"])


## "n / 31": where the pick is in the row.
func count_text() -> String:
	return "%d / %d" % [clampi(roundi(_target), 0, maxi(0, _arenas.size() - 1)) + 1, _arenas.size()]


func skill_row() -> ChoiceRow:
	return _skill_row


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


## Arrows and Enter here; Esc and Android's back go through main.gd's back chain.
func _unhandled_key_input(event: InputEvent) -> void:
	if not visible or not event.is_pressed():
		return
	if event.is_action_pressed("ui_left"):
		step(-1)
	elif event.is_action_pressed("ui_right"):
		step(1)
	elif event.is_action_pressed("ui_accept"):
		chosen.emit(current_id())
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
	# Centred between the owner line and the slider.
	var c := Vector2(s.x * 0.5 + d * (CARD.x + GAP), (240.0 + s.y - 244.0 - SLIDER_HEIGHT) * 0.5)
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
		_draw_stage(StageDefinition.by_id(id), r)
		var focus := i == roundi(_target)
		draw_rect(r, UiTheme.ACCENT if focus else Color(1, 1, 1, 0.25), false, 8.0 if focus else 3.0)
		if not focus:
			draw_rect(r, Color(0, 0, 0, 0.35))


## The stage's platforms (dark slabs with a light top) and hazards (red zones) over the card, at the
## painting's scale, so the layout shows before you fight on it.
func _draw_stage(stage: StageDefinition, r: Rect2) -> void:
	if stage == null:
		return
	for k in stage.hazard_zones.size():
		var hr := stage_rect(stage.hazard_zones[k], r)
		draw_rect(hr, Color(HAZARD_COLOUR, 0.28))
		draw_rect(hr, HAZARD_COLOUR, false, 2.0)
	for p in stage.platforms:
		var pr := stage_rect(p, r)
		pr.size.y = maxf(pr.size.y, 4.0)
		draw_rect(pr, Color(0.08, 0.07, 0.1, 0.78))
		draw_line(pr.position, pr.position + Vector2(pr.size.x, 0), Color(1, 0.93, 0.75, 0.95), 3.0)


## A stage rectangle (metres, y up) in the card r (the painted art's frame).
static func stage_rect(z: FxRect, r: Rect2) -> Rect2:
	var k := r.size.x / (2.0 * ART_HALF_WIDTH)
	var x0 := r.position.x + (Fx.to_float(z.min_x) + ART_HALF_WIDTH) * k
	var x1 := r.position.x + (Fx.to_float(z.max_x) + ART_HALF_WIDTH) * k
	var ky := r.size.y / (ART_TOP - ART_BOTTOM)
	var y0 := r.position.y + (ART_TOP - Fx.to_float(z.max_y)) * ky
	var y1 := r.position.y + (ART_TOP - Fx.to_float(z.min_y)) * ky
	return Rect2(x0, y0, x1 - x0, y1 - y0).intersection(r)


func _refresh() -> void:
	_syncing = true
	_slider.value = roundf(_target)
	_syncing = false
	var id := current_id()
	_title.text = ArtLibrary.arena_title(id)
	var owner := ArtLibrary.arena_owner(id)
	_owner.text = "%s's arena" % ArtLibrary.fighter_name(owner) if owner != "" else ""
	_count.text = count_text()
	_pick.text = _confirm_text


func _button(text: String, width: float) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.custom_minimum_size = Vector2(width, 110)
	b.add_theme_font_size_override("font_size", 42)
	return b
