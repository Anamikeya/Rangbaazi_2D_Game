class_name GameSettings
extends RefCounted
## The player's settings, kept in user://settings.cfg: their name for rooms, and the graphics
## options. Presentation only: nothing here ever reaches the sim.

const PATH := "user://settings.cfg"
const FPS_CHOICES := [60, 30]
const SETTINGS_VERSION := 2

static var player_name := ""
## The fighter picked in the character circle (one of the 31 ids), and the arena for practice.
static var character := "shahn"
static var arena := "rooftops"
## The fighter made in the character creator (a "mix" id, see ArtLibrary), or "" for none yet.
static var custom := ""
## Frames drawn per second (60 or 30). The game itself always runs at 60; 30 saves battery.
static var max_fps := 60
## Effects: shake on a hit you take. Off is calmer and a little cheaper.
static var effects := true
static var show_hitboxes := false
static var show_debug := false
## The camera follows the fight (zooming in on it); off, the whole arena is always shown.
static var follow_camera := true
## The how-to-play card has been shown once.
static var seen_help := false
## Volumes, 0 to 1: the music and ambience, and everything else.
static var music_volume := 0.7
static var effects_volume := 0.9

static var _loaded := false


static func load_settings() -> void:
	if _loaded:
		return
	_loaded = true
	var cfg := ConfigFile.new()
	if cfg.load(PATH) != OK:
		return
	player_name = clean_name(str(cfg.get_value("player", "name", "")))
	character = str(cfg.get_value("player", "character", "shahn"))
	arena = str(cfg.get_value("player", "arena", "rooftops"))
	custom = str(cfg.get_value("player", "custom", ""))
	max_fps = int(cfg.get_value("graphics", "max_fps", 60))
	if not FPS_CHOICES.has(max_fps):
		max_fps = 60
	effects = bool(cfg.get_value("graphics", "effects", true))
	show_hitboxes = bool(cfg.get_value("graphics", "show_hitboxes", false))
	# Settings saved before version 2 had the debug hitboxes on by default: turn them off once.
	if int(cfg.get_value("player", "settings_version", 1)) < SETTINGS_VERSION:
		show_hitboxes = false
	follow_camera = bool(cfg.get_value("graphics", "follow_camera", true))
	seen_help = bool(cfg.get_value("player", "seen_help", false))
	show_debug = bool(cfg.get_value("graphics", "show_debug", false))
	music_volume = clampf(float(cfg.get_value("sound", "music", 0.7)), 0.0, 1.0)
	effects_volume = clampf(float(cfg.get_value("sound", "effects", 0.9)), 0.0, 1.0)


static func save() -> void:
	var cfg := ConfigFile.new()
	cfg.set_value("player", "name", player_name)
	cfg.set_value("player", "character", character)
	cfg.set_value("player", "arena", arena)
	cfg.set_value("player", "custom", custom)
	cfg.set_value("graphics", "max_fps", max_fps)
	cfg.set_value("graphics", "effects", effects)
	cfg.set_value("graphics", "show_hitboxes", show_hitboxes)
	cfg.set_value("player", "settings_version", SETTINGS_VERSION)
	cfg.set_value("graphics", "follow_camera", follow_camera)
	cfg.set_value("player", "seen_help", seen_help)
	cfg.set_value("graphics", "show_debug", show_debug)
	cfg.set_value("sound", "music", music_volume)
	cfg.set_value("sound", "effects", effects_volume)
	cfg.save(PATH)


## Puts the settings into effect (the frame cap; the arena reads the rest when it draws).
static func apply() -> void:
	Engine.max_fps = max_fps
	apply_audio()


## Makes the Music and Effects buses (once) and sets their volumes.
static func apply_audio() -> void:
	# The buses come from res://default_bus_layout.tres. Adding them here at run time is only a
	# fallback: on the web, Godot wires a bus added at run time into a loop with Master, and the
	# browser silences every sound that goes through a loop.
	for bus in ["Music", "Effects"]:
		if AudioServer.get_bus_index(bus) < 0:
			AudioServer.add_bus()
			var i := AudioServer.bus_count - 1
			AudioServer.set_bus_name(i, bus)
			AudioServer.set_bus_send(i, "Master")
	_set_volume("Music", music_volume)
	_set_volume("Effects", effects_volume)


static func _set_volume(bus: String, volume: float) -> void:
	var i := AudioServer.get_bus_index(bus)
	if i >= 0:
		AudioServer.set_bus_mute(i, volume <= 0.001)
		AudioServer.set_bus_volume_db(i, linear_to_db(maxf(volume, 0.001)))


## Letters, digits, spaces, '-' and '_', at most 12 characters (the server cleans names the same way).
static func clean_name(text: String) -> String:
	var out := ""
	for ch in text.strip_edges():
		if out.length() >= 12:
			break
		if (ch >= "A" and ch <= "Z") or (ch >= "a" and ch <= "z") or (ch >= "0" and ch <= "9") or ch in [" ", "-", "_"]:
			out += ch
	return out.strip_edges()
