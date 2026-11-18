extends SceneTree
## Saves a few full-size frames of a CPU weapon brawl, for checking how weapons, boxes and
## projectiles look. Needs a window (not --headless):
##   godot --path client -s ../tools/shot_weapons.gd -- <output folder>

const SHOTS := [150, 330, 520, 760]
const CAST := ["shahn", "suhani", "rishabh", "shubham"]

var _view: ArenaView
var _state: GameState
var _rules: MatchRules
var _out := ""
var _frame := 0


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	_out = args[0] if not args.is_empty() else OS.get_user_data_dir()
	DisplayServer.window_set_size(Vector2i(1600, 900))
	root.size = Vector2i(1600, 900)
	_rules = MatchRules.new()
	_rules.weapons = true
	_rules.stage = StageDefinition.by_id("rooftops")
	var sigs := PackedInt32Array()
	for c in CAST:
		sigs.append(Weapons.signature_for(c))
	_rules.signatures = sigs
	_state = Simulation.create_initial_state(_rules, CAST.size(), 11)
	# Start everyone armed, so the first frames show weapons in hand.
	var start := [Weapons.BASEBALL_BAT, Weapons.BOW, Weapons.UZI, Weapons.SHOTGUN]
	for i in CAST.size():
		_state.fighters[i].weapon = start[i]
		_state.fighters[i].weapon_supply = Weapons.supply_of(start[i])
	_view = ArenaView.new()
	_view.rules = _rules
	_view.state = _state
	_view.fighter_characters = PackedStringArray(CAST)
	_view.names = PackedStringArray(["Shan", "Suhani", "Rishabh", "Shubham"])
	root.add_child(_view)
	process_frame.connect(_step)


func _step() -> void:
	var inputs := PackedInt32Array()
	for i in CAST.size():
		inputs.append(SparringBot.think(_state, _rules, i, SparringBot.Level.HARD))
	Simulation.advance(_state, _rules, inputs)
	_view.queue_redraw()
	_frame += 1
	if _frame - 2 in SHOTS:
		var image := root.get_texture().get_image()
		var path := _out.path_join("weapons_%d.png" % (_frame - 2))
		image.save_png(path)
		print("saved ", path)
	if _frame > SHOTS[-1] + 3:
		quit(0)
