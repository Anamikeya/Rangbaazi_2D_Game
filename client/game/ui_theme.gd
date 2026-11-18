class_name UiTheme
## The look of the menu and HUD controls, built in code (no external assets): the default font,
## large type and big rounded buttons that are easy to hit with a thumb.

const ACCENT := Color("f2a900")      # marigold, P1's colour
const PANEL := Color(0.14, 0.13, 0.18, 0.94)
const TEXT := Color("f4f1ea")
const TEXT_DIM := Color(0.96, 0.94, 0.9, 0.6)
const ERROR := Color("ff6b6b")


static func build() -> Theme:
	var theme := Theme.new()
	theme.default_font = ThemeDB.fallback_font
	theme.default_font_size = 36

	theme.set_stylebox("normal", "Button", _box(PANEL, ACCENT.darkened(0.25)))
	theme.set_stylebox("hover", "Button", _box(PANEL.lightened(0.12), ACCENT))
	theme.set_stylebox("pressed", "Button", _box(ACCENT, ACCENT.lightened(0.2)))
	theme.set_stylebox("disabled", "Button", _box(Color(PANEL, 0.6), Color(1, 1, 1, 0.15)))
	theme.set_stylebox("focus", "Button", _box(Color(0, 0, 0, 0), Color.WHITE, 4))
	theme.set_color("font_color", "Button", TEXT)
	theme.set_color("font_hover_color", "Button", Color.WHITE)
	theme.set_color("font_pressed_color", "Button", Color("1b1a22"))
	theme.set_color("font_hover_pressed_color", "Button", Color("1b1a22"))
	theme.set_color("font_focus_color", "Button", Color.WHITE)
	theme.set_color("font_disabled_color", "Button", TEXT_DIM)

	theme.set_stylebox("normal", "LineEdit", _box(Color(0.08, 0.08, 0.1, 0.95), Color(1, 1, 1, 0.35)))
	theme.set_stylebox("focus", "LineEdit", _box(Color(0, 0, 0, 0), ACCENT, 4))
	theme.set_color("font_color", "LineEdit", TEXT)
	theme.set_color("font_placeholder_color", "LineEdit", TEXT_DIM)
	theme.set_color("caret_color", "LineEdit", ACCENT)

	theme.set_color("font_color", "Label", TEXT)

	# Sliders big enough for a thumb: a thick track, a filled part in the accent colour and a large
	# round handle.
	var track := StyleBoxFlat.new()
	track.bg_color = Color(1, 1, 1, 0.18)
	track.set_corner_radius_all(10)
	track.content_margin_top = 10
	track.content_margin_bottom = 10
	var filled := track.duplicate() as StyleBoxFlat
	filled.bg_color = ACCENT
	theme.set_stylebox("slider", "HSlider", track)
	theme.set_stylebox("grabber_area", "HSlider", filled)
	theme.set_stylebox("grabber_area_highlight", "HSlider", filled)
	theme.set_icon("grabber", "HSlider", _disc(44, Color.WHITE))
	theme.set_icon("grabber_highlight", "HSlider", _disc(48, ACCENT.lightened(0.4)))
	return theme


## A round handle for sliders, drawn here (no image files).
static func _disc(diameter: int, color: Color) -> Texture2D:
	var image := Image.create(diameter, diameter, false, Image.FORMAT_RGBA8)
	var r := diameter / 2.0
	for y in diameter:
		for x in diameter:
			var d := Vector2(x + 0.5 - r, y + 0.5 - r).length()
			if d <= r:
				image.set_pixel(x, y, Color(color, clampf(r - d, 0.0, 1.0)) if d > r - 3.0 else color.darkened(0.08 if d > r - 6.0 else 0.0))
	return ImageTexture.create_from_image(image)


## The box behind a panel of buttons (the in-match menu): roomier than a button's.
static func panel_box() -> StyleBoxFlat:
	var box := _box(PANEL, ACCENT.darkened(0.25))
	box.set_content_margin_all(40)
	return box


## A rounded box with a border, padded so text has room.
static func _box(fill: Color, border: Color, border_width: int = 3) -> StyleBoxFlat:
	var box := StyleBoxFlat.new()
	box.bg_color = fill
	box.border_color = border
	box.set_border_width_all(border_width)
	box.set_corner_radius_all(18)
	box.content_margin_left = 28
	box.content_margin_right = 28
	box.content_margin_top = 14
	box.content_margin_bottom = 14
	return box
