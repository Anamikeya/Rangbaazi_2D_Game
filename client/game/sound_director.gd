class_name SoundDirector
extends Node
## The game's sound, from the art packs' original audio (client/audio, see tools/import_audio.py):
## each arena's music and ambience, each fighter's footsteps and whoosh, hits, jumps, landings,
## parries, Guts, KOs, the last-ten-seconds warning, the winner's jingle for the winning side (a
## whole team in a team match) and a lose sting for the rest, and the lobby's team and copy
## confirmations. Presentation only: it
## watches the GameState the scene draws (practice, duel or room alike) and plays sounds when
## something changes; it never changes the state. Volumes are the Music and Effects buses
## (GameSettings).

const VOICES := 12
const STEP_FRAMES := 15 # a footstep every quarter second while running
const RUN_SPEED := 0.03 # metres per frame that count as running
const WARN_FRAMES := 600 # the last ten seconds tick
const START_FRAME := 50 # "FIGHT!" (ArenaView's start banner)

var _music: AudioStreamPlayer
var _ambience: AudioStreamPlayer
var _voices: Array[AudioStreamPlayer] = []
var _next_voice := 0
var _streams: Dictionary = {}
var _scene := ""
# Per fighter, what we saw last frame: [action, grounded, frozen, falls, x, y, armor, step counter,
# weapon, Uzi shots].
var _seen: Array = []
var _seen_frame := -1
var _was_over := false
var _last_warn_second := -1
var _bursting: Array = [] # what could burst last frame (BurstWatch.snapshot)
## The level's half width plus 1 m (BurstWatch.bursts), set by the scene: 25 on the big level.
var arena_half := BurstWatch.ARENA_HALF
var _crates := 0 # weapon crates resting on the stage last frame


func _ready() -> void:
	GameSettings.apply_audio()
	_music = _player("Music")
	_ambience = _player("Music")
	_ambience.volume_db = -8.0
	for i in VOICES:
		_voices.append(_player("Effects"))


func _player(bus: String) -> AudioStreamPlayer:
	var p := AudioStreamPlayer.new()
	p.bus = bus if AudioServer.get_bus_index(bus) >= 0 else "Master"
	add_child(p)
	return p


## Sets the background: "menu", or an arena's art folder (its music and ambience). Restarts the
## loops only when the place changes.
func set_scene(scene: String) -> void:
	if scene == _scene:
		return
	_scene = scene
	var folder := "shahn" if scene == "menu" else scene
	_loop(_music, "res://audio/music/%s.wav" % folder)
	if scene == "menu":
		_ambience.stop()
		_ambience.set_meta("loop", false)
	else:
		_loop(_ambience, "res://audio/ambience/%s.wav" % folder)
	_music.volume_db = -6.0 if scene == "menu" else 0.0


func _loop(player: AudioStreamPlayer, path: String) -> void:
	var stream := _stream(path)
	if stream == null:
		player.stop()
		player.set_meta("loop", false)
		return
	player.set_meta("loop", true)
	if player.stream != stream or not player.playing:
		player.stream = stream
		player.play()


## On the web, Godot plays sounds as browser samples and the music and ambience don't loop there
## (the track plays once, then silence), so a loop that has stopped is started again here.
func _process(_delta: float) -> void:
	for player in [_music, _ambience]:
		if player != null and player.stream != null and bool(player.get_meta("loop", false)) and not player.playing:
			player.play()


## A one-shot sound effect (client/audio/sfx/<name>.wav).
func play(name: String, volume_db := 0.0, pitch := 1.0) -> void:
	_one_shot("res://audio/sfx/%s.wav" % name, volume_db, pitch)


## A fighter's own foley: "step" or "swish".
func foley(character: String, kind: String, volume_db := 0.0) -> void:
	_one_shot("res://audio/foley/%s.%s.wav" % [ArtLibrary.look(character), kind], volume_db, randf_range(0.94, 1.06))


func cheer(character: String) -> void:
	_one_shot("res://audio/cheer/%s.wav" % ArtLibrary.look(character), 2.0, 1.0)


func _one_shot(path: String, volume_db: float, pitch: float) -> void:
	var stream := _stream(path)
	if stream == null:
		return
	var p := _voices[_next_voice]
	_next_voice = (_next_voice + 1) % _voices.size()
	p.stream = stream
	p.volume_db = volume_db
	p.pitch_scale = pitch
	p.play()


func _stream(path: String) -> AudioStream:
	if not _streams.has(path):
		_streams[path] = load(path) if ResourceLoader.exists(path) else null
	return _streams[path]


## Plays what changed since the last frame. characters: each fighter's art id; you: the local
## fighter (their sounds are a little louder) or -1; frames_left: the room clock, or -1; rules: the
## match's (for who is on whose team at the result), or null.
func observe(state: GameState, characters: PackedStringArray, you: int, frames_left: int, rules: MatchRules = null) -> void:
	if state == null:
		_seen.clear()
		_was_over = false
		return
	if state.frame == _seen_frame:
		return
	var before := _seen_frame
	var fresh := _seen.size() != state.fighters.size() or state.frame != _seen_frame + 1
	_seen_frame = state.frame
	if fresh:
		_seen.clear()
		for f in state.fighters:
			_seen.append([f.action, f.grounded, f.frozen_by_hit, f.falls, f.pos_x, f.pos_y, f.armor_frames, 0, f.weapon, f.action_frame])
		_was_over = state.match_over
		_bursting = BurstWatch.snapshot(state)
		_crates = _count_landed_crates(state)
		if state.frame < START_FRAME:
			play("pickup", -2.0, 0.8) # "READY?"
		return

	# "FIGHT!" as the banner pops.
	if before < START_FRAME and state.frame >= START_FRAME and not state.match_over:
		play("guts", 0.0, 1.25)

	var loud := 0
	for i in state.fighters.size():
		var f := state.fighters[i]
		var s: Array = _seen[i]
		var who := characters[i] if i < characters.size() else "shahn"
		var near := 0.0 if i == you else -5.0
		if loud < 6 and f.is_active():
			var firing := WeaponTable.ROWS[Weapons.row_of_move(f.move) if f.action == Fighter.Action.ATTACK else 0]
			var release: int = firing[WeaponTable.C_S]
			var repeats: bool = (firing[WeaponTable.C_INTERVAL] > 0 or firing[WeaponTable.C_USE] == WeaponTable.USE_SPRAY) and f.weapon != Weapons.NONE
			if f.action == Fighter.Action.ATTACK and (s[0] != Fighter.Action.ATTACK or f.action_frame == 0):
				# A weapon's own sound as its move starts; a repeating one is heard shot by shot instead.
				if firing[WeaponTable.C_ID] != 0 and not repeats and f.weapon != Weapons.NONE:
					play("weapon_%d" % f.weapon, near)
				else:
					foley(who, "swish", near)
				loud += 1
			# A repeating weapon (the Uzi, the Tommy Gun, the Minigun) plays its sound on every shot, and a
			# spray on every tick of its stream:
			# its move has just reached its release frame, or has looped back to it for the next one.
			# Read from the fighter's move and frame, which a room is sent too.
			if repeats and f.action_frame >= release and (s[0] != Fighter.Action.ATTACK or int(s[9]) < release or f.action_frame < int(s[9])):
				play("weapon_%d" % f.weapon, near - 6.0, randf_range(0.95, 1.05))
			if f.weapon != Weapons.NONE and f.weapon != int(s[8]) and f.action != Fighter.Action.ATTACK:
				play("pickup", near)
			if f.frozen_by_hit and not s[2]:
				play("hit", near, randf_range(0.9, 1.1))
				if f.hit_stop >= 8:
					play("big_hit", near - 2.0)
					if i == you:
						play("hurt", -2.0) # a big one landed on you
				loud += 1
			if f.action == Fighter.Action.STUNNED and s[0] != Fighter.Action.STUNNED:
				play("parry", near + 2.0)
				loud += 1
			if f.armor_frames > 0 and s[6] == 0:
				play("guts", near)
			var vy := f.pos_y - int(s[5])
			if not f.grounded and s[1] and vy > 0:
				play("jump", near - 4.0)
			if f.grounded and not s[1]:
				play("land", near - 6.0)
			var vx := absi(f.pos_x - int(s[4]))
			if f.grounded and f.action == Fighter.Action.IDLE and vx > Fx.ratio(3, 100):
				s[7] = int(s[7]) + 1
				if int(s[7]) % STEP_FRAMES == 1:
					foley(who, "step", near - 8.0)
			else:
				s[7] = 0
		if f.falls > int(s[3]):
			play("ko", 0.0 if i == you else -3.0)
		s[0] = f.action
		s[1] = f.grounded
		s[2] = f.frozen_by_hit
		s[3] = f.falls
		s[4] = f.pos_x
		s[5] = f.pos_y
		s[6] = f.armor_frames
		s[8] = f.weapon
		s[9] = f.action_frame

	# A boom for every burst (two on one frame are one louder boom); one that only opens is a soft pop.
	var booms := 0
	var pops := 0
	for b: Array in BurstWatch.bursts(_bursting, state, arena_half):
		if bool(b[4]):
			pops += 1
		else:
			booms += 1
	if booms > 0:
		play("boom", 2.0 + minf(4.0, booms - 1.0), randf_range(0.95, 1.05))
	elif pops > 0:
		play("boom", -8.0, 1.6)
	_bursting = BurstWatch.snapshot(state)
	# A crate thumps down.
	var crates := _count_landed_crates(state)
	if crates > _crates:
		play("land", -8.0, 0.7)
	_crates = crates

	if frames_left >= 0 and frames_left <= WARN_FRAMES and not state.match_over:
		var second := frames_left / 60
		if second != _last_warn_second:
			_last_warn_second = second
			play("warning", -4.0)
	if state.match_over and not _was_over:
		# The result (rooms v1, polish 2): the winner's jingle for the winning side (in a team match
		# the whole winning team) and for anyone watching; the lose sting for the rest.
		var winner := state.winner_index
		match result_sting(state, rules, you):
			"win":
				cheer(characters[winner] if winner < characters.size() else "shahn")
			"lose":
				lose_sting()
	_was_over = state.match_over


## What this screen hears when the match ends: "win" if `you` (or a teammate of yours) won, or you
## are watching (-1) and someone did; "lose" if the other side won; "" for a draw. Static for tests.
static func result_sting(state: GameState, rules: MatchRules, you: int) -> String:
	var winner := state.winner_index
	if winner < 0:
		return ""
	if you < 0 or you >= state.fighters.size():
		return "win"
	var allied := winner == you if rules == null else Simulation.allies(rules, you, winner)
	return "win" if allied else "lose"


## The lose sting: the KO sound, low and slow (no new audio).
func lose_sting() -> void:
	play("ko", -3.0, 0.6)
	play("land", -6.0, 0.55)


## The lobby's small confirmations, from the existing UI sounds: you joined a team, the link was
## copied.
func team_joined() -> void:
	play("pickup", -4.0, 1.1)


func copied() -> void:
	play("pickup", -6.0, 1.45)


func _count_landed_crates(state: GameState) -> int:
	var n := 0
	for p in state.props:
		if p.kind == Weapons.PROP_BOX and p.grounded:
			n += 1
	return n
