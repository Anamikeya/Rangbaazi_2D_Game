class_name ArenaView
extends Node2D
## Draws a match with placeholder shapes: stage, fighters, hitboxes, labels, the HUD and text
## overlays (port of the drawing in Graybox/GrayboxMatch.cs). It only READS the GameState and
## MatchRules it is given; gameplay lives in client/sim alone. The scene sets the fields below
## and calls queue_redraw() once per tick.
##
## Combat states are drawn from the state alone: hitboxes in red, a dodging fighter faded (with a
## white outline while a hit would be parried), a parried fighter tagged STUN, a fighter frozen by
## a hit it took (knocked back or soaked by armor) shaking, a pending Guts as a gold bar under the
## feet that fills over 3 s, and Guts armor as a gold outline tagged GUTS!. A fighter outside the
## screen or under the stats bar (launched, or recovering from far out: the view is zoomed in, so
## the blast zone is wider and taller than the screen) shows as a marker at the nearest edge,
## pointing at it.

const PLAYER_COLORS := [
	Color("f2a900"), # marigold
	Color("1f9bb0"), # peacock blue
	Color("e0218a"), # rani pink
	Color("7cb518"), # leaf green
]
const PLATFORM_COLOR := Color("3a3a48")
const PLATFORM_EDGE := Color("e8e6f0")
const HITBOX_COLOR := Color(1, 0.2, 0.2, 0.55)
const GOLD := Color("ffd23f")
const OUTLINE := Color(0, 0, 0, 0.75)

## The C# graybox drew 50 px per metre at 1920 x 1080; this fits the arena to the screen under the
## stats bar instead, at least that size, so fighters stay readable on phones.
const VIEW_METRES := Vector2(22, 15)
const GROUND_Y := 0.6 # where y = 0 sits, as a fraction of the height under the stats bar
## With the touch controls on, the arena sits higher and a little smaller, so the move pad and the
## buttons in the bottom corners cover less of the stage edges.
const TOUCH_VIEW_METRES := Vector2(26, 16)
const TOUCH_GROUND_Y := 0.47
const STATUS_Y := 0.18
## "P1 WINS!" / "DRAW" sits at this fraction of the screen height.
const WINNER_Y := 0.34
## Off-screen markers stay this far inside the screen edges, under the stats bar and above the
## touch controls at the bottom.
const MARKER_MARGIN := 60.0
const MARKER_BOTTOM := 190.0
const MARKER_RADIUS := 30.0

## The stats bar along the top: one box per player, left to right, then how many are still in.
## Boxes share the width (less hud_right_inset) and shrink, type and all, as players are added.
const BAR_MARGIN := 20.0
const BOX_GAP := 14.0
const BOX_MAX_WIDTH := 470.0
const BOX_HEIGHT := 108.0 # at full width; scaled with the box
const BOX_MIN_SCALE := 0.5
## The "ALIVE n/m" box, as a fraction of a player box's width.
const ALIVE_WIDTH := 0.42
const BOX_FILL := Color(0.05, 0.045, 0.07, 0.72)
const DIM := Color(1, 1, 1, 0.6)
const DANGER := Color("ff5a4f") # Josh % turns towards this as it climbs
const DANGER_JOSH := 150.0

## The match to draw; null draws just the stage (menu, waiting for an online match).
var state: GameState
var rules: MatchRules = MatchRules.new()
## Per fighter: the HUD name ("P1", "P2 CPU", "P1 YOU") and the tag over its head ("CPU", "YOU"
## or "") unless a combat tag (GUTS!, STUN) takes its place.
var names: PackedStringArray = []
var tags: PackedStringArray = []
## Shown under "P1 WINS!" / "DRAW" when the match is over.
var over_hint := ""
## Connection status over the arena: one big line, then smaller ones.
var status_big := ""
var status_small: PackedStringArray = []
## Dim line at the bottom of the screen.
var debug_text := ""
## True while the touch controls are on screen (see TOUCH_VIEW_METRES).
var touch_layout := false
## Pixels the stats bar leaves free at the right end of the screen (the scene's menu button).
var hud_right_inset := 0.0

## How many times a match (not just the stage) was drawn. For the scene smoke test only.
var match_draws := 0

var _origin := Vector2.ZERO
var _ppm := 50.0 # pixels per metre
var _bar_bottom := 0.0 # the y under the stats bar; the arena is drawn below it
var _box_styles: Array[StyleBoxFlat] = [] # per player colour, built on first use


## The y just below the status lines, for placing a button under them.
func status_bottom() -> float:
	var h := get_viewport_rect().size.y
	return h * STATUS_Y + 30.0 + 40.0 * status_small.size()


func _draw() -> void:
	var font := ThemeDB.fallback_font
	var size := get_viewport_rect().size
	# The arena fits in the strip under the stats bar, so the bar never hides a fighter. The bar's
	# room is kept even before a match starts (2 players), so the stage doesn't jump when it does.
	_bar_bottom = BAR_MARGIN + BOX_HEIGHT * _box_scale(size, state.fighters.size() if state != null else 2)
	var strip := size.y - _bar_bottom
	var metres := TOUCH_VIEW_METRES if touch_layout else VIEW_METRES
	_ppm = maxf(50.0, minf(size.x / metres.x, strip / metres.y))
	_origin = Vector2(size.x * 0.5, _bar_bottom + strip * (TOUCH_GROUND_Y if touch_layout else GROUND_Y))

	draw_rect(_screen_rect(rules.stage.blast_zone), Color(1, 1, 1, 0.08), false, 2.0)
	for platform: FxRect in rules.stage.platforms:
		var r := _screen_rect(platform)
		draw_rect(r, PLATFORM_COLOR)
		draw_line(r.position, r.position + Vector2(r.size.x, 0), PLATFORM_EDGE, 3.0)

	if state != null:
		for i in state.fighters.size():
			var f := state.fighters[i]
			if f.is_active():
				_draw_fighter(font, f, i)
		# Hitboxes after every body, so a fighter drawn later doesn't hide the hit landing on it.
		for f in state.fighters:
			if not f.is_active():
				continue
			var hitbox := Simulation.active_hitbox(f, rules.tuning)
			if hitbox != null:
				draw_rect(_screen_rect(hitbox), HITBOX_COLOR)
		for i in state.fighters.size():
			if state.fighters[i].is_active():
				_draw_offscreen_marker(font, state.fighters[i], i, size)
		_draw_hud(font, size)
		match_draws += 1

	if status_big != "":
		_text(font, status_big, size.x / 2, size.y * STATUS_Y, 46, Color.WHITE)
	for i in status_small.size():
		_text(font, status_small[i], size.x / 2, size.y * STATUS_Y + 50.0 + 40.0 * i, 28, Color(1, 1, 1, 0.85))

	if debug_text != "":
		_text(font, debug_text, size.x / 2, size.y - 18.0, 16, Color(1, 1, 1, 0.5), false)


func _draw_fighter(font: Font, f: Fighter, i: int) -> void:
	var t := rules.tuning
	var k := _ppm / 50.0 # line widths and offsets grow with the arena
	var c: Color = PLAYER_COLORS[i % PLAYER_COLORS.size()]
	if f.action == Fighter.Action.HITSTUN or f.action == Fighter.Action.STUNNED:
		c = c.lightened(0.5)
	if Simulation.is_intangible(f, t):
		c.a = 0.35

	var body := _screen_rect(Simulation.hurtbox(f, t))
	# Shake while frozen by a hit it took (knocked back or soaked by Guts armor); not when frozen
	# because its own attack landed or it parried.
	if f.hit_stop > 0 and f.frozen_by_hit:
		body.position.x += (3.0 if state.frame % 2 == 0 else -3.0) * k
	draw_rect(body, c)
	if Simulation.in_parry_window(f, t):
		draw_rect(body.grow(3.0 * k), Color.WHITE, false, 3.0 * k)
	if f.armor_frames > 0:
		draw_rect(body.grow(5.0 * k), GOLD, false, 4.0 * k)

	var eye := body.position + Vector2(body.size.x * (0.75 if f.facing > 0 else 0.25), body.size.y * 0.2)
	draw_circle(eye, 5.0 * k, Color.BLACK)

	# Pending Guts: a bar under the feet that fills over the 3 s it takes to pay out.
	if f.guts_timer > 0:
		var filled := 1.0 - float(f.guts_timer) / rules.guts_delay_frames
		var bar := Rect2(body.position.x - 6 * k, body.end.y + 6 * k, body.size.x + 12 * k, 6 * k)
		draw_rect(bar, Color(GOLD, 0.3))
		draw_rect(Rect2(bar.position, Vector2(bar.size.x * filled, bar.size.y)), GOLD)

	# Centred over the body, with the tag on its own line, so labels of fighters standing next to
	# each other don't run together.
	var label_y := body.position.y - 10.0
	_text(font, "P%d" % (i + 1), body.get_center().x, label_y, 24, c)
	var tag := tags[i] if i < tags.size() else ""
	var combat_tag := ""
	if f.armor_frames > 0:
		combat_tag = "GUTS!"
	elif f.action == Fighter.Action.STUNNED:
		combat_tag = "STUN"
	if combat_tag != "":
		_text(font, combat_tag, body.get_center().x, label_y - 26.0, 18, GOLD)
	elif tag != "":
		_text(font, tag, body.get_center().x, label_y - 26.0, 18, c)


## A fighter wholly outside the screen: a disc in its colour with its number at the nearest
## point inside the edges, and a pointer towards where it is.
func _draw_offscreen_marker(font: Font, f: Fighter, i: int, size: Vector2) -> void:
	var body := _screen_rect(Simulation.hurtbox(f, rules.tuning))
	# Under the stats bar counts as off screen too.
	if Rect2(0, _bar_bottom, size.x, size.y - _bar_bottom).intersects(body):
		return
	var c: Color = PLAYER_COLORS[i % PLAYER_COLORS.size()]
	var target := body.get_center()
	var inner_min := Vector2(MARKER_MARGIN, maxf(MARKER_MARGIN, _bar_bottom + MARKER_RADIUS + 24.0))
	var inner_max := Vector2(size.x - MARKER_MARGIN, maxf(inner_min.y, size.y - MARKER_BOTTOM))
	var at := target.clamp(inner_min, inner_max)
	var dir := (target - at).normalized()
	if dir == Vector2.ZERO:
		dir = Vector2.UP
	var side := Vector2(-dir.y, dir.x) * 14.0
	var base := at + dir * (MARKER_RADIUS - 2.0)
	draw_colored_polygon(PackedVector2Array([at + dir * (MARKER_RADIUS + 20.0), base + side, base - side]), c)
	draw_circle(at, MARKER_RADIUS, Color(0.05, 0.045, 0.07, 0.85))
	draw_circle(at, MARKER_RADIUS, c, false, 4.0, true)
	_text(font, "P%d" % (i + 1), at.x, at.y + 8.0, 22, c, false)


## The stats bar along the top (see BAR_MARGIN), then "P1 WINS!" / "DRAW" once the match is over.
func _draw_hud(font: Font, size: Vector2) -> void:
	var n := state.fighters.size()
	var k := _box_scale(size, n)
	var box_size := Vector2(BOX_MAX_WIDTH * k, BOX_HEIGHT * k)
	var kills := _kills()
	var alive := 0
	for i in n:
		var f := state.fighters[i]
		if f.action != Fighter.Action.ELIMINATED:
			alive += 1
		var box := Rect2(Vector2(BAR_MARGIN + (box_size.x + BOX_GAP) * i, BAR_MARGIN), box_size)
		_draw_player_box(font, box, k, i, f, kills[i] if i < kills.size() else -1)

	# How many are still in: "ALIVE 2/2".
	var alive_box := Rect2(Vector2(BAR_MARGIN + (box_size.x + BOX_GAP) * n, BAR_MARGIN), Vector2(box_size.x * ALIVE_WIDTH, box_size.y))
	draw_style_box(_box_style(-1), alive_box)
	var mid_x := alive_box.get_center().x
	_text(font, "ALIVE", mid_x, alive_box.position.y + 34.0 * k, int(22 * k), DIM, false)
	_text(font, "%d/%d" % [alive, n], mid_x, alive_box.position.y + 84.0 * k, int(42 * k), Color.WHITE, false)

	if state.match_over:
		var text := "P%d WINS!" % (state.winner_index + 1) if state.winner_index >= 0 else "DRAW"
		_text(font, text, size.x / 2, size.y * WINNER_Y, 84, Color.WHITE)
		if over_hint != "":
			_text(font, over_hint, size.x / 2, size.y * WINNER_Y + 56.0, 28, Color(1, 1, 1, 0.85))


## One player's box: name and Josh % on top; stocks, KOs and Guts underneath. kills < 0: unknown.
func _draw_player_box(font: Font, box: Rect2, k: float, i: int, f: Fighter, kills: int) -> void:
	var c: Color = PLAYER_COLORS[i % PLAYER_COLORS.size()]
	draw_style_box(_box_style(i), box)
	var left := box.position.x + 24.0 * k
	var right := box.end.x - 18.0 * k
	var top_line := box.position.y + 44.0 * k
	var low_line := box.position.y + 90.0 * k

	var who := names[i] if i < names.size() else "P%d" % (i + 1)
	draw_string(font, Vector2(left, top_line), who, HORIZONTAL_ALIGNMENT_LEFT, -1, int(32 * k), c)
	var out := f.action == Fighter.Action.ELIMINATED
	var josh := "OUT" if out else "%d%%" % f.josh
	var josh_color := DIM if out else Color.WHITE.lerp(DANGER, clampf(f.josh / DANGER_JOSH, 0.0, 1.0))
	var josh_size := int(46 * k)
	var josh_width := font.get_string_size(josh, HORIZONTAL_ALIGNMENT_LEFT, -1, josh_size).x
	draw_string(font, Vector2(right - josh_width, top_line + 4.0 * k), josh, HORIZONTAL_ALIGNMENT_LEFT, -1, josh_size, josh_color)

	# Stocks as dots, then KOs, then Guts at the right.
	var x := left + 8.0 * k
	for s in rules.stocks:
		var dot := Vector2(x + 24.0 * k * s, low_line - 9.0 * k)
		if s < f.stocks:
			draw_circle(dot, 8.0 * k, c)
		else:
			draw_circle(dot, 8.0 * k, Color(c, 0.35), false, 2.0 * k, true)
	x += 24.0 * k * rules.stocks + 12.0 * k
	if kills >= 0:
		draw_string(font, Vector2(x, low_line), "KO %d" % kills, HORIZONTAL_ALIGNMENT_LEFT, -1, int(26 * k), Color.WHITE)
	if rules.guts:
		var guts := "GUTS..." if f.guts_timer > 0 else "GUTS used" if f.guts_used else "GUTS"
		var guts_color := DIM if f.guts_used and f.guts_timer == 0 else GOLD
		var guts_size := int(20 * k)
		var guts_width := font.get_string_size(guts, HORIZONTAL_ALIGNMENT_LEFT, -1, guts_size).x
		draw_string(font, Vector2(right - guts_width, low_line), guts, HORIZONTAL_ALIGNMENT_LEFT, -1, guts_size, guts_color)


## How big the stats boxes are (1 = BOX_MAX_WIDTH wide) for n players on this screen: they share
## the bar with the ALIVE box, and shrink as players are added.
func _box_scale(size: Vector2, n: int) -> float:
	var free := size.x - 2.0 * BAR_MARGIN - hud_right_inset - BOX_GAP * n
	var width := free / (maxi(n, 1) + ALIVE_WIDTH)
	return clampf(width / BOX_MAX_WIDTH, BOX_MIN_SCALE, 1.0)


## KOs per player, read from the state: in a 1v1 every stock one player loses is the other's KO.
## With more players the state doesn't record who knocked whom out, so they show as unknown (-1).
func _kills() -> PackedInt32Array:
	var n := state.fighters.size()
	var kills := PackedInt32Array()
	kills.resize(n)
	kills.fill(-1)
	if n == 2:
		for i in 2:
			kills[i] = maxi(0, rules.stocks - maxi(0, state.fighters[1 - i].stocks))
	return kills


## The box behind a player's stats, edged in their colour (-1: the neutral ALIVE box).
func _box_style(i: int) -> StyleBoxFlat:
	if _box_styles.is_empty():
		for p in PLAYER_COLORS.size() + 1:
			var box := StyleBoxFlat.new()
			box.bg_color = BOX_FILL
			box.border_color = Color(1, 1, 1, 0.25) if p == PLAYER_COLORS.size() else (PLAYER_COLORS[p] as Color)
			box.set_border_width_all(3)
			box.border_width_left = 3 if p == PLAYER_COLORS.size() else 10
			box.set_corner_radius_all(14)
			box.anti_aliasing = true
			_box_styles.append(box)
	return _box_styles[PLAYER_COLORS.size() if i < 0 else i % PLAYER_COLORS.size()]


## Text centred on centre_x. Measured rather than given a layout width, because draw_string clips
## text wider than the width it is given. An outline keeps it readable over the fighters.
func _text(font: Font, text: String, centre_x: float, baseline_y: float, font_size: int, color: Color, outline := true) -> void:
	var width := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	var pos := Vector2(centre_x - width / 2, baseline_y)
	if outline:
		draw_string_outline(font, pos, text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, maxi(4, font_size / 8), OUTLINE)
	draw_string(font, pos, text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, color)


## Sim position (Fx, y up) to screen pixels (y down). Floats from here on are for drawing only.
func _screen_point(x: int, y: int) -> Vector2:
	return Vector2(_origin.x + Fx.to_float(x) * _ppm, _origin.y - Fx.to_float(y) * _ppm)


func _screen_rect(r: FxRect) -> Rect2:
	var top_left := _screen_point(r.min_x, r.max_y)
	var bottom_right := _screen_point(r.max_x, r.min_y)
	return Rect2(top_left, bottom_right - top_left)
