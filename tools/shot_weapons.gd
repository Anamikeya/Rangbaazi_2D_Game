extends SceneTree
## Saves full-size frames of a CPU weapon brawl, for checking how the match looks: the camera,
## weapons, boxes, projectiles, effects, banners and the KO burst. Needs a window (not --headless):
##   godot --path client -s ../tools/shot_weapons.gd -- <output folder> [<stage id>] [<fighters>] [options]
## Options (rooms v1, polish 7):
##   --teams[=N]   a room on N teams (default: as many as a room of that size needs, 2 to 4), the
##                 fighters spread over them the way people joining a lobby are (1, 2, 3, 4, 1, ...)
##   --big         the big level (1.5 times as wide), as a room of 9 or more plays
##   --size=WxH    the window, e.g. --size=2400x1080 (default 1600x900)
##   --touch       lay the arena out for the touch controls (the floor on the line above the thumbs;
##                 the controls themselves are main.gd's and aren't drawn)
##   --lift        hold fighter 1 19 m up for two seconds from frame 600, so the camera pulls back
## More than 4 fighters, --teams or --big draw the match as a room does: the HUD strip, the clock,
## pill tags, the whole level, with the room's insets for Chat and the menu. Rooms are timed (40 s
## here) and, once over, show main.gd's result panel (ResultScreen) over the arena, with a stand-in
## countdown, so the result is in the frames. It saves frames 6 (the spawn rings), 40 and 75 (the start
## banners), a few later ones, 12 frames after the first KO, one during --lift, and one after the
## match ends (or at the last frame); the file names carry the window size.
## It also prints the average frame time over the match (with saving frames, so a rough figure).

const SHOTS := [6, 40, 75, 150, 330, 440, 490, 760, 1200]
const LAST := 3000
const CAST := ["shahn", "suhani", "rishabh", "shubham", "akshat", "salik", "ankit", "chandan"]
const ROOM_SECONDS := 40
const LIFT_FROM := 600
const LIFT_FRAMES := 120
const LIFT_Y := 19

var _view: ArenaView
var _state: GameState
var _rules: MatchRules
var _out := ""
var _tag := ""
var _frame := 0
var _falls := 0
var _ko_shot := -1
var _over_shot := -1
var _cast: Array = []
var _room := false
var _lift := false
var _started_usec := 0
var _result: ResultScreen


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	var plain: Array[String] = []
	var teams := -1
	var big := false
	var touch := false
	var window := Vector2i(1600, 900)
	for a: String in args:
		if a == "--teams":
			teams = 0
		elif a.begins_with("--teams="):
			teams = int(a.trim_prefix("--teams="))
		elif a == "--big":
			big = true
		elif a == "--touch":
			touch = true
		elif a == "--lift":
			_lift = true
		elif a.begins_with("--size="):
			var wh := a.trim_prefix("--size=").split("x")
			window = Vector2i(int(wh[0]), int(wh[1]))
		else:
			plain.append(a)
	_out = plain[0] if not plain.is_empty() else OS.get_user_data_dir()
	var stage_id: String = plain[1] if plain.size() > 1 else "rooftops"
	var n := clampi(int(plain[2]) if plain.size() > 2 else 4, 2, 15)
	_cast = CAST.slice(0, mini(n, CAST.size()))
	if n > CAST.size():
		# A bigger crowd: the rest of the 31 fighters, in the art library's order.
		for f: Dictionary in ArtLibrary.fighters():
			var id := str(f["id"])
			if _cast.size() < n and not _cast.has(id):
				_cast.append(id)
	DisplayServer.window_set_size(window)
	root.size = window
	_tag = "%dx%d%s" % [window.x, window.y, "_touch" if touch else ""]
	_room = n > 4 or teams >= 0 or big
	var stage := StageDefinition.by_id(stage_id, StageDefinition.SIZE_BIG if big else StageDefinition.SIZE_NORMAL)
	if stage == null:
		printerr("No stage '%s' (an arena id such as rooftops or shahn; --big needs an arena stage)." % stage_id)
		quit(1)
		return
	var sigs := PackedInt32Array()
	for c: String in _cast:
		sigs.append(Weapons.signature_for(c))
	if _room:
		var team_list := PackedInt32Array()
		if teams >= 0:
			var count := teams if teams >= 2 else RoomSession.teams_needed(n)
			count = clampi(count, RoomSession.teams_needed(n), RoomSession.MAX_TEAMS)
			for i in n:
				team_list.append(1 + i % count)
		_rules = MatchRules.room(stage, 10, sigs, team_list)
		_rules.time_limit_frames = ROOM_SECONDS * Simulation.TICKS_PER_SECOND
	else:
		_rules = MatchRules.new()
		_rules.weapons = true
		_rules.hazards = true
		_rules.flurry = true
		_rules.stocks = 1
		_rules.stage = stage
		_rules.signatures = sigs
	_state = Simulation.create_initial_state(_rules, _cast.size(), 11)
	_view = ArenaView.new()
	_view.rules = _rules
	_view.state = _state
	_view.show_hitboxes = false
	_view.fighter_characters = PackedStringArray(_cast)
	_view.touch_layout = touch
	var names := PackedStringArray()
	for c: String in _cast:
		names.append(ArtLibrary.fighter_name(c))
	_view.names = names
	if _room:
		# Drawn the way a room is (main.gd): the clock, the HUD strip clear of Chat and the menu,
		# the whole level, "you" as fighter 0.
		_view.room_mode = true
		_view.you_index = 0
		_view.show_banner = false
		_view.hud_right_inset = 2.0 * 20.0 + 2.6 * 104.0
		_view.hud_top_inset = 20.0 + 104.0 + 10.0
		_view.time_left_frames = _rules.time_limit_frames
	root.add_child(_view)
	process_frame.connect(_step)
	_started_usec = Time.get_ticks_usec()


func _step() -> void:
	var inputs := PackedInt32Array()
	for i in _cast.size():
		inputs.append(SparringBot.think(_state, _rules, i, SparringBot.Level.HARD))
	Simulation.advance(_state, _rules, inputs)
	if _lift and _frame >= LIFT_FROM and _frame < LIFT_FROM + LIFT_FRAMES and _state.fighters.size() > 1:
		# Only this tool does this, to show the pull-back: the game never moves a fighter by hand.
		var f := _state.fighters[1]
		f.pos_y = Fx.from_int(LIFT_Y)
		f.vel_y = 0
	if _room:
		_view.time_left_frames = maxi(0, _rules.time_limit_frames - _state.frame)
	_view.queue_redraw()
	_frame += 1
	var falls := 0
	for f in _state.fighters:
		falls += f.falls
	if falls > _falls and _ko_shot < 0:
		_ko_shot = _frame + 12
	_falls = falls
	if _state.match_over and _over_shot < 0:
		_over_shot = _frame + 70
		if _room:
			_show_result()
	# The picture taken now shows the frame drawn on the last pass.
	var shown := _frame - 2
	if shown in SHOTS:
		_save("frame_%d" % shown)
	if _lift and shown == LIFT_FROM + 60:
		_save("lift")
	if _frame == _ko_shot:
		_save("ko")
	if _frame == _over_shot or _frame == LAST:
		_save("over")
		var ms := (Time.get_ticks_usec() - _started_usec) / 1000.0 / maxf(1.0, _frame)
		print("frames %d, %.1f ms a frame on average (saving pictures included)" % [_frame, ms])
		quit(0)


## The room's result panel, as main.gd shows it (layer 3 over the arena; the countdown full).
func _show_result() -> void:
	var layer := CanvasLayer.new()
	layer.layer = 3
	root.add_child(layer)
	_result = ResultScreen.new()
	layer.add_child(_result)
	var w := _state.winner_index
	_result.show_room(_state, _rules, _view.names, _view.fighter_characters, 0,
		Nicknames.for_winner(_state, Nicknames.code_for(_state, w) if w >= 0 else -1), 1.0, RoomSession.RESULT_TICKS / 60)


func _save(label: String) -> void:
	var path := _out.path_join("shot_%s_%s.png" % [_tag, label])
	root.get_texture().get_image().save_png(path)
	print("saved ", path)
