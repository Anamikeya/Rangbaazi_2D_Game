extends SceneTree
## Builds the app icons from a fighter's face:
##   godot --headless --path client -s ../tools/make_icons.gd -- [<fighter id>]
## Writes client/icon.png (512, the project and Play Store icon) and the Android adaptive icon layers
## client/icon_fg.png and client/icon_bg.png (432 each; the face stays inside the 66% safe circle).

const BG := Color("1a1824")
const RING := Color("f2a900")


func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	var who: String = args[0] if args.size() > 0 else "sarthak"
	var face := _load_face(who)
	if face == null:
		printerr("no face for ", who)
		quit(1)
		return
	var full := _square(512, 0.80)
	_blit_face(full, face, 0.66)
	full.save_png("res://icon.png")
	# Adaptive layers: launchers show the middle 72 of 108 dp, masked to a circle or squircle.
	var fg := Image.create(432, 432, false, Image.FORMAT_RGBA8)
	_blit_face(fg, face, 0.44)
	fg.save_png("res://icon_fg.png")
	_square(432, 0.56).save_png("res://icon_bg.png")
	print("icons written for ", who)
	quit(0)


func _load_face(who: String) -> Image:
	var bytes := FileAccess.get_file_as_bytes("res://assets/characters/%s/head-neutral.wbin" % who)
	if bytes.size() < 8:
		return null
	var img := Image.new()
	if bytes[0] == 0x89:
		img.load_png_from_buffer(bytes)
	else:
		img.load_webp_from_buffer(bytes)
	img.convert(Image.FORMAT_RGBA8)
	var used := img.get_used_rect()
	return img.get_region(used) if used.size.x > 0 else img


## A dark square with a gold ring (radius = ring * half the side).
func _square(size: int, ring: float) -> Image:
	var img := Image.create(size, size, false, Image.FORMAT_RGBA8)
	img.fill(BG)
	_paint_disc(img, ring)
	return img


func _paint_disc(img: Image, ring: float) -> void:
	var n := img.get_width()
	var c := (n - 1) / 2.0
	for y in n:
		for x in n:
			var d := Vector2(x - c, y - c).length() / (n / 2.0)
			if d < ring:
				img.set_pixel(x, y, RING.darkened(0.15 * d))
			elif d < ring + 0.035:
				img.set_pixel(x, y, Color("7a5500"))


## Scales the face to `share` of the image's width and centres it.
func _blit_face(img: Image, face: Image, share: float) -> void:
	var f := face.duplicate() as Image
	var w := int(img.get_width() * share)
	var h := int(w * float(f.get_height()) / f.get_width())
	f.resize(w, h, Image.INTERPOLATE_LANCZOS)
	img.blend_rect(f, Rect2i(Vector2i.ZERO, f.get_size()), Vector2i((img.get_width() - w) / 2, (img.get_height() - h) / 2))
