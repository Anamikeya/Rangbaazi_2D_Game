class_name CharacterCircle
extends Control
## Fighter select: all 31 fighters stand in one big circle, seen from the front, the nearest ones
## big and bright, the far ones small and dim. Drag (or swipe) to spin the circle, tap a fighter to
## bring them to the front, or use the arrows / keys. The fighter in front strikes their select pose
## and shows their name; "Fight as ..." picks them. Every fighter has the same moves and numbers:
## the choice is looks and personality.
##
## Rooms v1, fix 24: even the far fighters are drawn at 0.55 of the front size or more and take a
## tap within 60 px; a strip of face chips above the buttons (every fighter by name, and "Create")
## slides like a carousel and jumps the circle to the one tapped. Android's back and Esc close it
## through main.gd's back chain.

signal chosen(id: String)
signal cancelled
## "Create your own" was pressed: open the character creator.
signal create_requested

const SPIN_EASE := 10.0 # how quickly the circle settles on its target (per second)
const FRONT_GAP := 3.0 # the fighter in front stands this many gaps clear of its neighbours
const DRAG_TURNS := 1.6 # a drag across the whole screen turns the circle this many fighters per 100 px
## The smallest a fighter on the circle is drawn (the front one is 1.05, times 1.3 in front).
const MIN_SCALE := 0.55
## A tap this close to a fighter (or closer, for the big ones) picks them.
const TAP_RADIUS := 60.0
## The ring: centred at this fraction of the height, this fraction of the width across (each side).
const RING_Y := 0.47
const RING_X := 0.42
const RING_DEPTH := 0.17
## The face strip, above the buttons: its height and its bottom's distance from the screen's bottom.
const STRIP_HEIGHT := 140.0
const STRIP_BOTTOM := 166.0

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
var _strip: FaceStrip


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()
	mouse_filter = Control.MOUSE_FILTER_STOP
	_fill()

	var top := VBoxContainer.new()
	top.set_anchors_and_offsets_preset(Control.PRESET_TOP_WIDE)
	top.offset_top = 30
	top.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(top)
	top.add_child(UiTheme.heading("CHOOSE YOUR FIGHTER"))
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
	UiTheme.make_primary(_pick)
	_pick.pressed.connect(func() -> void: chosen.emit(front_id()))
	bottom.add_child(_pick)
	var right := _button(">", 130)
	right.pressed.connect(func() -> void: step(1))
	bottom.add_child(right)
	var create := _button("Create your own", 420)
	create.pressed.connect(func() -> void: create_requested.emit())
	bottom.add_child(create)

	_strip = FaceStrip.new()
	_strip.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_WIDE)
	_strip.offset_left = 40
	_strip.offset_right = -40
	_strip.offset_top = -(STRIP_BOTTOM + STRIP_HEIGHT)
	_strip.offset_bottom = -STRIP_BOTTOM
	_strip.picked.connect(_bring_forward)
	_strip.create_picked.connect(func() -> void: create_requested.emit())
	add_child(_strip)

	# Above the front fighter (placed in _process, which knows where they stand).
	_hint = _label("Drag to spin, or tap a face", 30, UiTheme.TEXT_DIM)
	_hint.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_hint)


## The 31 fighters, and the player's own creation (if they've made one) standing with them.
func _fill() -> void:
	var ids := PackedStringArray()
	for f: Dictionary in ArtLibrary.fighters():
		ids.append(str(f["id"]))
	if not ArtLibrary.mix_parts(GameSettings.custom).is_empty():
		ids.append(GameSettings.custom)
	if ids == _ids:
		return
	_ids = ids
	_painters.clear()
	for id in _ids:
		_painters.append(FighterPainter.new(id))
	if _strip != null:
		_strip.ids = _ids


## Opens with this fighter in front.
func open(id: String) -> void:
	_fill()
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
	if _strip.ids != _ids:
		_strip.ids = _ids
	_strip.set_front(posmod(roundi(_target / _step_angle()), maxi(1, _ids.size())))
	# The hint stands just above the front fighter's head.
	var feet := Vector2(size.x * 0.5, size.y * RING_Y + size.y * RING_DEPTH)
	_hint.size = Vector2(size.x, 40)
	_hint.position = Vector2(0, feet.y - 330.0)
	queue_redraw()


## Turns the circle (the short way round) to bring fighter i forward.
func _bring_forward(i: int) -> void:
	if i < 0 or i >= _ids.size():
		return
	var current := roundi(_target / _step_angle())
	var delta := posmod(i - current, _ids.size())
	if delta > _ids.size() / 2:
		delta -= _ids.size()
	_target = (current + delta) * _step_angle()
	_refresh_labels()


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
	else:
		return # Esc / back: main.gd's back chain
	get_viewport().set_input_as_handled()


## A tap on a fighter turns the circle to bring them forward (a tap on the front one picks them).
func _tap(pos: Vector2) -> void:
	var best := tap_target(pos)
	if best < 0:
		return
	if _ids[best] == front_id():
		chosen.emit(front_id())
		return
	_bring_forward(best)


## The fighter a tap at pos lands on (the nearest within reach: TAP_RADIUS, or more for the big
## ones), or -1.
func tap_target(pos: Vector2) -> int:
	var best := -1
	var best_d := 1e9
	for i in _ids.size():
		var place := _place(i)
		var d := pos.distance_to(place[0] + Vector2(0, -60.0 * place[1]))
		if d < maxf(TAP_RADIUS, 90.0 * place[1]) and d < best_d:
			best = i
			best_d = d
	return best


## [feet position, scale, depth 0..1 (1 = front)] of fighter i on the circle now.
func _place(i: int) -> Array:
	var s := size
	var centre := Vector2(s.x * 0.5, s.y * RING_Y)
	var radius := Vector2(s.x * RING_X, s.y * RING_DEPTH)
	# How many fighters round from the front this one is (-n/2..n/2). The front fighter gets
	# FRONT_GAP times the usual gap on both sides; the rest close up evenly to make the room.
	var n := float(_ids.size())
	var half := n / 2.0
	var d := fposmod(i - _angle / _step_angle() + half, n) - half
	var a := absf(d)
	var spread := FRONT_GAP * a if a <= 1.0 or half <= FRONT_GAP else FRONT_GAP + (a - 1.0) * (half - FRONT_GAP) / (half - 1.0)
	var theta := PI / 2.0 + signf(d) * spread * _step_angle()
	var depth := (sin(theta) + 1.0) / 2.0
	var pos := centre + Vector2(radius.x * cos(theta), radius.y * sin(theta))
	# Far fighters shrink fast, so the ones near the front stand apart and the front one stands out.
	var scale := lerpf(MIN_SCALE, 1.05, pow(depth, 6.0))
	if depth > 0.985:
		scale *= 1.3
	return [pos, scale, depth]


func _draw() -> void:
	var s := size
	draw_rect(Rect2(Vector2.ZERO, s), Color(0.07, 0.06, 0.1))
	# The ring they stand on (a little wider than where they stand), clear of the face strip.
	var centre := Vector2(s.x * 0.5, s.y * RING_Y)
	var ring_x := s.x * (RING_X + 0.03)
	draw_set_transform(centre, 0.0, Vector2(1.0, (s.y * RING_DEPTH + 30.0) / ring_x))
	draw_circle(Vector2.ZERO, ring_x, Color(UiTheme.ACCENT, 0.08))
	draw_circle(Vector2.ZERO, ring_x, Color(UiTheme.ACCENT, 0.35), false, 6.0)
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


## The face strip, for tests.
func face_strip() -> FaceStrip:
	return _strip


## A carousel of face chips, one per fighter (their head and their name) and "Create" at the end.
## Drag to slide it; a tap on a face brings that fighter forward on the circle, a tap on "Create"
## opens the creator. The front fighter's chip is lit, and the strip slides to keep it in view.
class FaceStrip:
	extends Control

	signal picked(index: int)
	signal create_picked

	const CHIP := Vector2(150, 132)
	const GAP := 12.0

	var ids: PackedStringArray = []:
		set(value):
			ids = value
			_heads.clear()
			queue_redraw()
	var _front := -1
	var _heads: Dictionary = {} # id -> Texture2D (or null)
	var _scroll := 0.0
	var _target := 0.0
	var _dragging := false
	var _moved := 0.0
	var _held_until := 0 # after a drag, the strip stays where it was left for a moment

	func _ready() -> void:
		mouse_filter = Control.MOUSE_FILTER_STOP
		clip_contents = true

	## How many chips: every fighter and "Create".
	func count() -> int:
		return ids.size() + 1

	func set_front(i: int) -> void:
		if i != _front:
			_front = i
			queue_redraw()
		if not _dragging and Time.get_ticks_msec() >= _held_until:
			_target = _clamp(_x_of(i) + CHIP.x / 2.0 - size.x / 2.0)

	func _x_of(i: int) -> float:
		return i * (CHIP.x + GAP)

	func _clamp(s: float) -> float:
		return clampf(s, 0.0, maxf(0.0, count() * (CHIP.x + GAP) - GAP - size.x))

	## Which chip is at x (this control's coordinates): 0..ids.size()-1 a fighter, ids.size() for
	## "Create", -1 for a gap.
	func chip_at(x: float) -> int:
		var at := x + _scroll
		var i := int(floorf(at / (CHIP.x + GAP)))
		if i < 0 or i >= count() or at - _x_of(i) > CHIP.x:
			return -1
		return i

	## Where chip i is now, for tests.
	func chip_rect(i: int) -> Rect2:
		return Rect2(Vector2(_x_of(i) - _scroll, (size.y - CHIP.y) / 2.0), CHIP)

	func _process(delta: float) -> void:
		if not is_visible_in_tree():
			return
		if not _dragging and absf(_scroll - _target) > 0.5:
			_scroll = lerpf(_scroll, _target, minf(1.0, delta * 12.0))
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
					var i := chip_at(button.position.x)
					if i >= 0 and i < ids.size():
						_held_until = 0
						picked.emit(i)
					elif i == ids.size():
						create_picked.emit()
				else:
					_held_until = Time.get_ticks_msec() + 2500
			accept_event()
			return
		var motion := event as InputEventMouseMotion
		if motion != null and _dragging:
			_moved += absf(motion.relative.x)
			_scroll = _clamp(_scroll - motion.relative.x)
			_target = _scroll
			queue_redraw()
			accept_event()

	func _draw() -> void:
		var font := get_theme_default_font()
		for i in count():
			var r := chip_rect(i)
			if r.end.x < 0 or r.position.x > size.x:
				continue
			var lit := i == _front
			var box := StyleBoxFlat.new()
			box.bg_color = Color(UiTheme.ACCENT, 0.25) if lit else Color(0.14, 0.13, 0.18, 0.94)
			box.border_color = UiTheme.ACCENT if lit else Color(1, 1, 1, 0.18)
			box.set_border_width_all(4 if lit else 2)
			box.set_corner_radius_all(16)
			draw_style_box(box, r)
			var face := Rect2(r.position + Vector2(20, 8), Vector2(r.size.x - 40, r.size.y - 50))
			var label := ""
			if i < ids.size():
				var id := ids[i]
				if not _heads.has(id):
					_heads[id] = ArenaView.head_texture_for(id)
				var tex: Texture2D = _heads[id]
				if tex != null:
					var k := minf(face.size.x / tex.get_width(), face.size.y / tex.get_height())
					var s := Vector2(tex.get_width(), tex.get_height()) * k
					draw_texture_rect(tex, Rect2(face.get_center() - s / 2.0, s), false)
				label = ArtLibrary.fighter_name(id)
			else:
				# "Create": a big plus.
				var c := face.get_center()
				draw_rect(Rect2(c - Vector2(30, 6), Vector2(60, 12)), UiTheme.ACCENT)
				draw_rect(Rect2(c - Vector2(6, 30), Vector2(12, 60)), UiTheme.ACCENT)
				label = "Create"
			draw_string(font, Vector2(r.position.x + 4, r.end.y - 12), label, HORIZONTAL_ALIGNMENT_CENTER,
				r.size.x - 8, 26, UiTheme.ACCENT if lit else UiTheme.TEXT)
