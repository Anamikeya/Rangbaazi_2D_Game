extends SceneTree
## Saves full-size frames of a CPU weapon brawl, for checking how the match looks: the camera,
## weapons, boxes, projectiles, effects, banners and the KO burst. Needs a window (not --headless):
##   godot --path client -s ../tools/shot_weapons.gd -- <output folder> [<stage id>] [<fighters>]
## It saves frames 40 and 75 (the start banners), a few later ones, 12 frames after the first KO,
## and one after the match ends (or at the last frame).

const SHOTS := [40, 75, 150, 330, 440, 490, 760]
const LAST := 3000
const CAST := ["shahn", "suhani", "rishabh", "shubham", "akshat", "salik", "ankit", "chandan"]

var _view: ArenaView
var _state: GameState
var _rules: MatchRules
var _out := ""
var _frame := 0
var _falls := 0
var _ko_shot := -1
var _over_shot := -1
var _cast: Array = []


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	_out = args[0] if not args.is_empty() else OS.get_user_data_dir()
	var stage_id: String = args[1] if args.size() > 1 else "rooftops"
	_cast = CAST.slice(0, clampi(int(args[2]) if args.size() > 2 else 4, 2, CAST.size()))
	DisplayServer.window_set_size(Vector2i(1600, 900))
	root.size = Vector2i(1600, 900)
	_rules = MatchRules.new()
	_rules.weapons = true
	_rules.hazards = true
	_rules.flurry = true
	_rules.stocks = 1
	_rules.stage = StageDefinition.by_id(stage_id)
	var sigs := PackedInt32Array()
	for c: String in _cast:
		sigs.append(Weapons.signature_for(c))
	_rules.signatures = sigs
	_state = Simulation.create_initial_state(_rules, _cast.size(), 11)
	_view = ArenaView.new()
	_view.rules = _rules
	_view.state = _state
	_view.show_hitboxes = false
	_view.fighter_characters = PackedStringArray(_cast)
	var names := PackedStringArray()
	for c: String in _cast:
		names.append(ArtLibrary.fighter_name(c))
	_view.names = names
	if _cast.size() > 4:
		# A crowd is drawn the way a room is: the clock, the scoreboard, and the camera on "you".
		_view.room_mode = true
		_view.you_index = 0
		_view.time_left_frames = 9 * 60
		_rules.stocks = 3
		for f in _state.fighters:
			f.stocks = 3
	root.add_child(_view)
	process_frame.connect(_step)


func _step() -> void:
	var inputs := PackedInt32Array()
	for i in _cast.size():
		inputs.append(SparringBot.think(_state, _rules, i, SparringBot.Level.HARD))
	Simulation.advance(_state, _rules, inputs)
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
	# The picture taken now shows the frame drawn on the last pass.
	var shown := _frame - 2
	if shown in SHOTS:
		_save("frame_%d" % shown)
	if _frame == _ko_shot:
		_save("ko")
	if _frame == _over_shot or _frame == LAST:
		_save("over")
		quit(0)


func _save(label: String) -> void:
	var path := _out.path_join("shot_%s.png" % label)
	root.get_texture().get_image().save_png(path)
	print("saved ", path)
