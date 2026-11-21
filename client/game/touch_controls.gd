class_name TouchControls
extends Control
## On-screen controls for phones and tablets.
##   Left thumb: a Left / Right pad in the bottom-left corner. Above and below it are the Up and
##     Down zones, drawn as half-moons the width of the pad with a big arrow each: slide the thumb
##     up off the pad, or tap the upper half-moon, to hold Up; the same below for Down (the lower
##     one runs to the screen's bottom edge, so the recovery is easy to hit). They aim a heavy
##     (Up + Heavy is the recovery, Down + Heavy the slam) or an air dodge. Up / Down count only
##     outside the pad's band, so a thumb drifting a little while it steers never presses them.
##   Right thumb: two big buttons, Kick (the light attack) and Jump; Heavy and Dodge beside them;
##     a fixed Combo (Kick and Heavy at once: the flurry) at the right edge; and, only while a
##     weapon is in hand, Throw beside Kick with the weapon on it and a gold ring. Kick always says
##     "Kick"; armed, it shows the weapon and what it does with it as a small caption.
##   Everything drawn at rest stays in the band under floor_clear_y, where the arena's floor sits,
##   so no control covers the floor or anyone standing on it.
## The scene ORs buttons() into P1's buttons, so touch, keys and pad can be used together.
##
## Multi-touch: each finger (touch index) is tracked on its own, so a thumb can hold the move pad
## while the other presses Heavy. A finger that lands on the move pad keeps steering it wherever it
## slides until it lifts; a finger on the buttons can slide from one button to another.
##
## Drawn to read over any sky (rooms v1, fix 6): a dark translucent fill, the accent colour while
## pressed, and labels with a dark outline.
##
## Settings' "Touch buttons: Small / Medium / Large" and "Opacity: Low / Medium / High" (rooms v1,
## fix 20) are read here: GameSettings.touch_scale multiplies every size and every distance from the
## corners below (the constants are Medium), and GameSettings.touch_alpha the fills and rings. The
## floor line (floor_clear_y) follows, so the arena moves with the buttons.
##
## Lives in its own CanvasLayer, full screen, and ignores the mouse (mouse_filter IGNORE), so it
## never blocks the HUD buttons. Reading only: it never touches GameState.

## What a finger is on.
const NOTHING := -2
const MOVE := -1

## The move pad: a round Left button and a round Right button side by side.
const MOVE_RADIUS := 112.0
const MOVE_GAP := 36.0
## Centre of the Left button, measured from the bottom-left corner. Raised (from 230) so the Down
## half-moon under the pad is DOWN_ZONE_MIN tall or more (rooms v1, fix 18).
const MOVE_FROM_CORNER := Vector2(192, 240)
## A finger counts as on the move pad this far left or right of its buttons, anywhere below them
## to the screen's edge (the Down zone)...
const MOVE_REACH := 70.0
## ...and this far above them: the Up zone.
const UP_ZONE := 120.0
## The Down zone is at least this tall (from the pad's bottom to the screen's edge).
const DOWN_ZONE_MIN := 100.0
## A finger this close to the middle of the pad (between the two buttons) presses neither way.
const MOVE_DEAD := 24.0
## The Up / Down arrows: from tip to base at least this long.
const ARROW_SIZE := 54.0
## Buttons are hit a little outside their circles.
const BUTTON_REACH := 1.18

const COMBO := Buttons.LIGHT | Buttons.HEAVY

## Right-side buttons: sim buttons, label, radius, centre measured from the bottom-right corner.
## Every one sits in the band under the floor line (BAND_HEIGHT, as tall as the move pad reaches),
## so nothing drawn covers the floor or anyone standing on it (rooms v1, fix 5, as built): Kick
## (first: tests read it) in the corner, Jump to its left; Dodge and Heavy in the band's upper row,
## in the gaps beside Jump, Dodge the nearer to Kick (it is the parry, timed to a hit, so the thumb
## has the shortest way to it: final round); Combo at the right edge over Kick; Throw under Dodge,
## beside Kick, shown only while armed (fix 17). Combo and Throw are at least 112 px across at
## Medium (Android's 48 dp touch target, with BUTTON_REACH, at the phone's density).
const PAD_BUTTONS := [
	[Buttons.LIGHT, "Kick", 116.0, Vector2(240, 170)],
	[Buttons.JUMP, "Jump", 100.0, Vector2(560, 118)],
	[Buttons.HEAVY, "Heavy", 74.0, Vector2(700, 272)],
	[Buttons.DODGE, "Dodge", 74.0, Vector2(420, 272)],
	[COMBO, "Combo", 66.0, Vector2(76, 284)],
	[Buttons.DROP_WEAPON, "Throw", 56.0, Vector2(396, 64)],
]
## The height of the band the controls are drawn in, from the screen's bottom at Medium: the move
## pad's top (MOVE_FROM_CORNER.y + MOVE_RADIUS). No button rises above it.
const BAND_HEIGHT := 352.0

const FILL := Color(0.05, 0.045, 0.07, 0.38)
const FILL_PRESSED := Color(0.949, 0.663, 0.0, 0.6) # UiTheme.ACCENT at 0.6
const RING := Color(1, 1, 1, 0.55)
const LABEL := Color(1, 1, 1, 0.92)
const LABEL_OUTLINE := Color(0, 0, 0, 0.7)
const OUTLINE_SIZE := 6
## The smallest a button's label shrinks to (label_fit).
const LABEL_MIN := 20
const ZONE_FILL := Color(0.05, 0.045, 0.07, 0.22)

const ARMED_RING := Color(1, 0.82, 0.25, 0.9)
## The weapon's verb under "Kick" while armed.
const CAPTION_SIZE := 28

## The local fighter's weapon (Weapons id, 0 for none), set by the scene: Kick shows it and what it
## does with it, and Throw shows only while there is one.
var weapon := 0:
	set(value):
		if value != weapon:
			weapon = value
			queue_redraw()

## Shown but asleep: drawn dimmed, pressing nothing (under the how-to-play callouts, fix 27).
var frozen := false:
	set(value):
		if value != frozen:
			frozen = value
			release_all()
			modulate = Color(1, 1, 1, 0.6 if value else 1.0)

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


## Whether a weapon is in hand (Throw shows, Kick shows the weapon).
func armed() -> bool:
	return weapon > 0 and weapon < WeaponTable.COUNT


## The size factor from Settings (1 = Medium, the constants above).
static func scale_k() -> float:
	return GameSettings.touch_scale


## PAD_BUTTONS[i]'s radius at the current size.
static func radius_of(i: int) -> float:
	return float(PAD_BUTTONS[i][2]) * scale_k()


## The move pad's buttons' radius at the current size.
static func move_radius() -> float:
	return MOVE_RADIUS * scale_k()


## A colour with its opacity scaled by Settings' "Opacity" (never past opaque).
static func _faded(c: Color) -> Color:
	return Color(c, minf(1.0, c.a * GameSettings.touch_alpha))


## Every held button, as Buttons flags. Nothing while hidden or frozen.
func buttons() -> int:
	if not is_visible_in_tree() or frozen:
		return Buttons.NONE
	var b := Buttons.NONE
	for index: int in _targets:
		var target: int = _targets[index]
		if target == MOVE:
			b |= _move_buttons(_move_fingers[index])
		elif target >= 0 and _shown(target):
			b |= PAD_BUTTONS[target][0] as int
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
	if is_visible_in_tree() and not frozen:
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
## the screen). Left / Right: the centre of that half of the move pad. Up / Down: the middle of the
## half-moon above or below the pad (which presses only Up or Down). Kick and Heavy at once: Combo.
## The others: the centre of their circle. For tests.
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
			return _move_middle() + Vector2(0, -(MOVE_RADIUS + UP_ZONE * 0.5) * scale_k())
		Buttons.DOWN:
			return _move_middle() + Vector2(0, move_radius() + _down_zone() * 0.5)
	assert(false, "No touch control for button %d" % button)
	return Vector2.ZERO


## The screen y the floor sits on under touch (rooms v1, 2.4): the highest top of anything drawn at
## rest, the move pad and every button (Throw too, though it shows only while armed), in a view of
## this size (728 at 1080 tall at Medium), so the floor and everyone standing on it are above the
## controls. The Up zone is above it but drawn only while held, under the thumb (its arrow sits in
## the pad's top). ArenaView reads it, so moving or resizing these buttons (Settings' touch size
## too) moves the arena with them.
static func floor_clear_y(view: Vector2) -> float:
	var top := view.y
	for c: Array in drawn_circles(view):
		top = minf(top, (c[0] as Vector2).y - float(c[1]))
	return top


## The free part of the band under the floor line, between the move pad's reach and the leftmost
## right-hand button, in a view of this size: the arena's KO feed goes here under touch, so it
## covers nobody.
static func free_band(view: Vector2) -> Rect2:
	var k := scale_k()
	var left := (MOVE_FROM_CORNER.x + MOVE_RADIUS * 3.0 + MOVE_GAP + MOVE_REACH) * k + 16.0
	var right := view.x
	for b: Array in PAD_BUTTONS:
		right = minf(right, view.x - ((b[3] as Vector2).x + float(b[2])) * k - 16.0)
	var top := floor_clear_y(view)
	return Rect2(left, top, maxf(0.0, right - left), view.y - top)


## Every circle drawn at rest in a view of this size, [centre, radius]: the move pad's two and each
## button's (Throw included).
static func drawn_circles(view: Vector2) -> Array:
	var k := scale_k()
	var left := Vector2(MOVE_FROM_CORNER.x * k, view.y - MOVE_FROM_CORNER.y * k)
	var out: Array = [[left, MOVE_RADIUS * k], [left + Vector2((MOVE_RADIUS * 2 + MOVE_GAP) * k, 0), MOVE_RADIUS * k]]
	for b: Array in PAD_BUTTONS:
		out.append([view - (b[3] as Vector2) * k, float(b[2]) * k])
	return out


## The screen rectangles the controls cover in a view of this size (each button's circle as its
## square, the pad with its zones), so markers can keep out of them.
static func covered_rects(view: Vector2) -> Array[Rect2]:
	var k := scale_k()
	var out: Array[Rect2] = []
	var left := Vector2(MOVE_FROM_CORNER.x * k, view.y - MOVE_FROM_CORNER.y * k)
	var pad_w := (MOVE_RADIUS * 4.0 + MOVE_GAP) * k
	var top := left.y - (MOVE_RADIUS + UP_ZONE) * k
	out.append(Rect2(left.x - MOVE_RADIUS * k, top, pad_w, view.y - top))
	for b: Array in PAD_BUTTONS:
		var r: float = float(b[2]) * k
		var c: Vector2 = view - (b[3] as Vector2) * k
		out.append(Rect2(c - Vector2(r, r), Vector2(r, r) * 2.0))
	return out


func _left_center() -> Vector2:
	return Vector2(MOVE_FROM_CORNER.x * scale_k(), size.y - MOVE_FROM_CORNER.y * scale_k())


func _right_center() -> Vector2:
	return _left_center() + Vector2((MOVE_RADIUS * 2 + MOVE_GAP) * scale_k(), 0)


## Halfway between the Left and Right buttons.
func _move_middle() -> Vector2:
	return (_left_center() + _right_center()) / 2


## The Up arrow at rest: between the pad's two circles near their tops, its tip under the floor line.
func up_arrow_at() -> Vector2:
	return _move_middle() + Vector2(0, -move_radius() * 0.6)


## How tall the Down zone is: from the pad's bottom to the screen's edge.
func _down_zone() -> float:
	return maxf(DOWN_ZONE_MIN, size.y - (_move_middle().y + move_radius()))


func _button_center(i: int) -> Vector2:
	var from_corner: Vector2 = PAD_BUTTONS[i][3]
	return size - from_corner * scale_k()


## Whether button i is on screen now: Throw only while armed.
func _shown(i: int) -> bool:
	return PAD_BUTTONS[i][0] != Buttons.DROP_WEAPON or armed()


## Where a finger landing counts as on the move pad: around both buttons, with the Up zone above
## and the Down zone below, to the screen's edge.
func _move_area() -> Rect2:
	var k := scale_k()
	var top_left := _left_center() - Vector2(MOVE_RADIUS + MOVE_REACH, MOVE_RADIUS + UP_ZONE) * k
	var bottom_right := _right_center() + Vector2((MOVE_RADIUS + MOVE_REACH) * k, move_radius() + _down_zone())
	return Rect2(top_left, bottom_right - top_left)


func _target_at(pos: Vector2) -> int:
	var b := _button_at(pos)
	if b != NOTHING:
		return b
	if _move_area().has_point(pos):
		return MOVE
	return NOTHING


## The nearest shown button whose (slightly enlarged) circle holds pos, else NOTHING.
func _button_at(pos: Vector2) -> int:
	var best := NOTHING
	var best_distance := INF
	for i in PAD_BUTTONS.size():
		if not _shown(i):
			continue
		var d := pos.distance_to(_button_center(i))
		var radius := radius_of(i)
		if d <= radius * BUTTON_REACH and d < best_distance:
			best = i
			best_distance = d
	return best


## Directions pressed by a finger at pos on the move pad: Left or Right by which half it is over,
## Up or Down once it is above or below the pad's buttons (y down on screen, up in the sim).
func _move_buttons(pos: Vector2) -> int:
	var b := Buttons.NONE
	var middle := _move_middle()
	var dead := MOVE_DEAD * scale_k()
	if pos.x < middle.x - dead:
		b |= Buttons.LEFT
	elif pos.x > middle.x + dead:
		b |= Buttons.RIGHT
	if pos.y < middle.y - move_radius():
		b |= Buttons.UP
	elif pos.y > middle.y + move_radius():
		b |= Buttons.DOWN
	return b


# ---- drawing ----

func _draw() -> void:
	var font := ThemeDB.fallback_font
	var held := buttons()

	# The Up and Down half-moons, the width of the pad, each with a big arrow (lit while held). The
	# Up one rises above the floor line, so at rest only its arrow shows, in the notch at the top of
	# the pad; the half-moon itself is drawn only while held (under the thumb).
	var k := scale_k()
	var mr := move_radius()
	var fill := _faded(FILL)
	var ring_colour := _faded(RING)
	var middle := _move_middle()
	var half_w := (MOVE_RADIUS * 2.0 + MOVE_GAP * 0.5) * k
	for v: Array in [[-1.0, UP_ZONE * k, Buttons.UP], [1.0, _down_zone(), Buttons.DOWN]]:
		var dir: float = v[0]
		var depth: float = v[1]
		var lit := (held & (v[2] as int)) != 0
		if dir < 0.0 and not lit:
			_draw_arrow(up_arrow_at(), Vector2.UP, ARROW_SIZE * k / 1.6, LABEL, true)
			continue
		var base_y := middle.y + dir * mr * 0.55
		var pts := PackedVector2Array()
		for step in 25:
			var a := PI * step / 24.0
			pts.append(Vector2(middle.x - cos(a) * half_w, base_y + dir * sin(a) * (depth + mr * 0.45)))
		draw_colored_polygon(pts, FILL_PRESSED if lit else _faded(ZONE_FILL))
		pts.append(pts[0])
		draw_polyline(pts, Color(ring_colour, ring_colour.a * 0.6), 3.0, true)
		var arrow_at := middle + Vector2(0, dir * (mr + depth * 0.5))
		_draw_arrow(arrow_at, Vector2(0, dir), ARROW_SIZE * k / 1.6, Color.WHITE if lit else LABEL, true)

	# Move pad: two circles with a big arrow each, and a knob under each finger.
	for half: Array in [[_left_center(), Vector2.LEFT, Buttons.LEFT], [_right_center(), Vector2.RIGHT, Buttons.RIGHT]]:
		var c: Vector2 = half[0]
		var lit := (held & (half[2] as int)) != 0
		draw_circle(c, mr, FILL_PRESSED if lit else fill)
		draw_circle(c, mr, ring_colour, false, 4.0, true)
		_draw_arrow(c, half[1], mr * 0.42, LABEL, true)
	for index: int in _move_fingers:
		draw_circle(_move_fingers[index], mr * 0.3, FILL_PRESSED)

	# Action buttons.
	var armed_now := armed()
	for i in PAD_BUTTONS.size():
		if not _shown(i):
			continue
		var center := _button_center(i)
		var radius := radius_of(i)
		var button: int = PAD_BUTTONS[i][0]
		var lit := (held & button) == button
		draw_circle(center, radius, FILL_PRESSED if lit else fill)
		var ring := ARMED_RING if armed_now and (button == Buttons.LIGHT or button == Buttons.DROP_WEAPON) else ring_colour
		draw_circle(center, radius, ring, false, 4.0 if button != Buttons.DROP_WEAPON else 5.0, true)
		var text: String = PAD_BUTTONS[i][1]
		var font_size := maxi(30, int(radius * 0.34)) # the small buttons' labels stay readable
		var text_y := font_size * 0.35
		if armed_now and (button == Buttons.LIGHT or button == Buttons.DROP_WEAPON):
			# The weapon in hand on Kick (with what Kick does with it) and on Throw.
			var tex := ArtLibrary.weapon_texture(weapon)
			if tex != null:
				var w := radius * 1.05
				var h := w * tex.get_height() / maxf(1.0, tex.get_width())
				var most := radius * (0.62 if button == Buttons.LIGHT else 0.7)
				if h > most:
					w *= most / h
					h = most
				var lift := radius * (0.42 if button == Buttons.LIGHT else 0.22)
				draw_texture_rect(tex, Rect2(center + Vector2(-w / 2, -h / 2 - lift), Vector2(w, h)), false, Color(1, 1, 1, 0.95))
			if button == Buttons.LIGHT:
				text_y = radius * 0.2
				_label(font, center + Vector2(0, radius * 0.62), WeaponInfo.verb(weapon), CAPTION_SIZE, ARMED_RING)
			else:
				text_y = radius * 0.72
				font_size = 26
		font_size = label_fit(font, text, font_size, radius, text_y)
		_label(font, center + Vector2(0, text_y), text, font_size, LABEL)


## The font size at which `text`, its baseline text_y below a button's centre, fits inside the
## circle of that radius with its outline (the chord at the text's middle, less a margin): font_size
## or smaller, never under LABEL_MIN (final round: Combo and Throw ran past their rims).
static func label_fit(font: Font, text: String, font_size: int, radius: float, text_y: float) -> int:
	var middle := text_y - font_size * 0.35
	var chord := 2.0 * sqrt(maxf(0.0, radius * radius - middle * middle)) - 2.0 * OUTLINE_SIZE - 8.0
	var width := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	if width <= chord or width <= 0.0:
		return font_size
	return maxi(LABEL_MIN, int(font_size * chord / width))


## A label centred on x with its baseline at y, outlined so it reads over a light sky.
func _label(font: Font, at: Vector2, text: String, font_size: int, color: Color) -> void:
	var width := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	var pos := at - Vector2(width / 2, 0)
	draw_string_outline(font, pos, text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, OUTLINE_SIZE, LABEL_OUTLINE)
	draw_string(font, pos, text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, color)


## The verb's font size under Kick while armed (tests check every verb fits the button).
static func verb_font_size() -> int:
	return CAPTION_SIZE


## A filled triangle centred on c, pointing along dir, `half` from its centre to its tip, with a
## dark rim when `rim` is set.
func _draw_arrow(c: Vector2, dir: Vector2, half: float, color: Color, rim := false) -> void:
	var side := Vector2(-dir.y, dir.x) * half * 0.9
	var tip := c + dir * half
	var base := c - dir * half * 0.6
	var pts := PackedVector2Array([tip, base + side, base - side])
	draw_colored_polygon(pts, color)
	if rim:
		pts.append(tip)
		draw_polyline(pts, LABEL_OUTLINE, 2.5, true)
