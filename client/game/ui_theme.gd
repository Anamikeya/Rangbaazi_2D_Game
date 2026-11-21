class_name UiTheme
## The look of the menu and HUD controls, built in code (no external assets): the default font,
## large type and big rounded buttons that are easy to hit with a thumb.

const ACCENT := Color("f2a900")      # marigold, P1's colour
const PANEL := Color(0.14, 0.13, 0.18, 0.97) # panels over the arena: it hardly shows through
const TEXT := Color("f4f1ea")
const TEXT_DIM := Color(0.96, 0.94, 0.9, 0.6)
const ERROR := Color("ff6b6b")
const DISABLED_FILL := Color(0.098, 0.091, 0.126, 1.0) # PANEL.darkened(0.3), opaque
## Slider handles, across (virtual px).
const GRABBER := 84
## The heading over a screen, dim (SETTINGS, CHOOSE THE ARENA).
const HEADING_SIZE := 44


static func build() -> Theme:
	var theme := Theme.new()
	theme.default_font = ThemeDB.fallback_font
	theme.default_font_size = 36

	theme.set_stylebox("normal", "Button", _box(PANEL, ACCENT.darkened(0.25)))
	theme.set_stylebox("hover", "Button", _box(PANEL.lightened(0.12), ACCENT))
	theme.set_stylebox("pressed", "Button", _box(ACCENT, ACCENT.lightened(0.2)))
	theme.set_stylebox("hover_pressed", "Button", _box(ACCENT.lightened(0.1), ACCENT.lightened(0.3))) # a lit chip under the pointer
	# Opaque, so a greyed button never shows the arena through it (rooms v1, fix 28).
	theme.set_stylebox("disabled", "Button", _box(DISABLED_FILL, Color(1, 1, 1, 0.15)))
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
	# round handle (GRABBER virtual px across: as big as a fingertip on the phone, rooms v1 fix 15).
	var track := StyleBoxFlat.new()
	track.bg_color = Color(1, 1, 1, 0.18)
	track.set_corner_radius_all(12)
	track.content_margin_top = 12
	track.content_margin_bottom = 12
	var filled := track.duplicate() as StyleBoxFlat
	filled.bg_color = ACCENT
	theme.set_stylebox("slider", "HSlider", track)
	theme.set_stylebox("grabber_area", "HSlider", filled)
	theme.set_stylebox("grabber_area_highlight", "HSlider", filled)
	theme.set_icon("grabber", "HSlider", _disc(GRABBER, Color.WHITE))
	theme.set_icon("grabber_highlight", "HSlider", _disc(GRABBER, ACCENT.lightened(0.4)))
	return theme


## The same look with narrower padding inside buttons, for a row of short chips that has to fit a
## narrow column (the creator's poses).
static func compact(side_margin := 10) -> Theme:
	var theme := build()
	for state in ["normal", "hover", "pressed", "hover_pressed", "disabled", "focus"]:
		var box := theme.get_stylebox(state, "Button").duplicate() as StyleBoxFlat
		box.content_margin_left = side_margin
		box.content_margin_right = side_margin
		theme.set_stylebox(state, "Button", box)
	return theme


## A heading over a screen (SETTINGS, CHOOSE YOUR FIGHTER, CHOOSE THE ARENA): one style everywhere.
static func heading(text: String) -> Label:
	var l := Label.new()
	l.text = text
	l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	l.add_theme_font_size_override("font_size", HEADING_SIZE)
	l.add_theme_color_override("font_color", TEXT_DIM)
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return l


## A label drawn as a chip: a setting shown but not changeable here (a guest's view of the host's
## choices, "BIG ARENA"). Not a control.
static func info_chip(text: String, font_size := 30, color := TEXT) -> Label:
	var l := Label.new()
	l.text = text
	l.add_theme_font_size_override("font_size", font_size)
	l.add_theme_color_override("font_color", color)
	var box := StyleBoxFlat.new()
	box.bg_color = Color(0.1, 0.095, 0.13, 0.92)
	box.border_color = Color(color, 0.7)
	box.set_border_width_all(3)
	box.set_corner_radius_all(18)
	box.content_margin_left = 18
	box.content_margin_right = 18
	box.content_margin_top = 6
	box.content_margin_bottom = 6
	l.add_theme_stylebox_override("normal", box)
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return l


## Colours a button as the risky answer of a question ("Yes", "Leave"): the error colour.
static func make_danger(button: Button) -> void:
	button.add_theme_color_override("font_color", ERROR)
	button.add_theme_color_override("font_hover_color", ERROR.lightened(0.2))
	button.add_theme_color_override("font_focus_color", ERROR)


## "..." growing one dot at a time, so a wait shows that something is still happening.
static func dots() -> String:
	return ".".repeat(1 + (Time.get_ticks_msec() / 400) % 3)


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


## The box behind a group of controls on a screen (the menu's fighter card).
static func card_box() -> StyleBoxFlat:
	var box := _box(Color(0.1, 0.095, 0.13, 0.86), Color(1, 1, 1, 0.14), 2)
	box.set_content_margin_all(22)
	return box


## A filled button for the one thing a screen most wants pressed. `shade` lightens (above 0) or
## darkens it for the hover and pressed states.
static func primary_box(shade: float) -> StyleBoxFlat:
	var fill := ACCENT.lightened(shade) if shade >= 0.0 else ACCENT.darkened(-shade)
	return _box(fill, fill.lightened(0.25))


## Whether the game can close itself: on Android and desktop yes; in a browser the page can't
## close its own tab, so there's no Quit button there.
static func can_quit() -> bool:
	return not OS.has_feature("web")


## A question with two answers as chips side by side: "Quit?  No / Yes", "Leave the room?  Stay /
## Leave". The safe answer first; the risky one in the error colour. Never a button that changes its
## own label (rooms v1, fix 26). Returns the row; its children are the label, then the two buttons.
static func confirm_row(question: String, safe: String, risky: String, on_safe: Callable, on_risky: Callable,
		height := 96.0, font_size := 38) -> HBoxContainer:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 16)
	var ask := Label.new()
	ask.text = question
	ask.add_theme_font_size_override("font_size", font_size)
	ask.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	ask.mouse_filter = Control.MOUSE_FILTER_IGNORE
	row.add_child(ask)
	for answer: Array in [[safe, on_safe, false], [risky, on_risky, true]]:
		var b := Button.new()
		b.text = str(answer[0])
		b.focus_mode = Control.FOCUS_NONE
		b.custom_minimum_size = Vector2(170, height)
		b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		b.add_theme_font_size_override("font_size", font_size)
		b.pressed.connect(answer[1] as Callable)
		if answer[2]:
			make_danger(b)
		row.add_child(b)
	return row


## Makes a button the screen's main action: filled with the accent colour, dark type.
static func make_primary(button: Button) -> void:
	button.add_theme_stylebox_override("normal", primary_box(0.0))
	button.add_theme_stylebox_override("hover", primary_box(0.12))
	button.add_theme_stylebox_override("pressed", primary_box(-0.15))
	for state in ["font_color", "font_hover_color", "font_focus_color", "font_pressed_color"]:
		button.add_theme_color_override(state, Color("1b1a22"))


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
