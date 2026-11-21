extends SceneTree
## The colours the arena view paints outside an arena's painting (rooms v1, 2.5), worked out from
## its layers; tools/import_art.py runs it and writes them into each arena.json:
##   godot --headless --path client -s ../tools/arena_colours.gd -- <client/assets/arenas folder>
## Prints one line, "ARENA_COLOURS " and JSON: {"<arena id>": {"skyTop": "#rrggbb", "ground": "#rrggbb"}}.
##   skyTop: the far layer's (the lowest z) main colour along its top: from the first row from the
##           top that is at least half opaque and the SKY_ROWS rows under it, leaving out the
##           SKY_SIDE of the width at either side (the bookends), the opaque pixels
##           are put in coarse colour bins (SKY_BITS a channel) and the fullest bin's mean is
##           taken. (A plain mean of the top row mixed trees, flags and planets into the sky and
##           gave muddy bands: the review of rooms v1.) For the PAINTED_SKY arenas, whose top
##           edge is a canopy or arches with the sky showing through, it is the painted sky's
##           colour instead: the fullest bin of sky-blue pixels in the far layer's top SKY_DEPTH.
##   ground: the mean of the foreground strip (the highest z in front of the fighters) over its
##           opaque pixels; with no front layer (or nothing opaque in it), of the near layer's
##           bottom 40 rows.
## The layers are the .wbin files import_art.py copies (WebP under another name).

const OPAQUE := 0.9
const SKY_ROWS := 8
const SKY_BITS := 3
## The painting's sides are left out of the sky sample: most arenas have bookend buildings, flags
## or trees there.
const SKY_SIDE := 0.15
## Arenas whose painting's top edge is a canopy (Akhara Arena, Archer's Ascent) or arches (World
## Duel Dome) with blue sky showing through: a band in the canopy's or the arches' colour above the
## painting read as a slab, so their skyTop is the painted sky's (final round of rooms v1).
const PAINTED_SKY := ["siraj", "suhani", "avipray"]
const SKY_DEPTH := 0.4


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	var root: String = args[0] if not args.is_empty() else "res://assets/arenas"
	var out := {}
	for id in DirAccess.get_directories_at(root):
		var path := root.path_join(id).path_join("arena.json")
		if not FileAccess.file_exists(path):
			continue
		var arena: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(path))
		var layers: Array = arena.get("layers", [])
		if layers.is_empty():
			continue
		var back: Dictionary = layers[0]
		var front: Dictionary = {}
		var near: Dictionary = layers[0]
		for layer: Dictionary in layers:
			var z := float(layer.get("z", 0))
			if z < float(back.get("z", 0)):
				back = layer
			if z < 0.0 and z > float(near.get("z", -INF)):
				near = layer
			if z > 0.0 and (front.is_empty() or z > float(front.get("z", 0))):
				front = layer
		var colours := {}
		var far_image := _image(root.path_join(id), back)
		if far_image != null:
			var c: Variant = _painted_sky(far_image) if id in PAINTED_SKY else _sky_colour(far_image)
			if c != null:
				colours["skyTop"] = "#" + (c as Color).to_html(false)
		var ground: Variant = null
		var front_image := _image(root.path_join(id), front) if not front.is_empty() else null
		if front_image != null:
			ground = _mean(front_image, 0, front_image.get_height())
		if ground == null: # no front strip, or nothing opaque in it
			var near_image := _image(root.path_join(id), near)
			if near_image != null:
				ground = _mean(near_image, maxi(0, near_image.get_height() - 40), near_image.get_height())
		if ground != null:
			colours["ground"] = "#" + (ground as Color).to_html(false)
		out[id] = colours
	print("ARENA_COLOURS " + JSON.stringify(out))
	quit(0)


func _image(folder: String, layer: Dictionary) -> Image:
	var bytes := FileAccess.get_file_as_bytes(folder.path_join(str(layer.get("file", ""))))
	if bytes.is_empty():
		return null
	var image := Image.new()
	if image.load_webp_from_buffer(bytes) != OK and image.load_png_from_buffer(bytes) != OK:
		return null
	image.convert(Image.FORMAT_RGBA8)
	return image


## The main colour along the top of the painting (see skyTop above), or null.
func _sky_colour(image: Image) -> Variant:
	var w := image.get_width()
	var first := -1
	for y in image.get_height():
		var opaque := 0
		for x in w:
			if image.get_pixel(x, y).a >= OPAQUE:
				opaque += 1
		if opaque * 2 >= w:
			first = y
			break
	if first < 0:
		return null
	var levels := 1 << SKY_BITS
	var sums := {}
	var counts := {}
	for y in range(first, mini(image.get_height(), first + SKY_ROWS)):
		for x in range(int(w * SKY_SIDE), int(w * (1.0 - SKY_SIDE))):
			var p := image.get_pixel(x, y)
			if p.a < OPAQUE:
				continue
			var key := (mini(levels - 1, int(p.r * levels)) * levels + mini(levels - 1, int(p.g * levels))) * levels + mini(levels - 1, int(p.b * levels))
			sums[key] = sums.get(key, Vector3.ZERO) + Vector3(p.r, p.g, p.b)
			counts[key] = int(counts.get(key, 0)) + 1
	var best := -1
	for key: int in counts:
		if best < 0 or int(counts[key]) > int(counts[best]) or (int(counts[key]) == int(counts[best]) and key < best):
			best = key
	var mean: Vector3 = sums[best] / float(counts[best])
	return Color(mean.x, mean.y, mean.z)


## The painted sky's colour (PAINTED_SKY): the fullest coarse bin of sky-blue pixels (bluer than red,
## not much greener than blue, not dark) in the top SKY_DEPTH of the layer, or null.
func _painted_sky(image: Image) -> Variant:
	var levels := 1 << SKY_BITS
	var sums := {}
	var counts := {}
	for y in range(0, int(image.get_height() * SKY_DEPTH)):
		for x in image.get_width():
			var p := image.get_pixel(x, y)
			if p.a < OPAQUE or not (p.b > p.r + 0.08 and p.b >= p.g - 0.05 and p.get_luminance() > 0.3):
				continue
			var key := (mini(levels - 1, int(p.r * levels)) * levels + mini(levels - 1, int(p.g * levels))) * levels + mini(levels - 1, int(p.b * levels))
			sums[key] = sums.get(key, Vector3.ZERO) + Vector3(p.r, p.g, p.b)
			counts[key] = int(counts.get(key, 0)) + 1
	var best := -1
	for key: int in counts:
		if best < 0 or int(counts[key]) > int(counts[best]) or (int(counts[key]) == int(counts[best]) and key < best):
			best = key
	if best < 0:
		return null
	var mean: Vector3 = sums[best] / float(counts[best])
	return Color(mean.x, mean.y, mean.z)


## The mean of the opaque pixels of rows [from, to), or null if there are none.
func _mean(image: Image, from: int, to: int) -> Variant:
	var sum := Vector3.ZERO
	var n := 0
	for y in range(from, to):
		for x in image.get_width():
			var p := image.get_pixel(x, y)
			if p.a >= OPAQUE:
				sum += Vector3(p.r, p.g, p.b)
				n += 1
	if n == 0:
		return null
	sum /= n
	return Color(sum.x, sum.y, sum.z)
