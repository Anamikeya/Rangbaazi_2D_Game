class_name ArtLibrary
extends RefCounted
## Loads the art imported by tools/import_art.py (client/assets): fighters' rigs and moves, the
## shared moves, arenas' layers and platform pieces, and the index of names. Images are stored as
## .wbin (WebP or PNG bytes) and decoded here, so the build keeps them at their small original size.
## Everything is cached after first use. Presentation only: nothing here touches the sim.

const ROOT := "res://assets"

static var _index: Dictionary = {}
static var _characters: Dictionary = {}
static var _arenas: Dictionary = {}
static var _shared: Dictionary = {}
static var _textures: Dictionary = {}
static var _weapons: Dictionary = {}


static func index() -> Dictionary:
	if _index.is_empty():
		var text := FileAccess.get_file_as_string(ROOT + "/index.json")
		var parsed: Variant = JSON.parse_string(text) if text != "" else null
		_index = parsed if parsed is Dictionary else {"fighters": [], "arenas": [], "easings": []}
	return _index


## [{id, name, role, type}] for the 31 fighters, in the cast's order.
static func fighters() -> Array:
	return index().get("fighters", [])


## [{id, folder, title}] for the arenas; id is the sim's stage id.
static func arenas() -> Array:
	return index().get("arenas", [])


static func fighter_name(id: String) -> String:
	for f: Dictionary in fighters():
		if f["id"] == id:
			return str(f["name"])
	return id.capitalize()


static func fighter_role(id: String) -> String:
	for f: Dictionary in fighters():
		if f["id"] == id:
			return str(f.get("role", ""))
	return ""


static func arena_title(id: String) -> String:
	for a: Dictionary in arenas():
		if a["id"] == id:
			return str(a["title"])
	return id.capitalize()


## A fighter's rig and moves ({parts, clips}), or {} if it isn't in the assets.
static func character(id: String) -> Dictionary:
	if not _characters.has(id):
		var text := FileAccess.get_file_as_string("%s/characters/%s/clips.json" % [ROOT, id])
		var parsed: Variant = JSON.parse_string(text) if text != "" else null
		_characters[id] = parsed if parsed is Dictionary else {}
	return _characters[id]


## The 18 moves every fighter shares (a fighter's own version, if any, wins).
static func shared_clips() -> Dictionary:
	if _shared.is_empty():
		var text := FileAccess.get_file_as_string(ROOT + "/shared/clips.json")
		var parsed: Variant = JSON.parse_string(text) if text != "" else null
		_shared = (parsed as Dictionary).get("clips", {}) if parsed is Dictionary else {}
	return _shared


## A fighter's move: their own if they have one, otherwise the shared one, otherwise {}.
static func clip(character_id: String, clip_name: String) -> Dictionary:
	var own: Dictionary = character(character_id).get("clips", {})
	if own.has(clip_name):
		return own[clip_name]
	return shared_clips().get(clip_name, {})


## An arena's {title, layers, pieces, platforms}, or {}.
static func arena(stage_id: String) -> Dictionary:
	if not _arenas.has(stage_id):
		var text := FileAccess.get_file_as_string("%s/arenas/%s/arena.json" % [ROOT, stage_id])
		var parsed: Variant = JSON.parse_string(text) if text != "" else null
		_arenas[stage_id] = parsed if parsed is Dictionary else {}
	return _arenas[stage_id]


static func character_texture(character_id: String, file: String) -> Texture2D:
	return _texture("%s/characters/%s/%s" % [ROOT, character_id, file])


static func arena_texture(stage_id: String, file: String) -> Texture2D:
	return _texture("%s/arenas/%s/%s" % [ROOT, stage_id, file])


## A weapon's sprite (tools/cut_weapons.gd), by the sim's weapon id, or null.
static func weapon_texture(weapon: int) -> Texture2D:
	return _texture("%s/weapons/%d.wbin" % [ROOT, weapon])


## A weapon's grip point (0..1 across the sprite), by the sim's weapon id.
static func weapon_grip(weapon: int) -> Vector2:
	if _weapons.is_empty():
		var text := FileAccess.get_file_as_string(ROOT + "/weapons/weapons.json")
		var parsed: Variant = JSON.parse_string(text) if text != "" else null
		_weapons = parsed if parsed is Dictionary else {"_": {}}
	var w: Dictionary = _weapons.get(str(weapon), {})
	var g: Array = w.get("grip", [0.5, 0.5])
	return Vector2(float(g[0]), float(g[1]))


## Decodes a .wbin (WebP, or PNG for a few parts) once and keeps it.
static func _texture(path: String) -> Texture2D:
	if _textures.has(path):
		return _textures[path]
	var tex: Texture2D = null
	var bytes := FileAccess.get_file_as_bytes(path)
	if bytes.size() > 8:
		var image := Image.new()
		var err := ERR_FILE_UNRECOGNIZED
		if bytes[0] == 0x89 and bytes[1] == 0x50: # PNG
			err = image.load_png_from_buffer(bytes)
		else:
			err = image.load_webp_from_buffer(bytes)
		if err == OK:
			tex = ImageTexture.create_from_image(image)
	_textures[path] = tex
	return tex
