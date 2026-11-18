extends SceneTree
## Cuts the game's weapon sprites out of the art packs into client/assets/weapons:
##   <id>.wbin (WebP bytes) for each weapon, and weapons.json with each one's grip point and size.
## Run with the standard Godot (see CLAUDE.md), from the repo root:
##   godot --headless --path client -s ../tools/cut_weapons.gd -- <game-art folder>
## The final pack (round-brawler-v6/weapons) has a few cut already; the rest come from the v3
## sheets, using v3's weapon-atlas.json rectangles.

# Game weapon id (Weapons in client/sim) -> the art pack's weapon key.
const KEYS := {1: "w011", 2: "w031", 3: "w006", 4: "w061", 5: "w005", 6: "w008", 7: "w054"}


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	if args.is_empty():
		printerr("usage: -- <game-art folder>")
		quit(2)
		return
	var art: String = args[0]
	var atlas: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(art.path_join("round-brawler-v3/weapon-atlas.json")))["frames"]
	var out := ProjectSettings.globalize_path("res://assets/weapons")
	DirAccess.make_dir_recursive_absolute(out)
	var index := {}
	var sheets := {}
	for id: int in KEYS:
		var key: String = KEYS[id]
		var frame: Dictionary = atlas[key]
		var image: Image
		var cut := art.path_join("round-brawler-v6/weapons/%s.webp" % key)
		if FileAccess.file_exists(cut):
			image = Image.load_from_file(cut)
		else:
			var sheet_path := art.path_join("round-brawler-v3").path_join(str(frame["image"]))
			if not sheets.has(sheet_path):
				sheets[sheet_path] = Image.load_from_file(sheet_path)
			var r: Dictionary = frame["rect"]
			image = (sheets[sheet_path] as Image).get_region(Rect2i(int(r["x"]), int(r["y"]), int(r["w"]), int(r["h"])))
		if image == null or image.is_empty():
			printerr("no image for ", key)
			quit(1)
			return
		if image.get_width() > 256:
			image.resize(256, int(256.0 * image.get_height() / image.get_width()), Image.INTERPOLATE_LANCZOS)
		var bytes := image.save_webp_to_buffer(false, 0.9)
		var f := FileAccess.open(out.path_join("%d.wbin" % id), FileAccess.WRITE)
		f.store_buffer(bytes)
		f.close()
		index[str(id)] = {
			"key": key, "name": str(frame["name"]),
			"grip": [float(frame["grip"]["x"]), float(frame["grip"]["y"])],
			"size": [float(frame["drawWidth"]), float(frame["drawHeight"])],
		}
		print("%s %s: %dx%d, %d bytes" % [key, frame["name"], image.get_width(), image.get_height(), bytes.size()])
	var j := FileAccess.open(out.path_join("weapons.json"), FileAccess.WRITE)
	j.store_string(JSON.stringify(index, " "))
	j.close()
	quit(0)
