class_name TeamStyle
extends RefCounted
## The four room teams' names, colours and symbols (docs/design/rooms-teams-view-ui-v1.md, 1.9).
## Index 0 is "no team". The colours were checked for colour blindness (the smallest CIE76
## difference between two of them is 72.6 normal, 41.6 protan, 36.3 deutan, 32.0 tritan) and keep
## clear of gold (Guts, weapon words, crates) and red (Josh danger). A symbol, a dark rim and an
## outline go with every use, so colour is never the only cue.

const NAMES := ["", "BLUE", "ORANGE", "PINK", "ICE"]
const COLORS := [Color("ffffff"), Color("3d7bff"), Color("ff9a1a"), Color("d02a8c"), Color("6ee7ff")]
enum Symbol { NONE, CIRCLE, TRIANGLE, SQUARE, DIAMOND }
const SYMBOLS := [Symbol.NONE, Symbol.CIRCLE, Symbol.TRIANGLE, Symbol.SQUARE, Symbol.DIAMOND]
const RIM := Color(0, 0, 0, 0.85)


static func color_of(team: int) -> Color:
	return COLORS[team] if team >= 0 and team < COLORS.size() else COLORS[0]


static func name_of(team: int) -> String:
	return NAMES[team] if team >= 0 and team < NAMES.size() else ""


## "BLUE TEAM" and so on; "" for no team.
static func title_of(team: int) -> String:
	return "%s TEAM" % NAMES[team] if team > 0 and team < NAMES.size() else ""


## The team's symbol as a polygon around centre (a circle as 20 points), radius across.
static func glyph_points(centre: Vector2, radius: float, team: int) -> PackedVector2Array:
	var pts := PackedVector2Array()
	match SYMBOLS[team] if team >= 0 and team < SYMBOLS.size() else Symbol.NONE:
		Symbol.CIRCLE:
			for k in 20:
				pts.append(centre + Vector2.from_angle(TAU * k / 20.0) * radius)
		Symbol.TRIANGLE:
			for k in 3:
				pts.append(centre + Vector2.from_angle(-PI / 2 + TAU * k / 3.0) * radius * 1.12 + Vector2(0, radius * 0.14))
		Symbol.SQUARE:
			var h := radius * 0.84
			pts = PackedVector2Array([centre + Vector2(-h, -h), centre + Vector2(h, -h), centre + Vector2(h, h), centre + Vector2(-h, h)])
		Symbol.DIAMOND:
			pts = PackedVector2Array([centre + Vector2(0, -radius * 1.1), centre + Vector2(radius * 0.9, 0),
				centre + Vector2(0, radius * 1.1), centre + Vector2(-radius * 0.9, 0)])
	return pts


## Draws the team's symbol on any CanvasItem: the shape in the team colour with a 2 px dark rim.
## Polygons only, no font glyphs, so it looks the same on every device.
static func draw_glyph(canvas: CanvasItem, centre: Vector2, radius: float, team: int, alpha := 1.0) -> void:
	var pts := glyph_points(centre, radius, team)
	if pts.size() < 3:
		return
	canvas.draw_colored_polygon(pts, Color(color_of(team), alpha))
	var ring := pts.duplicate()
	ring.append(pts[0])
	canvas.draw_polyline(ring, Color(RIM, RIM.a * alpha), 2.0, true)
