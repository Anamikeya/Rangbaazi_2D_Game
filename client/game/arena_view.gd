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
	Color("997a54"), # cocoa (was sand, too close to marigold and rose)
	Color("f224f2"), # magenta (was ice, too close to sky: CIE76 23.4, now at least 48.0 from the rest)
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
const BOX_FILL := Color(0.05, 0.045, 0.07, 0.88)
## Under touch the stats boxes are this much bigger, and their type TOUCH_TEXT bigger, never under
## MIN_TEXT px (rooms v1, fix 16).
const TOUCH_BOX := 1.4
const TOUCH_TEXT := 1.5
const MIN_TEXT := 24
const DIM := Color(1, 1, 1, 0.6)

## The room HUD strip (rooms v1, 3.1): the clock plate, one total plate per team and one chip per
## fighter, in the band along the top.
const CLOCK_SIZE := Vector2(170, 62)
const CHIP_H := 58.0
const CHIP_MIN := 54.0   # narrower than this and the strip collapses (see hud_strip)
const CHIP_MAX := 150.0
const YOU_MIN := 170.0   # your own chip is 1.5 times as wide, and at least this
const PLATE_W := 84.0
const STRIP_GAP := 4.0
const GROUP_GAP := 12.0
const HEAD_PX := 40.0
## Chips narrower than this put their KOs at the foot, beside the Josh bar, not on the face.
const NARROW_CHIP := 80.0
const PLUS_ONE_FRAMES := 45
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
## True on a touch device even when the layout has no controls (a watcher on a phone): text in the
## arena (tags, labels) keeps the touch sizes. touch_layout implies it.
var touch_text := false
## Pixels the stats bar leaves free at the right end of the screen (the scene's menu button, and in
## a room Chat too).
var hud_right_inset := 0.0
## The y under the scene's top-right buttons: the KO feed starts here, and markers and effects stay
## below it (rooms v1, fix 1 and fix 30).
var hud_top_inset := 0.0
## Auto play is on: an AUTO badge on your HUD chip (rooms) or player box.
var auto_on := false
## While the result panel is up: no name tags (they showed through the panel; the winners' poses
## and rings carry on).
var hide_tags := false
## Whether "P1 WINS!" is drawn over the arena when a match is over. Off in rooms and practice,
## where the result panel (ResultScreen) says it.
var show_banner := true
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

## Outside rooms, the camera follows the fight on arena stages (zooming in on it, out as fighters
## spread), the way party brawlers do; off, and always in a room, the whole level is shown
## (rooms v1, 2.4: see fit_ppm).
var dynamic_camera := true
const CAMERA_MAX_ZOOM := 1.4   # times the full-width view
const CAMERA_MIN_WIDTH := 17.0 # metres across, at the closest
const CAMERA_MIN_HEIGHT := 8.5
const CAMERA_NEAR := 10.0      # in a crowd, the view frames you and fighters this close (metres)
const ARENA_HALF_WIDTH := 16.0 # the normal level's half width (the big one is 1.5 times it: _half_w)
const ART_BOTTOM := -1.5       # metres: the lowest the follow camera goes (the art ends 1.75 m under the floor)
const ART_TOP := 16.0          # and the highest (the art ends 16.25 m above it)
## The whole-level view: from FIT_BOTTOM (0, the floor, under touch) to FIT_TOP (the top platform at
## 10.5 m, a fighter and its tag) fits between the HUD band and the screen's bottom (or, under touch,
## the line above the move pad and Kick / Jump: TouchControls.floor_clear_y). A fighter's head above
## the view's top pulls it back, to PULL_BACK of the fit at most; it never zooms in past the fit.
const FIT_TOP := 14.0
const FIT_BOTTOM := -1.5
## Metres kept beside each end of the level in the whole-level view, so a fighter on an outer ledge,
## or knocked just past the edge and recovering, is seen whole (final round). The mirrored, dimmed
## out-zone art fills it.
const EDGE_MARGIN := 1.8
## How far past a side the follow camera may look once a match is over (_move_camera).
const RESULT_REACH := 1.5
const PULL_BACK := 0.85
const HEAD_ABOVE := 2.8        # metres from a fighter's feet to the top of its head and tag
## The art's level frame: 1280 x 720 level pixels; the big level is (k - 1) * 1280 wider (_level_k).
const LEVEL_WIDTH := 1280.0
const LEVEL_HEIGHT := 720.0
## Past the painted sides: the "out" zone, dimmed into this over OUT_FADE metres.
const OUT_DIM := Color(0.05, 0.045, 0.07, 0.62)
const OUT_FADE := 1.2
## Above the painting (_draw_beyond_art): its top fades into the sky over SKY_FADE metres, in
## SKY_FADE_STEPS eased bands, and the sky deepens over SKY_GRADIENT metres above it.
const SKY_FADE := 1.5
## The KO feed's type, and the smallest it shrinks to between the touch buttons (fit_feed_line).
const FEED_FONT := 24
const FEED_MIN_FONT := 20
const SKY_FADE_STEPS := 6
const SKY_GRADIENT := 9.0
## Name tags (rooms v1, 2.6): a dark pill edged in the fighter's colour.
const TAG_FILL := Color(0.05, 0.045, 0.07, 0.88)
const TAG_EDGE_MARGIN := 6.0
## The held weapon is never drawn shorter than this, however far out the camera is.
const HELD_MIN_PX := 40.0

var _origin := Vector2.ZERO
var _ppm := 50.0 # pixels per metre
# The camera: the point (metres) at the middle of the view, the zoom, and the last frame drawn.
var _cam := Vector2.ZERO
var _cam_ppm := 0.0
var _cam_frame := -2
var _shake := 0.0 # pixels, fading
# Effects (sparks, dust, KO bursts, pop-up words): presentation only, made by watching the state.
# Each: {kind, x, y (metres), age, life, who (art id), text, color, size}.
var _fx: Array = []
# Per fighter, what the last frame showed: [josh, grounded, falls, active, x, y, action, weapon, kos].
var _seen: Array = []
var _seen_frame := -2
# Where the shots that burst (bombs, mines, missiles, black holes) were last frame: [x, y, big].
var _bursting: Array = []
# The KO feed: [{text, color, age}], newest last.
var _feed: Array = []
var _bar_bottom := 0.0 # the y under the stats bar; the arena is drawn below it
var _box_styles := {} # colour (html) -> StyleBoxFlat, built on first use
# Name tags queued while the fighters are drawn ({i, x, y, word, you}), drawn by _draw_tags; and
# the top of each fighter's placed tag this frame (fighter -> y), for its chat bubble.
var _tags: Array = []
var _tag_tops := {}
# One head texture per fighter, built when the cast changes (rooms v1, 3.2): the HUD chips and the
# off-screen markers draw it with one draw_texture_rect.
var _heads: Array = []
var _heads_for := PackedStringArray()
# Each team's KO total last frame and the frame it last rose, for the "+1" on its plate.
var _team_kos := {}
var _team_rose := {}
var _team_frame := -1 # the match frame they were last updated at: a new match starts them afresh
var _draws := 0       # every match draw, so the "+1" runs for its time even once the state stops
# Draws since the match ended, and the match frame it ended at: the winners keep celebrating (and
# the camera keeps easing) while the state stands still (anim_frame).
var _over_draws := 0
var _over_from := -1
## The frame fighters are drawn (and the camera eased) at: the match frame, and while the result is
## up one more every draw, but never two a tick. A practice match stands still once it is over, so
## the draws carry the celebration on; a duel's state goes on counting behind its result, and adding
## the draws to it played the winners' poses at double speed (final round).
static func anim_frame(frame: int, over_from: int, over_draws: int) -> int:
	if over_draws <= 0:
		return frame
	return maxi(frame, over_from + over_draws - 1)


func _anim_frame() -> int:
	return anim_frame(state.frame, _over_from, _over_draws)


## How long the last _draw took (microseconds), for FrameMeter's log line.
var last_draw_usec := 0

## Motion (rooms v1, polish 1): what eases in does so over EASE_FRAMES (0.15 s at 60 a second). At a
## match's start the HUD strip's plates and chips drop in one after another, STRIP_STAGGER frames
## apart; a team's plate bumps when its total rises; a KO feed line slides in from the right; the
## status plate fades in when its words change.
const EASE_FRAMES := 9
const STRIP_STAGGER := 1.0
const PLATE_BUMP := 0.14
## Spawn-in (polish 3): a SPAWN_FRAMES (0.3 s) flash ring in the fighter's colour on spawn and
## respawn, at least SPAWN_MIN_PX across however far out the camera is.
const SPAWN_FRAMES := 18
const SPAWN_MIN_PX := 64.0
var _status_key := ""
var _status_msec := 0


## The y just below the status lines, for placing a button under them.
func status_bottom() -> float:
	var h := get_viewport_rect().size.y
	return h * STATUS_Y + 30.0 + 40.0 * status_small.size()


## The plate behind the status lines: as wide as the widest line, padded, centred.
func status_plate(font: Font, size: Vector2) -> Rect2:
	var w := font.get_string_size(status_big, HORIZONTAL_ALIGNMENT_LEFT, -1, 46).x if status_big != "" else 0.0
	for line in status_small:
		w = maxf(w, font.get_string_size(line, HORIZONTAL_ALIGNMENT_LEFT, -1, 28).x)
	var top := size.y * STATUS_Y - (46.0 if status_big != "" else -14.0) - 18.0
	var bottom := size.y * STATUS_Y + (50.0 + 40.0 * status_small.size() - 18.0 if not status_small.is_empty() else 14.0) + 18.0
	w = minf(w + 72.0, size.x - 40.0)
	return Rect2((size.x - w) / 2.0, top, w, bottom - top)


## Where player i's stats box is in the bar along the top (not in a room, which has no boxes).
func player_box(i: int) -> Rect2:
	var k := _box_scale(get_viewport_rect().size, state.fighters.size() if state != null else 2) * _box_unit()
	var box_size := Vector2(BOX_MAX_WIDTH * k, BOX_HEIGHT * k)
	return Rect2(Vector2(BAR_MARGIN + (box_size.x + BOX_GAP) * i, BAR_MARGIN), box_size)


## How much bigger the stats boxes are drawn: TOUCH_BOX under touch, else 1.
func _box_unit() -> float:
	return TOUCH_BOX if touch_layout else 1.0


## Places the camera for a screen of this size (logical pixels): the HUD band, the zoom and where
## the world's origin is. _draw calls it every frame; the scene smoke test calls it directly to check
## that a room always shows the whole level. Easing is by state.frame, so calling it twice for one
## frame changes nothing.
func place_camera(size: Vector2) -> void:
	# The arena fits in the strip under the stats bar, so the bar never hides a fighter. The bar's
	# room is kept even before a match starts (2 players), so the stage doesn't jump when it does.
	_bar_bottom = BAR_MARGIN + BOX_HEIGHT * _box_scale(size, state.fighters.size() if state != null else 2) * _box_unit()
	if room_mode:
		_bar_bottom = BAR_MARGIN + 70.0
	var strip := size.y - _bar_bottom
	var metres := TOUCH_VIEW_METRES if touch_layout else VIEW_METRES
	var ground := TOUCH_GROUND_Y if touch_layout else GROUND_Y
	_ppm = maxf(50.0, minf(size.x / metres.x, strip / metres.y))
	_origin = Vector2(size.x * 0.5, _bar_bottom + strip * ground)
	if not _arena_stage():
		return
	if room_mode or not dynamic_camera:
		_fit_camera(size)
	else:
		_move_camera(size, dynamic_camera)


## The camera as it is now, for tests: [pixels per metre, the screen point of the world's origin, the
## y under the HUD band]; and a stage rectangle's place on the screen.
func camera() -> Array:
	return [_ppm, _origin, _bar_bottom]


func screen_rect_of(r: FxRect) -> Rect2:
	return _screen_rect(r)


func _arena_stage() -> bool:
	return rules.stage.id != "graybox" and not rules.stage.id.begins_with("test")


func _draw() -> void:
	var drawing_from := Time.get_ticks_usec()
	var font := ThemeDB.fallback_font
	var size := get_viewport_rect().size
	var arena_stage := _arena_stage()
	place_camera(size)
	if state != null:
		_watch_fx()
		if state.match_over:
			if _over_draws == 0:
				_over_from = state.frame
			_over_draws += 1
		else:
			_over_draws = 0
		_draws += 1
		if state.frame < _team_frame:
			_team_kos.clear()
			_team_rose.clear()
		_team_frame = state.frame
	if _shake > 0.2 and shake_enabled:
		if arena_stage and (room_mode or not dynamic_camera):
			# The whole level stays on screen: the jolt is upwards only (there is sky to spare above
			# the top platform), never sideways or down onto the thumbs.
			_origin.y -= absf(cos(_shake * 5.1)) * _shake
		else:
			_origin += Vector2(sin(_shake * 7.3), cos(_shake * 5.1)) * _shake
	_shake *= 0.86
	_tags.clear()

	var arena := ArtLibrary.arena(rules.stage.id) if arena_stage else {}
	if arena.is_empty():
		draw_rect(_screen_rect(rules.stage.blast_zone), Color(1, 1, 1, 0.08), false, 2.0)
	else:
		var sky := Color(str(arena.get("sky", "#1a1626")))
		var sky_top := Color(str(arena.get("skyTop", arena.get("sky", "#1a1626"))))
		draw_rect(Rect2(Vector2.ZERO, size), sky_top)
		_draw_layers(arena, false)
		_draw_beyond_art(arena, sky, sky_top, size)
	for i in rules.stage.platforms.size():
		var platform: FxRect = rules.stage.platforms[i]
		if not arena.is_empty() and _draw_platform_art(arena, i, platform):
			continue
		var r := _screen_rect(platform)
		draw_rect(r, PLATFORM_COLOR)
		draw_line(r.position, r.position + Vector2(r.size.x, 0), PLATFORM_EDGE, 3.0)
	_draw_hazards(font, arena)

	if state != null:
		_draw_props(false)
		for i in state.fighters.size():
			var f := state.fighters[i]
			if not f.is_active():
				continue
			# Hidden in a cloud: not drawn, except faintly for the viewer's own side.
			var seen := _seen_alpha(i)
			if seen <= 0.0:
				continue
			_draw_fighter(font, f, i, seen)
			_draw_held_weapon(font, f, i, seen)
			if seen >= 1.0:
				_draw_statuses(f, i)
		_draw_props(true)
		_draw_blasts()
		_draw_sprays()
		_draw_fx(font)
		# Hitboxes after every body, so a fighter drawn later doesn't hide the hit landing on it.
		for f in state.fighters:
			if not f.is_active() or not show_hitboxes:
				continue
			var hitbox := Simulation.active_hitbox(f, rules.tuning)
			if hitbox != null:
				draw_rect(_screen_rect(hitbox), HITBOX_COLOR)
		if not arena.is_empty():
			_draw_layers(arena, true)
		_draw_tags(font, size)
		_draw_bubbles(font)
		_draw_offscreen_markers(size)
		_draw_offscreen_crates(size)
		if room_mode:
			_draw_room_hud(font, size)
		else:
			_draw_hud(font, size)
		_draw_feed(font, size)
		_draw_start_banner(font, size)
		match_draws += 1

	var status_fade := _status_ease()
	var status_drop := (1.0 - status_fade) * -10.0
	if status_big != "" or not status_small.is_empty():
		# A dark rounded plate behind the lines, so they read over any sky (rooms v1, fix 23); it fades
		# in when the words change (polish 1).
		var plate := status_plate(font, size)
		plate.position.y += status_drop
		var box := StyleBoxFlat.new()
		box.bg_color = Color(UiTheme.PANEL, UiTheme.PANEL.a * status_fade)
		box.border_color = Color(UiTheme.ACCENT, 0.5 * status_fade)
		box.set_border_width_all(2)
		box.set_corner_radius_all(18)
		draw_style_box(box, plate)
	if status_big != "":
		_text(font, status_big, size.x / 2, size.y * STATUS_Y + status_drop, 46, Color(1, 1, 1, status_fade))
	for i in status_small.size():
		_text(font, status_small[i], size.x / 2, size.y * STATUS_Y + 50.0 + 40.0 * i + status_drop, 28, Color(1, 1, 1, 0.85 * status_fade))

	if debug_text != "":
		_text(font, debug_text, size.x / 2, size.y - 18.0, 16, Color(1, 1, 1, 0.5), false)
	last_draw_usec = Time.get_ticks_usec() - drawing_from


## Fighter i's colour: its team's (TeamStyle) in a team match, else its own from PLAYER_COLORS.
func _color_of(i: int) -> Color:
	if rules.has_teams():
		return TeamStyle.color_of(rules.team_of(i))
	return PLAYER_COLORS[posmod(i, PLAYER_COLORS.size())]


## How a fighter is drawn on this screen: 1 seen, 0 hidden in a cloud (Simulation.is_hidden), or
## faintly when it is the viewer's own fighter or a teammate, who always see each other.
func _seen_alpha(i: int) -> float:
	if not Simulation.is_hidden(state, rules, i):
		return 1.0
	return HIDDEN_OWN_ALPHA if _own_side(i) else 0.0


## Whether fighter i is on the side of whoever watches this screen: you (a room's you_index, or the
## fighter tagged YOU), and your teammates. With nobody tagged (two people at one keyboard) every
## fighter that isn't a CPU is someone's own.
func _own_side(i: int) -> bool:
	var mine := you_index
	if mine < 0:
		for k in tags.size():
			if tags[k] == "YOU" or tags[k] == "AUTO":
				mine = k
	if mine >= 0:
		return Simulation.allies(rules, mine, i)
	return i >= tags.size() or tags[i] != "CPU"


func _draw_fighter(font: Font, f: Fighter, i: int, seen := 1.0) -> void:
	var painter := _painter(i)
	if painter != null:
		_draw_fighter_art(font, painter, f, i, seen)
		return
	var t := rules.tuning
	var k := _ppm / 50.0 # line widths and offsets grow with the arena
	var c: Color = _color_of(i)
	if f.action == Fighter.Action.HITSTUN or f.action == Fighter.Action.STUNNED:
		c = c.lightened(0.5)
	if Simulation.is_intangible(f, t):
		c.a = 0.35
	c.a *= seen

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
	_queue_tag(f, i, body.get_center().x, label_y)


## The box markers keep inside: below the HUD band and the scene's top-right buttons, inside the
## sides, and under touch above the thumbs (TouchControls.floor_clear_y), so no marker sits under a
## button. `room` is how far from the edges (a marker's radius, a crate's half).
func _marker_bounds(size: Vector2, room: float) -> Rect2:
	var top := maxf(_bar_bottom, hud_top_inset) + room + 24.0
	var bottom := (TouchControls.floor_clear_y(size) if touch_layout else size.y - MARKER_MARGIN + 10.0) - room - 4.0
	var inner_min := Vector2(MARKER_MARGIN, top)
	var inner_max := Vector2(size.x - MARKER_MARGIN, maxf(top, bottom))
	return Rect2(inner_min, inner_max - inner_min)


## Moves a marker at `at` (inside `bounds`) along its edge until it is 2 radii from every marker
## already placed: along the top and bottom sideways, along the sides up and down.
static func _spread_marker(at: Vector2, placed: Array[Vector2], bounds: Rect2, radius: float) -> Vector2:
	var along := Vector2(1, 0) if is_equal_approx(at.y, bounds.position.y) or is_equal_approx(at.y, bounds.end.y) else Vector2(0, 1)
	var step := 2.0 * radius + 4.0
	for tries in 16:
		var clash := false
		for q in placed:
			if q.distance_to(at) < 2.0 * radius:
				clash = true
				break
		if not clash:
			return at
		# Try the next free place on either side, nearest first.
		var k := (tries / 2 + 1) * (1 if tries % 2 == 0 else -1)
		var moved := (at + along * step * k).clamp(bounds.position, bounds.end)
		var free := true
		for q in placed:
			if q.distance_to(moved) < 2.0 * radius:
				free = false
				break
		if free:
			return moved
	return at


## Fighters wholly outside the view (launched above the top, or past the sides): each one's cached
## head in a disc with a rim in its colour (and its team's symbol), at the nearest point inside the
## edges, with a pointer towards where it is. Markers that would touch spread along their edge.
func _draw_offscreen_markers(size: Vector2) -> void:
	var view := Rect2(0, maxf(_bar_bottom, 0.0), size.x, size.y - _bar_bottom)
	var bounds := _marker_bounds(size, MARKER_RADIUS)
	var placed: Array[Vector2] = []
	var order: Array[int] = []
	if you_index >= 0 and you_index < state.fighters.size():
		order.append(you_index) # yours is placed first, so it never moves
	for i in state.fighters.size():
		if i != you_index:
			order.append(i)
	for i in order:
		var f := state.fighters[i]
		if not f.is_active() or _seen_alpha(i) <= 0.0:
			continue
		var body := _screen_rect(Simulation.hurtbox(f, rules.tuning))
		if view.intersects(body): # under the HUD band counts as off screen too
			continue
		var target := body.get_center()
		var at := _spread_marker(target.clamp(bounds.position, bounds.end), placed, bounds, MARKER_RADIUS)
		placed.append(at)
		_draw_marker(at, target, i)


func _draw_marker(at: Vector2, target: Vector2, i: int) -> void:
	var c: Color = _color_of(i)
	var dir := (target - at).normalized()
	if dir == Vector2.ZERO:
		dir = Vector2.UP
	var side := Vector2(-dir.y, dir.x) * 14.0
	var base := at + dir * (MARKER_RADIUS - 2.0)
	draw_colored_polygon(PackedVector2Array([at + dir * (MARKER_RADIUS + 20.0), base + side, base - side]), c)
	draw_circle(at, MARKER_RADIUS, Color(0.05, 0.045, 0.07, 0.9))
	_draw_head(i, Rect2(at - Vector2(MARKER_RADIUS, MARKER_RADIUS) * 0.82, Vector2(MARKER_RADIUS, MARKER_RADIUS) * 1.64))
	draw_circle(at, MARKER_RADIUS, c, false, 3.0, true)
	if rules.has_teams() and rules.team_of(i) > 0:
		TeamStyle.draw_glyph(self, at + Vector2(MARKER_RADIUS * 0.72, MARKER_RADIUS * 0.72), 9.0, rules.team_of(i))
	if _is_you(i):
		draw_circle(at, MARKER_RADIUS + 3.0, Color.WHITE, false, 2.0, true)


# ---- the head cache (rooms v1, 3.2) ----

## Fighter i's head picture: the head part of its rig, loaded once per cast. Null without art.
func _head_texture(i: int) -> Texture2D:
	if _heads_for != fighter_characters:
		_heads.clear()
		for id in fighter_characters:
			_heads.append(head_texture_for(id))
		_heads_for = fighter_characters.duplicate()
	return _heads[i] if i >= 0 and i < _heads.size() else null


static func head_texture_for(id: String) -> Texture2D:
	var rig := ArtLibrary.character(id)
	if rig.is_empty():
		rig = ArtLibrary.character("shahn")
		id = "shahn"
	var head: Dictionary = rig.get("parts", {}).get("head", {})
	var textures: Dictionary = head.get("textures", {})
	var file: Variant = textures.get(str(head.get("texture", "neutral")), null)
	if file == null and not textures.is_empty():
		file = textures.values()[0]
	return ArtLibrary.character_texture(id, str(file)) if file != null else null


## Fighter i's head fitted into r (its shape kept); a disc in its colour where there is no art.
func _draw_head(i: int, r: Rect2, alpha := 1.0) -> void:
	var tex := _head_texture(i)
	if tex == null:
		draw_circle(r.get_center(), minf(r.size.x, r.size.y) * 0.4, Color(_color_of(i), alpha))
		return
	var k := minf(r.size.x / tex.get_width(), r.size.y / tex.get_height())
	var s := Vector2(tex.get_width(), tex.get_height()) * k
	draw_texture_rect(tex, Rect2(r.get_center() - s / 2.0, s), false, Color(1, 1, 1, alpha))


## Weapon crates and loose weapons outside the view (the camera is zoomed in on the fight): a small
## crate with the weapon on it at the nearest edge, and a pointer towards it. Kept below the HUD and
## the top-right buttons, and under touch above the thumbs.
func _draw_offscreen_crates(size: Vector2) -> void:
	var view := Rect2(0, _bar_bottom, size.x, size.y - _bar_bottom)
	var bounds := _marker_bounds(size, 20.0)
	var inner_min := bounds.position
	var inner_max := bounds.end
	for p in state.props:
		var decoy := p.kind == Weapons.PROP_SHOT and p.shot == Weapons.SHOT_DECOY
		if p.kind != Weapons.PROP_BOX and p.kind != Weapons.PROP_ITEM and not decoy:
			continue
		var r := _screen_rect(Simulation.prop_box(p))
		if view.intersects(r):
			continue
		var target := r.get_center()
		var at := target.clamp(inner_min, inner_max)
		var dir := (target - at).normalized()
		if dir == Vector2.ZERO:
			dir = Vector2.UP
		var side := Vector2(-dir.y, dir.x) * 10.0
		var base := at + dir * 22.0
		draw_colored_polygon(PackedVector2Array([at + dir * 36.0, base + side, base - side]), GOLD)
		var box := Rect2(at - Vector2(20, 20), Vector2(40, 40))
		draw_rect(box, CRATE)
		draw_rect(box, CRATE_EDGE, false, 3.0)
		var tex := ArtLibrary.weapon_texture(p.supply if decoy else p.weapon) # a decoy shows what it pretends to hold
		if tex != null:
			var w := 34.0
			var h := minf(34.0, w * tex.get_height() / maxf(1.0, tex.get_width()))
			draw_texture_rect(tex, Rect2(at - Vector2(w, h) / 2.0, Vector2(w, h)), false)


## The stats bar along the top (see BAR_MARGIN): a box per player, then with more than two the
## ALIVE box; and "P1 WINS!" / "DRAW" once the match is over where show_banner asks for it (a duel:
## practice and rooms have the result panel).
func _draw_hud(font: Font, size: Vector2) -> void:
	var n := state.fighters.size()
	var k := _box_scale(size, n) * _box_unit()
	var box_size := Vector2(BOX_MAX_WIDTH * k, BOX_HEIGHT * k)
	var kills := _kills()
	var alive := 0
	for i in n:
		var f := state.fighters[i]
		if f.action != Fighter.Action.ELIMINATED:
			alive += 1
		var box := Rect2(Vector2(BAR_MARGIN + (box_size.x + BOX_GAP) * i, BAR_MARGIN), box_size)
		_draw_player_box(font, box, k, i, f, kills[i] if i < kills.size() else -1)

	# How many are still in: "ALIVE 3/4". Not in a 1v1, where it says nothing.
	if n > 2:
		var alive_box := Rect2(Vector2(BAR_MARGIN + (box_size.x + BOX_GAP) * n, BAR_MARGIN), Vector2(box_size.x * ALIVE_WIDTH, box_size.y))
		draw_style_box(_box_style(-1), alive_box)
		var mid_x := alive_box.get_center().x
		_text(font, "ALIVE", mid_x, alive_box.position.y + 34.0 * k, _hud_font(22, k), DIM, false)
		_text(font, "%d/%d" % [alive, n], mid_x, alive_box.position.y + 84.0 * k, _hud_font(42, k), Color.WHITE, false)

	if state.match_over and show_banner:
		var w := state.winner_index
		var text := "%s WINS!" % (names[w] if w < names.size() else "P%d" % (w + 1)) if w >= 0 else "DRAW"
		var e := ease_in(_over_draws / float(EASE_FRAMES)) # eases in (polish 1)
		var c: Color = _color_of(w) if w >= 0 else Color.WHITE
		_banner(font, text, size, size.y * WINNER_Y, int(84 * (0.8 + 0.2 * e)), Color(c, e), e)
		if over_hint != "":
			_text(font, over_hint, size.x / 2, size.y * WINNER_Y + 62.0, 28, Color(1, 1, 1, 0.9 * e))


## A stats-box font size: `base` at full size, scaled with the box (k includes TOUCH_BOX); under
## touch TOUCH_TEXT times that (so the type grows more than the box), and never under MIN_TEXT.
func _hud_font(base: float, k: float) -> int:
	var text := k / _box_unit() * (TOUCH_TEXT if touch_layout else 1.0)
	return maxi(MIN_TEXT if touch_layout else 12, int(base * text))


## One player's box: face, name (and an AUTO badge) and Josh % on top; stocks, KOs and the weapon
## in hand (its icon, name and ammo) or Guts underneath. kills < 0: unknown.
func _draw_player_box(font: Font, box: Rect2, k: float, i: int, f: Fighter, kills: int) -> void:
	var c: Color = _color_of(i)
	draw_style_box(_box_style(i), box)
	var left := box.position.x + 24.0 * k
	var right := box.end.x - 18.0 * k
	# The fighter's face at the left of the box, so you know which box is whose at a glance.
	var painter := _painter(i)
	if painter != null:
		var out_now := f.action == Fighter.Action.ELIMINATED
		painter.draw(self, Vector2(box.position.x + 62.0 * k, box.end.y - 12.0 * k), 0.36 * k, 1, "idle", 30, 0.35 if out_now else 1.0)
		left = box.position.x + 118.0 * k
	var top_line := box.position.y + 46.0 * k
	var low_line := box.position.y + 92.0 * k

	var out := f.action == Fighter.Action.ELIMINATED
	var josh := "OUT" if out else "%d%%" % f.josh
	var josh_color := DIM if out else Color.WHITE.lerp(DANGER, clampf(f.josh / DANGER_JOSH, 0.0, 1.0))
	var josh_size := _hud_font(46, k)
	var josh_width := font.get_string_size(josh, HORIZONTAL_ALIGNMENT_LEFT, -1, josh_size).x
	draw_string(font, Vector2(right - josh_width, top_line + 4.0 * k), josh, HORIZONTAL_ALIGNMENT_LEFT, -1, josh_size, josh_color)

	var who := names[i] if i < names.size() else "P%d" % (i + 1)
	var name_size := _hud_font(32, k)
	var name_room := right - josh_width - 16.0 * k - left
	var badge := auto_on and _is_you(i)
	if badge:
		name_room -= 84.0 * k
	while who.length() > 3 and font.get_string_size(who, HORIZONTAL_ALIGNMENT_LEFT, -1, name_size).x > name_room:
		who = who.left(who.length() - 1)
	draw_string(font, Vector2(left, top_line), who, HORIZONTAL_ALIGNMENT_LEFT, -1, name_size, c)
	if badge:
		var x0 := left + font.get_string_size(who, HORIZONTAL_ALIGNMENT_LEFT, -1, name_size).x + 10.0 * k
		_auto_badge(font, Vector2(x0, top_line - name_size * 0.78), maxi(18, int(name_size * 0.6)))

	# Stocks as dots, then KOs, then the weapon or Guts at the right.
	var x := left + 8.0 * k
	for s in rules.stocks:
		var dot := Vector2(x + 24.0 * k * s, low_line - 9.0 * k)
		if s < f.stocks:
			draw_circle(dot, 8.0 * k, c)
		else:
			draw_circle(dot, 8.0 * k, Color(c, 0.35), false, 2.0 * k, true)
	x += 24.0 * k * rules.stocks + 12.0 * k
	if kills >= 0:
		var ko_text := "KO %d" % kills
		var ko_size := _hud_font(26, k)
		draw_string(font, Vector2(x, low_line), ko_text, HORIZONTAL_ALIGNMENT_LEFT, -1, ko_size, Color.WHITE)
		x += font.get_string_size(ko_text, HORIZONTAL_ALIGNMENT_LEFT, -1, ko_size).x + 14.0 * k
	if f.weapon != Weapons.NONE and f.is_active():
		# Armed: the weapon, its name and what is left in it, where Guts would be. The name only
		# where it fits, cut short before it would run into the KOs.
		var tex := ArtLibrary.weapon_texture(f.weapon)
		var supply := "" if _never_runs_out(f.weapon) else str(f.weapon_supply)
		var supply_size := _hud_font(28, k)
		var supply_width := font.get_string_size(supply, HORIZONTAL_ALIGNMENT_LEFT, -1, supply_size).x
		draw_string(font, Vector2(right - supply_width, low_line), supply, HORIZONTAL_ALIGNMENT_LEFT, -1, supply_size, GOLD)
		var icon_left := right - supply_width
		if tex != null:
			var icon_h := 34.0 * k
			var icon_w := minf(64.0 * k, icon_h * tex.get_width() / maxf(1.0, tex.get_height()))
			icon_h = icon_w * tex.get_height() / maxf(1.0, tex.get_width())
			icon_left = right - supply_width - icon_w - 8.0 * k
			draw_texture_rect(tex, Rect2(icon_left, low_line - icon_h + 4.0 * k, icon_w, icon_h), false)
		var weapon_name := WeaponInfo.name(f.weapon)
		var name_px := _hud_font(20, k)
		var room := icon_left - 8.0 * k - x
		while weapon_name.length() > 3 and font.get_string_size(weapon_name, HORIZONTAL_ALIGNMENT_LEFT, -1, name_px).x > room:
			weapon_name = weapon_name.left(weapon_name.length() - 1)
		if font.get_string_size(weapon_name, HORIZONTAL_ALIGNMENT_LEFT, -1, name_px).x <= room:
			var name_w := font.get_string_size(weapon_name, HORIZONTAL_ALIGNMENT_LEFT, -1, name_px).x
			draw_string(font, Vector2(icon_left - 8.0 * k - name_w, low_line), weapon_name, HORIZONTAL_ALIGNMENT_LEFT, -1, name_px, GOLD)
	elif rules.guts:
		var guts := "GUTS..." if f.guts_timer > 0 else "GUTS used" if f.guts_used else "GUTS"
		var guts_color := DIM if f.guts_used and f.guts_timer == 0 else GOLD
		var guts_size := _hud_font(20, k)
		var guts_width := font.get_string_size(guts, HORIZONTAL_ALIGNMENT_LEFT, -1, guts_size).x
		draw_string(font, Vector2(right - guts_width, low_line), guts, HORIZONTAL_ALIGNMENT_LEFT, -1, guts_size, guts_color)


## A small gold "AUTO" pill with its top-left at `at`: the CPU is playing your fighter.
func _auto_badge(font: Font, at: Vector2, font_size: int) -> void:
	var w := font.get_string_size("AUTO", HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x + 14.0
	var r := Rect2(at, Vector2(w, font_size + 8.0))
	draw_style_box(_pill(GOLD), r)
	draw_string(font, Vector2(at.x + 7.0, at.y + 4.0 + font_size * 0.86), "AUTO", HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, Color("1b1a22"))


## A filled rounded pill in one colour (cached).
func _pill(fill: Color) -> StyleBoxFlat:
	var key := "pill" + fill.to_html()
	if not _box_styles.has(key):
		var box := StyleBoxFlat.new()
		box.bg_color = fill
		box.set_corner_radius_all(10)
		box.anti_aliasing = true
		_box_styles[key] = box
	return _box_styles[key]


## How big the stats boxes are (1 = BOX_MAX_WIDTH wide, before _box_unit) for n players on this
## screen: they share the bar (with the ALIVE box when there are more than two), and shrink as
## players are added.
func _box_scale(size: Vector2, n: int) -> float:
	var free := size.x - 2.0 * BAR_MARGIN - hud_right_inset - BOX_GAP * n
	var width := free / (maxi(n, 1) + (ALIVE_WIDTH if n > 2 else 0.0)) / _box_unit()
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
	var c := Color(1, 1, 1, 0.25) if i < 0 else _color_of(i)
	var key := c.to_html()
	if not _box_styles.has(key):
		var box := StyleBoxFlat.new()
		box.bg_color = BOX_FILL
		box.border_color = c
		box.set_border_width_all(3)
		box.border_width_left = 3 if i < 0 else 10
		box.set_corner_radius_all(14)
		box.anti_aliasing = true
		_box_styles[key] = box
	return _box_styles[key]


## Text centred on centre_x. Measured rather than given a layout width, because draw_string clips
## text wider than the width it is given. An outline keeps it readable over the fighters.
func _text(font: Font, text: String, centre_x: float, baseline_y: float, font_size: int, color: Color, outline := true) -> void:
	var width := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	var pos := Vector2(centre_x - width / 2, baseline_y)
	if outline:
		draw_string_outline(font, pos, text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, maxi(4, font_size / 8), Color(OUTLINE, OUTLINE.a * minf(1.0, color.a * 1.25)))
	draw_string(font, pos, text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, color)


## 0 to 1 over EASE_FRAMES, eased out (fast, then settling): the motion curve of polish 1.
static func ease_in(t: float) -> float:
	var k := clampf(t, 0.0, 1.0)
	return 1.0 - (1.0 - k) * (1.0 - k) * (1.0 - k)


## The status plate's fade: 0 when its words have just changed, 1 after 0.15 s. Moving dots at the
## end of a line don't count as a change.
func _status_ease() -> float:
	var key := status_big.rstrip(". ") + "|" + "|".join(Array(status_small).map(func(s: String) -> String: return s.rstrip(". ")))
	var now := Time.get_ticks_msec()
	if key != _status_key:
		_status_key = key
		_status_msec = now
	return ease_in((now - _status_msec) / (EASE_FRAMES * 1000.0 / 60.0))


## How long the weapon on a crate is drawn, in metres (three times the 0.55 it started at).
const CRATE_WEAPON := 1.65
const CRATE := Color("a0672c")
const CRATE_EDGE := Color("5a3512")
const DECOY_MARK := Color("ff3b30")  # the mark only a decoy's owner's side sees
const PULSE := Color("8fe3ff")       # a speaker's rings
const PULSE_FRAMES := 15             # how long a pulse's rings spread
const GATE_COLOURS := [Color("b85cff"), Color("3fd0ff"), Color("ff9f1c"), Color("7dff5a"), Color("ff3fa4"), Color("ffd23f")]
const BULLET := Color("ffe27a")
const ARROW := Color("6b4a2a")
const PEBBLE := Color("8d8678")
const BOLT := Color("5dffb0")
const FLASH := Color("ffd166")
const AIM := Color("ff3b30")
## A blast that hits only the nearest fighter and takes at least this long to fire (the Sniper
## Rifle) shows the line it will fire along while it winds up.
const AIM_STARTUP := 30
## A blast's flash lingers this many frames after its last active one.
const FLASH_FRAMES := 5
# Statuses on a fighter and sprays (combat spec v0.4, section 5).
const SLOWED := Color(0.35, 0.6, 1.0, 0.24)
const SLOWED_TINT := Color(0.62, 0.78, 1.0)
const NET := Color("c9b58a")
const TRAP_JAW := Color("b8bcc4") # a trap's steel jaws
const ICE := Color(0.7, 0.92, 1.0, 0.5)
const SPARK := Color("fff36b")
const POISON := Color("6ee05a")
const FLAME := Color("ff8a1f")
const MARK_COLOURS := [Color("ff3fa4"), Color("ffd23f"), Color("3fd0ff"), Color("7dff5a"), Color("b85cff")]
const STEAM := Color(0.95, 0.95, 1.0)
const WIND := Color(0.85, 0.95, 1.0)
const WATER := Color("4aa8ff")
const PULL := Color("ff4f6a")
const GUARD := Color("fff3c4")      # the arc a guard covers
const CLOUD := Color("8e9a6e")      # a stink cloud
const PUDDLE := Color("4aa3e8")     # a puddle that slips
const GOO := Color("6fd63a")        # a poison patch
const GLUE := Color("e0c068")       # a sticky patch
const CLAY := Color("b5652e")       # a clay wall
const HIDDEN_OWN_ALPHA := 0.35      # your own fighter, and teammates, hidden in a cloud
const LIFT := Color("b48cff")
const LEECH := Color("d01c3c")


## The weapon a row is drawn as: itself, or for a hidden row (a part of a weapon) that weapon.
## 0 for an id that is no row.
static func _art_of(weapon: int) -> int:
	if weapon <= 0 or weapon >= WeaponTable.COUNT:
		return 0
	return WeaponTable.ROWS[weapon][WeaponTable.C_CREDIT]


## How long a weapon is drawn in the hand, in metres.
static func _length_of(weapon: int) -> float:
	var length := WeaponInfo.length(_art_of(weapon))
	return length if length > 0.0 else 0.8


## A weapon with no count to show: it never runs out (the Bowling Ball).
static func _never_runs_out(weapon: int) -> bool:
	return weapon > 0 and weapon < WeaponTable.COUNT and WeaponTable.ROWS[weapon][WeaponTable.C_SPEND] == WeaponTable.SPEND_NEVER


## A weapon sprite with its grip at `at`, `length` metres long, turned by `angle` (radians; positive
## turns the tip down, mirrored when facing left).
func _draw_weapon_sprite(weapon: int, at: Vector2, length: float, angle: float, facing: int, alpha := 1.0) -> void:
	var art := _art_of(weapon)
	var tex := ArtLibrary.weapon_texture(art)
	if tex == null:
		draw_circle(at, length * _ppm * 0.3, Color(GOLD, alpha))
		return
	var k := length * _ppm / tex.get_width()
	var grip := ArtLibrary.weapon_grip(art) * Vector2(tex.get_size())
	draw_set_transform(at, angle * facing, Vector2(k * facing, k))
	draw_texture(tex, -grip, Color(1, 1, 1, alpha))
	draw_set_transform_matrix(Transform2D.IDENTITY)


## The weapon in a fighter's hand, swung through its attack, and its ammo under the feet. A guard
## holds it up in front, with a pale arc over the side it covers.
func _draw_held_weapon(_font: Font, f: Fighter, i: int, seen := 1.0) -> void:
	if f.weapon == Weapons.NONE:
		return
	var hand := _screen_point(f.pos_x + Fx.ratio(45, 100) * f.facing, f.pos_y + Fx.ONE)
	var painter := _painter(i)
	if painter != null and painter.hand_valid:
		hand = painter.hand_position # the painted front hand, so the weapon moves with the pose
	var angle := -0.5 # held up and forward
	var m := Simulation.current_move(f, rules.tuning)
	# A swing comes round over the shoulder. A poke (a swing that borrows the jab) and everything
	# that shoots or blasts points ahead, and moves with the painted hand.
	var held := WeaponTable.ROWS[f.weapon if f.weapon < WeaponTable.COUNT else 0]
	if held[WeaponTable.C_USE] == WeaponTable.USE_SWING and WeaponInfo.clip(f.weapon) != "jab":
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
	if Simulation.guarding(f):
		angle = -1.45 # held up in front, covering the face and body
		hand = _screen_point(f.pos_x + Fx.ratio(55, 100) * f.facing, f.pos_y + Fx.ratio(3, 5))
		var front := _screen_point(f.pos_x, f.pos_y + Fx.ratio(9, 10))
		var from := -PI / 2.0 + 0.2 if f.facing > 0 else PI / 2.0 + 0.2
		var to := PI / 2.0 - 0.2 if f.facing > 0 else 1.5 * PI - 0.2
		draw_arc(front, _ppm * 0.85, from, to, 16, Color(GUARD, 0.55 * seen), maxf(3.0, _ppm * 0.07), true)
	# Never shorter than HELD_MIN_PX, so a weapon reads on the whole-level view of a big room.
	# The weapon's name and ammo are in the player box, or in a room on your HUD chip (rooms v1, 2.6
	# and 3.1), never under the feet.
	_draw_weapon_sprite(f.weapon, hand, maxf(_length_of(f.weapon), HELD_MIN_PX / maxf(1.0, _ppm)), angle, f.facing, seen)


## Boxes, loose weapons and flat patches on the ground (behind the fighters), or projectiles,
## walls and clouds (in front of them: a cloud hides whoever is in it).
func _draw_props(shots: bool) -> void:
	var tick := float(state.frame)
	for p in state.props:
		if p.kind == Weapons.PROP_NONE or p.kind == Weapons.PROP_PENDING_BOX:
			continue
		var behind := p.kind != Weapons.PROP_SHOT or p.shot == Weapons.SHOT_DECOY \
				or (p.shot == Weapons.SHOT_ZONE and WeaponTable.ROWS[Weapons.shot_row(p)][WeaponTable.C_HIDES] == 0)
		if behind == shots:
			continue
		var r := _screen_rect(Simulation.prop_box(p))
		var centre := r.get_center()
		var dir := -1 if p.vel_x < 0 else 1
		match p.kind:
			Weapons.PROP_BOX:
				_draw_crate(r, p.weapon)
			Weapons.PROP_ITEM:
				var bob := sin(tick * 0.12) * _ppm * 0.06
				draw_set_transform(Vector2(centre.x, r.end.y), 0.0, Vector2(1.0, 0.3))
				draw_circle(Vector2.ZERO, _ppm * 0.5, Color(GOLD, 0.35))
				draw_set_transform_matrix(Transform2D.IDENTITY)
				_draw_weapon_sprite(p.weapon, centre + Vector2(0, bob), _length_of(p.weapon) * 0.8, 0.0, 1)
			Weapons.PROP_SHOT:
				_draw_shot(p, r, centre, dir, tick)


## A Prop Box: a wooden crate with the weapon inside, big enough to tell which it is from across the
## arena. A decoy is drawn with this too, holding the weapon it pretends to.
func _draw_crate(r: Rect2, weapon: int) -> void:
	var centre := r.get_center()
	draw_rect(r, CRATE)
	draw_rect(r, CRATE_EDGE, false, maxf(2.0, _ppm * 0.05))
	draw_line(r.position, r.end, CRATE_EDGE, maxf(1.5, _ppm * 0.03))
	draw_line(Vector2(r.end.x, r.position.y), Vector2(r.position.x, r.end.y), CRATE_EDGE, maxf(1.5, _ppm * 0.03))
	var tex := ArtLibrary.weapon_texture(weapon)
	if tex == null:
		_draw_weapon_sprite(weapon, centre, CRATE_WEAPON, 0.0, 1)
		return
	var w := CRATE_WEAPON * _ppm
	var h := w * tex.get_height() / maxf(1.0, tex.get_width())
	if h > w: # a tall sprite: its height is the limit
		w *= w / h
		h = CRATE_WEAPON * _ppm
	draw_texture_rect(tex, Rect2(Vector2(centre.x - w / 2.0, r.position.y + r.size.y * 0.35 - h / 2.0), Vector2(w, h)), false)


## A decoy (a Decoy Crate or a rigged crate): exactly a Prop Box with the weapon it pretends to hold
## (its supply). Only its owner's side sees the small mark on its corner that gives it away.
func _draw_decoy(p: Prop, r: Rect2) -> void:
	var looks := p.supply if p.supply > 0 and p.supply < WeaponTable.COUNT else Weapons.BELAN
	_draw_crate(r, looks)
	if p.owner < 0 or not _own_side(p.owner):
		return
	var at := Vector2(r.end.x, r.position.y - _ppm * 0.05) # above the corner, clear of the sprite
	draw_circle(at, _ppm * 0.14, DECOY_MARK)
	draw_circle(at, _ppm * 0.14, OUTLINE, false, maxf(1.5, _ppm * 0.025), true)
	if WeaponTable.ROWS[Weapons.shot_row(p)][WeaponTable.C_TOUCH] == 0: # rigged: a fuse spark
		draw_circle(at, _ppm * 0.04, Color.WHITE)


## A speaker: its sprite, and on every pulse (the match clock: frame % its rehit) rings that spread
## out in front of it for a quarter of a second, as far as the pulse reaches.
func _draw_speaker(p: Prop, r: Rect2, tick: float) -> void:
	var row := WeaponTable.ROWS[Weapons.shot_row(p)]
	var facing := -1 if p.supply < 0 else 1
	_draw_weapon_sprite(p.weapon, Vector2(r.get_center().x, r.end.y - r.size.y * 0.5), maxf(0.7, _length_of(p.weapon)), 0.0, facing)
	var every: int = maxi(1, row[WeaponTable.C_REHIT])
	var since := int(tick) % every
	var reach := Fx.to_float(row[WeaponTable.C_REACH]) * _ppm
	var mouth := Vector2(r.get_center().x + facing * r.size.x * 0.45, r.end.y - _ppm * 1.0)
	if since < PULSE_FRAMES:
		var t := float(since) / PULSE_FRAMES
		for k in 3:
			var rad := maxf(4.0, reach * clampf(t + k * 0.12, 0.0, 1.0))
			var from := -PI / 3.0 if facing > 0 else PI - PI / 3.0
			draw_arc(mouth, rad, from, from + PI * 2.0 / 3.0, 18, Color(PULSE, 0.75 * (1.0 - t)), maxf(2.0, _ppm * 0.07), true)
	else: # a faint hint of where it will pulse, so nobody is caught by surprise
		draw_arc(mouth, _ppm * 0.35, -PI / 4.0 if facing > 0 else PI - PI / 4.0, (PI / 4.0) if facing > 0 else PI + PI / 4.0, 8,
			Color(PULSE, 0.35), maxf(1.5, _ppm * 0.04), true)


## A gate: a swirl the size of its box, turning; a pair (the same owner) shares a colour, and one with
## no partner yet is drawn faint.
func _draw_gate(p: Prop, r: Rect2, tick: float) -> void:
	var colour: Color = GATE_COLOURS[maxi(0, p.owner) % GATE_COLOURS.size()]
	var paired := false
	for q in state.props:
		if q != p and q.kind == Weapons.PROP_SHOT and q.shot == Weapons.SHOT_GATE and q.owner == p.owner:
			paired = true
	var fade := clampf(p.timer / 60.0, 0.0, 1.0) * (1.0 if paired else 0.45)
	var centre := r.get_center()
	var rad := r.size.y * 0.5
	draw_circle(centre, rad, Color(colour, 0.22 * fade))
	for k in 3:
		var turn := tick * 0.12 + k * TAU / 3.0
		draw_arc(centre, rad * (0.35 + 0.22 * k), turn, turn + PI * 1.1, 14, Color(colour, 0.9 * fade), maxf(2.0, _ppm * 0.06), true)
	draw_circle(centre, rad, Color(colour.lightened(0.3), 0.8 * fade), false, maxf(2.0, _ppm * 0.05), true)


## The anvil: its sprite where it hangs or falls, and its shadow on the first platform under it,
## darker and smaller the nearer it comes, so everyone can see where it will land.
func _draw_drop(p: Prop, r: Rect2) -> void:
	var top := Simulation.top_below(rules.stage, p.pos_x, p.pos_y)
	if not top.is_empty():
		var floor_at := _screen_point(p.pos_x, top[0])
		var height := Fx.to_float(p.pos_y - top[0])
		var near := clampf(1.0 - height / 9.0, 0.0, 1.0)
		var w := r.size.x * (0.9 - 0.25 * near)
		draw_set_transform(floor_at, 0.0, Vector2(1.0, 0.28))
		draw_circle(Vector2.ZERO, w, Color(0, 0, 0, 0.25 + 0.35 * near))
		draw_set_transform_matrix(Transform2D.IDENTITY)
	_draw_weapon_sprite(p.weapon, r.get_center(), maxf(0.8, _length_of(p.weapon)), 0.0, 1)


## A shot, with its weapon's sprite: by the look its row asks for (WeaponInfo.shot), else by its
## flight. A flight with no drawing of its own here is the weapon's sprite at the prop's box, so no
## prop a later weapon adds can be on the stage unseen.
func _draw_shot(p: Prop, r: Rect2, centre: Vector2, dir: int, tick: float) -> void:
	var length := _length_of(p.weapon)
	match WeaponInfo.shot(p.weapon):
		"spin": # the thing itself, spinning (a thrown star)
			_draw_weapon_sprite(p.weapon, centre, maxf(0.5, length * 0.85), tick * 0.6 * dir, 1)
			return
		"pebble":
			draw_circle(centre, maxf(5.0, _ppm * 0.13), PEBBLE)
			draw_circle(centre, maxf(5.0, _ppm * 0.13), OUTLINE, false, maxf(1.5, _ppm * 0.03), true)
			return
		"bolt": # a glowing energy bolt
			var tail := centre - Vector2(dir * maxf(18.0, _ppm * 0.55), 0)
			var head := centre + Vector2(dir * _ppm * 0.2, 0)
			draw_line(tail, head, Color(BOLT, 0.35), maxf(6.0, _ppm * 0.22))
			draw_line(tail.lerp(head, 0.3), head, BOLT, maxf(3.0, _ppm * 0.11))
			draw_circle(head, _ppm * 0.09, Color.WHITE)
			return
		"rocket":
			_draw_rocket(p, centre, null)
			return
		"orb": # a dark ball with a purple glow, easy to see
			var orb := r.size.x * 0.5 * (1.0 + 0.05 * sin(tick * 0.35))
			draw_circle(centre, orb * 1.3, Color(0.55, 0.25, 0.95, 0.25))
			draw_circle(centre, orb, Color(0.35, 0.12, 0.6, 0.85))
			draw_circle(centre, orb * 0.55, Color("14081f"))
			return
		"wave": # a shockwave running along the ground
			var base := Vector2(centre.x, r.end.y)
			for k in 3:
				var h := r.size.y * (1.1 - 0.3 * k)
				var x := base.x - dir * _ppm * 0.3 * k
				draw_colored_polygon(PackedVector2Array([Vector2(x - dir * _ppm * 0.5, base.y), Vector2(x, base.y - h),
					Vector2(x + dir * _ppm * 0.25, base.y)]), Color(BOLT, 0.7 - 0.2 * k))
			return
	match p.shot:
		Weapons.SHOT_ROLL:
			_draw_weapon_sprite(p.weapon, centre, length, Fx.to_float(p.pos_x) / 0.3, 1)
		Weapons.SHOT_BULLET: # a trail at least 18 px long and 3 px thick, however far out the camera is
			draw_line(centre, centre - Vector2(dir * maxf(18.0, _ppm * 0.45), 0), BULLET, maxf(3.0, _ppm * 0.06))
		Weapons.SHOT_ARROW:
			var tip := centre + Vector2(dir * _ppm * 0.35, 0)
			draw_line(tip, tip - Vector2(dir * maxf(18.0, _ppm * 0.75), 0), ARROW, maxf(3.0, _ppm * 0.05))
			draw_colored_polygon(PackedVector2Array([tip + Vector2(dir * _ppm * 0.12, 0),
				tip + Vector2(0, -_ppm * 0.07), tip + Vector2(0, _ppm * 0.07)]), Color("d8d8e0"))
		Weapons.SHOT_BOMB:
			_draw_weapon_sprite(p.weapon, centre, length * 0.82, 0.0, dir)
			if int(tick) % 6 < 3:
				draw_circle(centre + Vector2(0, -_ppm * 0.3), _ppm * 0.08, Color("ffb020"))
		Weapons.SHOT_PAD:
			# The trampoline lies flat on the ground, as wide as its box.
			var bed := ArtLibrary.weapon_texture(_art_of(p.weapon))
			var squash := 1.0 + 0.06 * sin(tick * 0.3)
			if bed == null:
				draw_rect(Rect2(r.position.x, r.end.y - _ppm * 0.25, r.size.x, _ppm * 0.25), Color("3aa0e8"))
			else:
				var w := r.size.x * 1.25
				var h := w * bed.get_height() / maxf(1.0, bed.get_width()) * squash
				draw_texture_rect(bed, Rect2(Vector2(centre.x - w / 2.0, r.end.y - h), Vector2(w, h)), false)
		Weapons.SHOT_MINE:
			# A mine has a light: dim while it arms, then blinking red. A trap (a mine with no blast)
			# is just its sprite, faded while it arms.
			var armed := p.timer <= 0
			var trap: bool = WeaponTable.ROWS[Weapons.shot_row(p)][WeaponTable.C_BLAST] == 0
			_draw_weapon_sprite(p.weapon, Vector2(centre.x, r.end.y - _ppm * 0.18), length, 0.0, 1, 0.55 if trap and not armed else 1.0)
			if not trap and (not armed or int(tick) % 20 < 10):
				draw_circle(Vector2(centre.x, r.end.y - _ppm * 0.42), _ppm * 0.07, Color("ff3b30") if armed else Color("7a6a60"))
		Weapons.SHOT_ZONE:
			_draw_zone(p, r, tick)
		Weapons.SHOT_WALL:
			_draw_wall(p, r)
		Weapons.SHOT_MISSILE:
			_draw_rocket(p, centre, ArtLibrary.weapon_texture(_art_of(p.weapon)))
		Weapons.SHOT_HOLE:
			# A dark ball; once it starts pulling, rings close in on it from its reach.
			var hole := WeaponTable.ROWS[Weapons.shot_row(p)]
			var pulling: bool = p.timer <= hole[WeaponTable.C_PULL_FRAMES]
			if pulling:
				var reach := Fx.to_float(hole[WeaponTable.C_REACH]) * _ppm
				draw_circle(centre, reach, Color(0.45, 0.2, 0.75, 0.10))
				for k in 3:
					var t := fmod(tick / 26.0 + k / 3.0, 1.0)
					draw_circle(centre, reach * (1.0 - t), Color(0.72, 0.45, 1.0, 0.55 * t), false, maxf(2.0, _ppm * 0.05), true)
			var ball := _ppm * (0.32 + (0.12 if pulling else 0.0) + 0.03 * sin(tick * 0.4))
			draw_circle(centre, ball * 1.25, Color(0.6, 0.3, 0.95, 0.55))
			draw_circle(centre, ball, Color("14081f"))
		Weapons.SHOT_THROWN, Weapons.SHOT_BOOMERANG, Weapons.SHOT_LOB:
			_draw_weapon_sprite(p.weapon, centre, length * 0.9, tick * 0.45 * dir, 1)
		Weapons.SHOT_DECOY:
			_draw_decoy(p, r)
		Weapons.SHOT_SPEAKER:
			_draw_speaker(p, r, tick)
		Weapons.SHOT_GATE:
			_draw_gate(p, r, tick)
		Weapons.SHOT_DROP:
			_draw_drop(p, r)
		_:
			_draw_sprite_in_box(p.weapon, r)


## A zone: a flat patch on the ground coloured by what it does (a puddle blue, goo green, glue amber),
## or for a row that hides a soft, nearly opaque cloud over its whole box, drawn in front of the
## fighters. Both fade out over their last second.
func _draw_zone(p: Prop, r: Rect2, tick: float) -> void:
	var row := WeaponTable.ROWS[Weapons.shot_row(p)]
	var fade := clampf(p.timer / 60.0, 0.0, 1.0)
	if row[WeaponTable.C_HIDES] != 0:
		var puffs := 7
		for k in puffs:
			var t := float(k) / (puffs - 1)
			var wobble := sin(tick * 0.05 + k * 1.7) * _ppm * 0.12
			var at := Vector2(r.position.x + r.size.x * (0.1 + 0.8 * t), r.end.y - r.size.y * (0.35 + 0.25 * sin(k * 2.3)) + wobble)
			draw_circle(at, r.size.y * 0.42, Color(CLOUD, 0.82 * fade))
		draw_circle(r.get_center(), r.size.y * 0.45, Color(CLOUD.darkened(0.15), 0.9 * fade))
		return
	var colour := GLUE
	match row[WeaponTable.C_EFFECT]:
		WeaponTable.EFFECT_SLIP: colour = PUDDLE
		WeaponTable.EFFECT_POISON, WeaponTable.EFFECT_BURN: colour = GOO
	var patch := Rect2(r.position.x, r.end.y - _ppm * 0.2, r.size.x, _ppm * 0.3)
	draw_set_transform(patch.get_center(), 0.0, Vector2(1.0, patch.size.y / maxf(1.0, patch.size.x)))
	draw_circle(Vector2.ZERO, patch.size.x * 0.5, Color(colour, 0.85 * fade))
	draw_circle(Vector2.ZERO, patch.size.x * 0.32, Color(colour.lightened(0.25), 0.5 * fade))
	draw_set_transform_matrix(Transform2D.IDENTITY)
	for k in 3: # a few bubbles or glints, on the match clock
		var t := fmod(tick / 40.0 + k / 3.0, 1.0)
		draw_circle(Vector2(patch.position.x + patch.size.x * (0.2 + 0.3 * k), patch.position.y - t * _ppm * 0.2), _ppm * 0.05 * (1.0 - t),
			Color(colour.lightened(0.4), 0.8 * fade))


## A wall: a clay block as big as its box, cracking with every swing it has taken (its wear).
func _draw_wall(p: Prop, r: Rect2) -> void:
	var brk: int = maxi(1, WeaponTable.ROWS[Weapons.shot_row(p)][WeaponTable.C_BRK])
	draw_rect(r, CLAY)
	var band := r.size.y / 4.0
	for k in 3:
		draw_line(Vector2(r.position.x, r.position.y + band * (k + 1)), Vector2(r.end.x, r.position.y + band * (k + 1)), CLAY.darkened(0.25), maxf(1.0, _ppm * 0.02))
	draw_rect(r, CLAY.darkened(0.45), false, maxf(2.0, _ppm * 0.05))
	var wear := mini(p.ignore_fighter, brk)
	for k in wear * 2: # cracks, more of them as it wears
		var x := r.position.x + r.size.x * (0.2 + 0.6 * fmod(k * 0.37 + 0.1, 1.0))
		var y := r.position.y + r.size.y * (0.15 + 0.7 * fmod(k * 0.53 + 0.2, 1.0))
		draw_polyline(PackedVector2Array([Vector2(x, y), Vector2(x + _ppm * 0.15, y + _ppm * 0.2), Vector2(x + _ppm * 0.05, y + _ppm * 0.4)]),
			Color("2b1a10"), maxf(1.5, _ppm * 0.03))


## A rocket flying the way it is heading, with its smoke: the weapon's own sprite (the Homing
## Missile is its rocket), or with no sprite a plain drawn rocket (the RPG's launcher stays in the
## hand). Flown the other way it is flipped, so it is never upside down.
func _draw_rocket(p: Prop, centre: Vector2, rocket: Texture2D) -> void:
	var heading := Vector2(float(p.vel_x), -float(p.vel_y))
	var turn := heading.angle() if heading.length_squared() > 0.0 else 0.0
	for k in 3: # its smoke
		draw_circle(centre - Vector2.from_angle(turn) * _ppm * (0.45 + 0.22 * k), _ppm * (0.12 - 0.025 * k), Color(0.9, 0.9, 0.9, 0.5 - 0.13 * k))
	var flip := -1.0 if absf(turn) > PI / 2.0 else 1.0
	draw_set_transform(centre, turn, Vector2(1.0, flip))
	if rocket == null:
		var body := _ppm * 0.7
		var thick := _ppm * 0.24
		draw_rect(Rect2(-body / 2.0, -thick / 2.0, body, thick), Color("5d7a2e"))
		draw_rect(Rect2(-body / 2.0, -thick / 2.0, body, thick), OUTLINE, false, maxf(1.5, _ppm * 0.03))
		draw_colored_polygon(PackedVector2Array([Vector2(body / 2.0, -thick / 2.0), Vector2(body / 2.0 + thick * 1.3, 0.0),
			Vector2(body / 2.0, thick / 2.0)]), Color("e8483c"))
		draw_colored_polygon(PackedVector2Array([Vector2(-body / 2.0, -thick * 0.4), Vector2(-body / 2.0 - thick * 1.2, 0.0),
			Vector2(-body / 2.0, thick * 0.4)]), Color("ffb020"))
	else:
		var w := _ppm * 0.9
		var h := w * rocket.get_height() / maxf(1.0, rocket.get_width())
		draw_texture_rect(rocket, Rect2(Vector2(-w / 2.0, -h / 2.0), Vector2(w, h)), false)
	draw_set_transform_matrix(Transform2D.IDENTITY)


## The fallback for a prop this view has no drawing for: a faint outline of its box, and its
## weapon's sprite standing on the box's floor (as big as fits, and never smaller than 0.6 m).
func _draw_sprite_in_box(weapon: int, r: Rect2) -> void:
	draw_rect(r, Color(1, 1, 1, 0.12))
	draw_rect(r, Color(1, 1, 1, 0.4), false, maxf(1.5, _ppm * 0.03))
	var tex := ArtLibrary.weapon_texture(_art_of(weapon))
	if tex == null:
		draw_circle(r.get_center(), maxf(_ppm * 0.2, minf(r.size.x, r.size.y) * 0.4), GOLD)
		return
	var size := Vector2(tex.get_size())
	var k := maxf(minf(r.size.x / size.x, r.size.y / size.y), _ppm * 0.6 / maxf(size.x, size.y))
	size *= k
	draw_texture_rect(tex, Rect2(Vector2(r.get_center().x - size.x / 2.0, r.end.y - size.y), size), false)


## A blast, from the fighter's own move and frame (so a room draws it too). On its active frames
## and a few after: a flash the length of its box. A blast on both sides is a dome over the whole
## box; one that hits only the nearest fighter is a tracer that stops in whoever it hit; any
## other is a cone from the muzzle. Before that, a slow blast that hits only the nearest fighter
## (the Sniper Rifle) shows the line it will fire along, for everyone to see.
func _draw_blasts() -> void:
	for i in state.fighters.size():
		var f := state.fighters[i]
		if not f.is_active() or f.action != Fighter.Action.ATTACK:
			continue
		var row := WeaponTable.ROWS[Weapons.row_of_move(f.move)]
		if row[WeaponTable.C_USE] != WeaponTable.USE_BLAST:
			continue
		var m := rules.tuning.moves.get_move(f.move)
		var box := Simulation.move_hitbox(f, m)
		var r := _screen_rect(box)
		var first: bool = row[WeaponTable.C_FIRST] != 0
		var mid_y := r.get_center().y
		var muzzle := Vector2(r.position.x if f.facing > 0 else r.end.x, mid_y)
		var far := Vector2(r.end.x if f.facing > 0 else r.position.x, mid_y)
		if first:
			far.x = _first_on_the_line(i, box, far.x)
		if f.action_frame < m.startup:
			if first and m.startup >= AIM_STARTUP:
				# The aim line: steadier and brighter as the shot comes, blinking in its last frames.
				var ready := float(f.action_frame) / m.startup
				if ready < 0.8 or f.action_frame % 4 < 2:
					draw_line(muzzle, far, Color(AIM, 0.25 + 0.55 * ready), maxf(1.5, _ppm * 0.03))
					draw_circle(far, _ppm * (0.06 + 0.08 * ready), Color(AIM, 0.5 + 0.4 * ready))
			continue
		var age := f.action_frame - m.startup
		if age >= m.active + FLASH_FRAMES:
			continue
		var fade := 1.0 - float(age) / (m.active + FLASH_FRAMES)
		if m.hits_both_sides:
			# As wide as the box from its first frame (a hit freezes the move there), with a ring
			# that keeps racing out on the match clock.
			var reach := r.size.x / 2.0
			var wave := fmod(state.frame / 8.0, 1.0)
			draw_set_transform(Vector2(r.get_center().x, r.end.y), 0.0, Vector2(1.0, r.size.y / maxf(1.0, reach)))
			var dome := PackedVector2Array()
			for k in 25:
				dome.append(Vector2.from_angle(PI + PI * k / 24.0) * reach)
			draw_colored_polygon(dome, Color(1, 1, 1, 0.2 * fade))
			draw_arc(Vector2.ZERO, reach, PI, TAU, 24, Color(FLASH, 0.9 * fade), maxf(3.0, _ppm * 0.1), true)
			draw_arc(Vector2.ZERO, reach * (0.3 + 0.7 * wave), PI, TAU, 24, Color(1, 1, 1, 0.6 * fade * (1.0 - wave)), maxf(2.0, _ppm * 0.06), true)
			draw_set_transform_matrix(Transform2D.IDENTITY)
			continue
		if first:
			draw_line(muzzle, far, Color(FLASH, 0.45 * fade), maxf(5.0, _ppm * 0.16))
			draw_line(muzzle, far, Color(1, 1, 1, 0.95 * fade), maxf(2.0, _ppm * 0.05))
			draw_circle(far, _ppm * 0.22 * fade, Color(1, 1, 1, 0.8 * fade))
		else:
			var half := r.size.y / 2.0
			draw_colored_polygon(PackedVector2Array([muzzle + Vector2(0, -half * 0.25), Vector2(far.x, r.position.y),
				Vector2(far.x, r.end.y), muzzle + Vector2(0, half * 0.25)]), Color(FLASH, 0.5 * fade))
		draw_circle(muzzle, _ppm * 0.28 * fade, Color(1, 1, 1, 0.9 * fade))


## A spray's stream, from the fighter's own move and frame (so a room draws it too), on every frame
## its box is out: a cone the size of the box, or for a row that touches only the nearest fighter a
## beam that stops in them, coloured by what the stream does.
func _draw_sprays() -> void:
	for i in state.fighters.size():
		var f := state.fighters[i]
		if not f.is_active() or f.action != Fighter.Action.ATTACK:
			continue
		var row := WeaponTable.ROWS[Weapons.row_of_move(f.move)]
		if row[WeaponTable.C_USE] != WeaponTable.USE_SPRAY:
			continue
		var m := rules.tuning.moves.get_move(f.move)
		if not m.is_active_on(f.action_frame):
			continue
		var box := Simulation.move_hitbox(f, m)
		var r := _screen_rect(box)
		var colour := _spray_colour(row)
		var mid_y := r.get_center().y
		var muzzle := Vector2(r.position.x if f.facing > 0 else r.end.x, mid_y)
		var far := Vector2(r.end.x if f.facing > 0 else r.position.x, mid_y)
		var flicker := 0.8 + 0.2 * sin(state.frame * 0.9 + i)
		if row[WeaponTable.C_FIRST] != 0:
			far.x = _first_on_the_line(i, box, far.x)
			draw_line(muzzle, far, Color(colour, 0.45 * flicker), maxf(6.0, _ppm * 0.2))
			draw_line(muzzle, far, Color(colour.lightened(0.5), 0.9), maxf(2.0, _ppm * 0.05))
			for k in 3: # pulses running along it
				var t := fmod(state.frame / 10.0 + k / 3.0, 1.0)
				draw_circle(muzzle.lerp(far, t), _ppm * 0.08, Color(1, 1, 1, 0.7 * (1.0 - t)))
			continue
		var half := r.size.y / 2.0
		draw_colored_polygon(PackedVector2Array([muzzle + Vector2(0, -half * 0.2), Vector2(far.x, r.position.y),
			Vector2(far.x, r.end.y), muzzle + Vector2(0, half * 0.2)]), Color(colour, 0.42 * flicker))
		for k in 5: # puffs of it, carried out along the cone on the match clock
			var t := fmod(state.frame / 14.0 + k / 5.0, 1.0)
			var at := muzzle.lerp(far, t) + Vector2(0, sin(k * 2.1 + state.frame * 0.3) * half * 0.6 * t)
			draw_circle(at, _ppm * (0.08 + 0.22 * t), Color(colour.lightened(0.3), 0.75 * (1.0 - t)))


## What a spray looks like, by what it does: flame, ice, a leech's tether, a magnet's pull, the
## gravity beam, a gust, a water jet, sparks (a saw) or steam.
static func _spray_colour(row: PackedInt64Array) -> Color:
	match row[WeaponTable.C_EFFECT]:
		WeaponTable.EFFECT_BURN: return FLAME
		WeaponTable.EFFECT_CHILL: return ICE
		WeaponTable.EFFECT_LEECH: return LEECH
		WeaponTable.EFFECT_POISON: return POISON
		WeaponTable.EFFECT_SLOW: return SLOWED
	if row[WeaponTable.C_DRIFT] < 0:
		return PULL
	if row[WeaponTable.C_LIFT] > 0:
		return LIFT
	if row[WeaponTable.C_BLOW] != 0:
		return WIND
	if row[WeaponTable.C_DRIFT] > 0:
		return WATER
	if row[WeaponTable.C_CHIP] != 0 and row[WeaponTable.C_WALK] != 0:
		return SPARK
	return STEAM


## A fighter's statuses, over its body: slowed, a blue tint; marked, splats of colour; poisoned,
## green bubbles, or burning, small flames (by the row of the weapon it came from); held, by the
## weapon holding it: an ice block (a freeze), sparks (a shock), a spin (a slip), a trap's jaws (a root
## by a trap) or a net (any other root).
func _draw_statuses(f: Fighter, i: int) -> void:
	var body := _screen_rect(Simulation.hurtbox(f, rules.tuning))
	var painted := _painter(i) != null
	if painted:
		# The painted fighter is rounder and bigger than its hurtbox: cover the art.
		var px := _ppm / ART_PX_PER_M
		var feet := _screen_point(f.pos_x, f.pos_y)
		body = Rect2(feet.x - 85.0 * px, feet.y - 170.0 * px, 170.0 * px, 170.0 * px)
	var tick := float(state.frame)
	var k := _ppm / 50.0
	if f.slow_frames > 0 and not painted: # the painted fighter is tinted instead
		draw_rect(body, SLOWED)
	if f.mark_frames > 0:
		for n in 5:
			var at := body.position + body.size * Vector2(fmod(0.17 + n * 0.37, 1.0), fmod(0.23 + n * 0.29, 1.0))
			draw_circle(at, (6.0 + 3.0 * (n % 3)) * k, Color(MARK_COLOURS[(n + i) % MARK_COLOURS.size()], 0.8))
	if f.dot_frames > 0:
		var burn: bool = WeaponTable.ROWS[f.dot_weapon][WeaponTable.C_EFFECT] == WeaponTable.EFFECT_BURN
		for n in 3:
			var t := fmod(tick / (24.0 if burn else 40.0) + n / 3.0, 1.0)
			var x := body.position.x + body.size.x * (0.2 + 0.3 * n)
			var y := body.end.y - body.size.y * t
			if burn:
				var h := (10.0 + 4.0 * sin(tick * 0.7 + n)) * k * (1.0 - t)
				if h < 0.5:
					continue # a flame too small to see: its three points would nearly meet, and triangulation fails
				draw_colored_polygon(PackedVector2Array([Vector2(x - h * 0.45, y), Vector2(x, y - h * 1.6), Vector2(x + h * 0.45, y)]), Color(FLAME, 0.9 * (1.0 - t)))
			else:
				draw_circle(Vector2(x, y), (4.0 + 3.0 * t) * k, Color(POISON, 0.8 * (1.0 - t)))
				draw_circle(Vector2(x, y), (4.0 + 3.0 * t) * k, Color(1, 1, 1, 0.5 * (1.0 - t)), false, 1.5 * k)
	if f.stun_weapon == 0:
		return
	match WeaponTable.ROWS[f.stun_weapon][WeaponTable.C_EFFECT]:
		WeaponTable.EFFECT_CHILL:
			var block := body.grow(6.0 * k)
			draw_rect(block, ICE)
			draw_rect(block, Color(1, 1, 1, 0.85), false, 3.0 * k)
			draw_line(block.position + Vector2(block.size.x * 0.2, block.size.y * 0.15), block.position + Vector2(block.size.x * 0.45, block.size.y * 0.05), Color.WHITE, 2.0 * k)
		WeaponTable.EFFECT_STUN:
			for n in 4:
				var a := tick * 0.5 + n * TAU / 4.0
				var from := body.get_center() + Vector2.from_angle(a) * body.size.x * 0.7
				var mid := from + Vector2.from_angle(a + 1.2) * 8.0 * k
				draw_polyline(PackedVector2Array([from, mid, mid + Vector2.from_angle(a - 0.4) * 10.0 * k]), SPARK, 2.5 * k)
		WeaponTable.EFFECT_SLIP:
			draw_arc(Vector2(body.get_center().x, body.position.y), body.size.x * 0.6, tick * 0.4, tick * 0.4 + PI * 1.4, 16, Color.WHITE, 3.0 * k)
		_ when WeaponTable.ROWS[f.stun_weapon][WeaponTable.C_FLIGHT] == Weapons.SHOT_MINE:
			# Rooted by a trap (the Mouse Trap): its jaws shut on the fighter's feet.
			var feet := Vector2(body.get_center().x, body.end.y - 10.0 * k)
			var half := body.size.x * 0.4
			var bite := 0.85 + 0.15 * sin(tick * 0.6) # the jaws strain shut
			for side: float in [-1.0, 1.0]:
				var jaw := PackedVector2Array([Vector2(feet.x - half, feet.y)])
				for n in 9:
					var x := feet.x - half + 2.0 * half * (n + 0.5) / 9.0
					jaw.append(Vector2(x, feet.y - side * (6.0 + (10.0 if n % 2 == 0 else 2.0)) * k * bite))
				jaw.append(Vector2(feet.x + half, feet.y))
				draw_polyline(jaw, OUTLINE, 6.0 * k)
				draw_polyline(jaw, TRAP_JAW, 3.0 * k)
			draw_line(Vector2(feet.x - half - 4.0 * k, feet.y), Vector2(feet.x + half + 4.0 * k, feet.y), OUTLINE, 5.0 * k)
			for end: float in [-1.0, 1.0]:
				draw_circle(Vector2(feet.x + end * half, feet.y), 5.0 * k, TRAP_JAW.darkened(0.35))
				draw_circle(Vector2(feet.x + end * half, feet.y), 5.0 * k, OUTLINE, false, 2.0 * k)
		_:
			var net := Rect2(body.position.x - 4.0 * k, body.position.y + body.size.y * 0.45, body.size.x + 8.0 * k, body.size.y * 0.55 + 4.0 * k)
			for n in 5:
				var x := net.position.x + net.size.x * n / 4.0
				draw_line(Vector2(x, net.position.y), Vector2(net.position.x + net.size.x * (4 - n) / 4.0, net.end.y), NET, 2.0 * k)
			for n in 4:
				var y := net.position.y + net.size.y * n / 3.0
				draw_line(Vector2(net.position.x, y), Vector2(net.end.x, y), NET, 2.0 * k)


## Where a line from fighter i along its blast's box stops: the screen x of the nearest other
## fighter in the box who isn't on its team (the one a `first` blast hits), or far_x with nobody.
func _first_on_the_line(i: int, box: FxRect, far_x: float) -> float:
	var me := state.fighters[i]
	var best := -1
	var best_dist := 0
	for j in state.fighters.size():
		var other := state.fighters[j]
		if Simulation.allies(rules, i, j) or not other.is_active():
			continue
		if not box.overlaps(Simulation.hurtbox(other, rules.tuning)):
			continue
		var dist := absi(other.pos_x - me.pos_x)
		if best < 0 or dist < best_dist:
			best = j
			best_dist = dist
	return _screen_point(state.fighters[best].pos_x, 0).x if best >= 0 else far_x


## Sim position (Fx, y up) to screen pixels (y down). Floats from here on are for drawing only.
func _screen_point(x: int, y: int) -> Vector2:
	return Vector2(_origin.x + Fx.to_float(x) * _ppm, _origin.y - Fx.to_float(y) * _ppm)


func _screen_rect(r: FxRect) -> Rect2:
	var top_left := _screen_point(r.min_x, r.max_y)
	var bottom_right := _screen_point(r.max_x, r.min_y)
	return Rect2(top_left, bottom_right - top_left)


## The room's HUD (rooms v1, 3.1): one strip along the top, the same for every room, free-for-all
## or teams. The clock plate sits at the strip's centre; in teams each team's group (its total
## plate, then a chip per member) in team order, the first half of the groups left of the clock; in a
## free-for-all a chip per fighter in fighter order, the first half left of the clock. Your chip is
## wider and carries your Josh, KOs and weapon. The result is the result panel's (ResultScreen).
func _draw_room_hud(font: Font, size: Vector2) -> void:
	var n := state.fighters.size()
	var teams := PackedInt32Array()
	var kos := PackedInt32Array()
	for i in n:
		teams.append(rules.team_of(i) if rules.has_teams() else 0)
		kos.append(state.fighters[i].kos)
	var layout := hud_strip(size.x, hud_right_inset, teams, you_index, kos)

	var secs := ceili(time_left_frames / 60.0)
	var clock := "%d:%02d" % [secs / 60, secs % 60]
	# A dark plate behind the clock, so it reads on any sky; it throbs in the last ten seconds.
	var throb := 1.0 + (0.08 * absf(sin(time_left_frames * 0.105)) if secs <= 10 and not state.match_over else 0.0)
	var plate: Rect2 = layout["clock"]
	plate = plate.grow_individual(plate.size.x * (throb - 1.0) / 2.0, 0.0, plate.size.x * (throb - 1.0) / 2.0, 0.0)
	var clock_x := plate.get_center().x
	draw_set_transform(Vector2(0.0, strip_drop(state.frame, 0.0)))
	draw_style_box(_box_style(-1), plate)
	_text(font, clock, plate.get_center().x, plate.position.y + 49.0, int(50 * throb), Color.WHITE if secs > 30 else DANGER, false)

	var leader := _leading_team()
	for item: Dictionary in layout["items"]:
		var r: Rect2 = item["rect"]
		# At the match's start each one drops in, outwards from the clock (polish 1).
		draw_set_transform(Vector2(0.0, strip_drop(state.frame, absf(r.get_center().x - clock_x))))
		match str(item["kind"]):
			"chip":
				_draw_strip_chip(font, r, int(item["i"]))
			"plate":
				_draw_team_plate(font, r, int(item["team"]), int(item["team"]) == leader)
			"more":
				draw_style_box(_box_style(-1), r)
				_text(font, "+%d" % int(item["count"]), r.get_center().x, r.position.y + 38.0, 26, Color.WHITE, false)
	draw_set_transform(Vector2.ZERO)


## How far above its place (px, 0 or less) a HUD strip item `away` px from the clock is drawn on
## match frame `frame`: each drops in over EASE_FRAMES, STRIP_STAGGER frames after the one 70 px
## nearer the clock, so the strip fills outwards in about 0.4 s.
static func strip_drop(frame: int, away: float) -> float:
	var delay := floorf(away / 70.0) * STRIP_STAGGER
	var e := ease_in((frame - delay) / EASE_FRAMES)
	return -(1.0 - e) * (CHIP_H + BAR_MARGIN + 12.0)


## Where everything in the room HUD strip goes, for a screen `width` wide with `right_inset` kept
## clear at the right (Chat and the menu), fighters on `teams` (all 0: a free-for-all), `you` (-1:
## watching) and their KOs. Returns {"clock": Rect2, "items": [{"kind": "chip", "i", "rect"} |
## {"kind": "plate", "team", "rect"} | {"kind": "more", "count", "rect"}], "chip_w": float,
## "collapsed": bool}. When the chips don't fit at CHIP_MIN: in teams, the other teams shrink to
## their total plates and only yours keeps its chips; in a free-for-all, you and the most KOs that
## fit, then a "+n" chip. Static, so tests can check it fits every screen.
static func hud_strip(width: float, right_inset: float, teams: PackedInt32Array, you: int, kos: PackedInt32Array) -> Dictionary:
	var n := teams.size()
	var x0 := BAR_MARGIN
	var x1 := width - right_inset
	var y := BAR_MARGIN + 2.0
	var avail := x1 - x0 - CLOCK_SIZE.x - 2.0 * GROUP_GAP
	var team_mode := false
	for t in teams:
		team_mode = team_mode or t > 0
	# The groups: one per team in team order (a fighter with no team, which a room never has, is
	# a group of its own without a plate), or one group of everyone.
	var groups: Array = []
	if team_mode:
		var ids: Array[int] = []
		for t in teams:
			if not ids.has(t):
				ids.append(t)
		ids.sort()
		for t in ids:
			var members: Array[int] = []
			for i in n:
				if teams[i] == t:
					members.append(i)
			groups.append({"team": t, "members": members, "chips": true})
	else:
		var everyone: Array[int] = []
		for i in n:
			everyone.append(i)
		groups.append({"team": 0, "members": everyone, "chips": true})
	var more := 0
	var widths := _strip_widths(groups, you, avail, 0)
	var collapsed := widths.x < CHIP_MIN
	if collapsed:
		if team_mode:
			for g: Dictionary in groups:
				g["chips"] = (g["members"] as Array).has(you)
		else:
			# You first, then the most KOs (the lower index on a tie), as many as fit with "+n".
			var order: Array[int] = []
			for i in n:
				order.append(i)
			order.sort_custom(func(a: int, b: int) -> bool:
				if (a == you) != (b == you):
					return a == you
				return kos[a] > kos[b] or (kos[a] == kos[b] and a < b))
			var keep := n
			while keep > 1:
				keep -= 1
				var picked: Array[int] = []
				for k in keep:
					picked.append(order[k])
				picked.sort()
				groups[0]["members"] = picked
				if _strip_widths(groups, you, avail, n - keep).x >= CHIP_MIN:
					break
			more = n - (groups[0]["members"] as Array).size()
		widths = _strip_widths(groups, you, avail, more)
	var chip_w := clampf(widths.x, CHIP_MIN, CHIP_MAX)
	var you_w := minf(widths.y, maxf(YOU_MIN, chip_w * 1.5)) if widths.y > 0.0 else chip_w

	# Lay out each side as a list of [kind, value, width, gap before], then place the clock at the
	# strip's centre if both sides fit beside it, else as near as they allow.
	var sides: Array = [[], []]
	if team_mode:
		var left_groups := ceili(groups.size() / 2.0)
		for g in groups.size():
			var side: Array = sides[0 if g < left_groups else 1]
			var group: Dictionary = groups[g]
			var first := true
			if int(group["team"]) > 0:
				side.append(["plate", group["team"], PLATE_W, GROUP_GAP if not side.is_empty() else 0.0])
				first = false
			if bool(group["chips"]):
				for i: int in group["members"]:
					var gap := (GROUP_GAP if not side.is_empty() else 0.0) if first else STRIP_GAP
					side.append(["chip", i, you_w if i == you else chip_w, gap])
					first = false
	else:
		var members: Array = groups[0]["members"]
		var left_count := ceili((members.size() + (1 if more > 0 else 0)) / 2.0)
		for k in members.size():
			var side: Array = sides[0 if k < left_count else 1]
			var i: int = members[k]
			side.append(["chip", i, you_w if i == you else chip_w, STRIP_GAP if not side.is_empty() else 0.0])
		if more > 0:
			var side: Array = sides[1] if (sides[0] as Array).size() >= left_count else sides[0]
			side.append(["more", more, CHIP_MIN, STRIP_GAP if not side.is_empty() else 0.0])
	var side_w := [0.0, 0.0]
	for s in 2:
		for e: Array in sides[s]:
			side_w[s] += float(e[2]) + float(e[3])
	var centre := (x0 + x1) / 2.0
	var clock_x := centre - CLOCK_SIZE.x / 2.0
	var left_start := clock_x - GROUP_GAP - float(side_w[0])
	if left_start < x0:
		clock_x += x0 - left_start
	var right_end := clock_x + CLOCK_SIZE.x + GROUP_GAP + float(side_w[1])
	if right_end > x1:
		clock_x -= right_end - x1
	var items: Array = []
	var x := clock_x - GROUP_GAP - float(side_w[0])
	for s in 2:
		if s == 1:
			x = clock_x + CLOCK_SIZE.x + GROUP_GAP
		for e: Array in sides[s]:
			x += float(e[3])
			var r := Rect2(x, y, float(e[2]), CHIP_H)
			match str(e[0]):
				"chip":
					items.append({"kind": "chip", "i": e[1], "rect": r})
				"plate":
					items.append({"kind": "plate", "team": e[1], "rect": r})
				"more":
					items.append({"kind": "more", "count": e[1], "rect": r})
			x += float(e[2])
	return {"clock": Rect2(clock_x, y, CLOCK_SIZE.x, CLOCK_SIZE.y), "items": items, "chip_w": chip_w, "collapsed": collapsed}


## [a chip's width, your chip's width (0 if yours isn't shown)] for these groups in `avail` px, with
## a "+n" chip if more > 0.
static func _strip_widths(groups: Array, you: int, avail: float, more: int) -> Vector2:
	var chips := 0
	var plates := 0
	var gaps := 0.0
	var has_you := false
	for g: Dictionary in groups:
		var items := 0
		if int(g["team"]) > 0:
			plates += 1
			items += 1
		if bool(g["chips"]):
			var members: Array = g["members"]
			chips += members.size()
			items += members.size()
			has_you = has_you or members.has(you)
		gaps += STRIP_GAP * maxi(0, items - 1)
	gaps += GROUP_GAP * maxi(0, groups.size() - 2) # two group gaps sit by the clock, counted in avail
	var free := avail - plates * PLATE_W - gaps
	if more > 0:
		free -= CHIP_MIN + STRIP_GAP
	if chips == 0:
		return Vector2(CHIP_MAX, 0.0)
	if not has_you:
		return Vector2(free / chips, 0.0)
	var w := free / (chips + 0.5)
	if w * 1.5 >= YOU_MIN or chips == 1:
		return Vector2(w, maxf(w * 1.5, minf(YOU_MIN, free)))
	return Vector2((free - YOU_MIN) / (chips - 1), YOU_MIN)


## The team ahead on KOs, then fewer falls (Standings.teams_ranked), or 0 while the best two are level.
func _leading_team() -> int:
	if not rules.has_teams():
		return 0
	var ranked := Standings.teams_ranked(state, rules)
	if ranked.is_empty():
		return 0
	if ranked.size() > 1 and ranked[0]["kos"] == ranked[1]["kos"] and ranked[0]["falls"] == ranked[1]["falls"]:
		return 0
	return int(ranked[0]["team"])


## A team's total plate: its colour band, symbol and KOs; the leader's edged in white; "+1" rising
## under it when its total goes up.
func _draw_team_plate(font: Font, r: Rect2, team: int, leading: bool) -> void:
	var c := TeamStyle.color_of(team)
	var total := Standings.team_kos(state, rules, team)
	# "+1" when the total rises (from the frame it rose, for PLUS_ONE_FRAMES), and the plate bumps.
	var last: int = _team_kos.get(team, -1)
	if last >= 0 and total > last:
		_team_rose[team] = _draws
	_team_kos[team] = total
	var since := _draws - int(_team_rose.get(team, -100000))
	var bump := plate_bump(since)
	r = r.grow_individual(r.size.x * bump / 2.0, r.size.y * bump / 2.0, r.size.x * bump / 2.0, r.size.y * bump / 2.0)
	draw_style_box(_plate_style(leading), r)
	draw_rect(Rect2(r.position.x + 3.0, r.position.y + 3.0, 10.0, r.size.y - 6.0), c)
	# The total never runs into the symbol: past 9 the symbol gets smaller, and the number's size
	# fits what is left (34 px for one digit, about 30 for two, 24 for three).
	var text := str(total)
	var glyph_r := 12.0 if text.length() < 2 else 9.0
	var glyph_x := r.position.x + 17.0 + glyph_r
	TeamStyle.draw_glyph(self, Vector2(glyph_x, r.get_center().y), glyph_r, team)
	var font_size := int(34 * (1.0 + bump))
	var w := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	var room := r.end.x - 5.0 - (glyph_x + glyph_r + 4.0)
	if w > room:
		font_size = maxi(16, int(font_size * room / w))
		w = font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	var tx := maxf(glyph_x + glyph_r + 4.0, minf(r.position.x + 48.0, r.end.x - 5.0 - w))
	var base := r.get_center().y + font_size * 0.36
	draw_string_outline(font, Vector2(tx, base), text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, 4, OUTLINE)
	draw_string(font, Vector2(tx, base), text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, c.lightened(0.2) if bump <= 0.0 else c.lightened(0.2).lerp(Color.WHITE, bump / PLATE_BUMP * 0.6))
	if since >= 0 and since < PLUS_ONE_FRAMES:
		var t := float(since) / PLUS_ONE_FRAMES
		# It pops out bigger than it settles (an ease that overshoots), then rises and fades.
		var pop := plus_one_pop(since)
		_text(font, "+1", r.get_center().x, r.end.y + 30.0 - 14.0 * t, int(30 * pop), Color(GOLD, 1.0 - t * t))


## How much bigger a team's plate is drawn `since` draws after its total rose: up and back over
## 12 draws (0.2 s), else 0.
static func plate_bump(since: int) -> float:
	if since < 0 or since >= 12:
		return 0.0
	return PLATE_BUMP * sin(PI * since / 12.0)


## The "+1" text's size factor `since` draws after the total rose: from 0.4 it overshoots to about
## 1.2 by the 4th draw and settles at 1 by the 8th; then 1 until it has faded.
static func plus_one_pop(since: int) -> float:
	var x := clampf(since / 8.0, 0.0, 1.0)
	const C1 := 4.0
	const C3 := C1 + 1.0
	var back := 1.0 + C3 * pow(x - 1.0, 3.0) + C1 * pow(x - 1.0, 2.0)
	return 0.4 + 0.6 * back


## Fighter i's chip: a 7 px edge in its colour, its head, its KOs, a bar along the bottom coloured
## by its Josh; dimmed while it is out, "CPU" on it once its player left. Your own is wider, edged
## in white, with your Josh %, KOs and the weapon in hand with its ammo (and AUTO while auto play is on).
func _draw_strip_chip(font: Font, r: Rect2, i: int) -> void:
	var f := state.fighters[i]
	var mine := i == you_index
	var a := 1.0 if f.is_active() else 0.4
	var c := _color_of(i)
	draw_style_box(_chip_style(mine), r)
	draw_rect(Rect2(r.position.x + 3.0, r.position.y + 3.0, 7.0, r.size.y - 6.0), Color(c, a))
	# On a narrow chip the head is a little smaller and the KOs sit at the foot, beside the Josh bar,
	# so the badge never covers the face (the face is how chips are told apart).
	var narrow := not mine and r.size.x < NARROW_CHIP
	_draw_head(i, Rect2(r.position.x + 12.0, r.position.y + 3.0, HEAD_PX + 6.0, HEAD_PX - (6.0 if narrow else 0.0)), a)
	var danger := clampf(f.josh / DANGER_JOSH, 0.0, 1.0)
	var josh_color := Color.WHITE.lerp(DANGER, danger)
	var ko := str(f.kos)
	var ko_size := 18 if narrow else 22
	var ko_w := font.get_string_size(ko, HORIZONTAL_ALIGNMENT_LEFT, -1, ko_size).x
	# Others' chips show their KOs once they have some: a "0" on every face was clutter at 58 px a
	# chip (polish 9); the team plates carry the totals.
	var show_ko := mine or f.kos > 0
	# The Josh bar along the bottom: its length and colour (short of the KOs on a narrow chip).
	var bar_end := r.end.x - 8.0 - ((ko_w + 8.0) if narrow and show_ko else 0.0)
	var bar := Rect2(r.position.x + 12.0, r.end.y - 9.0, bar_end - r.position.x - 12.0, 4.0)
	draw_rect(bar, Color(1, 1, 1, 0.15 * a))
	draw_rect(Rect2(bar.position, Vector2(bar.size.x * maxf(0.04, danger), bar.size.y)), Color(josh_color, a))
	# The KOs in a small dark badge: at the top-right corner of a wide chip, at the foot of a narrow
	# one, and on yours at the top right, clear of your head, Josh and weapon.
	var ko_at := Vector2(r.end.x - 7.0 - ko_w, r.position.y + 23.0)
	if narrow:
		ko_at = Vector2(r.end.x - 5.0 - ko_w, r.end.y - 3.0)
	if show_ko:
		var badge_h := 22.0 if narrow else 26.0
		draw_style_box(_badge_style(), Rect2(ko_at.x - 4.0, ko_at.y - badge_h + 6.0, ko_w + 8.0, badge_h))
		draw_string(font, ko_at, ko, HORIZONTAL_ALIGNMENT_LEFT, -1, ko_size, Color(GOLD, a) if f.kos > 0 else Color(1, 1, 1, 0.85 * a))
	if mine:
		var x := r.position.x + 64.0
		var josh := "%d%%" % f.josh if f.is_active() else "OUT"
		draw_string(font, Vector2(x, r.position.y + 27.0), josh, HORIZONTAL_ALIGNMENT_LEFT, -1, 26, josh_color if f.is_active() else DIM)
		if f.weapon != Weapons.NONE and f.is_active():
			var tex := ArtLibrary.weapon_texture(f.weapon)
			var wx := x
			if tex != null:
				var ih := 20.0
				var iw := minf(40.0, ih * tex.get_width() / maxf(1.0, tex.get_height()))
				ih = iw * tex.get_height() / maxf(1.0, tex.get_width())
				draw_texture_rect(tex, Rect2(wx, r.position.y + 47.0 - ih, iw, ih), false)
				wx += iw + 5.0
			if not _never_runs_out(f.weapon):
				draw_string(font, Vector2(wx, r.position.y + 47.0), str(f.weapon_supply), HORIZONTAL_ALIGNMENT_LEFT, -1, 20, GOLD)
		if auto_on:
			_auto_badge(font, Vector2(r.position.x + 6.0, r.position.y - 12.0), 16)
	if i < tags.size() and tags[i] == "CPU":
		_text(font, "CPU", r.get_center().x, r.position.y + 14.0, 15, Color(1, 1, 1, 0.9))


## A chip's box: the dark fill, rounded; yours edged 3 px in white.
func _chip_style(mine: bool) -> StyleBoxFlat:
	var key := "chip" + ("y" if mine else "")
	if not _box_styles.has(key):
		var box := StyleBoxFlat.new()
		box.bg_color = BOX_FILL
		box.set_corner_radius_all(10)
		box.anti_aliasing = true
		if mine:
			box.border_color = Color.WHITE
			box.set_border_width_all(3)
		_box_styles[key] = box
	return _box_styles[key]


## The small dark badge behind a chip's KO count.
func _badge_style() -> StyleBoxFlat:
	if not _box_styles.has("badge"):
		var box := StyleBoxFlat.new()
		box.bg_color = Color(0.05, 0.045, 0.07, 0.9)
		box.set_corner_radius_all(7)
		box.anti_aliasing = true
		_box_styles["badge"] = box
	return _box_styles["badge"]


## A team plate's box: the dark fill; the leading team's edged 3 px in white.
func _plate_style(leading: bool) -> StyleBoxFlat:
	var key := "plate" + ("l" if leading else "")
	if not _box_styles.has(key):
		var box := StyleBoxFlat.new()
		box.bg_color = BOX_FILL
		box.set_corner_radius_all(10)
		box.anti_aliasing = true
		box.border_color = Color.WHITE if leading else Color(1, 1, 1, 0.2)
		box.set_border_width_all(3 if leading else 1)
		_box_styles[key] = box
	return _box_styles[key]


## Quick-chat lines over the fighters who sent them, fading out: above the speaker's name tag (as
## _draw_tags placed it this frame), never on it.
func _draw_bubbles(font: Font) -> void:
	for b: Dictionary in bubbles:
		var i := int(b.get("fighter", -1))
		if i < 0 or i >= state.fighters.size() or not state.fighters[i].is_active():
			continue
		var body := _screen_rect(Simulation.hurtbox(state.fighters[i], rules.tuning))
		var text := str(b.get("text", ""))
		var alpha := float(b.get("alpha", 1.0))
		var width := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, 26).x
		var above := body.position.y - 68.0
		if _tag_tops.has(i):
			above = float(_tag_tops[i]) - 6.0
		var box := Rect2(body.get_center().x - width / 2 - 14.0, above - 42.0, width + 28.0, 42.0)
		var size := get_viewport_rect().size
		box.position.x = clampf(box.position.x, TAG_EDGE_MARGIN, maxf(TAG_EDGE_MARGIN, size.x - box.size.x - TAG_EDGE_MARGIN))
		box.position.y = maxf(box.position.y, _bar_bottom + TAG_EDGE_MARGIN)
		draw_rect(box, Color(1, 1, 1, 0.92 * alpha))
		draw_string(font, Vector2(box.position.x + 14.0, box.position.y + 31.0), text, HORIZONTAL_ALIGNMENT_LEFT, -1, 26, Color(0.1, 0.09, 0.12, alpha))


# ---- name tags (rooms v1, 2.6) ----

## Whether fighter i is the one this screen plays: a room's you_index, or the fighter tagged YOU.
func _is_you(i: int) -> bool:
	if you_index >= 0:
		return i == you_index
	return i < tags.size() and (tags[i] == "YOU" or tags[i] == "AUTO")


## What fighter i's tag says: "YOU 45%" for you; with more than 6 fighters, a teammate's name (cut
## past 8 letters: short_tag_name) and its Josh, everyone else just the Josh; with 6 or fewer, name
## and Josh for all.
func tag_text(i: int) -> String:
	var f := state.fighters[i]
	if _is_you(i):
		return "YOU %d%%" % f.josh
	var who := names[i] if i < names.size() else "P%d" % (i + 1)
	if state.fighters.size() > 6:
		var mine := you_index
		if mine >= 0 and rules.has_teams() and Simulation.allies(rules, mine, i):
			return "%s %d%%" % [short_tag_name(who), f.josh]
		return "%d%%" % f.josh
	return "%s %d%%" % [who, f.josh]


## A teammate's name in a crowded tag: whole up to TAG_NAME_MAX letters (every display name in the
## cast fits), else cut to one fewer with an ellipsis, so a cut never reads as a misspelling.
static func short_tag_name(who: String) -> String:
	return who if who.length() <= TAG_NAME_MAX else who.left(TAG_NAME_MAX - 1) + "…"


const TAG_NAME_MAX := 8


## Fighter i's tag, to be drawn by _draw_tags with its bottom centre at (x, y).
func _queue_tag(f: Fighter, i: int, x: float, y: float) -> void:
	var word := tags[i] if i < tags.size() and tags[i] != "YOU" else ""
	if f.armor_frames > 0:
		word = "GUTS!"
	elif f.action == Fighter.Action.STUNNED:
		word = "STUN"
	_tags.append({"i": i, "x": x, "y": y, "word": word, "you": _is_you(i)})


## The tags queued this frame: yours first, then by fighter. One that would overlap a tag already
## placed moves to the nearest free spot (place_tags: up to 3 levels up, or a tag's width aside),
## with a leader line down to its fighter; each stays 6 px inside the screen (and under the HUD
## band). A combat word (GUTS!, STUN) or CPU leads the text inside the pill.
func _draw_tags(font: Font, size: Vector2) -> void:
	_tag_tops.clear()
	if hide_tags:
		_tags.clear()
	if _tags.is_empty():
		return
	_tags.sort_custom(func(a: Dictionary, b: Dictionary) -> bool:
		return (bool(a["you"]) and not bool(b["you"])) or (bool(a["you"]) == bool(b["you"]) and int(a["i"]) < int(b["i"])))
	var team_match := rules.has_teams()
	var wanted: Array[Rect2] = []
	var texts := PackedStringArray()
	var words := PackedStringArray()
	var sizes := PackedInt32Array()
	for t: Dictionary in _tags:
		var i := int(t["i"])
		var mine := bool(t["you"])
		var text := tag_text(i)
		# A combat word (GUTS!, STUN) or a leaver's CPU leads the pill, at the pill's own size, so
		# it is read with the tag and placed with it ("CPU 42%").
		var word := str(t["word"])
		var lead := word + " " if word != "" else ""
		var font_size := 30 if mine else (26 if _touch_text() else 22)
		var mark := 12.0 if team_match and rules.team_of(i) > 0 else 0.0
		var width := font.get_string_size(lead + text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x + 2.0 * TAG_PAD + (2.0 * mark + 6.0 if mark > 0.0 else 0.0)
		var height := font_size + 12.0
		wanted.append(Rect2(float(t["x"]) - width / 2.0, float(t["y"]) - height, width, height))
		texts.append(text)
		words.append(lead)
		sizes.append(font_size)
	var bounds := Rect2(TAG_EDGE_MARGIN, _bar_bottom + TAG_EDGE_MARGIN, size.x - 2.0 * TAG_EDGE_MARGIN, size.y - _bar_bottom - 2.0 * TAG_EDGE_MARGIN)
	var placed := place_tags(wanted, bounds)
	# Two passes: every leader line first, then the pills, so no line crosses a pill; your pill is
	# drawn last, on top of everything (it was placed first).
	for k in _tags.size():
		var t: Dictionary = _tags[k]
		var anchor := Vector2(float(t["x"]), float(t["y"]))
		var r: Rect2 = placed[k]
		if r.end.y < anchor.y - 3.0 or anchor.x < r.position.x or anchor.x > r.end.x: # moved: a leader line down to its fighter
			var c := _color_of(int(t["i"]))
			draw_line(Vector2(clampf(anchor.x, r.position.x + 10.0, r.end.x - 10.0), r.end.y), anchor, Color(0, 0, 0, 0.6), 4.0, true)
			draw_line(Vector2(clampf(anchor.x, r.position.x + 10.0, r.end.x - 10.0), r.end.y), anchor, Color(c, 0.9), 2.0, true)
	var order: Array[int] = []
	for k in _tags.size():
		if not bool(_tags[k]["you"]):
			order.append(k)
	for k in _tags.size():
		if bool(_tags[k]["you"]):
			order.append(k)
	for k in order:
		var t: Dictionary = _tags[k]
		var i := int(t["i"])
		var mine := bool(t["you"])
		var font_size := sizes[k]
		var mark := 12.0 if team_match and rules.team_of(i) > 0 else 0.0
		var r: Rect2 = placed[k]
		draw_style_box(_tag_style(Color.WHITE if mine else _color_of(i), mine), r)
		var x := r.position.x + TAG_PAD
		if mark > 0.0:
			TeamStyle.draw_glyph(self, Vector2(x + mark, r.get_center().y), mark, rules.team_of(i))
			x += 2.0 * mark + 6.0
		var baseline := r.position.y + 6.0 + font_size * 0.86
		var lead := words[k]
		if lead != "":
			draw_string(font, Vector2(x, baseline), lead, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, GOLD if lead != "CPU " else Color(0.75, 0.85, 1.0))
			x += font.get_string_size(lead, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
		var josh_color := Color.WHITE.lerp(DANGER, clampf(state.fighters[i].josh / DANGER_JOSH, 0.0, 1.0))
		draw_string(font, Vector2(x, baseline), texts[k], HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, josh_color)
		_tag_tops[i] = r.position.y
	_tags.clear()


## Where each tag goes, in order (yours first): `wanted` are the tags over their fighters, `bounds`
## what they must stay inside. A tag that would overlap one already placed tries, nearest first,
## TAG_STEPS levels up (its height and 2 px each), and at each level its width and 2 px to either
## side, and takes the first spot that is free; in a pile where none is, the spot it overlaps least.
## Static, so tests can check a pile of 15 (rooms v1, polish 9).
static func place_tags(wanted: Array[Rect2], bounds: Rect2) -> Array[Rect2]:
	var placed: Array[Rect2] = []
	for want in wanted:
		var best := _clamp_rect(want, bounds)
		var best_overlap := _overlap(best, placed)
		if best_overlap > 0.0:
			var step := want.size.y + 2.0
			var side := want.size.x + 2.0
			# Nearest first (a tag is about three times as wide as it is tall): up, up two, either
			# side, up three, then aside and up.
			for move: Vector2i in TAG_MOVES:
				var r := _clamp_rect(Rect2(want.position + Vector2(move.x * side, -move.y * step), want.size), bounds)
				var o := _overlap(r, placed)
				if o < best_overlap - 0.5:
					best = r
					best_overlap = o
				if o <= 0.0:
					break
		placed.append(best)
	return placed


## Where a crowded tag may go (place_tags), in tag widths aside (x) and heights up (y): up to
## three levels up, and at each level one aside either way.
const TAG_MOVES := [Vector2i(0, 1), Vector2i(0, 2), Vector2i(-1, 0), Vector2i(1, 0), Vector2i(0, 3),
	Vector2i(-1, 1), Vector2i(1, 1), Vector2i(-1, 2), Vector2i(1, 2), Vector2i(-1, 3), Vector2i(1, 3)]
const TAG_PAD := 12.0


static func _clamp_rect(r: Rect2, bounds: Rect2) -> Rect2:
	r.position.x = clampf(r.position.x, bounds.position.x, maxf(bounds.position.x, bounds.end.x - r.size.x))
	r.position.y = clampf(r.position.y, bounds.position.y, maxf(bounds.position.y, bounds.end.y - r.size.y))
	return r


## The area of r that the placed rectangles cover (summed; 0 when it is free).
static func _overlap(r: Rect2, placed: Array[Rect2]) -> float:
	var total := 0.0
	for q in placed:
		var i := r.intersection(q)
		if i.has_area():
			total += i.get_area()
	return total


## The pill behind a tag: the dark fill, edged 3 px in the fighter's colour (yours white).
func _tag_style(edge: Color, mine: bool) -> StyleBoxFlat:
	var key := "tag" + edge.to_html() + ("y" if mine else "")
	if not _box_styles.has(key):
		var box := StyleBoxFlat.new()
		box.bg_color = TAG_FILL
		box.border_color = edge
		box.set_border_width_all(3)
		box.set_corner_radius_all(20)
		box.anti_aliasing = true
		_box_styles[key] = box
	return _box_styles[key]


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


func _draw_fighter_art(font: Font, painter: FighterPainter, f: Fighter, i: int, seen := 1.0) -> void:
	var t := rules.tuning
	var k := _ppm / 50.0
	var feet := _screen_point(f.pos_x, f.pos_y)
	if f.hit_stop > 0 and f.frozen_by_hit and shake_enabled:
		feet.x += (3.0 if state.frame % 2 == 0 else -3.0) * k
	var c: Color = _color_of(i)
	var px := _ppm / ART_PX_PER_M
	# A coloured disc under the feet, with a dark rim, says who is who (or whose team) in a crowd.
	draw_set_transform(feet, 0.0, Vector2(1.0, 0.28))
	draw_circle(Vector2.ZERO, 34.0 * px * 2.2, Color(c, 0.75 * seen))
	draw_set_transform_matrix(Transform2D.IDENTITY)
	var disc_r := 34.0 * px * 2.2
	draw_polyline(Transform2D(0.0, Vector2(disc_r, disc_r), 0.0, feet) * _disc_outline(), Color(0, 0, 0, 0.7 * seen), 2.0, true)
	if f.armor_frames > 0:
		draw_circle(feet + Vector2(0, -90.0 * px), 110.0 * px, Color(GOLD, 0.35))
	var result := FighterPainter.Result.NONE
	if state.match_over and state.winner_index >= 0:
		# The whole winning team celebrates (team_of(-1) is 0, so a draw is safe).
		result = FighterPainter.Result.WON if Simulation.allies(rules, i, state.winner_index) else FighterPainter.Result.LOST
	if result == FighterPainter.Result.WON:
		# A ring in their colour at the winners' feet, pulsing, behind the result panel.
		var pulse := 1.0 + 0.12 * sin(_over_draws * 0.12)
		var ring_r := 34.0 * px * 3.0 * pulse
		draw_polyline(Transform2D(0.0, Vector2(ring_r, ring_r * 0.3 / 0.28), 0.0, feet) * _disc_outline(), c.lightened(0.3), maxf(3.0, 6.0 * px), true)
	# The match frame, still counting while the result is up (the state stands still then), so the
	# winners keep celebrating.
	var frame := _anim_frame()
	painter.observe(f, frame)
	var picked := painter.pick(f, frame, result, rules)
	var alpha := (0.45 if Simulation.is_intangible(f, t) else 1.0) * seen
	# A hit flashes the fighter red for as long as it freezes them.
	var tint := Color(1.0, 0.5, 0.5) if f.hit_stop > 0 and f.frozen_by_hit else Color.WHITE
	if tint == Color.WHITE and f.slow_frames > 0:
		tint = SLOWED_TINT # slowed (soaked, chilled, stuck): a blue tint
	painter.draw(self, feet, px, f.facing, picked[0], picked[1], alpha, tint)

	var label_y := feet.y - 185.0 * px
	if f.guts_timer > 0:
		var filled := 1.0 - float(f.guts_timer) / rules.guts_delay_frames
		var bar := Rect2(feet.x - 40.0 * k, feet.y + 8.0 * k, 80.0 * k, 6.0 * k)
		draw_rect(bar, Color(GOLD, 0.3))
		draw_rect(Rect2(bar.position, Vector2(bar.size.x * filled, bar.size.y)), GOLD)
	_queue_tag(f, i, feet.x, label_y) # drawn after the front art, out of each other's way (_draw_tags)


## The floor disc's outline, a unit ellipse 0.28 as tall as it is wide (made once; scaled and placed
## per fighter with a Transform2D, so no sines per fighter per frame).
static var _disc_points := PackedVector2Array()


static func _disc_outline() -> PackedVector2Array:
	if _disc_points.is_empty():
		for step in 33:
			var a := TAU * step / 32.0
			_disc_points.append(Vector2(cos(a), sin(a) * 0.28))
	return _disc_points


## The size of text drawn in the arena (labels over heads): under touch TOUCH_TEXT times bigger and
## never under MIN_TEXT (rooms v1, fix 16).
func _arena_font(base: int) -> int:
	return maxi(MIN_TEXT, int(base * TOUCH_TEXT)) if _touch_text() else base


func _touch_text() -> bool:
	return touch_layout or touch_text


## How much wider than the painting the level is: 1, or 1.5 on the big level (StageDefinition.big).
func _level_k() -> float:
	return 1.5 if rules.stage.size == StageDefinition.SIZE_BIG else 1.0


## The level's half width in metres: 16, or 24 on the big level.
func _half_w() -> float:
	return ARENA_HALF_WIDTH * _level_k()


func half_width() -> float:
	return _half_w()


## Level pixels (the arena art's 1280 x 720 frame, on the big level 1280 * k wide with its centre at
## 640 * k) to screen pixels.
func _level_point(x: float, y: float) -> Vector2:
	return Vector2(_origin.x + (x - LEVEL_CENTRE_X * _level_k()) / LEVEL_PX_PER_M * _ppm, _origin.y - (LEVEL_FLOOR_Y - y) / LEVEL_PX_PER_M * _ppm)


## The painted layers behind the fighters (z < 0), or in front of them (z > 0). On the big level
## each layer is split at its centre with an insert of e = (k - 1) * 1280 level px between the halves
## (rooms v1, 2.5): [0, 640], then [640 - e/2, 640] mirrored, then [640 - e/2, 640], then [640, 1280],
## so every seam joins identical columns and the bookend buildings stay at the true edges. Past the
## painted sides (a screen wider than the level) the outer columns are mirrored outward: the "out"
## zone, dimmed (_draw_beyond_art for the back layers; the front ones are drawn dark there).
func _draw_layers(arena: Dictionary, front: bool) -> void:
	var stage_id := rules.stage.id
	var size := get_viewport_rect().size
	var e := (_level_k() - 1.0) * LEVEL_WIDTH
	var scale := _ppm / LEVEL_PX_PER_M # screen pixels per level pixel
	for layer: Dictionary in arena.get("layers", []):
		if (float(layer.get("z", 0)) > 0.0) != front:
			continue
		var tex := ArtLibrary.arena_texture(stage_id, str(layer["file"]))
		if tex == null:
			continue
		var x := float(layer.get("x", 0))
		var y := float(layer.get("y", 0))
		var w := float(layer.get("w", LEVEL_WIDTH))
		var h := float(layer.get("h", LEVEL_HEIGHT))
		var sx := tex.get_width() / maxf(1.0, w) # texture pixels per level pixel, across
		var mid := w / 2.0
		_layer_piece(tex, sx, 0.0, mid, x, y, h, false)
		if e > 0.0:
			_layer_piece(tex, sx, mid - e / 2.0, mid, x + mid, y, h, true)
			_layer_piece(tex, sx, mid - e / 2.0, mid, x + mid + e / 2.0, y, h, false)
		_layer_piece(tex, sx, mid, w, x + mid + e, y, h, false)
		var dark := Color(0.4, 0.4, 0.42) if front else Color.WHITE
		var left := _level_point(x, 0.0).x
		var right := _level_point(x + w + e, 0.0).x
		if left > 0.0:
			var need := minf(w, left / scale + 1.0)
			_layer_piece(tex, sx, 0.0, need, x - need, y, h, true, dark)
		if right < size.x:
			var need := minf(w, (size.x - right) / scale + 1.0)
			_layer_piece(tex, sx, w - need, w, x + w + e, y, h, true, dark)


## Columns [from, to] of a layer (level px) drawn from level x `at`, mirrored or not.
func _layer_piece(tex: Texture2D, sx: float, from: float, to: float, at: float, y: float, h: float, mirrored: bool, modulate := Color.WHITE) -> void:
	var a := _level_point(at, y)
	var b := _level_point(at + (to - from), y + h)
	var src := Rect2(from * sx, 0.0, (to - from) * sx, tex.get_height())
	if mirrored:
		draw_set_transform(Vector2(b.x, 0.0), 0.0, Vector2(-1, 1))
		draw_texture_rect_region(tex, Rect2(0.0, a.y, b.x - a.x, b.y - a.y), src, modulate)
		draw_set_transform_matrix(Transform2D.IDENTITY)
	else:
		draw_texture_rect_region(tex, Rect2(a, b - a), src, modulate)


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


# ---- the camera ----

## The screen y of the bottom of the whole-level view: under touch, the line just above the move pad
## and Kick / Jump (TouchControls.floor_clear_y), where the floor sits; else the screen's bottom.
func _floor_line(size: Vector2) -> float:
	return TouchControls.floor_clear_y(size) if touch_layout else size.y


## The world y at that line: the floor itself under touch, so nobody stands under the thumbs.
func _fit_bottom() -> float:
	return 0.0 if touch_layout else FIT_BOTTOM


## The whole-level zoom (rooms v1, 2.4): the level's full width with EDGE_MARGIN beside each end, and
## FIT_TOP of height above _fit_bottom, between the HUD band and _floor_line. The camera is never
## closer than this.
func fit_ppm(size: Vector2) -> float:
	return minf(size.x / (2.0 * (_half_w() + EDGE_MARGIN)), (_floor_line(size) - _bar_bottom) / (FIT_TOP - _fit_bottom()))


## The whole level, always (every room, and the "whole arena" setting): centred, never panned and
## never zoomed in. When the highest fighter's head would go above the top of the view, it pulls
## back, to PULL_BACK of the fit at most, easing there and back. The floor stays on _floor_line.
func _fit_camera(size: Vector2) -> void:
	var fit := fit_ppm(size)
	var bottom := _fit_bottom()
	var room := _floor_line(size) - _bar_bottom
	var target := fit
	if state != null:
		var head := -INF
		for f in state.fighters:
			if f.is_active():
				head = maxf(head, Fx.to_float(f.pos_y) + HEAD_ABOVE)
		if head > bottom + room / fit:
			target = maxf(PULL_BACK * fit, minf(fit, room / (head - bottom)))
	_ease_zoom(target)
	_cam_ppm = clampf(_cam_ppm, PULL_BACK * fit, fit) # also when the window changed size
	_cam = Vector2.ZERO
	_ppm = _cam_ppm
	_origin = Vector2(size.x * 0.5, _floor_line(size) + bottom * _ppm)


## Eases the zoom towards target, a step per match frame; starts there on a new match, the first
## frame, or with no match.
func _ease_zoom(target_ppm: float, target := Vector2.ZERO, move := false) -> void:
	var frame := _anim_frame() if state != null else -1
	if _cam_ppm <= 0.0 or frame < 0 or frame < _cam_frame or frame > _cam_frame + 30:
		_cam = target
		_cam_ppm = target_ppm
	elif frame != _cam_frame:
		if move:
			_cam = _cam.lerp(target, 0.07)
		_cam_ppm = lerpf(_cam_ppm, target_ppm, 0.05)
	_cam_frame = frame


## Follows the fight (outside rooms, with the "follows the fight" setting). The widest view is
## exactly the painted arena's width, so the screen is always full of arena and never shows past its
## sides; a fighter who goes off a side is gone. Zoomed: the fighters that matter, with room around
## them, never closer than CAMERA_MIN_WIDTH nor CAMERA_MAX_ZOOM times the widest view. It pans up and
## down to keep them in view, within the art's top and bottom, over the strip between the HUD band
## and _floor_line (so under touch nothing is framed under the thumbs). It eases towards that view.
func _move_camera(size: Vector2, zoom: bool) -> void:
	var strip := _floor_line(size) - _bar_bottom
	var half_width := _half_w()
	# Once the match is over the view may go RESULT_REACH past a side (into the mirrored out zone),
	# so a winner celebrating at the edge is seen whole (final round).
	var reach := half_width + (RESULT_REACH if state != null and state.match_over else 0.0)
	var full_ppm := size.x / (2.0 * half_width)
	var target_ppm := full_ppm
	var half_h := strip / (2.0 * full_ppm)
	var target := Vector2(0.0, ART_BOTTOM + half_h) # the floor near the bottom
	var focus := _focus() if state != null else ([] as Array[Fighter])
	if not focus.is_empty():
		var lo := Vector2(INF, INF)
		var hi := Vector2(-INF, -INF)
		for f in focus:
			var at := Vector2(Fx.to_float(f.pos_x), Fx.to_float(f.pos_y))
			lo = lo.min(at)
			hi = hi.max(at)
		var top := hi.y + 3.0 # heads, labels and a jump's worth of sky
		var bottom := lo.y - 1.5
		if zoom:
			var w := maxf(hi.x - lo.x + 9.0, CAMERA_MIN_WIDTH)
			var h := maxf(top - bottom, CAMERA_MIN_HEIGHT)
			target_ppm = clampf(minf(size.x / w, strip / h), full_ppm, full_ppm * CAMERA_MAX_ZOOM)
		var half_w := size.x / (2.0 * target_ppm)
		half_h = strip / (2.0 * target_ppm)
		target.x = clampf((lo.x + hi.x) / 2.0, -reach + half_w, reach - half_w)
		# Keep the highest fighter's head in view before the lowest one's feet.
		var cy := top - half_h if top - bottom > 2.0 * half_h else (top + bottom) / 2.0
		target.y = clampf(cy, ART_BOTTOM + half_h, maxf(ART_BOTTOM + half_h, ART_TOP - half_h))
	_ease_zoom(target_ppm, target, true)
	# While easing (or after the window changed size), still never past the sides.
	_cam_ppm = clampf(_cam_ppm, full_ppm, full_ppm * CAMERA_MAX_ZOOM)
	var edge := maxf(0.0, reach - size.x / (2.0 * _cam_ppm))
	_cam.x = clampf(_cam.x, -edge, edge)
	_ppm = _cam_ppm
	_origin = Vector2(size.x * 0.5 - _cam.x * _ppm, _bar_bottom + strip * 0.5 + _cam.y * _ppm)


## Who the camera frames: the winner once the match is over; in a crowd, you and the fighters near
## you (the whole arena while you're out); otherwise everyone still in.
func _focus() -> Array[Fighter]:
	var out: Array[Fighter] = []
	var n := state.fighters.size()
	if state.match_over and state.winner_index >= 0 and state.winner_index < n and state.fighters[state.winner_index].is_active():
		out.append(state.fighters[state.winner_index])
		return out
	if n > 4 and you_index >= 0 and you_index < n:
		var me := state.fighters[you_index]
		if not me.is_active():
			return out
		var near := Fx.from_int(int(CAMERA_NEAR))
		for f in state.fighters:
			if f.is_active() and absi(f.pos_x - me.pos_x) <= near and absi(f.pos_y - me.pos_y) <= near:
				out.append(f)
		return out
	for f in state.fighters:
		if f.is_active():
			out.append(f)
	return out


# ---- effects ----

## Watches what changed since the last frame and starts the effects for it: a spark on every hit
## (the attacker's own, bigger for bigger hits), dust on landing, a puff on a dodge, a burst and a
## line in the feed for a KO, the weapon's name when someone picks one up, a ring on respawn.
func _watch_fx() -> void:
	if state.frame == _seen_frame:
		return
	var n := state.fighters.size()
	var fresh := _seen.size() != n or state.frame < _seen_frame or state.frame > _seen_frame + 10
	_seen_frame = state.frame
	if fresh:
		_seen.clear()
		for f in state.fighters:
			_seen.append([f.josh, f.grounded, f.falls, f.is_active(), Fx.to_float(f.pos_x), Fx.to_float(f.pos_y), f.action, f.weapon, f.kos, f.hit_count])
		if state.frame < 10:
			_fx.clear()
			_feed.clear()
			# Everyone enters with a flash ring in their colour, so 15 arrivals are easy to tell apart.
			for i in n:
				var f := state.fighters[i]
				if f.is_active():
					_add_spawn(i, Fx.to_float(f.pos_x), Fx.to_float(f.pos_y), state.frame)
		return
	_watch_bursts()
	for i in n:
		var f := state.fighters[i]
		var s: Array = _seen[i]
		var x := Fx.to_float(f.pos_x)
		var y := Fx.to_float(f.pos_y)
		var active := f.is_active()
		var who := _character(i)
		var c: Color = _color_of(i)
		if active and bool(s[3]):
			var hurt := f.josh - int(s[0])
			if hurt > 0:
				var by := f.last_hit_by - 1
				_add_fx("hit", x, y + 0.95, 14, _character(by) if by >= 0 and by != i else who, 1.0 + minf(hurt, 25.0) / 18.0)
				if hurt >= 8:
					_shake = maxf(_shake, minf(12.0, hurt * 0.6))
			# A sweet spot shouts its name over the fighter it landed on.
			if f.hit_count != int(s[9]) and f.last_hit_sweet and WeaponInfo.shout(f.last_hit_weapon) != "":
				_fx.append({"kind": "word", "x": x, "y": y + 2.6, "age": 0, "life": 60, "text": WeaponInfo.shout(f.last_hit_weapon), "color": Color("ff5a4f"), "size": 1.5})
				_shake = maxf(_shake, 9.0)
			if f.grounded and not bool(s[1]) and f.action != Fighter.Action.HITSTUN:
				_add_fx("landing", x, y, 16, who, 1.0)
			if f.action == Fighter.Action.DODGE and int(s[6]) != Fighter.Action.DODGE:
				_add_fx("dodge", x, y + 0.9, 16, who, 1.0)
			if f.weapon != Weapons.NONE and f.weapon != int(s[7]) and f.action != Fighter.Action.ATTACK:
				_fx.append({"kind": "word", "x": x, "y": y + 2.3, "age": 0, "life": 50, "text": WeaponInfo.name(f.weapon).to_upper() + "!", "color": GOLD, "size": 1.0})
		elif active and not bool(s[3]):
			_add_spawn(i, x, y, 0) # a respawn
		if f.falls > int(s[2]):
			_fx.append({"kind": "ko", "x": float(s[4]), "y": float(s[5]) + 0.9, "age": 0, "life": 46, "color": c, "size": 1.0})
			_shake = maxf(_shake, 16.0)
			var killer := -1
			for j in n:
				if j != i and state.fighters[j].kos > int(_seen[j][8]):
					killer = j
			_feed.append({"killer": killer, "victim": i, "age": 0})
			if _feed.size() > 4:
				_feed.pop_front()
	for i in n:
		var f := state.fighters[i]
		var s: Array = _seen[i]
		s[0] = f.josh
		s[1] = f.grounded
		s[2] = f.falls
		s[3] = f.is_active()
		if f.is_active():
			s[4] = Fx.to_float(f.pos_x)
			s[5] = Fx.to_float(f.pos_y)
		s[6] = f.action
		s[7] = f.weapon
		s[8] = f.kos
		s[9] = f.hit_count


## A burst since last frame (BurstWatch): a flash where it was, purple for a black hole, a soft
## puff for one that only opens.
func _watch_bursts() -> void:
	for b: Array in BurstWatch.bursts(_bursting, state, _half_w() + 1.0):
		var big := bool(b[2])
		var opens := bool(b[4])
		_fx.append({"kind": "blast", "x": b[0], "y": float(b[1]) + 0.25, "age": 0, "life": 22, "size": maxf(0.6, float(b[3])),
			"color": Color("b070ff") if big else (Color("d8e8d0") if opens else Color("ffb020"))})
		_shake = maxf(_shake, 10.0 if big else (2.0 if opens else 7.0))
	_bursting = BurstWatch.snapshot(state)


func _character(i: int) -> String:
	return fighter_characters[i] if i >= 0 and i < fighter_characters.size() else ""


## A fighter's name for the feed: the name without its "YOU" / "CPU" tag.
func _short_name(i: int) -> String:
	if i < 0:
		return "?"
	var who := names[i] if i < names.size() else "P%d" % (i + 1)
	return who


## The spawn-in flash ring (polish 3) for fighter i standing at (x, y) metres, `age` frames in
## already (a room's first frame drawn may be a few frames into the match).
func _add_spawn(i: int, x: float, y: float, age: int) -> void:
	if age >= SPAWN_FRAMES:
		return
	_fx.append({"kind": "spawn", "x": x, "y": y + 0.9, "age": age, "life": SPAWN_FRAMES, "color": _color_of(i), "size": 1.0})


func _add_fx(kind: String, x: float, y: float, life: int, who: String, fx_size: float) -> void:
	_fx.append({"kind": kind, "x": x, "y": y, "age": 0, "life": life, "who": who, "size": fx_size})
	if _fx.size() > 48:
		_fx.pop_front()


func _fx_point(fx: Dictionary) -> Vector2:
	return Vector2(_origin.x + float(fx["x"]) * _ppm, _origin.y - float(fx["y"]) * _ppm)


func _draw_fx(font: Font) -> void:
	var size := get_viewport_rect().size
	var alive: Array = []
	for fx: Dictionary in _fx:
		var age := int(fx["age"])
		var life := int(fx["life"])
		var t := float(age) / life
		var at := _fx_point(fx)
		var fade := 1.0 - clampf((t - 0.55) / 0.45, 0.0, 1.0)
		var pop := 0.55 + 0.6 * (1.0 - pow(1.0 - minf(1.0, t * 4.0), 2.0))
		match str(fx["kind"]):
			"hit", "landing", "dodge":
				var kind := str(fx["kind"])
				var metres := (1.2 if kind == "hit" else 1.6) * float(fx["size"]) * pop
				var tex := ArtLibrary.effect_texture(str(fx.get("who", "")), kind)
				var half := metres * _ppm / 2.0
				if kind == "landing":
					at.y -= half * 0.45 # the dust sits on the ground
				if tex != null:
					draw_texture_rect(tex, Rect2(at - Vector2(half, half), Vector2(half, half) * 2.0), false, Color(1, 1, 1, fade))
				else:
					_draw_burst(at, half, 8, Color(1, 1, 1, fade), maxf(2.0, _ppm * 0.05))
			"ko":
				# Where they went out, pulled inside the screen so the burst is always seen: under the
				# HUD and the top-right buttons, and under touch above the thumbs.
				var low := (TouchControls.floor_clear_y(size) - 40.0) if touch_layout else size.y - 150.0
				at = at.clamp(Vector2(110.0, maxf(_bar_bottom, hud_top_inset) + 110.0), Vector2(size.x - 110.0, maxf(maxf(_bar_bottom, hud_top_inset) + 110.0, low)))
				var c: Color = fx["color"]
				var reach := (0.6 + 2.6 * t) * maxf(_ppm, 60.0 / 3.2) # at least 60 px at its widest
				_draw_burst(at, reach, 12, Color(c, fade), maxf(4.0, _ppm * 0.12))
				draw_circle(at, reach * 0.55, Color(1, 1, 1, 0.5 * fade), false, maxf(3.0, _ppm * 0.08), true)
				_text(font, "KO!", at.x, at.y + 16.0, int(64 * pop), Color(c.lightened(0.3), fade))
			"word":
				var c: Color = fx["color"]
				var word := str(fx["text"])
				var word_size := maxi(22, int(26 * float(fx["size"]) * (pop if float(fx["size"]) > 1.0 else 1.0)))
				# Kept whole on screen: its measured width 20 px inside the sides (rooms v1, fix 30).
				var half := font.get_string_size(word, HORIZONTAL_ALIGNMENT_LEFT, -1, word_size).x / 2.0
				var wx := clampf(at.x, 20.0 + half, maxf(20.0 + half, size.x - 20.0 - half))
				_text(font, word, wx, at.y - t * _ppm * 0.9, word_size, Color(c, fade))
			"blast":
				# A burst as wide as its blast: a flash, a ring racing out, and rays.
				var c: Color = fx["color"]
				var reach := float(fx["size"]) * _ppm * (0.35 + 0.65 * minf(1.0, t * 2.5))
				draw_circle(at, reach * (1.0 - 0.5 * t), Color(1.0, 0.95, 0.8, 0.75 * (1.0 - t)))
				draw_circle(at, reach, Color(c, fade), false, maxf(4.0, _ppm * 0.12), true)
				_draw_burst(at, reach * 1.15, 10, Color(c, fade), maxf(3.0, _ppm * 0.08))
			"spawn":
				# A white flash in the fighter's colour, then a ring racing out and fading: 0.3 s.
				var c: Color = fx["color"]
				var reach := maxf(1.5 * _ppm, SPAWN_MIN_PX) * (0.35 + 0.65 * ease_in(t * 1.6))
				var gone := 1.0 - t
				draw_circle(at, reach * 0.8, Color(c.lightened(0.35), 0.45 * gone * gone))
				if t < 0.3:
					draw_circle(at, reach * 0.45, Color(1, 1, 1, 0.7 * (1.0 - t / 0.3)))
				draw_circle(at, reach, Color(c, gone), false, maxf(4.0, _ppm * 0.1), true)
				draw_circle(at, reach, Color(0, 0, 0, 0.35 * gone), false, 1.5, true)
		fx["age"] = age + 1
		if age + 1 < life:
			alive.append(fx)
	_fx = alive


## Outside the painting (rooms v1, 2.5), after the back layers: above the art's top the arena's sky,
## the painting fading into it over its top SKY_FADE metres (an eased fade, so neither end shows a
## line); below the art's bottom its ground colour, darkening by half downwards; past the painted
## sides the "out" zone, the mirrored art dimmed into OUT_DIM over OUT_FADE metres. Arenas without
## skyTop / ground use sky and sky darkened.
func _draw_beyond_art(arena: Dictionary, sky: Color, sky_top: Color, size: Vector2) -> void:
	var ground := Color(str(arena["ground"])) if arena.has("ground") else sky.darkened(0.55)
	var art_top := _level_point(0.0, 0.0).y
	if art_top > 0.0:
		_draw_sky_above(sky_top, art_top, size)
		var fade := SKY_FADE * _ppm
		var points := PackedVector2Array()
		var colours := PackedColorArray()
		for k in SKY_FADE_STEPS + 1:
			var t := float(k) / SKY_FADE_STEPS
			var y := art_top + fade * t
			var c := Color(sky_top, 1.0 - smoothstep(0.0, 1.0, t))
			points.append_array([Vector2(0, y), Vector2(size.x, y)])
			colours.append_array([c, c])
		for k in SKY_FADE_STEPS:
			var i := 2 * k
			draw_polygon(PackedVector2Array([points[i], points[i + 1], points[i + 3], points[i + 2]]),
				PackedColorArray([colours[i], colours[i + 1], colours[i + 3], colours[i + 2]]))
	var art_bottom := _level_point(0.0, LEVEL_HEIGHT).y
	if art_bottom < size.y:
		draw_polygon(PackedVector2Array([Vector2(0, art_bottom - 1.0), Vector2(size.x, art_bottom - 1.0), Vector2(size.x, size.y), Vector2(0, size.y)]),
			PackedColorArray([ground, ground, ground.darkened(0.5), ground.darkened(0.5)]))
	var clear := Color(OUT_DIM, 0.0)
	var band := OUT_FADE * _ppm
	for side: int in [-1, 1]:
		var edge := _origin.x + side * _half_w() * _ppm
		if (side < 0 and edge <= 0.0) or (side > 0 and edge >= size.x):
			continue # the side is off screen
		var outer := edge + side * band
		draw_polygon(PackedVector2Array([Vector2(edge, 0), Vector2(outer, 0), Vector2(outer, size.y), Vector2(edge, size.y)]),
			PackedColorArray([clear, OUT_DIM, OUT_DIM, clear]))
		var far := 0.0 if side < 0 else size.x
		if (side < 0 and outer > 0.0) or (side > 0 and outer < size.x):
			draw_rect(Rect2(minf(outer, far), 0.0, absf(far - outer), size.y), OUT_DIM)


## The sky above the painting, from the screen's top down to y = art_top: skyTop at the painting's
## edge, deepening to sky_deep(skyTop) SKY_GRADIENT metres higher, so the band over a 16:9 or 4:3
## big level reads as sky rather than a flat panel (final round).
func _draw_sky_above(sky_top: Color, art_top: float, size: Vector2) -> void:
	var deep := sky_deep(sky_top)
	var reach := SKY_GRADIENT * _ppm
	var top_y := maxf(0.0, art_top - reach)
	var top_colour := sky_top.lerp(deep, (art_top - top_y) / reach)
	if top_y > 0.0:
		draw_rect(Rect2(0.0, 0.0, size.x, top_y + 1.0), deep)
	draw_polygon(PackedVector2Array([Vector2(0, top_y), Vector2(size.x, top_y), Vector2(size.x, art_top + 1.0), Vector2(0, art_top + 1.0)]),
		PackedColorArray([top_colour, top_colour, sky_top, sky_top]))


## The tone the sky deepens to above the painting: a little darker and more saturated than skyTop,
## as a real sky is overhead.
static func sky_deep(sky_top: Color) -> Color:
	return Color.from_hsv(sky_top.h, clampf(sky_top.s * 1.12, 0.0, 1.0), sky_top.v * 0.8)


## The arena's three hazards in their art, by where each is in its cycle (Hazards): resting,
## warning (it shakes under a "!"), active, cooling down. With hazards off they rest.
func _draw_hazards(font: Font, arena: Dictionary) -> void:
	var stage := rules.stage
	var frame := state.frame if state != null else 0
	var list: Array = arena.get("hazards", [])
	for k in stage.hazard_kinds.size():
		var phase := Hazards.phase_name(frame, k) if rules.hazards and state != null else "idle"
		var zone := _screen_rect(stage.hazard_zones[k])
		var foot := Vector2(zone.get_center().x, zone.end.y)
		var warn := phase == "warning"
		if k < list.size():
			var h: Dictionary = list[k]
			var file: Variant = h.get("states", {}).get(phase, null)
			var tex := ArtLibrary.arena_texture(stage.id, str(file)) if file != null else null
			if tex != null:
				var w := float(h.get("w", 160)) / LEVEL_PX_PER_M * _ppm
				var hh := float(h.get("h", 160)) / LEVEL_PX_PER_M * _ppm
				var shake := sin(frame * 1.7) * _ppm * 0.05 if warn else 0.0
				draw_texture_rect(tex, Rect2(foot.x - w / 2 + shake, foot.y - hh, w, hh), false)
			else:
				draw_rect(zone, Color(DANGER, 0.35 if phase == "active" else 0.12))
		else:
			draw_rect(zone, Color(DANGER, 0.35 if phase == "active" else 0.12))
		if warn and frame % 16 < 10:
			_text(font, "!", foot.x, zone.position.y - 8.0, int(maxf(28.0, _ppm * 0.9)), GOLD)
		elif phase == "active":
			# What it does, at a glance: up for a launch pad, sideways for a pusher, a burst for a striker.
			var c := Color(1, 1, 1, 0.55 + 0.25 * sin(frame * 0.6))
			var mid := zone.get_center()
			match stage.hazard_kinds[k]:
				Hazards.LAUNCH:
					_draw_chevrons(mid, Vector2.UP, c)
				Hazards.PUSH:
					_draw_chevrons(mid, Vector2(stage.hazard_dirs[k], 0), c)
				_:
					_draw_burst(mid, _ppm * 0.9, 8, Color(DANGER, c.a), maxf(3.0, _ppm * 0.07))


## Three chevrons marching along `dir`: which way a launch pad or pusher sends you.
func _draw_chevrons(mid: Vector2, dir: Vector2, color: Color) -> void:
	var side := Vector2(-dir.y, dir.x)
	var step := _ppm * 0.45
	var march := fmod((state.frame if state != null else 0) * 0.06, 1.0) * step
	for n in 3:
		var tip := mid + dir * (step * (n - 1) + march)
		var arm := _ppm * 0.28
		draw_polyline(PackedVector2Array([tip - dir * arm + side * arm, tip, tip - dir * arm - side * arm]), color, maxf(3.0, _ppm * 0.08), true)


## Rays around a point: the plain spark, and the KO burst.
func _draw_burst(at: Vector2, reach: float, rays: int, color: Color, width: float) -> void:
	for r in rays:
		var dir := Vector2.from_angle(TAU * r / rays + 0.3)
		draw_line(at + dir * reach * 0.45, at + dir * reach, color, width, true)


## The KO feed: who knocked out whom, the killer and the victim each in their colour, newest at the
## bottom, fading after 4 s. Under touch it sits in the band under the floor line, between the move
## pad and the right-hand buttons (TouchControls.free_band), where it covers nobody. Otherwise it
## runs down the right side under the scene's top-right buttons (hud_top_inset), on a light plate,
## and a line goes faint while a fighter is behind it (review of rooms v1).
func _draw_feed(font: Font, size: Vector2) -> void:
	var band := TouchControls.free_band(size) if touch_layout else Rect2()
	var in_band := touch_layout and band.size.x >= 160.0
	var y := (band.position.y + 40.0) if in_band else maxf(_bar_bottom, hud_top_inset) + 34.0
	var right := band.end.x - 12.0 if in_band else size.x - BAR_MARGIN - 14.0
	var behind := [] if in_band else _fighter_rects()
	var keep: Array = []
	for e: Dictionary in _feed:
		var age := int(e["age"])
		var fade := 1.0 - clampf((age - 180.0) / 60.0, 0.0, 1.0)
		var killer := int(e["killer"])
		var victim := int(e["victim"])
		var parts: Array = [[_short_name(killer), _color_of(killer).lightened(0.25), true], ["  KO  ", Color.WHITE, false], [_short_name(victim), _color_of(victim).lightened(0.25), true]] \
			if killer >= 0 else [[_short_name(victim), _color_of(victim).lightened(0.25), true], [" fell", Color.WHITE, false]]
		var font_size := FEED_FONT
		var width := _parts_width(font, parts, font_size)
		if in_band:
			# Between the buttons the line must fit the band: names are cut before the type shrinks
			# far, so it never runs into a button (final round).
			var fitted := fit_feed_line(font, parts, band.size.x - 24.0)
			parts = fitted[0]
			font_size = fitted[1]
			width = fitted[2]
		# A new line slides in from the right (polish 1); in the band, between buttons, it fades in.
		var x := right - width + (1.0 - ease_in(age / float(EASE_FRAMES))) * (width + 60.0)
		var alpha := fade
		if in_band:
			x = maxf(right - width, band.position.x + 12.0)
			alpha *= ease_in(age / float(EASE_FRAMES))
		var plate := Rect2(x - 12.0, y - 26.0, width + 24.0, 34.0)
		for r: Rect2 in behind:
			if plate.intersects(r):
				alpha *= 0.35
				break
		draw_rect(plate, Color(0.05, 0.045, 0.07, (0.8 if in_band else 0.5) * alpha))
		for part: Array in parts:
			var c: Color = part[1]
			draw_string(font, Vector2(x, y), str(part[0]), HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, Color(c, alpha))
			x += font.get_string_size(str(part[0]), HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
		y += 38.0
		e["age"] = age + 1
		if age + 1 < 240:
			keep.append(e)
	_feed = keep


## A KO feed line ([[text, colour, is a name], ...]) fitted to `room` pixels: at FEED_FONT if it
## fits; else with the names cut to TAG_NAME_MAX letters (short_tag_name); else in smaller type, down
## to FEED_MIN_FONT; else with the names cut shorter, to 3 letters and an ellipsis at least. Returns
## [parts, font size, width].
static func fit_feed_line(font: Font, parts: Array, room: float) -> Array:
	var font_size := FEED_FONT
	var width := _parts_width(font, parts, font_size)
	if width <= room:
		return [parts, font_size, width]
	parts = _cut_names(parts, TAG_NAME_MAX)
	width = _parts_width(font, parts, font_size)
	if width > room:
		font_size = maxi(FEED_MIN_FONT, int(font_size * room / width))
		width = _parts_width(font, parts, font_size)
	var letters := TAG_NAME_MAX - 1
	while width > room and letters >= 3:
		parts = _cut_names(parts, letters)
		width = _parts_width(font, parts, font_size)
		letters -= 1
	return [parts, font_size, width]


## The parts with each name cut to `most` letters (the last one an ellipsis).
static func _cut_names(parts: Array, most: int) -> Array:
	var out: Array = []
	for part: Array in parts:
		var text := str(part[0])
		var is_name := part.size() > 2 and bool(part[2])
		if is_name and text.length() > most:
			text = text.left(most - 1) + "…"
		out.append([text, part[1], is_name])
	return out


static func _parts_width(font: Font, parts: Array, font_size: int) -> float:
	var width := 0.0
	for part: Array in parts:
		width += font.get_string_size(str(part[0]), HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	return width


## Each active fighter's body and tag on screen this frame (from the feet to HEAD_ABOVE up, 1.2 m
## wide), so overlays can go faint over them.
func _fighter_rects() -> Array:
	var out: Array = []
	if state == null:
		return out
	for f in state.fighters:
		if f.is_active():
			var feet := _screen_point(f.pos_x, f.pos_y)
			out.append(Rect2(feet.x - 0.6 * _ppm, feet.y - HEAD_ABOVE * _ppm, 1.2 * _ppm, HEAD_ABOVE * _ppm))
	return out


## "READY?" then "FIGHT!" over the first two seconds of a match.
func _draw_start_banner(font: Font, size: Vector2) -> void:
	if state.match_over or state.frame >= 110:
		return
	if state.frame < 50:
		# It eases in over the first 0.15 s (polish 1).
		var e := ease_in(state.frame / float(EASE_FRAMES))
		_banner(font, "READY?", size, size.y * WINNER_Y, int(72 * (0.8 + 0.2 * e)), Color(1, 1, 1, e), e)
		return
	var t := (state.frame - 50) / 60.0
	var pop := 1.5 - 0.5 * minf(1.0, t * 6.0)
	var fade := 1.0 - clampf((t - 0.6) / 0.4, 0.0, 1.0)
	_banner(font, "FIGHT!", size, size.y * WINNER_Y, int(96 * pop), Color(GOLD, fade), fade)


## Big text on a dark band across the screen.
func _banner(font: Font, text: String, size: Vector2, baseline_y: float, font_size: int, color: Color, fade := 1.0) -> void:
	draw_rect(Rect2(0.0, baseline_y - font_size * 0.95, size.x, font_size * 1.3), Color(0.05, 0.045, 0.07, 0.5 * fade))
	_text(font, text, size.x / 2, baseline_y, font_size, color)
