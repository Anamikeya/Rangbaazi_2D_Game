class_name RoomScreen
extends Control
## The room lobby (RoomSession). Left: a fixed top bar (Leave room, the room's code in large type and
## a Copy link button), one chip row for the mode (free-for-all, or 2 to 4 teams), and a scrolling
## list of who's in: the team cards (TeamBoard) or, in a free-for-all, a grid of player chips.
## Middle: your fighter (tap to change, in the character circle), ringed in your team's colour.
## Right: the arena card with "Change arena" under it (the host's, in the arena slider), the status
## line, then the host's sliders for the match length (10 to 15 minutes) and the CPUs, each with its
## ends labelled, and Start. Guests see the host's choices as read-only chips ("10 min", "12 CPUs",
## "3 teams") and a waiting line. Leaving asks first ("Leave the room?  Stay / Leave"), from the
## Leave room button and from Android's back alike. Built from code; main.gd feeds it the session
## each tick (docs/design/rooms-teams-view-ui-v1.md, 1.8 and fixes 4, 7, 14, 15).

signal minutes_changed(minutes: int)
signal bots_changed(count: int)
signal teams_changed(count: int)
signal team_picked(team: int)
signal start_pressed
## "Leave" was picked on the "Leave the room?" question.
signal leave_pressed
signal fighter_requested
signal arena_requested
## Copy link: main.gd copies the room's link (or code) the same way the duel's button does.
signal copy_requested

## The mode chips, left to right: free-for-all, then 2, 3 and 4 teams.
const MODE_OPTIONS := ["Free-for-all", "2 teams", "3 teams", "4 teams"]
const STATUS_TEAMS_CAP := "4 a team at most: 9+ players need 3 teams, 13+ need 4."
const STATUS_ONE_TEAM := "Put someone on a second team to start."
const SLIDER_HEIGHT := 96.0

var _title: Label
var _share: Label
var _copy: Button
var _status: Label
var _players_title: Label
var _mode_row: ChoiceRow
var _scroll: ScrollContainer
var _board: TeamBoard
var _grid: GridContainer
var _leave: Button
var _fighter: ArtPreview
var _fighter_name: Label
var _arena: ArtPreview
var _arena_name: Label
## "BIG ARENA" beside the arena's name once the room is big enough for the big level (not a control).
var _big_chip: Label
var _change_arena: Button
var _host_box: VBoxContainer
var _minutes_slider: HSlider
var _minutes_label: Label
var _bots_slider: HSlider
var _bots_label: Label
var _start: Button
## A guest's view of the host's choices: chips that can't be tapped, and a waiting line.
var _guest_box: VBoxContainer
var _guest_minutes: Label
var _guest_bots: Label
var _guest_mode: Label
var _guest_wait: Label
## "Leave the room?  Stay / Leave", over the lobby.
var _leave_layer: Control
var _last_key := ""
## While the host drags a slider, their value wins over the server's until it has caught up.
var _local_until_msec := 0
const LOCAL_MSEC := 1000
var _syncing := false
## Motion (rooms v1, polish 1): START pulses gently while it can be pressed (PULSE_SEC a beat), the
## status line fades in when its words change, and new chips and team cards fade in, each over
## FADE_SEC.
const PULSE_SEC := 1.6
## The room code's type in the top bar, and the smallest it shrinks to (_fit_title).
const TITLE_SIZE := 64
const TITLE_MIN := 40
const PULSE_SCALE := 0.035
const FADE_SEC := 0.15
var _status_text := ""


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()
	var backdrop := ColorRect.new()
	backdrop.color = Color(0.05, 0.045, 0.07, 0.96) # the arena behind is a hint, not a second picture
	backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(backdrop)

	var margin := MarginContainer.new()
	margin.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	for side in ["left", "right", "top", "bottom"]:
		margin.add_theme_constant_override("margin_" + side, 50)
	add_child(margin)
	var columns := HBoxContainer.new()
	columns.add_theme_constant_override("separation", 50)
	margin.add_child(columns)

	# Left: the room and who's in.
	var left := VBoxContainer.new()
	left.add_theme_constant_override("separation", 14)
	left.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	columns.add_child(left)
	# A fixed top bar: Leave room can never be pushed off the screen by a long list. Then the room's
	# code in large type with Copy link beside it (fix 7).
	var top := HBoxContainer.new()
	top.add_theme_constant_override("separation", 20)
	left.add_child(top)
	_leave = _button("Leave room")
	_leave.custom_minimum_size = Vector2(230, 88)
	_leave.add_theme_font_size_override("font_size", 32)
	_leave.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	_leave.pressed.connect(ask_leave)
	top.add_child(_leave)
	_title = _label("", 64, UiTheme.ACCENT, HORIZONTAL_ALIGNMENT_CENTER)
	_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_title.clip_text = true
	_title.resized.connect(_fit_title)
	top.add_child(_title)
	_copy = _button("Copy link")
	UiTheme.make_primary(_copy)
	_copy.custom_minimum_size = Vector2(250, 88)
	_copy.add_theme_font_size_override("font_size", 34)
	_copy.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	_copy.pressed.connect(func() -> void: copy_requested.emit())
	top.add_child(_copy)
	_share = _label("", 28, UiTheme.TEXT_DIM, HORIZONTAL_ALIGNMENT_LEFT)
	_share.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	left.add_child(_share)
	_mode_row = ChoiceRow.make("", MODE_OPTIONS, 0, func(i: int) -> void:
		teams_changed.emit(0 if i == 0 else i + 1), 0.0, 88.0)
	for i in MODE_OPTIONS.size():
		# Small enough that the four chips fit the left column at 16:9 and 4:3.
		_mode_row.chip_at(i).add_theme_font_size_override("font_size", 28)
	left.add_child(_mode_row)
	_players_title = _label("", 28, UiTheme.TEXT_DIM, HORIZONTAL_ALIGNMENT_LEFT)
	left.add_child(_players_title)
	_scroll = ScrollContainer.new()
	_scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	left.add_child(_scroll)
	var list := VBoxContainer.new()
	list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_scroll.add_child(list)
	_board = TeamBoard.new()
	_board.team_picked.connect(func(team: int) -> void: team_picked.emit(team))
	list.add_child(_board)
	_grid = GridContainer.new()
	_grid.columns = 3
	_grid.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_grid.add_theme_constant_override("h_separation", 12)
	_grid.add_theme_constant_override("v_separation", 12)
	list.add_child(_grid)

	# Middle: your fighter.
	var middle := VBoxContainer.new()
	middle.add_theme_constant_override("separation", 14)
	middle.custom_minimum_size = Vector2(380, 0)
	columns.add_child(middle)
	middle.add_child(_label("YOUR FIGHTER", 28, UiTheme.TEXT_DIM))
	_fighter = ArtPreview.new()
	_fighter.custom_minimum_size = Vector2(380, 380)
	_fighter.pressed.connect(func() -> void: fighter_requested.emit())
	middle.add_child(_fighter)
	_fighter_name = _label("", 44, UiTheme.ACCENT)
	middle.add_child(_fighter_name)
	var change := _button("Change fighter")
	change.pressed.connect(func() -> void: fighter_requested.emit())
	middle.add_child(change)

	# Right: the arena and the host's settings.
	var right := VBoxContainer.new()
	right.add_theme_constant_override("separation", 14)
	right.custom_minimum_size = Vector2(620, 0)
	columns.add_child(right)
	_arena = ArtPreview.new()
	_arena.custom_minimum_size = Vector2(620, 250)
	_arena.pressed.connect(func() -> void: arena_requested.emit())
	right.add_child(_arena)
	var name_row := HBoxContainer.new()
	name_row.alignment = BoxContainer.ALIGNMENT_CENTER
	name_row.add_theme_constant_override("separation", 18)
	right.add_child(name_row)
	_arena_name = _label("", 40, UiTheme.ACCENT)
	name_row.add_child(_arena_name)
	_big_chip = UiTheme.info_chip("BIG ARENA", 26, UiTheme.ACCENT)
	_big_chip.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	_big_chip.visible = false
	name_row.add_child(_big_chip)
	# The arena picture is a button, but it doesn't look like one: say so with one (fix 15).
	_change_arena = _button("Change arena")
	_change_arena.custom_minimum_size = Vector2(0, 84)
	_change_arena.add_theme_font_size_override("font_size", 34)
	_change_arena.pressed.connect(func() -> void: arena_requested.emit())
	right.add_child(_change_arena)
	_status = _label("", 28, UiTheme.TEXT_DIM)
	_status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	right.add_child(_status)

	_host_box = VBoxContainer.new()
	_host_box.add_theme_constant_override("separation", 8)
	right.add_child(_host_box)
	_minutes_label = _label("", 32, UiTheme.TEXT, HORIZONTAL_ALIGNMENT_LEFT)
	_host_box.add_child(_minutes_label)
	_minutes_slider = _slider(10, 15, func(v: int) -> void:
		_minutes_label.text = "Match length: %d min" % v
		minutes_changed.emit(v))
	_host_box.add_child(_slider_row(_minutes_slider, "10", "15"))
	_bots_label = _label("", 32, UiTheme.TEXT, HORIZONTAL_ALIGNMENT_LEFT)
	_host_box.add_child(_bots_label)
	_bots_slider = _slider(0, 14, func(v: int) -> void:
		_bots_label.text = "CPU fighters: %d" % v
		bots_changed.emit(v))
	_host_box.add_child(_slider_row(_bots_slider, "0", "14"))
	_start = _button("START MATCH")
	UiTheme.make_primary(_start)
	_start.custom_minimum_size = Vector2(0, 116)
	_start.add_theme_font_size_override("font_size", 48)
	_start.pressed.connect(func() -> void: start_pressed.emit())
	_host_box.add_child(_start)

	# A guest sees the host's choices, read-only, and that they are waiting (fix 14).
	_guest_box = VBoxContainer.new()
	_guest_box.add_theme_constant_override("separation", 16)
	_guest_box.visible = false
	right.add_child(_guest_box)
	_guest_box.add_child(_label("THE HOST'S MATCH", 28, UiTheme.TEXT_DIM, HORIZONTAL_ALIGNMENT_LEFT))
	var chips := HFlowContainer.new()
	chips.add_theme_constant_override("h_separation", 14)
	chips.add_theme_constant_override("v_separation", 12)
	_guest_box.add_child(chips)
	_guest_minutes = UiTheme.info_chip("", 34)
	_guest_bots = UiTheme.info_chip("", 34)
	_guest_mode = UiTheme.info_chip("", 34)
	for c in [_guest_minutes, _guest_bots, _guest_mode]:
		chips.add_child(c)
	_guest_wait = _label("", 40, UiTheme.ACCENT, HORIZONTAL_ALIGNMENT_LEFT)
	_guest_wait.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_guest_box.add_child(_guest_wait)

	_build_leave_question()


## START's gentle pulse while it can be pressed: a little bigger and brighter on each beat.
func _process(_delta: float) -> void:
	if _start == null or not visible:
		return
	var live := _host_box.visible and not _start.disabled and not _leave_layer.visible
	var k := start_pulse(Time.get_ticks_msec() / 1000.0) if live else 0.0
	_start.pivot_offset = _start.size / 2.0
	_start.scale = Vector2.ONE * (1.0 + PULSE_SCALE * k)
	_start.self_modulate = Color(1.0 + 0.18 * k, 1.0 + 0.18 * k, 1.0 + 0.18 * k)


## The pulse at `seconds`: 0 to 1 and back, a smooth beat every PULSE_SEC.
static func start_pulse(seconds: float) -> float:
	return 0.5 - 0.5 * cos(TAU * fposmod(seconds, PULSE_SEC) / PULSE_SEC)


## Fades a control in over FADE_SEC (new chips and cards, a changed status line).
func _fade_in(c: CanvasItem) -> void:
	if not c.is_inside_tree():
		return
	c.modulate.a = 0.0
	var tween := c.create_tween()
	tween.tween_property(c, "modulate:a", 1.0, FADE_SEC).set_trans(Tween.TRANS_CUBIC).set_ease(Tween.EASE_OUT)


## "Leave the room?  Stay / Leave" over the whole lobby: a dimmed screen and a card.
func _build_leave_question() -> void:
	_leave_layer = Control.new()
	_leave_layer.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_leave_layer.visible = false
	add_child(_leave_layer)
	var dim := ColorRect.new()
	dim.color = Color(0.03, 0.025, 0.045, 0.75)
	dim.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_leave_layer.add_child(dim)
	var center := CenterContainer.new()
	center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_leave_layer.add_child(center)
	var box := PanelContainer.new()
	box.add_theme_stylebox_override("panel", UiTheme.panel_box())
	center.add_child(box)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 24)
	column.custom_minimum_size = Vector2(760, 0)
	box.add_child(column)
	var row := UiTheme.confirm_row("Leave the room?", "Stay", "Leave", cancel_leave, func() -> void:
		cancel_leave()
		leave_pressed.emit(), 110.0, 44)
	column.add_child(row)


## Asks "Leave the room?  Stay / Leave" (the Leave room button, Android's back, Esc).
func ask_leave() -> void:
	_leave_layer.show()


func cancel_leave() -> void:
	_leave_layer.hide()


func leave_asked() -> bool:
	return _leave_layer.visible


## The Copy link button's label ("Copy link", "Copy code", "Copied!"), set by main.gd.
func set_copy_label(text: String) -> void:
	if _copy.text != text:
		_copy.text = text


func copy_button() -> Button:
	return _copy


## Shows the session's lobby. share_link is "" where there is none (a local test server).
func show_lobby(session: RoomSession, share_link: String) -> void:
	_fighter.fighter_id = session.my_character() if session.my_character() != "" else GameSettings.character
	_fighter_name.text = ArtLibrary.fighter_name(_fighter.fighter_id)
	_arena.arena_id = session.stage_id
	_arena_name.text = ArtLibrary.arena_title(session.stage_id)
	var notice := session.current_notice()
	# The waiting line's dots move on every tick, whatever else changed.
	_guest_wait.text = ("Waiting for the host to start" + UiTheme.dots()) if session.room_phase == "lobby" \
		else ("A match is on: you'll join the next one" + UiTheme.dots())
	var key := "%s|%d|%d|%s|%s|%s|%d|%s" % [session.room, session.host_id, session.minutes, str(session.players),
		share_link, session.stage_id, session.team_count, notice]
	if key == _last_key:
		return
	if Time.get_ticks_msec() < _local_until_msec:
		return # the host is dragging: keep their numbers until the server has caught up
	_last_key = key
	_title.text = session.room
	_fit_title()
	_share.text = ("Friends tap the link, or type the code under Join on the menu." if share_link != ""
		else "Friends type this code under Join on the menu.")
	var host := session.is_host()
	var members := session.players.size()
	var teams := session.team_count > 0
	# 9 or more play the big level (StageDefinition.size_for), as the server will pick at the start.
	_big_chip.visible = StageDefinition.size_for(members) == StageDefinition.SIZE_BIG

	# The mode chips: lit for everyone, tappable for the host; too few teams for the room greyed.
	_mode_row.select(0 if not teams else session.team_count - 1)
	_mode_row.set_locked(not host)
	# A guest can't change the mode, and THE HOST'S MATCH chips already say it: no row of chips
	# that look tappable and do nothing (final round).
	_mode_row.visible = host
	var needed := RoomSession.teams_needed(members)
	for i in MODE_OPTIONS.size():
		_mode_row.set_enabled(i, i == 0 or i + 1 >= needed)

	_players_title.text = "Players %d/%d" % [members, RoomSession.MAX_PLAYERS]
	_board.visible = teams
	_grid.visible = not teams
	if teams:
		_board.show_teams(session.team_count, session.players, session.my_id, session.host_id)
	else:
		_fill_grid(session)
	_fighter.ring_color = TeamStyle.color_of(session.my_team()) if teams and session.my_team() > 0 else Color(0, 0, 0, 0)

	_host_box.visible = host
	_change_arena.visible = host
	_guest_box.visible = not host
	_guest_minutes.text = "%d min" % session.minutes
	_guest_bots.text = "%d CPU%s" % [session.bot_count(), "" if session.bot_count() == 1 else "s"]
	_guest_mode.text = "%d teams" % session.team_count if teams else "Free-for-all"
	_syncing = true
	_minutes_slider.value = session.minutes
	_bots_slider.value = session.bot_count()
	_syncing = false
	_minutes_label.text = "Match length: %d min" % session.minutes
	_bots_label.text = "CPU fighters: %d" % session.bot_count()

	var sizes := session.team_sizes()
	var used := 0
	var most := 0
	var fewest := RoomSession.TEAM_SIZE + 1
	for t in range(1, session.team_count + 1):
		if sizes[t] > 0:
			used += 1
		most = maxi(most, sizes[t])
		fewest = mini(fewest, sizes[t])
	var can_start := not teams or used >= 2
	_start.disabled = not can_start
	# One status line for every reason, the most pressing first.
	if notice != "":
		_status.text = notice
	elif teams and not can_start:
		_status.text = STATUS_ONE_TEAM
	elif teams and most - fewest >= 2:
		# Only people make teams this uneven: the server keeps CPU-filled teams within one.
		_status.text = "Teams are uneven (%d v %d)." % [most, fewest]
	elif host and needed > 2:
		_status.text = STATUS_TEAMS_CAP if teams or members >= 9 else ""
	elif teams:
		_status.text = "Tap a team to join it. Slide to set the match, then start." if host \
			else "Tap a team to join it."
	else:
		_status.text = "Slide to set the match, then start." if host \
			else "Tap your fighter to change it."
	if _status.text != _status_text:
		_status_text = _status.text
		_fade_in(_status)


## The room code at up to TITLE_SIZE, smaller (to TITLE_MIN) when a wide one (M, W) would not fit
## between Leave room and Copy link: it is what the host reads out, so it is never cut.
func _fit_title() -> void:
	var font := _title.get_theme_font("font")
	var room := _title.size.x - 8.0
	if font == null or room <= 0.0 or _title.text == "":
		return
	var font_size := TITLE_SIZE
	var width := font.get_string_size(_title.text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	if width > room:
		font_size = maxi(TITLE_MIN, int(font_size * room / width))
	if _title.get_theme_font_size("font_size") != font_size:
		_title.add_theme_font_size_override("font_size", font_size)


## The room code's label, for tests.
func title_label() -> Label:
	return _title


## The free-for-all list: a chip per member, three across.
func _fill_grid(session: RoomSession) -> void:
	while _grid.get_child_count() > session.players.size():
		var last := _grid.get_child(_grid.get_child_count() - 1)
		_grid.remove_child(last)
		last.queue_free()
	while _grid.get_child_count() < session.players.size():
		var chip := PlayerChip.new()
		_grid.add_child(chip)
		_fade_in(chip)
	for k in session.players.size():
		var p: Dictionary = session.players[k]
		var chip := _grid.get_child(k) as PlayerChip
		var id := int(p.get("id", -1))
		chip.character = str(p.get("character", "shahn"))
		chip.player_name = str(p.get("name", "?"))
		chip.tag = "CPU" if bool(p.get("cpu", false)) or bool(p.get("left", false)) else \
			("you" if id == session.my_id else ("host" if id == session.host_id else ""))
		chip.mine = id == session.my_id
		chip.queue_redraw()


## The team board, for tests (scene smoke).
func team_board() -> TeamBoard:
	return _board


func mode_row() -> ChoiceRow:
	return _mode_row


func leave_button() -> Button:
	return _leave


func big_arena_chip() -> Label:
	return _big_chip


func start_button() -> Button:
	return _start


func status_text() -> String:
	return _status.text


func guest_chips() -> Array[Label]:
	return [_guest_minutes, _guest_bots, _guest_mode]


func guest_box() -> Control:
	return _guest_box


func host_box() -> Control:
	return _host_box


func minutes_slider() -> HSlider:
	return _minutes_slider


## A slider with its two ends labelled (the least and the most it can be).
func _slider_row(slider: HSlider, lo: String, hi: String) -> HBoxContainer:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 16)
	var a := _label(lo, 30, UiTheme.TEXT_DIM)
	a.custom_minimum_size = Vector2(44, 0)
	row.add_child(a)
	slider.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(slider)
	var b := _label(hi, 30, UiTheme.TEXT_DIM)
	b.custom_minimum_size = Vector2(44, 0)
	row.add_child(b)
	return row


func _slider(lo: int, hi: int, on_change: Callable) -> HSlider:
	var s := HSlider.new()
	s.min_value = lo
	s.max_value = hi
	s.step = 1
	s.tick_count = hi - lo + 1
	s.ticks_on_borders = true
	s.custom_minimum_size = Vector2(0, SLIDER_HEIGHT)
	s.focus_mode = Control.FOCUS_NONE
	s.value_changed.connect(func(v: float) -> void:
		if _syncing:
			return
		_local_until_msec = Time.get_ticks_msec() + LOCAL_MSEC
		on_change.call(int(v)))
	return s


func _button(text: String) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.custom_minimum_size = Vector2(0, 96)
	b.add_theme_font_size_override("font_size", 38)
	return b


func _label(text: String, font_size: int, color: Color, align := HORIZONTAL_ALIGNMENT_CENTER) -> Label:
	var l := Label.new()
	l.text = text
	l.horizontal_alignment = align
	l.add_theme_font_size_override("font_size", font_size)
	l.add_theme_color_override("font_color", color)
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return l


## One member in the free-for-all list: their fighter's face, their name and a "host" / "you" /
## "CPU" tag. Not a control.
class PlayerChip extends Control:
	var character := ""
	var player_name := ""
	var tag := ""
	var mine := false
	var _painter: FighterPainter

	func _init() -> void:
		custom_minimum_size = Vector2(220, 92)
		size_flags_horizontal = Control.SIZE_EXPAND_FILL
		mouse_filter = Control.MOUSE_FILTER_IGNORE
		clip_contents = true

	func _draw() -> void:
		var font := get_theme_default_font()
		draw_rect(Rect2(Vector2.ZERO, size), Color(ArenaView.BOX_FILL, 0.92))
		if mine:
			draw_rect(Rect2(Vector2.ZERO, size).grow(-2), Color.WHITE, false, 4.0)
		if character != "":
			if _painter == null or _painter.character_id != character:
				_painter = FighterPainter.new(character)
			if _painter.is_ready():
				_painter.draw(self, Vector2(46, size.y - 4), 0.44, 1, "idle", 30)
		draw_string(font, Vector2(94, 40), player_name, HORIZONTAL_ALIGNMENT_LEFT, size.x - 100, 28, Color.WHITE)
		if tag != "":
			draw_string(font, Vector2(94, 74), tag, HORIZONTAL_ALIGNMENT_LEFT, -1, 22, UiTheme.ACCENT if tag != "CPU" else Color(1, 1, 1, 0.55))
