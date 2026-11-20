class_name SoundDirector
extends Node
## The game's sound, from the art packs' original audio (client/audio, see tools/import_audio.py):
## each arena's music and ambience, each fighter's footsteps and whoosh, hits, jumps, landings,
## parries, Guts, KOs, the last-ten-seconds warning and the winner's jingle. Presentation only: it
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
var _bombs := 0 # bombs, mines, missiles and black holes out last frame: one fewer is a burst
var _bullets := 0
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
## fighter (their sounds are a little louder) or -1; frames_left: the room clock, or -1.
func observe(state: GameState, characters: PackedStringArray, you: int, frames_left: int) -> void:
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
			_seen.append([f.action, f.grounded, f.frozen_by_hit, f.falls, f.pos_x, f.pos_y, f.armor_frames, 0, f.weapon, f.uzi_shots])
		_was_over = state.match_over
		_bombs = _count_bursting(state)
		_bullets = _count_shots(state, Weapons.SHOT_BULLET)
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
			if f.action == Fighter.Action.ATTACK and (s[0] != Fighter.Action.ATTACK or f.action_frame == 0):
				if Weapons.is_weapon_move(f.move) and f.move != MoveData.Id.WEAPON_THROW and f.move != MoveData.Id.UZI_FIRE and f.weapon != Weapons.NONE:
					play("weapon_%d" % f.weapon, near)
				else:
					foley(who, "swish", near)
				loud += 1
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
		s[9] = f.uzi_shots

	var bombs := _count_bursting(state)
	if bombs < _bombs:
		play("boom", 2.0)
	_bombs = bombs
	# A crate thumps down.
	var crates := _count_landed_crates(state)
	if crates > _crates:
		play("land", -8.0, 0.7)
	_crates = crates
	# Every new bullet rattles (rooms don't send the Uzi's shot count, so count bullets).
	var bullets := _count_shots(state, Weapons.SHOT_BULLET)
	if bullets > _bullets:
		play("weapon_5", -6.0, randf_range(0.95, 1.05))
	_bullets = bullets

	if frames_left >= 0 and frames_left <= WARN_FRAMES and not state.match_over:
		var second := frames_left / 60
		if second != _last_warn_second:
			_last_warn_second = second
			play("warning", -4.0)
	if state.match_over and not _was_over and state.winner_index >= 0:
		var winner := state.winner_index
		cheer(characters[winner] if winner < characters.size() else "shahn")
	_was_over = state.match_over


func _count_shots(state: GameState, shot: int) -> int:
	var n := 0
	for p in state.props:
		if p.kind == Weapons.PROP_SHOT and p.shot == shot:
			n += 1
	return n


## Everything on the stage that ends in a burst.
func _count_bursting(state: GameState) -> int:
	return _count_shots(state, Weapons.SHOT_BOMB) + _count_shots(state, Weapons.SHOT_MINE) \
		+ _count_shots(state, Weapons.SHOT_MISSILE) + _count_shots(state, Weapons.SHOT_HOLE)


func _count_landed_crates(state: GameState) -> int:
	var n := 0
	for p in state.props:
		if p.kind == Weapons.PROP_BOX and p.grounded:
			n += 1
	return n
