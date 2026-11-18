class_name RoomScreen
extends Control
## The room lobby (RoomSession). Left: the room code to share and who's in. Middle: your fighter
## (tap to change, in the character circle). Right: the arena card (the host taps it to choose,
## in the arena slider), sliders for the match length (10 to 15 minutes) and the CPUs, and Start.
## Guests see the host's choices and wait. Built from code; main.gd feeds it the session each tick.

signal minutes_changed(minutes: int)
signal bots_changed(count: int)
signal start_pressed
signal leave_pressed
signal fighter_requested
signal arena_requested

var _title: Label
var _share: Label
var _status: Label
var _players: Label
var _fighter: ArtPreview
var _fighter_name: Label
var _arena: ArtPreview
var _arena_name: Label
var _host_box: VBoxContainer
var _minutes_slider: HSlider
var _minutes_label: Label
var _bots_slider: HSlider
var _bots_label: Label
var _last_key := ""
## While the host drags a slider, their value wins over the server's until it has caught up.
var _local_until_msec := 0
const LOCAL_MSEC := 1000
var _syncing := false


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()
	var backdrop := ColorRect.new()
	backdrop.color = Color(0.05, 0.045, 0.07, 0.9)
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
	left.add_theme_constant_override("separation", 16)
	left.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	columns.add_child(left)
	_title = _label("", 80, UiTheme.ACCENT, HORIZONTAL_ALIGNMENT_LEFT)
	left.add_child(_title)
	_share = _label("", 26, UiTheme.TEXT_DIM, HORIZONTAL_ALIGNMENT_LEFT)
	_share.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	left.add_child(_share)
	_players = _label("", 30, UiTheme.TEXT, HORIZONTAL_ALIGNMENT_LEFT)
	left.add_child(_players)
	var leave := _button("Leave room")
	leave.pressed.connect(func() -> void: leave_pressed.emit())
	left.add_child(leave)

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
	right.add_child(_label("ARENA", 28, UiTheme.TEXT_DIM))
	_arena = ArtPreview.new()
	_arena.custom_minimum_size = Vector2(620, 300)
	_arena.pressed.connect(func() -> void: arena_requested.emit())
	right.add_child(_arena)
	_arena_name = _label("", 40, UiTheme.ACCENT)
	right.add_child(_arena_name)
	_status = _label("", 28, UiTheme.TEXT_DIM)
	_status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	right.add_child(_status)

	_host_box = VBoxContainer.new()
	_host_box.add_theme_constant_override("separation", 10)
	right.add_child(_host_box)
	_minutes_label = _label("", 32, UiTheme.TEXT, HORIZONTAL_ALIGNMENT_LEFT)
	_host_box.add_child(_minutes_label)
	_minutes_slider = _slider(10, 15, func(v: int) -> void:
		_minutes_label.text = "Match length: %d min" % v
		minutes_changed.emit(v))
	_host_box.add_child(_minutes_slider)
	_bots_label = _label("", 32, UiTheme.TEXT, HORIZONTAL_ALIGNMENT_LEFT)
	_host_box.add_child(_bots_label)
	_bots_slider = _slider(0, 14, func(v: int) -> void:
		_bots_label.text = "CPU fighters: %d" % v
		bots_changed.emit(v))
	_host_box.add_child(_bots_slider)
	var start := _button("START MATCH")
	start.custom_minimum_size = Vector2(0, 120)
	start.add_theme_font_size_override("font_size", 48)
	start.pressed.connect(func() -> void: start_pressed.emit())
	_host_box.add_child(start)


## Shows the session's lobby. share_link is "" where there is none (a local test server).
func show_lobby(session: RoomSession, share_link: String) -> void:
	_fighter.fighter_id = session.my_character() if session.my_character() != "" else GameSettings.character
	_fighter_name.text = ArtLibrary.fighter_name(_fighter.fighter_id)
	_arena.arena_id = session.stage_id
	_arena_name.text = ArtLibrary.arena_title(session.stage_id)
	var key := "%s|%d|%d|%s|%s|%s" % [session.room, session.host_id, session.minutes, str(session.players), share_link, session.stage_id]
	if key == _last_key:
		return
	if Time.get_ticks_msec() < _local_until_msec:
		return # the host is dragging: keep their numbers until the server has caught up
	_last_key = key
	_title.text = "Room %s" % session.room
	_share.text = "Friends join with the code %s%s" % [session.room, ("\nor this link: %s" % share_link) if share_link != "" else ""]
	var lines := PackedStringArray()
	for p: Dictionary in session.players:
		var tags := PackedStringArray()
		if int(p.get("id", -1)) == session.host_id:
			tags.append("host")
		if int(p.get("id", -1)) == session.my_id:
			tags.append("you")
		lines.append("%s  -  %s%s" % [str(p.get("name", "?")), ArtLibrary.fighter_name(str(p.get("character", "shahn"))),
			("  (%s)" % ", ".join(tags)) if not tags.is_empty() else ""])
	_players.text = "Players %d/%d\n%s" % [session.players.size(), RoomSession.MAX_PLAYERS, "\n".join(lines)]
	var host := session.is_host()
	_host_box.visible = host
	_syncing = true
	_minutes_slider.value = session.minutes
	_bots_slider.value = session.bot_count()
	_syncing = false
	_minutes_label.text = "Match length: %d min" % session.minutes
	_bots_label.text = "CPU fighters: %d" % session.bot_count()
	_status.text = "Tap the arena to change it. Slide to set the match, then start." if host \
		else "Waiting for the host to start the match..."


func _slider(lo: int, hi: int, on_change: Callable) -> HSlider:
	var s := HSlider.new()
	s.min_value = lo
	s.max_value = hi
	s.step = 1
	s.tick_count = hi - lo + 1
	s.ticks_on_borders = true
	s.custom_minimum_size = Vector2(0, 70)
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
