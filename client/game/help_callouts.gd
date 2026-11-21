class_name HelpCallouts
extends Control
## How to play on touch (rooms v1, fix 27): the real touch controls stay on screen under the card,
## dimmed and asleep, and each one gets a callout beside it saying what it does, in the words the
## buttons themselves show (Kick, Jump, Heavy, Dodge, Combo). Placed from TouchControls.point_for,
## so the callouts follow the buttons at any size (Settings' touch size included). Drawn only.

## The how-to-play card sits in the top part of the screen, above this fraction at most and above
## every callout (card_bottom).
const CARD_BOTTOM := 0.5
## The card needs this much of the screen's height above the callouts.
const CARD_MIN := 0.44
const FONT_SIZE := 30
const PAD := Vector2(14, 8)
const GAP := 18.0
const FILL := Color(0.05, 0.045, 0.07, 0.9)
const LINE := Color(1, 0.82, 0.25, 0.9)
## How far a callout keeps from a control's circle.
const CIRCLE_CLEAR := 6.0

var touch: TouchControls


## [{text, rect, to}] for a screen of the touch control's size: each callout's box, and the point on
## its button's edge the leader line goes to. A box never overlaps another, never covers a control
## drawn on screen (the move pad, the buttons, Throw while armed: TouchControls.drawn_circles), and
## stays on screen: one that would is moved up, clear of what it hit (final round: at Large, Up's box
## covered Dodge, and armed, Throw's covered Jump).
func callouts() -> Array:
	var out: Array = []
	if touch == null:
		return out
	var font := ThemeDB.fallback_font
	var mr := TouchControls.move_radius()
	var pad_right := touch.point_for(Buttons.RIGHT) + Vector2(mr, 0)
	var left_x := pad_right.x + GAP * 2.0
	# [text, anchor (the button's edge), where the box goes: "right" of it, "left" of it, "above"]
	var wanted: Array = [
		["Move: left / right", pad_right, "right"],
		# Above the pad's top notch, where the Up arrow is (the Up half-moon shows only while held).
		["Up: aim up.  Up + Heavy leaps back", touch.up_arrow_at() - Vector2(0, mr * 0.45), "above"],
		["Down: aim down", Vector2(pad_right.x - mr * 0.5, touch.point_for(Buttons.DOWN).y), "right"],
	]
	# Dodge's callout is placed first of the buttons', so the parry's gets the spot right over it.
	var order: Array[int] = []
	for i in TouchControls.PAD_BUTTONS.size():
		if TouchControls.PAD_BUTTONS[i][0] == Buttons.DODGE:
			order.push_front(i)
		else:
			order.append(i)
	for i in order:
		var b: Array = TouchControls.PAD_BUTTONS[i]
		var button: int = b[0]
		if button == Buttons.DROP_WEAPON and not touch.armed():
			continue
		var c := touch.point_for(button)
		var r := TouchControls.radius_of(i)
		match button:
			Buttons.LIGHT:
				wanted.append(["Kick: attack", c - Vector2(0, r), "above"])
			Buttons.JUMP:
				wanted.append(["Jump (twice)", c - Vector2(r, 0), "left"])
			# The buttons sit in one low band: most callouts go above them, Jump's to the left (the
			# open middle). A box that still meets a control is moved up below.
			Buttons.HEAVY:
				wanted.append(["Heavy: a big hit", c - Vector2(0, r), "above"])
			Buttons.DODGE:
				wanted.append(["Dodge: just as a hit lands", c - Vector2(0, r), "above"])
			TouchControls.COMBO:
				wanted.append(["Combo: a flurry", c - Vector2(0, r), "above"])
			Buttons.DROP_WEAPON:
				wanted.append(["Throw your weapon", c - Vector2(0, r), "above"])
	var circles := shown_circles()
	var placed: Array[Rect2] = []
	for w: Array in wanted:
		var text: String = w[0]
		var at: Vector2 = w[1]
		var box_size := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, FONT_SIZE) + PAD * 2.0
		var pos := Vector2.ZERO
		match str(w[2]):
			"right":
				pos = Vector2(maxf(at.x + GAP, left_x), at.y - box_size.y / 2.0)
			"left":
				pos = Vector2(at.x - GAP - box_size.x, at.y - box_size.y / 2.0)
			"above":
				pos = Vector2(at.x - box_size.x / 2.0, at.y - GAP * 0.5 - box_size.y)
		var rect := _clamped(Rect2(pos, box_size))
		# Clear of the callouts already placed and of every control on screen: the nearest free spot,
		# first along its own row (up to a box's width either way), then a row higher, and so on, so
		# the boxes sit side by side above the buttons rather than in a tall stack.
		var found := false
		var step := box_size.y + 6.0
		for level in 6:
			for dx: float in [0.0, -0.5, 0.5, -1.0, 1.0, -1.5, 1.5]:
				var r := _clamped(Rect2(rect.position + Vector2(dx * box_size.x, -level * step), box_size))
				if _clear(r, placed, circles):
					rect = r
					found = true
					break
			if found:
				break
		if not found:
			# Nowhere near: straight up past whatever it meets.
			for _try in 12:
				var top := INF
				for q in placed:
					if q.grow(4.0).intersects(rect):
						top = minf(top, q.position.y - 6.0)
				for cr: Array in circles:
					if rect_meets_circle(rect, cr[0], float(cr[1]) + CIRCLE_CLEAR):
						top = minf(top, (cr[0] as Vector2).y - float(cr[1]) - CIRCLE_CLEAR)
				if top == INF:
					break
				rect.position.y = maxf(8.0, top - rect.size.y)
		placed.append(rect)
		out.append({"text": text, "rect": rect, "to": at})
	return out


## A box kept inside the screen.
func _clamped(r: Rect2) -> Rect2:
	r.position.x = clampf(r.position.x, 8.0, size.x - r.size.x - 8.0)
	r.position.y = clampf(r.position.y, 8.0, size.y - r.size.y - 4.0)
	return r


## Whether a box is clear of the boxes placed and of every control on screen.
func _clear(r: Rect2, placed: Array[Rect2], circles: Array) -> bool:
	for q in placed:
		if q.grow(4.0).intersects(r):
			return false
	for cr: Array in circles:
		if rect_meets_circle(r, cr[0], float(cr[1]) + CIRCLE_CLEAR):
			return false
	return true


## The controls drawn on screen now: [centre, radius] of the move pad's two circles and each shown
## button (Throw only while armed).
func shown_circles() -> Array:
	var out: Array = []
	var all := TouchControls.drawn_circles(size)
	out.append(all[0])
	out.append(all[1])
	for i in TouchControls.PAD_BUTTONS.size():
		if TouchControls.PAD_BUTTONS[i][0] != Buttons.DROP_WEAPON or touch.armed():
			out.append(all[2 + i])
	return out


## Whether a rectangle meets a circle.
static func rect_meets_circle(r: Rect2, centre: Vector2, radius: float) -> bool:
	var nearest := Vector2(clampf(centre.x, r.position.x, r.end.x), clampf(centre.y, r.position.y, r.end.y))
	return nearest.distance_to(centre) < radius


## The y the how-to-play card must end above: half the screen, or the top of the highest callout.
func card_bottom() -> float:
	var y := size.y * CARD_BOTTOM
	for c: Dictionary in callouts():
		y = minf(y, (c["rect"] as Rect2).position.y - 12.0)
	return y


func _draw() -> void:
	var font := ThemeDB.fallback_font
	for c: Dictionary in callouts():
		var rect: Rect2 = c["rect"]
		var to: Vector2 = c["to"]
		var from := Vector2(clampf(to.x, rect.position.x, rect.end.x), clampf(to.y, rect.position.y, rect.end.y))
		if from.distance_to(to) > 2.0:
			draw_line(from, to, LINE, 3.0, true)
		var box := StyleBoxFlat.new()
		box.bg_color = FILL
		box.border_color = LINE
		box.set_border_width_all(2)
		box.set_corner_radius_all(12)
		draw_style_box(box, rect)
		draw_string(font, rect.position + Vector2(PAD.x, PAD.y + FONT_SIZE * 0.85), str(c["text"]),
			HORIZONTAL_ALIGNMENT_LEFT, -1, FONT_SIZE, Color.WHITE)
