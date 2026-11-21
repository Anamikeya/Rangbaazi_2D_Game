extends SceneTree
## Cuts the game's weapon sprites out of the art packs into client/assets/weapons:
##   <id>.wbin (WebP bytes) for each weapon, and weapons.json with each one's grip point and size.
## Run with the standard Godot (see CLAUDE.md), from the repo root:
##   godot --headless --path client -s ../tools/cut_weapons.gd -- <game-art folder>
## Which weapons, and which pack key each one is, comes from tools/weapons.json (the weapon table's
## source): every row with a `key`. The hidden rows have none; their props draw the weapon they are
## a part of. A weapon the newer packs have cut already (round-brawler-v7/weapons, then
## round-brawler-v6/weapons) uses that cut; the rest come from the v3 sheets, by the rectangles in
## v3's weapon-atlas.json.

## Packs that hold ready-made cuts, the newest first: a delta pack's file wins.
const CUT_PACKS := ["round-brawler-v7", "round-brawler-v6"]
## Sprites are stored at most this wide: a weapon is never drawn bigger than a crate's label.
const MAX_WIDTH := 256


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	if args.is_empty():
		printerr("usage: -- <game-art folder>")
		quit(2)
		return
	var art: String = args[0]
	var table_path := ProjectSettings.globalize_path("res://").path_join("../tools/weapons.json").simplify_path()
	var rows: Variant = JSON.parse_string(FileAccess.get_file_as_string(table_path))
	if not rows is Array:
		printerr("can't read ", table_path)
		quit(1)
		return
	var atlas: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(art.path_join("round-brawler-v3/weapon-atlas.json")))["frames"]
	var out := ProjectSettings.globalize_path("res://assets/weapons")
	DirAccess.make_dir_recursive_absolute(out)
	var index := {}
	var sheets := {}
	var total := 0
	for row: Dictionary in rows:
		if str(row.get("key", "")) == "":
			continue # a hidden row: a part of another weapon
		var id := int(row["id"])
		var key := str(row["key"])
		if not atlas.has(key):
			printerr("the atlas has no ", key, " (weapon ", id, ")")
			quit(1)
			return
		var frame: Dictionary = atlas[key]
		var image: Image
		for pack: String in CUT_PACKS:
			var cut := art.path_join(pack).path_join("weapons/%s.webp" % key)
			if FileAccess.file_exists(cut):
				image = Image.load_from_file(cut)
				break
		if image == null:
			var sheet_path := art.path_join("round-brawler-v3").path_join(str(frame["image"]))
			if not sheets.has(sheet_path):
				sheets[sheet_path] = Image.load_from_file(sheet_path)
			var r: Dictionary = frame["rect"]
			image = (sheets[sheet_path] as Image).get_region(Rect2i(int(r["x"]), int(r["y"]), int(r["w"]), int(r["h"])))
		if image == null or image.is_empty():
			printerr("no image for ", key)
			quit(1)
			return
		if image.get_width() > MAX_WIDTH:
			image.resize(MAX_WIDTH, int(float(MAX_WIDTH) * image.get_height() / image.get_width()), Image.INTERPOLATE_LANCZOS)
		var bytes := image.save_webp_to_buffer(false, 0.9)
		var f := FileAccess.open(out.path_join("%d.wbin" % id), FileAccess.WRITE)
		f.store_buffer(bytes)
		f.close()
		total += bytes.size()
		index[str(id)] = {
			"key": key, "name": str(frame["name"]),
			"grip": [float(frame["grip"]["x"]), float(frame["grip"]["y"])],
			"size": [float(frame["drawWidth"]), float(frame["drawHeight"])],
		}
		print("%d %s %s: %dx%d, %d bytes" % [id, key, frame["name"], image.get_width(), image.get_height(), bytes.size()])
	var j := FileAccess.open(out.path_join("weapons.json"), FileAccess.WRITE)
	j.store_string(JSON.stringify(index, " "))
	j.close()
	print("%d weapons, %.1f MB" % [index.size(), total / 1e6])
	quit(0)
