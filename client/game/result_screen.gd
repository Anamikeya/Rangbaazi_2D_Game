class_name ResultScreen
extends Control
## The result panel (docs/design/rooms-teams-view-ui-v1.md, 3.3; fixes 2 and 13), on its own
## CanvasLayer over the arena, which keeps running behind it at the whole-level view (the winners in
## their WON pose with a ring). main.gd shows it when the match is over and hides the touch controls.
##   Rooms, free-for-all: everyone ranked (Standings.fighters_ranked): place, face, name, KOs, falls;
##     the winner on top, your row lit; two columns past 8. A bar counts down to the lobby.
##   Rooms, teams: "BLUE TEAM WINS!" (or the draw and why), the MVP and their nickname, then the teams
##     in rank order (Standings.teams_ranked), each with its members and their KOs.
##   Practice: "YOU WIN!" / "YOU LOSE", the two fighters, and Play again, Change fighter, Change arena
##     and Menu.
## It only reads the state it is given.

signal play_again_pressed
signal change_fighter_pressed
signal change_arena_pressed
signal menu_pressed

const FILL := Color(0.08, 0.075, 0.11, 0.96) # the arena's tags don't show through
const EDGE := Color(1, 1, 1, 0.16)
const ROW_LIT := Color(1, 1, 1, 0.12)
const GOLD := Color("ffd23f")
const DIM := Color(1, 1, 1, 0.62)
const TOP := 112.0      # under the HUD band
const MARGIN := 40.0
const ROW_MAX := 64.0   # a fighter's row at most
const TEAM_HEAD := 64.0 # a team's header row
const MEMBER_ROW := 44.0
const PRACTICE_ROW := 78.0
const TIEBREAK_H := 36.0 # the "on fewer falls" line under a winner's title

var state: GameState
var rules: MatchRules
var names := PackedStringArray()
var characters := PackedStringArray()
## The fighter this screen plays, or -1 (watching; or two people at one keyboard).
var you := -1
var nickname := ""
var practice := false
## The room's countdown to the lobby: the fraction left (1 to 0), or below 0 for none.
var countdown := -1.0
var seconds_left := 0
var hint := ""

var _heads := {}
## Motion (rooms v1, polish 1): the panel fades in over FADE_SEC, then the rows (or team cards) slide
## in from the right one by one, ROW_SEC apart, each over FADE_SEC.
const FADE_SEC := 0.15
const ROW_SEC := 0.06
const ROW_SLIDE := 80.0
var _shown_msec := 0
var _buttons: HBoxContainer
var _play_again: Button
var _hint: Label
var _panel := Rect2()


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	theme = UiTheme.build()
	_buttons = HBoxContainer.new()
	_buttons.add_theme_constant_override("separation", 18)
	add_child(_buttons)
	_play_again = _button("Play again", play_again_pressed)
	UiTheme.make_primary(_play_again)
	_play_again.custom_minimum_size = Vector2(380, 110)
	_play_again.add_theme_font_size_override("font_size", 44)
	_button("Change fighter", change_fighter_pressed)
	_button("Change arena", change_arena_pressed)
	_button("Menu", menu_pressed)
	_hint = Label.new()
	_hint.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_hint.add_theme_font_size_override("font_size", 28)
	_hint.add_theme_color_override("font_color", DIM)
	_hint.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_hint)
	resized.connect(_layout)


## Seconds since the panel appeared.
func _age() -> float:
	return (Time.get_ticks_msec() - _shown_msec) / 1000.0


## The panel's fade, and a redraw every frame while anything is still moving in.
func _process(_delta: float) -> void:
	if not visible:
		return
	var age := _age()
	modulate.a = ArenaView.ease_in(age / FADE_SEC)
	if age < FADE_SEC + ROW_SEC * 16.0 + FADE_SEC:
		queue_redraw()


## How far right (px) row k is drawn `age` seconds after the panel appeared, or -1 while it hasn't
## started: it slides in over FADE_SEC, ROW_SEC after the one above it.
static func row_slide(k: int, age: float) -> float:
	var t := (age - FADE_SEC * 0.5 - ROW_SEC * k) / FADE_SEC
	if t < 0.0:
		return -1.0
	return (1.0 - ArenaView.ease_in(t)) * ROW_SLIDE


func _button(text: String, emit: Signal) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.custom_minimum_size = Vector2(0, 110)
	b.add_theme_font_size_override("font_size", 36)
	b.pressed.connect(func() -> void: emit.emit())
	_buttons.add_child(b)
	return b


## Shows a room's result. `left` is the countdown's fraction left (below 0: none).
func show_room(match_state: GameState, match_rules: MatchRules, fighter_names: PackedStringArray, fighter_characters: PackedStringArray,
		mine: int, nick: String, left: float, seconds: int) -> void:
	state = match_state
	rules = match_rules
	names = fighter_names
	characters = fighter_characters
	you = mine
	nickname = nick
	practice = false
	countdown = left
	seconds_left = seconds
	hint = ""
	_show()


## Shows a practice result: `mine` is 0, or -1 with two people at one keyboard.
func show_practice(match_state: GameState, match_rules: MatchRules, fighter_names: PackedStringArray, fighter_characters: PackedStringArray,
		mine: int, nick: String, keys_hint: String) -> void:
	state = match_state
	rules = match_rules
	names = fighter_names
	characters = fighter_characters
	you = mine
	nickname = nick
	practice = true
	countdown = -1.0
	hint = keys_hint
	_show()


func _show() -> void:
	if not visible:
		show()
		_shown_msec = Time.get_ticks_msec()
		_process(0.0)
	_buttons.visible = practice
	_hint.visible = practice and hint != ""
	_hint.text = hint
	_layout()
	queue_redraw()


func _layout() -> void:
	if state == null:
		return
	_panel = panel_rect(size)
	if practice:
		_buttons.reset_size()
		var w := _buttons.get_combined_minimum_size().x
		# The keys hint goes under the buttons' real bottom; the buttons move up by its height when
		# it shows (_content_height leaves 40 px for it).
		var lift := 40.0 if _hint.visible else 0.0
		_buttons.position = Vector2(_panel.get_center().x - w / 2.0, _panel.end.y - 150.0 - lift)
		_hint.reset_size()
		var buttons_bottom := _buttons.position.y + _buttons.get_combined_minimum_size().y
		_hint.position = Vector2(_panel.get_center().x - _hint.size.x / 2.0, buttons_bottom + 8.0)


## Where the panel is on a screen of this size (for the smoke test too): as tall as what it holds,
## under the HUD band, never past the screen's foot.
func panel_rect(view: Vector2) -> Rect2:
	var room := view.y - TOP - 30.0
	var h := minf(room, _content_height())
	var w := minf(view.x - 2.0 * MARGIN, 1400.0 if practice else 1560.0)
	var top := TOP + (maxf(0.0, (room - h) / 2.0) if practice else 0.0)
	return Rect2(Vector2((view.x - w) / 2.0, top), Vector2(w, h))


## The height of what the panel shows: the title and its line, the rows or team cards, and the
## buttons (practice) or the countdown (rooms).
func _content_height() -> float:
	if state == null:
		return 600.0
	var head := 178.0 + (TIEBREAK_H if not practice and tiebreak_line(state, rules) != "" else 0.0)
	if practice:
		return head + state.fighters.size() * PRACTICE_ROW + 30.0 + 150.0 + (40.0 if hint != "" else 0.0)
	if rules != null and rules.has_teams():
		var ranked := Standings.teams_ranked(state, rules)
		var most := 0
		for t: Dictionary in ranked:
			most = maxi(most, (t["members"] as Array).size())
		var per_col := ceili(ranked.size() / 2.0) if ranked.size() > 1 else 1
		return head + per_col * (TEAM_HEAD + most * MEMBER_ROW + 28.0) + 80.0
	var n := state.fighters.size()
	return head + ceili(n / (2.0 if n > 8 else 1.0)) * ROW_MAX + 90.0


# ---- drawing ----

func _draw() -> void:
	if state == null:
		return
	var font := ThemeDB.fallback_font
	draw_style_box(_box(FILL, EDGE, 20), _panel)
	var y := _panel.position.y + 86.0
	var teams := not practice and rules != null and rules.has_teams()
	y = _draw_title(font, y, teams)
	if teams:
		_draw_teams(font, y)
	else:
		_draw_ranked(font, y)
	if countdown >= 0.0:
		_draw_countdown(font)


## The headline and the nickname (or MVP) line under it; returns the y under them.
func _draw_title(font: Font, y: float, teams: bool) -> float:
	var w := state.winner_index
	var cx := _panel.get_center().x
	if teams:
		var ranked := Standings.teams_ranked(state, rules)
		var team := rules.team_of(w) if w >= 0 else 0
		if team > 0:
			var text := "%s WINS!" % TeamStyle.title_of(team)
			_centred(font, text, cx, y, 68, TeamStyle.color_of(team))
			var half := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, 68).x / 2.0
			TeamStyle.draw_glyph(self, Vector2(cx - half - 44.0, y - 24.0), 24.0, team)
			TeamStyle.draw_glyph(self, Vector2(cx + half + 44.0, y - 24.0), 24.0, team)
			y += 52.0
			y = _draw_tiebreak(font, y, cx)
			var mvp := "MVP  %s" % _name(w)
			if nickname != "":
				mvp += "  %s" % nickname
			_centred(font, mvp, cx, y, 34, GOLD)
		else:
			var text := "DRAW"
			if ranked.size() >= 2:
				text = "DRAW: %s and %s level on %d KOs and %d falls" % [TeamStyle.name_of(ranked[0]["team"]), TeamStyle.name_of(ranked[1]["team"]),
					int(ranked[0]["kos"]), int(ranked[0]["falls"])]
			_centred(font, text, cx, y, 52 if text.length() > 4 else 68, Color.WHITE)
		return y + 40.0
	var title := "DRAW"
	var color := Color.WHITE
	if w >= 0:
		if practice and you >= 0:
			title = "YOU WIN!" if w == you else "YOU LOSE"
		else:
			title = "YOU WIN!" if w == you else "%s WINS!" % _name(w).to_upper()
		color = GOLD if w == you or not practice else Color.WHITE
		if practice and you >= 0 and w != you:
			color = Color("ff8a80")
	_centred(font, title, cx, y, 68, color)
	y += 52.0
	if not practice:
		y = _draw_tiebreak(font, y, cx)
	var draw_line_text := draw_line_for(state, names) if w < 0 and not practice else ""
	if draw_line_text != "":
		var fit := font.get_string_size(draw_line_text, HORIZONTAL_ALIGNMENT_LEFT, -1, 34).x
		var line_size := 34 if fit <= _panel.size.x - 60.0 else maxi(20, int(34 * (_panel.size.x - 60.0) / fit))
		_centred(font, draw_line_text, cx, y, line_size, Color.WHITE)
	elif nickname != "":
		var line := nickname if (practice and you >= 0 and w == you) or w < 0 else "%s  %s" % [_name(w), nickname]
		_centred(font, line, cx, y, 34, GOLD)
	return y + 40.0


## The tiebreak line under a winner's title, when the win was decided on falls; returns the y under it.
func _draw_tiebreak(font: Font, y: float, cx: float) -> float:
	var line := tiebreak_line(state, rules)
	if line == "":
		return y
	_centred(font, line, cx, y - 6.0, 30, Color(1, 1, 1, 0.8))
	return y + TIEBREAK_H


## "on fewer falls (11 v 12)" when the winner (a team, or in a free-for-all a fighter) is level on KOs
## with the next best and won on fewer falls, so the result says why (final round); else "".
static func tiebreak_line(match_state: GameState, match_rules: MatchRules) -> String:
	if match_state == null or match_state.winner_index < 0:
		return ""
	if match_rules != null and match_rules.has_teams():
		var ranked := Standings.teams_ranked(match_state, match_rules)
		if ranked.size() >= 2 and int(ranked[0]["kos"]) == int(ranked[1]["kos"]) and int(ranked[0]["falls"]) < int(ranked[1]["falls"]):
			return "on fewer falls (%d v %d)" % [int(ranked[0]["falls"]), int(ranked[1]["falls"])]
		return ""
	var order := Standings.fighters_ranked(match_state)
	if order.size() < 2:
		return ""
	var a := match_state.fighters[int(order[0])]
	var b := match_state.fighters[int(order[1])]
	if a.kos == b.kos and a.falls < b.falls:
		return "on fewer falls (%d v %d)" % [a.falls, b.falls]
	return ""


## A free-for-all draw says who drew under its "DRAW", as the team draw does: "CPU 5 and CPU 7 level
## on 3 KOs and 2 falls", everyone sharing first place (three or more as "A, B and C"). "" when
## nobody ties.
static func draw_line_for(match_state: GameState, fighter_names: PackedStringArray) -> String:
	var order := Standings.fighters_ranked(match_state)
	if order.size() < 2:
		return ""
	var best := match_state.fighters[int(order[0])]
	var level := PackedStringArray()
	for i: int in order:
		var f := match_state.fighters[i]
		if f.kos == best.kos and f.falls == best.falls:
			level.append(fighter_names[i] if i < fighter_names.size() else "P%d" % (i + 1))
	if level.size() < 2:
		return ""
	var who := level[0]
	for k in range(1, level.size()):
		who += (" and " if k == level.size() - 1 else ", ") + level[k]
	return "%s level on %d KOs and %d falls" % [who, best.kos, best.falls]


## Everyone ranked: place, face, name, KOs and falls, the winner on top, your row lit; two columns
## past 8 fighters.
func _draw_ranked(font: Font, top: float) -> void:
	var order := Standings.fighters_ranked(state)
	var n := order.size()
	var cols := 2 if n > 8 else 1
	var per_col := ceili(float(n) / cols)
	var bottom := _panel.end.y - ((180.0 + (40.0 if _hint.visible else 0.0)) if practice else 80.0)
	var row_h := minf(PRACTICE_ROW if practice else ROW_MAX, (bottom - top) / maxf(1.0, per_col))
	var inner := _panel.grow(-30.0)
	var col_gap := 30.0
	var col_w := (inner.size.x - col_gap * (cols - 1)) / cols
	if cols == 1:
		col_w = minf(col_w, 980.0)
	var x0 := _panel.get_center().x - (col_w * cols + col_gap * (cols - 1)) / 2.0
	var place := 0
	for k in n:
		var i: int = order[k]
		# Fighters level on KOs and falls share a place.
		if k == 0 or state.fighters[i].kos != state.fighters[order[k - 1]].kos or state.fighters[i].falls != state.fighters[order[k - 1]].falls:
			place = k + 1
		var col := k / per_col
		var r := Rect2(x0 + col * (col_w + col_gap), top + (k % per_col) * row_h, col_w, row_h - 6.0)
		# The rows slide in one by one, the winner's first (polish 1).
		var slide := row_slide(k, _age())
		if slide < 0.0:
			continue
		draw_set_transform(Vector2(slide, 0.0))
		_draw_fighter_row(font, r, i, place, k == 0 and state.winner_index == i)
		draw_set_transform(Vector2.ZERO)


func _draw_fighter_row(font: Font, r: Rect2, i: int, place: int, winner: bool) -> void:
	var f := state.fighters[i]
	var mine := i == you
	if mine:
		draw_style_box(_box(ROW_LIT, Color.WHITE, 12, 3), r)
	elif winner:
		draw_style_box(_box(Color(GOLD, 0.1), Color(GOLD, 0.6), 12, 2), r)
	var c := _color_of(i)
	draw_rect(Rect2(r.position.x + 4.0, r.position.y + 6.0, 7.0, r.size.y - 12.0), c)
	var size_px := clampi(int(r.size.y * 0.52), 22, 38)
	var base := r.position.y + r.size.y * 0.5 + size_px * 0.36
	_right(font, str(place), r.position.x + 62.0, base, size_px + 4, GOLD if place == 1 else Color.WHITE)
	var head := minf(r.size.y - 6.0, 60.0)
	_draw_head(i, Rect2(r.position.x + 74.0, r.get_center().y - head / 2.0, head * 1.2, head))
	var who := _name(i) + ("  (you)" if mine else "")
	var name_x := r.position.x + 82.0 + head * 1.2
	var stats_w := 290.0 if r.size.x > 640.0 else 210.0
	var room := r.end.x - stats_w - name_x - 10.0
	while who.length() > 3 and font.get_string_size(who, HORIZONTAL_ALIGNMENT_LEFT, -1, size_px).x > room:
		who = who.left(who.length() - 1)
	draw_string(font, Vector2(name_x, base), who, HORIZONTAL_ALIGNMENT_LEFT, -1, size_px, c.lightened(0.35) if not mine else Color.WHITE)
	if rules != null and rules.has_teams() and rules.team_of(i) > 0:
		TeamStyle.draw_glyph(self, Vector2(r.position.x + 74.0, r.position.y + 10.0), 8.0, rules.team_of(i))
	var kos := "%d KO%s" % [f.kos, "" if f.kos == 1 else "s"]
	var falls := "%d fall%s" % [f.falls, "" if f.falls == 1 else "s"]
	_right(font, falls, r.end.x - 16.0, base, size_px - 4, DIM)
	_right(font, kos, r.end.x - 16.0 - stats_w * 0.48, base, size_px, Color.WHITE)


## The teams in rank order: a header (symbol, name, KOs and falls) and each member's face, name and
## KOs under it; two columns of cards for three or four teams.
func _draw_teams(font: Font, top: float) -> void:
	var ranked := Standings.teams_ranked(state, rules)
	var cols := 1 if ranked.size() <= 1 else 2
	var inner := _panel.grow(-30.0)
	var col_gap := 30.0
	var col_w := (inner.size.x - col_gap * (cols - 1)) / cols
	var bottom := _panel.end.y - 80.0
	var per_col := ceili(float(ranked.size()) / cols)
	var most := 0
	for t: Dictionary in ranked:
		most = maxi(most, (t["members"] as Array).size())
	var member_h := MEMBER_ROW
	var card_h := TEAM_HEAD + most * member_h + 14.0
	if per_col * (card_h + 14.0) > bottom - top:
		member_h = maxf(28.0, ((bottom - top) / per_col - 14.0 - TEAM_HEAD - 14.0) / maxf(1.0, most))
		card_h = TEAM_HEAD + most * member_h + 14.0
	var leader := 0
	if ranked.size() > 0 and (ranked.size() == 1 or ranked[0]["kos"] != ranked[1]["kos"] or ranked[0]["falls"] != ranked[1]["falls"]):
		leader = int(ranked[0]["team"])
	for k in ranked.size():
		var t: Dictionary = ranked[k]
		var team := int(t["team"])
		var col := k % cols
		var row := k / cols
		var card := Rect2(inner.position.x + col * (col_w + col_gap), top + row * (card_h + 14.0), col_w, card_h)
		# The team cards slide in one by one, the winners' first (polish 1).
		var slide := row_slide(k * 2, _age())
		if slide < 0.0:
			continue
		draw_set_transform(Vector2(slide, 0.0))
		var c := TeamStyle.color_of(team)
		draw_style_box(_box(Color(0, 0, 0, 0.25), Color.WHITE if team == leader else Color(c, 0.5), 14, 3 if team == leader else 2), card)
		draw_rect(Rect2(card.position.x + 4.0, card.position.y + 6.0, 10.0, card.size.y - 12.0), c)
		var hy := card.position.y + TEAM_HEAD * 0.5
		_right(font, str(k + 1), card.position.x + 56.0, hy + 14.0, 40, GOLD if team == leader else Color.WHITE)
		TeamStyle.draw_glyph(self, Vector2(card.position.x + 88.0, hy), 18.0, team)
		draw_string(font, Vector2(card.position.x + 118.0, hy + 14.0), TeamStyle.name_of(team), HORIZONTAL_ALIGNMENT_LEFT, -1, 40, c.lightened(0.15))
		_right(font, "%d KOs   %d falls" % [int(t["kos"]), int(t["falls"])], card.end.x - 20.0, hy + 12.0, 32, Color.WHITE)
		var my := card.position.y + TEAM_HEAD
		for i: int in t["members"]:
			var r := Rect2(card.position.x + 30.0, my, card.size.x - 50.0, member_h - 4.0)
			if i == you:
				draw_style_box(_box(ROW_LIT, Color.WHITE, 10, 2), r)
			var fs := clampi(int(member_h * 0.66), 20, 30)
			var base := r.position.y + r.size.y * 0.5 + fs * 0.36
			var head := r.size.y - 4.0
			_draw_head(i, Rect2(r.position.x + 8.0, r.get_center().y - head / 2.0, head * 1.2, head))
			var who := _name(i) + ("  (you)" if i == you else "") + ("  MVP" if i == state.winner_index else "")
			draw_string(font, Vector2(r.position.x + 16.0 + head * 1.2, base), who, HORIZONTAL_ALIGNMENT_LEFT, r.size.x - 200.0, fs,
				GOLD if i == state.winner_index else Color.WHITE)
			_right(font, "%d KO%s" % [state.fighters[i].kos, "" if state.fighters[i].kos == 1 else "s"], r.end.x - 10.0, base, fs, Color.WHITE)
			my += member_h
		draw_set_transform(Vector2.ZERO)


## The bar counting down to the lobby, along the panel's foot.
func _draw_countdown(font: Font) -> void:
	var bar := Rect2(_panel.position.x + 40.0, _panel.end.y - 34.0, _panel.size.x - 80.0, 10.0)
	draw_rect(bar, Color(1, 1, 1, 0.15))
	draw_rect(Rect2(bar.position, Vector2(bar.size.x * clampf(countdown, 0.0, 1.0), bar.size.y)), GOLD)
	_centred(font, "Back to the lobby in %d s" % seconds_left, _panel.get_center().x, bar.position.y - 12.0, 26, DIM)


# ---- helpers ----

func _name(i: int) -> String:
	return names[i] if i >= 0 and i < names.size() else "P%d" % (i + 1)


func _color_of(i: int) -> Color:
	if rules != null and rules.has_teams():
		return TeamStyle.color_of(rules.team_of(i))
	return ArenaView.PLAYER_COLORS[posmod(i, ArenaView.PLAYER_COLORS.size())]


func _draw_head(i: int, r: Rect2) -> void:
	var id := characters[i] if i >= 0 and i < characters.size() else ""
	if not _heads.has(id):
		_heads[id] = ArenaView.head_texture_for(id) if id != "" else null
	var tex: Texture2D = _heads[id]
	if tex == null:
		draw_circle(r.get_center(), minf(r.size.x, r.size.y) * 0.4, _color_of(i))
		return
	var k := minf(r.size.x / tex.get_width(), r.size.y / tex.get_height())
	var s := Vector2(tex.get_width(), tex.get_height()) * k
	draw_texture_rect(tex, Rect2(r.get_center() - s / 2.0, s), false)


func _centred(font: Font, text: String, cx: float, baseline: float, font_size: int, color: Color) -> void:
	var w := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	draw_string_outline(font, Vector2(cx - w / 2.0, baseline), text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, maxi(4, font_size / 9), Color(0, 0, 0, 0.6))
	draw_string(font, Vector2(cx - w / 2.0, baseline), text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, color)


func _right(font: Font, text: String, right: float, baseline: float, font_size: int, color: Color) -> void:
	var w := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	draw_string(font, Vector2(right - w, baseline), text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, color)


static func _box(fill: Color, edge: Color, radius: int, width := 2) -> StyleBoxFlat:
	var box := StyleBoxFlat.new()
	box.bg_color = fill
	box.border_color = edge
	box.set_border_width_all(width)
	box.set_corner_radius_all(radius)
	box.anti_aliasing = true
	return box
