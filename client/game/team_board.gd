class_name TeamBoard
extends GridContainer
## The lobby's teams (docs/design/rooms-teams-view-ui-v1.md, 1.8): one card per team, two to a row.
## A whole card is one flat button, so a tap anywhere on it asks to join that team. Each card has
## the team's colour band and symbol, its name, "3/4", up to four rows (each player's face and name,
## with "(host)" / "(you)"), and on a full card "Tap to take a CPU's place" or "FULL". Your own team has
## a border all round. Built from code; RoomScreen feeds it the lobby.

signal team_picked(team: int)

const CARD_SIZE := Vector2(350, 240)
const ROW_FONT := 28
## A player's row: its height, and the face drawn at its left (final round: faces, as the
## free-for-all chips have).
const ROW_H := 36.0
const FACE := 32.0
const HEADER_FONT := 34


func _init() -> void:
	columns = 2
	add_theme_constant_override("h_separation", 16)
	add_theme_constant_override("v_separation", 16)
	size_flags_horizontal = Control.SIZE_EXPAND_FILL


## Shows `team_count` cards for the lobby's players ([{id, name, cpu, team, left}]).
func show_teams(team_count: int, players: Array, my_id: int, host_id: int) -> void:
	while get_child_count() > team_count:
		var last := get_child(get_child_count() - 1)
		remove_child(last)
		last.queue_free()
	while get_child_count() < team_count:
		var card := TeamCard.new()
		card.team = get_child_count() + 1
		card.pressed.connect(func() -> void: team_picked.emit(card.team))
		add_child(card)
		# A new card fades in (rooms v1, polish 1).
		if card.is_inside_tree():
			card.modulate.a = 0.0
			card.create_tween().tween_property(card, "modulate:a", 1.0, RoomScreen.FADE_SEC).set_ease(Tween.EASE_OUT)
	for c in get_children():
		var card := c as TeamCard
		var rows: Array = []
		for p: Dictionary in players:
			if int(p.get("team", 0)) == card.team:
				rows.append(p)
		card.rows = rows
		card.my_id = my_id
		card.host_id = host_id
		card.queue_redraw()


## The card of a team (1 to 4), or null. For tests.
func card_of(team: int) -> Button:
	return get_child(team - 1) as Button if team >= 1 and team <= get_child_count() else null


class TeamCard extends Button:
	var team := 1
	var rows: Array = []
	var my_id := -1
	var host_id := -1

	func _init() -> void:
		flat = true
		focus_mode = Control.FOCUS_NONE
		custom_minimum_size = TeamBoard.CARD_SIZE
		size_flags_horizontal = Control.SIZE_EXPAND_FILL
		clip_contents = true

	func _mine() -> bool:
		for p: Dictionary in rows:
			if int(p.get("id", -1)) == my_id:
				return true
		return false

	func _has_cpu() -> bool:
		for p: Dictionary in rows:
			if bool(p.get("cpu", false)):
				return true
		return false

	func _draw() -> void:
		var font := get_theme_default_font()
		var c := TeamStyle.color_of(team)
		var r := Rect2(Vector2.ZERO, size)
		var fill := ArenaView.BOX_FILL
		if is_hovered() or button_pressed:
			fill = fill.lightened(0.08)
		draw_rect(r, Color(fill, 0.92))
		draw_rect(Rect2(0, 0, 10, size.y), c)
		if _mine():
			draw_rect(r.grow(-2), Color.WHITE, false, 4.0)
		TeamStyle.draw_glyph(self, Vector2(38, 32), 14.0, team)
		draw_string(font, Vector2(62, 44), TeamStyle.name_of(team), HORIZONTAL_ALIGNMENT_LEFT, -1, HEADER_FONT, c)
		var count := "%d/%d" % [rows.size(), RoomSession.TEAM_SIZE]
		var cw := font.get_string_size(count, HORIZONTAL_ALIGNMENT_LEFT, -1, HEADER_FONT).x
		draw_string(font, Vector2(size.x - 16 - cw, 44), count, HORIZONTAL_ALIGNMENT_LEFT, -1, HEADER_FONT, Color.WHITE)
		var y := 82.0
		for k in RoomSession.TEAM_SIZE:
			if k < rows.size():
				var p: Dictionary = rows[k]
				var text := str(p.get("name", "?"))
				var id := int(p.get("id", -1))
				if bool(p.get("left", false)):
					text += " (CPU)"
				elif id == my_id:
					text += " (you)"
				elif id == host_id:
					text += " (host)"
				var face := ArenaView.head_texture_for(str(p.get("character", "shahn")))
				if face != null:
					var fk := minf(FACE / face.get_width(), FACE / face.get_height())
					var fs := Vector2(face.get_width(), face.get_height()) * fk
					draw_texture_rect(face, Rect2(Vector2(22.0 + (FACE - fs.x) / 2.0, y - 26.0 + (FACE - fs.y) / 2.0), fs), false,
						Color(1, 1, 1, 0.6 if bool(p.get("left", false)) else 1.0))
				draw_string(font, Vector2(62, y), text, HORIZONTAL_ALIGNMENT_LEFT, size.x - 74, ROW_FONT, Color.WHITE if id == my_id else Color(1, 1, 1, 0.85))
			else:
				draw_string(font, Vector2(62, y), "open", HORIZONTAL_ALIGNMENT_LEFT, -1, ROW_FONT, Color(1, 1, 1, 0.35))
			y += ROW_H
		if rows.size() >= RoomSession.TEAM_SIZE and not _mine():
			var footer := "Tap to take a CPU's place" if _has_cpu() else "FULL"
			var fw := font.get_string_size(footer, HORIZONTAL_ALIGNMENT_LEFT, -1, 22).x
			var fc := UiTheme.ACCENT if _has_cpu() else Color(1, 1, 1, 0.4)
			draw_rect(Rect2(10, size.y - 34, size.x - 10, 34), Color(0, 0, 0, 0.45))
			draw_string(font, Vector2(10 + (size.x - 10 - fw) / 2, size.y - 10), footer, HORIZONTAL_ALIGNMENT_LEFT, -1, 22, fc)
