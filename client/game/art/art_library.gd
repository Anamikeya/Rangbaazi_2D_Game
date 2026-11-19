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


## A mixed fighter (the character creator): "mix.<head>.<hands>.<feet>.<moves>.<name>", each of the
## four one of the 31 fighters' ids. The head brings its faces and hat; the moves are how it's
## animated. The same string goes to the room server (ArenaLimits.CleanCharacter checks it too).
const MIX := "mix."


static func is_mix(id: String) -> bool:
	return id.begins_with(MIX)


static func make_mix(head: String, hands: String, feet: String, moves: String, name: String) -> String:
	return "%s%s.%s.%s.%s.%s" % [MIX, head, hands, feet, moves, GameSettings.clean_name(name)]


## [head, hands, feet, moves, name] of a mixed fighter, or empty if the id isn't a valid one.
static func mix_parts(id: String) -> PackedStringArray:
	var bits := id.split(".")
	if not is_mix(id) or bits.size() != 6:
		return PackedStringArray()
	for i in range(1, 5):
		if not is_fighter(bits[i]):
			return PackedStringArray()
	return PackedStringArray([bits[1], bits[2], bits[3], bits[4], GameSettings.clean_name(bits[5])])


static func is_fighter(id: String) -> bool:
	for f: Dictionary in fighters():
		if f["id"] == id:
			return true
	return false


## The one of the 31 a fighter looks and sounds like: itself, or a mixed fighter's head.
static func look(id: String) -> String:
	var m := mix_parts(id)
	return m[0] if not m.is_empty() else id


static func fighter_name(id: String) -> String:
	if is_mix(id):
		var m := mix_parts(id)
		return m[4] if not m.is_empty() and m[4] != "" else "Custom"
	for f: Dictionary in fighters():
		if f["id"] == id:
			return str(f["name"])
	return id.capitalize()


static func fighter_role(id: String) -> String:
	if is_mix(id):
		return "Your own fighter"
	for f: Dictionary in fighters():
		if f["id"] == id:
			return str(f.get("role", ""))
	return ""


## The character id of the fighter an arena belongs to, or "".
static func arena_owner(id: String) -> String:
	for a: Dictionary in arenas():
		if a["id"] == id:
			return str(a.get("owner", a.get("folder", "")))
	return ""


static func arena_title(id: String) -> String:
	for a: Dictionary in arenas():
		if a["id"] == id:
			return str(a["title"])
	return id.capitalize()


## A fighter's rig and moves ({parts, clips}), or {} if it isn't in the assets.
static func character(id: String) -> Dictionary:
	if not _characters.has(id) and is_mix(id):
		_characters[id] = _compose(id)
	if not _characters.has(id):
		var text := FileAccess.get_file_as_string("%s/characters/%s/clips.json" % [ROOT, id])
		var parsed: Variant = JSON.parse_string(text) if text != "" else null
		_characters[id] = parsed if parsed is Dictionary else {}
	return _characters[id]


## A mixed fighter's rig: the head (with its hat and extras), hands and feet of three fighters,
## animated with a fourth's moves. Texture files are written "<fighter>/<file>".
static func _compose(id: String) -> Dictionary:
	var m := mix_parts(id)
	if m.is_empty():
		return {}
	var parts := {}
	_take(parts, m[0], ["head"], "")
	_take(parts, m[1], ["backHand", "frontHand"], "hand_")
	_take(parts, m[2], ["backFoot", "frontFoot"], "foot_")
	return {"id": id, "parts": parts, "clips": character(m[3]).get("clips", {})}


## Copies the named parts of a fighter's rig, and the extras that ride on them, into `out`.
static func _take(out: Dictionary, from: String, names: Array, prefix: String) -> void:
	var source: Dictionary = character(from).get("parts", {})
	for part: String in source.keys():
		var spec: Dictionary = source[part]
		var named := names.has(part)
		if not named and not names.has(str(spec.get("parent", ""))):
			continue
		var copy := spec.duplicate(true)
		var textures: Dictionary = copy.get("textures", {})
		for slot: String in textures.keys():
			textures[slot] = "%s/%s" % [from, textures[slot]]
		out[part if named or prefix == "" else prefix + part] = copy


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
	if file.contains("/"): # a mixed fighter's part: "<fighter>/<file>"
		return _texture("%s/characters/%s" % [ROOT, file])
	return _texture("%s/characters/%s/%s" % [ROOT, character_id, file])


static func arena_texture(stage_id: String, file: String) -> Texture2D:
	return _texture("%s/arenas/%s/%s" % [ROOT, stage_id, file])


## A fighter's effect sprite: kind is "hit", "dodge" or "landing". Null if there is none.
static func effect_texture(character_id: String, kind: String) -> Texture2D:
	if character_id == "":
		return null
	return _texture("%s/effects/%s-%s.wbin" % [ROOT, look(character_id), kind])


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
