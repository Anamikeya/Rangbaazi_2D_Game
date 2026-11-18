class_name CharacterCircle
extends Control
## Fighter select: all 31 fighters stand in one big circle, seen from the front, the nearest ones
## big and bright, the far ones small and dim. Drag (or swipe) to spin the circle, tap a fighter to
## bring them to the front, or use the arrows / keys. The fighter in front strikes their select pose
## and shows their name; "Fight as ..." picks them. Every fighter has the same moves and numbers:
## the choice is looks and personality.

signal chosen(id: String)
signal cancelled

const SPIN_EASE := 10.0 # how quickly the circle settles on its target (per second)
const DRAG_TURNS := 1.6 # a drag across the whole screen turns the circle this many fighters per 100 px

var _ids: PackedStringArray = []
var _painters: Array = []
var _angle := 0.0 # radians the circle has turned; fighter i is in front when angle = i * step
var _target := 0.0
var _dragging := false
var _drag_moved := 0.0
var _press_pos := Vector2.ZERO
var _frame := 0
var _title: Label
var _role: Label
var _pick: Button
var _hint: Label


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()
	mouse_filter = Control.MOUSE_FILTER_STOP
	for f: Dictionary in ArtLibrary.fighters():
		_ids.append(str(f["id"]))
		_painters.append(FighterPainter.new(str(f["id"])))

	var top := VBoxContainer.new()
	top.set_anchors_and_offsets_preset(Control.PRESET_TOP_WIDE)
	top.offset_top = 30
	top.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(top)
	var heading := _label("CHOOSE YOUR FIGHTER", 44, UiTheme.TEXT_DIM)
	top.add_child(heading)
	_title = _label("", 96, UiTheme.ACCENT)
	_title.add_theme_constant_override("outline_size", 10)
	_title.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.6))
	top.add_child(_title)
	_role = _label("", 36, UiTheme.TEXT)
	top.add_child(_role)

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
	_pick = _button("Fight as ...", 520)
	_pick.pressed.connect(func() -> void: chosen.emit(front_id()))
	bottom.add_child(_pick)
	var right := _button(">", 130)
	right.pressed.connect(func() -> void: step(1))
	bottom.add_child(right)

	_hint = _label("Drag to spin the circle, tap a fighter to bring them forward", 26, UiTheme.TEXT_DIM)
	_hint.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_WIDE)
	_hint.offset_top = -190
	_hint.offset_bottom = -155
	_hint.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_hint)


## Opens with this fighter in front.
func open(id: String) -> void:
	var i := maxi(0, _ids.find(id))
	_target = i * _step_angle()
	_angle = _target
	show()
	_refresh_labels()


func front_id() -> String:
	if _ids.is_empty():
		return "shahn"
	return _ids[posmod(roundi(_target / _step_angle()), _ids.size())]


func step(direction: int) -> void:
	_target = (roundi(_target / _step_angle()) + direction) * _step_angle()
	_refresh_labels()


func _step_angle() -> float:
	return TAU / maxf(1.0, _ids.size())


func _process(delta: float) -> void:
	if not visible:
		return
	_frame += 1
	if not _dragging:
		_angle = lerpf(_angle, _target, minf(1.0, delta * SPIN_EASE))
	queue_redraw()


func _gui_input(event: InputEvent) -> void:
	var button := event as InputEventMouseButton
	if button != null and button.button_index == MOUSE_BUTTON_LEFT:
		if button.pressed:
			_dragging = true
			_drag_moved = 0.0
			_press_pos = button.position
		else:
			_dragging = false
			if _drag_moved < 12.0:
				_tap(button.position)
			else:
				_target = roundi(_angle / _step_angle()) * _step_angle()
			_refresh_labels()
		accept_event()
		return
	var motion := event as InputEventMouseMotion
	if motion != null and _dragging:
		_drag_moved += absf(motion.relative.x)
		_angle -= motion.relative.x / 100.0 * DRAG_TURNS * _step_angle()
		_target = _angle
		_refresh_labels()
		accept_event()


func _unhandled_key_input(event: InputEvent) -> void:
	if not visible or not event.is_pressed():
		return
	if event.is_action_pressed("ui_left"):
		step(-1)
	elif event.is_action_pressed("ui_right"):
		step(1)
	elif event.is_action_pressed("ui_accept"):
		chosen.emit(front_id())
	elif event.is_action_pressed("ui_cancel"):
		cancelled.emit()
	else:
		return
	get_viewport().set_input_as_handled()


## A tap on a fighter turns the circle to bring them forward (a tap on the front one picks them).
func _tap(pos: Vector2) -> void:
	var best := -1
	var best_d := 1e9
	for i in _ids.size():
		var place := _place(i)
		var d := pos.distance_to(place[0] + Vector2(0, -60.0 * place[1]))
		if d < 90.0 * place[1] and d < best_d:
			best = i
			best_d = d
	if best < 0:
		return
	if _ids[best] == front_id():
		chosen.emit(front_id())
		return
	# Turn the short way round.
	var current := roundi(_target / _step_angle())
	var delta := posmod(best - current, _ids.size())
	if delta > _ids.size() / 2:
		delta -= _ids.size()
	_target = (current + delta) * _step_angle()


## [feet position, scale, depth 0..1 (1 = front)] of fighter i on the circle now.
func _place(i: int) -> Array:
	var s := size
	var centre := Vector2(s.x * 0.5, s.y * 0.6)
	var radius := Vector2(s.x * 0.45, s.y * 0.2)
	var theta := PI / 2.0 + (i * _step_angle() - _angle)
	var depth := (sin(theta) + 1.0) / 2.0
	var pos := centre + Vector2(radius.x * cos(theta), radius.y * sin(theta))
	# Far fighters shrink fast, so the ones near the front stand apart and the front one stands out.
	var scale := lerpf(0.35, 1.05, pow(depth, 3.0))
	if depth > 0.985:
		scale *= 1.3
	return [pos, scale, depth]


func _draw() -> void:
	var s := size
	draw_rect(Rect2(Vector2.ZERO, s), Color(0.07, 0.06, 0.1))
	# The ring they stand on.
	var centre := Vector2(s.x * 0.5, s.y * 0.6)
	draw_set_transform(centre, 0.0, Vector2(1.0, 0.39))
	draw_circle(Vector2.ZERO, s.x * 0.47, Color(UiTheme.ACCENT, 0.08))
	draw_circle(Vector2.ZERO, s.x * 0.47, Color(UiTheme.ACCENT, 0.35), false, 6.0)
	draw_set_transform_matrix(Transform2D.IDENTITY)
	var order: Array = []
	for i in _ids.size():
		order.append(i)
	order.sort_custom(func(a: int, b: int) -> bool: return _place(a)[2] < _place(b)[2])
	var front := front_id()
	for i: int in order:
		var place := _place(i)
		var depth: float = place[2]
		var scale: float = place[1]
		var p: FighterPainter = _painters[i]
		var is_front := _ids[i] == front and absf(_angle - _target) < _step_angle() * 0.5
		if is_front:
			draw_set_transform(place[0], 0.0, Vector2(1.0, 0.3))
			draw_circle(Vector2.ZERO, 120.0 * scale, Color(UiTheme.ACCENT, 0.5))
			draw_set_transform_matrix(Transform2D.IDENTITY)
		var dim := lerpf(0.35, 1.0, depth)
		p.draw(self, place[0], scale * 0.9, 1, "select_pose" if is_front else "idle", _frame + i * 7, dim)


func _refresh_labels() -> void:
	var id := front_id()
	_title.text = ArtLibrary.fighter_name(id).to_upper()
	_role.text = ArtLibrary.fighter_role(id)
	_pick.text = "Fight as %s" % ArtLibrary.fighter_name(id)


func _button(text: String, width: float) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.custom_minimum_size = Vector2(width, 110)
	b.add_theme_font_size_override("font_size", 42)
	return b


func _label(text: String, font_size: int, color: Color) -> Label:
	var l := Label.new()
	l.text = text
	l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	l.add_theme_font_size_override("font_size", font_size)
	l.add_theme_color_override("font_color", color)
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return l
