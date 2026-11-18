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
	Color("ff7a3d"), # saffron
	Color("8e6cf0"), # lilac
	Color("2ec4a6"), # mint
	Color("e8e05a"), # lemon
	Color("d84a4a"), # brick
	Color("5aa9e6"), # sky
	Color("c779d0"), # orchid
	Color("a0c24a"), # lime
	Color("f29ec8"), # rose
	Color("c9a26b"), # sand
	Color("7fdbff"), # ice
	Color("ffffff"), # white
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
## Rooms of up to 15 (RoomSession): names over heads, a clock and a KO scoreboard instead of the
## stats boxes. time_left_frames is the clock; you_index is the local player's fighter or -1.
var room_mode := false
var time_left_frames := 0
var you_index := -1
## Quick-chat bubbles over fighters: [{fighter, text, alpha}].
var bubbles: Array = []
## Graphics settings (GameSettings): shake on hits, and the red hitboxes.
var shake_enabled := true
var show_hitboxes := true
## Which fighter (one of the 31 art ids) each fighter index is drawn as; empty draws the graybox shapes.
var fighter_characters: PackedStringArray = []

## The art: one FighterPainter per fighter index (rebuilt when the cast changes), and the arena's
## painted layers and platforms when the stage has them (ArtLibrary).
var _painters: Array = []
var _painter_ids := PackedStringArray()
## Logical art pixels per metre. The round fighters are drawn a little bigger than their 1.8 m
## hurtbox (head, hat and hands stick out), so they read well in a room of fifteen.
const ART_PX_PER_M := 72.0
## The arena art's level pixels: 40 per metre, x from 640, the floor's top at y = 650.
const LEVEL_PX_PER_M := 40.0
const LEVEL_CENTRE_X := 640.0
const LEVEL_FLOOR_Y := 650.0

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
	if room_mode:
		_bar_bottom = BAR_MARGIN + 70.0
	var strip := size.y - _bar_bottom
	var metres := TOUCH_VIEW_METRES if touch_layout else VIEW_METRES
	var ground := TOUCH_GROUND_Y if touch_layout else GROUND_Y
	var arena_stage := rules.stage.id != "graybox" and not rules.stage.id.begins_with("test")
	if arena_stage:
		# An arena is 32 m wide and 12 m tall: show all of it, the floor near the bottom.
		metres = Vector2(33, 14.5)
		ground = 0.8 if touch_layout else 0.88
	_ppm = minf(size.x / metres.x, strip / metres.y) if arena_stage else maxf(50.0, minf(size.x / metres.x, strip / metres.y))
	_origin = Vector2(size.x * 0.5, _bar_bottom + strip * ground)

	var arena := ArtLibrary.arena(rules.stage.id) if arena_stage else {}
	if arena.is_empty():
		draw_rect(_screen_rect(rules.stage.blast_zone), Color(1, 1, 1, 0.08), false, 2.0)
	else:
		draw_rect(Rect2(Vector2.ZERO, size), Color(str(arena.get("sky", "#1a1626"))))
		_draw_layers(arena, false)
	for i in rules.stage.platforms.size():
		var platform: FxRect = rules.stage.platforms[i]
		if not arena.is_empty() and _draw_platform_art(arena, i, platform):
			continue
		var r := _screen_rect(platform)
		draw_rect(r, PLATFORM_COLOR)
		draw_line(r.position, r.position + Vector2(r.size.x, 0), PLATFORM_EDGE, 3.0)

	if state != null:
		_draw_props(false)
		for i in state.fighters.size():
			var f := state.fighters[i]
			if f.is_active():
				_draw_fighter(font, f, i)
				_draw_held_weapon(font, f, i)
		_draw_props(true)
		# Hitboxes after every body, so a fighter drawn later doesn't hide the hit landing on it.
		for f in state.fighters:
			if not f.is_active() or not show_hitboxes:
				continue
			var hitbox := Simulation.active_hitbox(f, rules.tuning)
			if hitbox != null:
				draw_rect(_screen_rect(hitbox), HITBOX_COLOR)
		if not arena.is_empty():
			_draw_layers(arena, true)
		_draw_bubbles(font)
		for i in state.fighters.size():
			if state.fighters[i].is_active():
				_draw_offscreen_marker(font, state.fighters[i], i, size)
		if room_mode:
			_draw_room_hud(font, size)
		else:
			_draw_hud(font, size)
		match_draws += 1

	if status_big != "":
		_text(font, status_big, size.x / 2, size.y * STATUS_Y, 46, Color.WHITE)
	for i in status_small.size():
		_text(font, status_small[i], size.x / 2, size.y * STATUS_Y + 50.0 + 40.0 * i, 28, Color(1, 1, 1, 0.85))

	if debug_text != "":
		_text(font, debug_text, size.x / 2, size.y - 18.0, 16, Color(1, 1, 1, 0.5), false)


func _draw_fighter(font: Font, f: Fighter, i: int) -> void:
	var painter := _painter(i)
	if painter != null:
		_draw_fighter_art(font, painter, f, i)
		return
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
	if f.hit_stop > 0 and f.frozen_by_hit and shake_enabled:
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
	if room_mode:
		# Name and Josh over the head (the room has no stats boxes); yours is marked.
		var who := names[i] if i < names.size() else "P%d" % (i + 1)
		_text(font, "%s  %d%%" % [who, f.josh], body.get_center().x, label_y, 22, c)
		if i == you_index:
			_text(font, "YOU", body.get_center().x, label_y - 24.0, 18, Color.WHITE)
		elif f.armor_frames > 0:
			_text(font, "GUTS!", body.get_center().x, label_y - 24.0, 18, GOLD)
		return
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


## How long each weapon is drawn, in metres, by the sim's weapon id.
const WEAPON_LENGTH := [0.0, 0.95, 1.3, 0.6, 1.15, 0.75, 0.55, 0.95]
const CRATE := Color("a0672c")
const CRATE_EDGE := Color("5a3512")
const BULLET := Color("ffe27a")
const ARROW := Color("6b4a2a")


## A weapon sprite with its grip at `at`, `length` metres long, turned by `angle` (radians; positive
## turns the tip down, mirrored when facing left).
func _draw_weapon_sprite(weapon: int, at: Vector2, length: float, angle: float, facing: int, alpha := 1.0) -> void:
	var tex := ArtLibrary.weapon_texture(weapon)
	if tex == null:
		draw_circle(at, length * _ppm * 0.3, Color(GOLD, alpha))
		return
	var k := length * _ppm / tex.get_width()
	var grip := ArtLibrary.weapon_grip(weapon) * Vector2(tex.get_size())
	draw_set_transform(at, angle * facing, Vector2(k * facing, k))
	draw_texture(tex, -grip, Color(1, 1, 1, alpha))
	draw_set_transform_matrix(Transform2D.IDENTITY)


## The weapon in a fighter's hand, swung through its attack, and its ammo under the feet.
func _draw_held_weapon(font: Font, f: Fighter, i: int) -> void:
	if f.weapon == Weapons.NONE:
		return
	var hand := _screen_point(f.pos_x + Fx.ratio(45, 100) * f.facing, f.pos_y + Fx.ONE)
	var angle := -0.5 # held up and forward
	var m := Simulation.current_move(f, rules.tuning)
	if f.weapon == Weapons.BELAN or f.weapon == Weapons.BASEBALL_BAT:
		if m != null:
			var af := float(f.action_frame)
			if af < m.startup:
				angle = lerpf(-0.5, -2.2, af / maxf(1.0, m.startup))       # wind up, back over the shoulder
			elif af < m.startup + m.active:
				angle = lerpf(-2.2, 1.0, (af - m.startup + 1.0) / maxf(1.0, m.active)) # the swing
			else:
				angle = lerpf(1.0, -0.5, (af - m.startup - m.active) / maxf(1.0, m.recovery))
	elif m != null and f.move == MoveData.Id.WEAPON_THROW:
		angle = -1.6
	else:
		angle = 0.0 # guns, the bow, the ball and the bombs point ahead
	_draw_weapon_sprite(f.weapon, hand, WEAPON_LENGTH[f.weapon], angle, f.facing)
	if not room_mode or i == you_index:
		var feet := _screen_point(f.pos_x, f.pos_y)
		var label: String = Weapons.NAMES[f.weapon] if f.weapon == Weapons.BOWLING_BALL else "%s  %d" % [Weapons.NAMES[f.weapon], f.weapon_supply]
		_text(font, label, feet.x, feet.y + 26.0, 18, GOLD)


## Boxes and loose weapons (behind the fighters), or projectiles (in front of them).
func _draw_props(shots: bool) -> void:
	var tick := float(state.frame)
	for p in state.props:
		if p.kind == Weapons.PROP_NONE or p.kind == Weapons.PROP_PENDING_BOX:
			continue
		if (p.kind == Weapons.PROP_SHOT) != shots:
			continue
		var r := _screen_rect(Simulation.prop_box(p))
		var centre := r.get_center()
		var dir := -1 if p.vel_x < 0 else 1
		match p.kind:
			Weapons.PROP_BOX:
				draw_rect(r, CRATE)
				draw_rect(r, CRATE_EDGE, false, maxf(2.0, _ppm * 0.05))
				draw_line(r.position, r.end, CRATE_EDGE, maxf(1.5, _ppm * 0.03))
				draw_line(Vector2(r.end.x, r.position.y), Vector2(r.position.x, r.end.y), CRATE_EDGE, maxf(1.5, _ppm * 0.03))
				_draw_weapon_sprite(p.weapon, centre, 0.55, 0.0, 1)
			Weapons.PROP_ITEM:
				var bob := sin(tick * 0.12) * _ppm * 0.06
				draw_set_transform(Vector2(centre.x, r.end.y), 0.0, Vector2(1.0, 0.3))
				draw_circle(Vector2.ZERO, _ppm * 0.5, Color(GOLD, 0.35))
				draw_set_transform_matrix(Transform2D.IDENTITY)
				_draw_weapon_sprite(p.weapon, centre + Vector2(0, bob), WEAPON_LENGTH[p.weapon] * 0.8, 0.0, 1)
			Weapons.PROP_SHOT:
				match p.shot:
					Weapons.SHOT_ROLL:
						_draw_weapon_sprite(Weapons.BOWLING_BALL, centre, 0.6, Fx.to_float(p.pos_x) / 0.3, 1)
					Weapons.SHOT_BULLET:
						draw_line(centre, centre - Vector2(dir * _ppm * 0.45, 0), BULLET, maxf(2.0, _ppm * 0.06))
					Weapons.SHOT_ARROW:
						var tip := centre + Vector2(dir * _ppm * 0.35, 0)
						draw_line(tip, tip - Vector2(dir * _ppm * 0.75, 0), ARROW, maxf(2.0, _ppm * 0.05))
						draw_colored_polygon(PackedVector2Array([tip + Vector2(dir * _ppm * 0.12, 0),
							tip + Vector2(0, -_ppm * 0.07), tip + Vector2(0, _ppm * 0.07)]), Color("d8d8e0"))
					Weapons.SHOT_BOMB:
						_draw_weapon_sprite(Weapons.CHERRY_BOMBS, centre, 0.45, 0.0, 1)
						if int(tick) % 6 < 3:
							draw_circle(centre + Vector2(0, -_ppm * 0.3), _ppm * 0.08, Color("ffb020"))
					_:
						_draw_weapon_sprite(p.weapon, centre, WEAPON_LENGTH[p.weapon] * 0.9, tick * 0.45 * dir, 1)


## Sim position (Fx, y up) to screen pixels (y down). Floats from here on are for drawing only.
func _screen_point(x: int, y: int) -> Vector2:
	return Vector2(_origin.x + Fx.to_float(x) * _ppm, _origin.y - Fx.to_float(y) * _ppm)


func _screen_rect(r: FxRect) -> Rect2:
	var top_left := _screen_point(r.min_x, r.max_y)
	var bottom_right := _screen_point(r.max_x, r.min_y)
	return Rect2(top_left, bottom_right - top_left)


## The room's HUD: the clock in the middle, the top KO scorers on the left (and you, if you're not
## among them), then the result once the clock runs out.
func _draw_room_hud(font: Font, size: Vector2) -> void:
	var secs := ceili(time_left_frames / 60.0)
	var clock := "%d:%02d" % [secs / 60, secs % 60]
	_text(font, clock, size.x / 2, BAR_MARGIN + 52.0, 54, Color.WHITE if secs > 30 else DANGER)

	var order: Array[int] = []
	for i in state.fighters.size():
		order.append(i)
	order.sort_custom(func(a: int, b: int) -> bool:
		var fa := state.fighters[a]
		var fb := state.fighters[b]
		return fa.kos > fb.kos or (fa.kos == fb.kos and (fa.falls < fb.falls or (fa.falls == fb.falls and a < b))))
	var shown := order.slice(0, 5)
	if you_index >= 0 and not shown.has(you_index):
		shown.append(you_index)
	var y := _bar_bottom + 34.0
	# A dark panel behind the list, so it reads over any arena's sky.
	draw_rect(Rect2(BAR_MARGIN - 10.0, _bar_bottom + 4.0, 330.0, 32.0 * shown.size() + 14.0), Color(0.05, 0.045, 0.07, 0.72))
	for rank in shown.size():
		var i: int = shown[rank]
		var f := state.fighters[i]
		var who := names[i] if i < names.size() else "P%d" % (i + 1)
		var line := "%d. %s   KO %d" % [order.find(i) + 1, who, f.kos]
		var c: Color = PLAYER_COLORS[i % PLAYER_COLORS.size()]
		draw_string_outline(font, Vector2(BAR_MARGIN, y), line, HORIZONTAL_ALIGNMENT_LEFT, -1, 26, 4, OUTLINE)
		draw_string(font, Vector2(BAR_MARGIN, y), line, HORIZONTAL_ALIGNMENT_LEFT, -1, 26, c if i != you_index else Color.WHITE)
		y += 32.0

	if state.match_over:
		var winner := state.winner_index
		var text := "%s WINS!" % (names[winner] if winner >= 0 and winner < names.size() else "P%d" % (winner + 1)) if winner >= 0 else "DRAW"
		_text(font, text, size.x / 2, size.y * WINNER_Y, 84, Color.WHITE)
		if over_hint != "":
			_text(font, over_hint, size.x / 2, size.y * WINNER_Y + 64.0, 40, GOLD)


## Quick-chat lines over the fighters who sent them, fading out.
func _draw_bubbles(font: Font) -> void:
	for b: Dictionary in bubbles:
		var i := int(b.get("fighter", -1))
		if i < 0 or i >= state.fighters.size() or not state.fighters[i].is_active():
			continue
		var body := _screen_rect(Simulation.hurtbox(state.fighters[i], rules.tuning))
		var text := str(b.get("text", ""))
		var alpha := float(b.get("alpha", 1.0))
		var width := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, 26).x
		var box := Rect2(body.get_center().x - width / 2 - 14.0, body.position.y - 110.0, width + 28.0, 42.0)
		draw_rect(box, Color(1, 1, 1, 0.92 * alpha))
		draw_string(font, Vector2(box.position.x + 14.0, box.position.y + 31.0), text, HORIZONTAL_ALIGNMENT_LEFT, -1, 26, Color(0.1, 0.09, 0.12, alpha))


# ---- the art ----

## The painter for fighter i, or null where the cast isn't known (the graybox shapes are drawn).
func _painter(i: int) -> FighterPainter:
	if fighter_characters.is_empty() or i >= fighter_characters.size():
		return null
	if _painter_ids != fighter_characters:
		_painters.clear()
		for id in fighter_characters:
			_painters.append(FighterPainter.new(id))
		_painter_ids = fighter_characters.duplicate()
	var p: FighterPainter = _painters[i]
	return p if p.is_ready() else null


func _draw_fighter_art(font: Font, painter: FighterPainter, f: Fighter, i: int) -> void:
	var t := rules.tuning
	var k := _ppm / 50.0
	painter.observe(f, state.frame)
	var feet := _screen_point(f.pos_x, f.pos_y)
	if f.hit_stop > 0 and f.frozen_by_hit and shake_enabled:
		feet.x += (3.0 if state.frame % 2 == 0 else -3.0) * k
	var c: Color = PLAYER_COLORS[i % PLAYER_COLORS.size()]
	var px := _ppm / ART_PX_PER_M
	# A coloured disc under the feet says who is who in a crowd of fifteen.
	draw_set_transform(feet, 0.0, Vector2(1.0, 0.28))
	draw_circle(Vector2.ZERO, 34.0 * px * 2.2, Color(c, 0.55))
	draw_set_transform_matrix(Transform2D.IDENTITY)
	if f.armor_frames > 0:
		draw_circle(feet + Vector2(0, -90.0 * px), 110.0 * px, Color(GOLD, 0.35))
	var picked := painter.pick(f, state.frame)
	var alpha := 0.45 if Simulation.is_intangible(f, t) else 1.0
	painter.draw(self, feet, px, f.facing, picked[0], picked[1], alpha)

	var label_y := feet.y - 185.0 * px
	var who := names[i] if i < names.size() else "P%d" % (i + 1)
	_text(font, "%s  %d%%" % [who, f.josh] if room_mode else "P%d" % (i + 1), feet.x, label_y, 22, c)
	var tag := "YOU" if i == you_index else (tags[i] if i < tags.size() else "")
	if f.armor_frames > 0:
		tag = "GUTS!"
	elif f.action == Fighter.Action.STUNNED:
		tag = "STUN"
	if tag != "":
		_text(font, tag, feet.x, label_y - 24.0, 18, Color.WHITE if tag == "YOU" else GOLD)
	if f.guts_timer > 0:
		var filled := 1.0 - float(f.guts_timer) / rules.guts_delay_frames
		var bar := Rect2(feet.x - 40.0 * k, feet.y + 8.0 * k, 80.0 * k, 6.0 * k)
		draw_rect(bar, Color(GOLD, 0.3))
		draw_rect(Rect2(bar.position, Vector2(bar.size.x * filled, bar.size.y)), GOLD)


## Level pixels (the arena art's 1280 x 720 frame) to screen pixels.
func _level_point(x: float, y: float) -> Vector2:
	return Vector2(_origin.x + (x - LEVEL_CENTRE_X) / LEVEL_PX_PER_M * _ppm, _origin.y - (LEVEL_FLOOR_Y - y) / LEVEL_PX_PER_M * _ppm)


## The painted layers behind the fighters (z < 0), or in front of them (z > 0).
func _draw_layers(arena: Dictionary, front: bool) -> void:
	var stage_id := rules.stage.id
	for layer: Dictionary in arena.get("layers", []):
		if (float(layer.get("z", 0)) > 0.0) != front:
			continue
		var tex := ArtLibrary.arena_texture(stage_id, str(layer["file"]))
		if tex == null:
			continue
		var x := float(layer.get("x", 0))
		var y := float(layer.get("y", 0))
		var top_left := _level_point(x, y)
		var bottom_right := _level_point(x + float(layer.get("w", 1280)), y + float(layer.get("h", 720)))
		draw_texture_rect(tex, Rect2(top_left, bottom_right - top_left), false)


## One platform in its arena's art: left cap, tiled middle, right cap, the art hanging below the
## collision top. False if the arena has no pieces for it (the plain shape is drawn instead).
func _draw_platform_art(arena: Dictionary, index: int, platform: FxRect) -> bool:
	var kinds: Array = arena.get("platforms", [])
	if index >= kinds.size():
		return false
	var piece: Dictionary = arena.get("pieces", {}).get(kinds[index], {})
	if piece.is_empty():
		return false
	var stage_id := rules.stage.id
	var left := ArtLibrary.arena_texture(stage_id, str(piece.get("left", "")))
	var middle := ArtLibrary.arena_texture(stage_id, str(piece.get("middle", "")))
	var right := ArtLibrary.arena_texture(stage_id, str(piece.get("right", "")))
	if left == null or middle == null or right == null:
		return false
	var r := _screen_rect(platform)
	var scale := _ppm / LEVEL_PX_PER_M # screen pixels per level pixel
	var cap := float(piece.get("cap", 20)) * scale
	var tile := float(piece.get("tile", 72)) * scale
	var h := maxf(r.size.y, cap * left.get_height() / maxf(1.0, left.get_width()))
	draw_texture_rect(left, Rect2(r.position.x, r.position.y, cap, h), false)
	draw_texture_rect(right, Rect2(r.end.x - cap, r.position.y, cap, h), false)
	var x := r.position.x + cap
	var end := r.end.x - cap
	while x < end - 0.5:
		var w := minf(tile, end - x)
		draw_texture_rect_region(middle, Rect2(x, r.position.y, w, h), Rect2(0, 0, middle.get_width() * w / tile, middle.get_height()))
		x += w
	return true
