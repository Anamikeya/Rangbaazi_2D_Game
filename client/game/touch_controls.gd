class_name TouchControls
extends Control
## On-screen controls for phones and tablets.
##   Left thumb: a Left / Right pad in the bottom-left corner. Above and below it are Up and Down
##     zones (marked with arrows): slide the thumb up off the pad, or tap the up arrow, to hold
##     Up; the same below for Down. They aim a heavy (Up + Heavy is the recovery, Down + Heavy the
##     slam) or an air dodge. Up / Down count only outside the pad's band, so a thumb drifting a
##     little while it steers left and right never presses them by accident.
##   Right thumb: two big buttons, Kick (the light attack) and Jump, with smaller Heavy and Dodge
##     above them.
## The scene ORs buttons() into P1's buttons, so touch, keys and pad can be used together.
##
## Multi-touch: each finger (touch index) is tracked on its own, so a thumb can hold the move pad
## while the other presses Heavy. A finger that lands on the move pad keeps steering it wherever it
## slides until it lifts; a finger on the buttons can slide from one button to another.
##
## Lives in its own CanvasLayer, full screen, and ignores the mouse (mouse_filter IGNORE), so it
## never blocks the HUD buttons. Reading only: it never touches GameState.

## What a finger is on.
const NOTHING := -2
const MOVE := -1

## The move pad: a round Left button and a round Right button side by side.
const MOVE_RADIUS := 112.0
const MOVE_GAP := 36.0
## Centre of the Left button, measured from the bottom-left corner.
const MOVE_FROM_CORNER := Vector2(192, 230)
## A finger counts as on the move pad this far left, right or below its buttons...
const MOVE_REACH := 70.0
## ...and this far above them: the Up zone.
const UP_ZONE := 120.0
## A finger this close to the middle of the pad (between the two buttons) presses neither way.
const MOVE_DEAD := 24.0
## The Up / Down arrows sit this far above / below the pad's buttons.
const ARROW_OFFSET := 46.0
## Buttons are hit a little outside their circles.
const BUTTON_REACH := 1.18

## Right-side buttons: sim button, label, radius, centre measured from the bottom-right corner.
## Kick and Jump are the big ones; Heavy sits above Kick and Dodge above Jump.
const PAD_BUTTONS := [
	[Buttons.LIGHT, "Kick", 128.0, Vector2(210, 210)],
	[Buttons.JUMP, "Jump", 116.0, Vector2(490, 150)],
	[Buttons.HEAVY, "Heavy", 80.0, Vector2(230, 445)],
	[Buttons.DODGE, "Dodge", 80.0, Vector2(480, 370)],
	[Buttons.DROP_WEAPON, "Throw", 64.0, Vector2(410, 560)],
]

const FILL := Color(1, 1, 1, 0.13)
const FILL_PRESSED := Color(1, 0.82, 0.25, 0.45)
const RING := Color(1, 1, 1, 0.42)
const LABEL := Color(1, 1, 1, 0.8)

## What the big attack button says while a weapon is in hand, by the sim's weapon id.
const WEAPON_VERBS := ["Kick", "Bonk", "Swing", "Roll", "Blast", "Fire", "Lob", "Shoot"]
const ARMED_RING := Color(1, 0.82, 0.25, 0.9)

## The local fighter's weapon (Weapons id, 0 for none), set by the scene: the attack button shows
## it and names what it does; the small button is Throw with a weapon and Combo without one.
var weapon := 0:
	set(value):
		if value != weapon:
			weapon = value
			queue_redraw()

## touch index -> NOTHING, MOVE or an index into PAD_BUTTONS.
var _targets: Dictionary[int, int] = {}
## touch index -> where the finger is now, for fingers on the move pad.
var _move_fingers: Dictionary[int, Vector2] = {}


## Whether this device wants on-screen controls: a touchscreen, a phone or tablet (app or
## browser), or --touch on the command line (to test them on a desktop).
static func wanted() -> bool:
	return forced() or handheld() or DisplayServer.is_touchscreen_available()


static func forced() -> bool:
	return OS.get_cmdline_user_args().has("--touch") or OS.get_cmdline_args().has("--touch")


## A phone or tablet (app or browser). Elsewhere, e.g. a laptop with a touchscreen, the scene
## hides the controls while keys or a pad are in use.
static func handheld() -> bool:
	return OS.has_feature("mobile") or OS.has_feature("web_android") or OS.has_feature("web_ios")


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	visibility_changed.connect(release_all)


## Every held button, as Buttons flags. Nothing while hidden.
func buttons() -> int:
	if not is_visible_in_tree():
		return Buttons.NONE
	var b := Buttons.NONE
	for index: int in _targets:
		var target: int = _targets[index]
		if target == MOVE:
			b |= _move_buttons(_move_fingers[index])
		elif target >= 0:
			var button: int = PAD_BUTTONS[target][0]
			# Empty-handed, the Throw button is Combo: Kick and Heavy on the same frame (the flurry).
			b |= (Buttons.LIGHT | Buttons.HEAVY) if button == Buttons.DROP_WEAPON and weapon == 0 else button
	return b & Buttons.KNOWN


## Lets go of everything, e.g. when hidden or when the app loses focus mid-touch.
func release_all() -> void:
	_targets.clear()
	_move_fingers.clear()
	queue_redraw()


func _notification(what: int) -> void:
	if what == NOTIFICATION_APPLICATION_FOCUS_OUT or what == NOTIFICATION_WM_WINDOW_FOCUS_OUT:
		release_all()


func _input(event: InputEvent) -> void:
	if is_visible_in_tree():
		handle(event)


## Tracks a finger from a touch or drag event. The scene also hands over the touch that made it
## show the controls, which arrived while they were still hidden.
func handle(event: InputEvent) -> void:
	if event is InputEventScreenTouch:
		var touch := event as InputEventScreenTouch
		var pos := _local(touch.position)
		if touch.pressed and not touch.canceled:
			# A press for an index we still track means its release never came: start over.
			var target := _target_at(pos)
			_targets[touch.index] = target
			if target == MOVE:
				_move_fingers[touch.index] = pos
			else:
				_move_fingers.erase(touch.index)
		else:
			_targets.erase(touch.index)
			_move_fingers.erase(touch.index)
		queue_redraw()
	elif event is InputEventScreenDrag:
		var drag := event as InputEventScreenDrag
		if not _targets.has(drag.index):
			return
		var pos := _local(drag.position)
		if _targets[drag.index] == MOVE:
			_move_fingers[drag.index] = pos
		else:
			_targets[drag.index] = _button_at(pos) # slide between buttons; off them presses nothing
		queue_redraw()


## Event position (viewport coordinates) in this control's coordinates.
func _local(viewport_pos: Vector2) -> Vector2:
	return get_global_transform_with_canvas().affine_inverse() * viewport_pos


# ---- layout ----

## Where a finger presses this button, in this control's coordinates (the viewport's, as it fills
## the screen). Left / Right: the centre of that half of the move pad. Up / Down: the arrow above
## or below the middle of the pad (which presses only Up or Down). The others: the centre of their
## circle. For tests.
func point_for(button: int) -> Vector2:
	for i in PAD_BUTTONS.size():
		if PAD_BUTTONS[i][0] == button:
			return _button_center(i)
	match button:
		Buttons.LEFT:
			return _left_center()
		Buttons.RIGHT:
			return _right_center()
		Buttons.UP:
			return _move_middle() + Vector2(0, -(MOVE_RADIUS + ARROW_OFFSET))
		Buttons.DOWN:
			return _move_middle() + Vector2(0, MOVE_RADIUS + ARROW_OFFSET)
	assert(false, "No touch control for button %d" % button)
	return Vector2.ZERO


func _left_center() -> Vector2:
	return Vector2(MOVE_FROM_CORNER.x, size.y - MOVE_FROM_CORNER.y)


func _right_center() -> Vector2:
	return _left_center() + Vector2(MOVE_RADIUS * 2 + MOVE_GAP, 0)


## Halfway between the Left and Right buttons.
func _move_middle() -> Vector2:
	return (_left_center() + _right_center()) / 2


func _button_center(i: int) -> Vector2:
	var from_corner: Vector2 = PAD_BUTTONS[i][3]
	return size - from_corner


## Where a finger landing counts as on the move pad: around both buttons, with the Up zone above.
func _move_area() -> Rect2:
	var top_left := _left_center() - Vector2(MOVE_RADIUS + MOVE_REACH, MOVE_RADIUS + UP_ZONE)
	var bottom_right := _right_center() + Vector2(MOVE_RADIUS + MOVE_REACH, MOVE_RADIUS + MOVE_REACH)
	return Rect2(top_left, bottom_right - top_left)


func _target_at(pos: Vector2) -> int:
	var b := _button_at(pos)
	if b != NOTHING:
		return b
	if _move_area().has_point(pos):
		return MOVE
	return NOTHING


## The nearest button whose (slightly enlarged) circle holds pos, else NOTHING.
func _button_at(pos: Vector2) -> int:
	var best := NOTHING
	var best_distance := INF
	for i in PAD_BUTTONS.size():
		var d := pos.distance_to(_button_center(i))
		var radius: float = PAD_BUTTONS[i][2]
		if d <= radius * BUTTON_REACH and d < best_distance:
			best = i
			best_distance = d
	return best


## Directions pressed by a finger at pos on the move pad: Left or Right by which half it is over,
## Up or Down once it is above or below the pad's buttons (y down on screen, up in the sim).
func _move_buttons(pos: Vector2) -> int:
	var b := Buttons.NONE
	var middle := _move_middle()
	if pos.x < middle.x - MOVE_DEAD:
		b |= Buttons.LEFT
	elif pos.x > middle.x + MOVE_DEAD:
		b |= Buttons.RIGHT
	if pos.y < middle.y - MOVE_RADIUS:
		b |= Buttons.UP
	elif pos.y > middle.y + MOVE_RADIUS:
		b |= Buttons.DOWN
	return b


# ---- drawing ----

func _draw() -> void:
	var font := ThemeDB.fallback_font
	var held := buttons()

	# Move pad: two circles with a big arrow each, the Up / Down arrows above and below the middle
	# (lit while held), and a knob under each finger.
	for half: Array in [[_left_center(), Vector2.LEFT, Buttons.LEFT], [_right_center(), Vector2.RIGHT, Buttons.RIGHT]]:
		var c: Vector2 = half[0]
		var lit := (held & (half[2] as int)) != 0
		draw_circle(c, MOVE_RADIUS, FILL_PRESSED if lit else FILL)
		draw_circle(c, MOVE_RADIUS, RING, false, 4.0, true)
		_draw_arrow(c, half[1], MOVE_RADIUS * 0.42, LABEL)
	for v: Array in [[Vector2.UP, Buttons.UP], [Vector2.DOWN, Buttons.DOWN]]:
		var dir: Vector2 = v[0]
		var lit := (held & (v[1] as int)) != 0
		_draw_arrow(_move_middle() + dir * (MOVE_RADIUS + ARROW_OFFSET), dir, 30.0, FILL_PRESSED if lit else RING)
	for index: int in _move_fingers:
		draw_circle(_move_fingers[index], MOVE_RADIUS * 0.3, FILL_PRESSED)

	# Action buttons.
	for i in PAD_BUTTONS.size():
		var center := _button_center(i)
		var radius: float = PAD_BUTTONS[i][2]
		var button: int = PAD_BUTTONS[i][0]
		var armed := weapon > 0 and weapon < WEAPON_VERBS.size()
		var combo := button == Buttons.DROP_WEAPON and not armed
		var lit := (held & (Buttons.LIGHT | Buttons.HEAVY)) == (Buttons.LIGHT | Buttons.HEAVY) if combo else (held & button) != 0
		var dim := 1.0
		draw_circle(center, radius, FILL_PRESSED if lit else Color(FILL, FILL.a * dim))
		var ring := ARMED_RING if armed and (button == Buttons.LIGHT or button == Buttons.DROP_WEAPON) else RING
		draw_circle(center, radius, Color(ring, ring.a * dim), false, 4.0, true)
		var text: String = "Combo" if combo else PAD_BUTTONS[i][1]
		var font_size := maxi(30, int(radius * 0.34)) # the small button's label stays readable
		var text_y := font_size * 0.35
		if armed and button == Buttons.LIGHT:
			# The weapon in hand, and what the button does with it.
			text = WEAPON_VERBS[weapon]
			var tex := ArtLibrary.weapon_texture(weapon)
			if tex != null:
				var w := radius * 1.05
				var h := w * tex.get_height() / maxf(1.0, tex.get_width())
				if h > radius * 0.8:
					w *= radius * 0.8 / h
					h = radius * 0.8
				draw_texture_rect(tex, Rect2(center + Vector2(-w / 2, -h / 2 - radius * 0.2), Vector2(w, h)), false, Color(1, 1, 1, 0.95))
				text_y = radius * 0.62
		var width := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
		draw_string(font, center + Vector2(-width / 2, text_y), text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, Color(LABEL, LABEL.a * dim))


## A filled triangle centred on c, pointing along dir, `half` from its centre to its tip.
func _draw_arrow(c: Vector2, dir: Vector2, half: float, color: Color) -> void:
	var side := Vector2(-dir.y, dir.x) * half * 0.9
	var tip := c + dir * half
	var base := c - dir * half * 0.6
	draw_colored_polygon(PackedVector2Array([tip, base + side, base - side]), color)
