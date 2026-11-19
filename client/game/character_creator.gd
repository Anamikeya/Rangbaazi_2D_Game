class_name CharacterCreator
extends Control
## The character creator: build your own fighter from the 31 fighters' pieces. Four strips to slide
## through, one each for the head (with its hat and faces), the hands, the feet and the moves (whose
## animations it uses), a name, and a live preview you can make stand, run, punch and celebrate.
## The result is a "mix" id (ArtLibrary.make_mix) that works anywhere a fighter's id does. Looks
## only: every fighter has the same moves and numbers, mixed ones included.

signal saved(id: String)
signal cancelled

const ROWS := [["HEAD", "head"], ["HANDS", "frontHand"], ["FEET", "frontFoot"], ["MOVES", "head"]]
const POSES := [["Stand", "idle"], ["Run", "run_loop"], ["Punch", "jab"], ["Heavy", "side_heavy"], ["Win", "victory_a"]]

var _ids: PackedStringArray = []
var _strips: Array[PartStrip] = []
var _preview: ArtPreview
var _name_edit: LineEdit
var _save: Button
var _rng := RandomNumberGenerator.new()


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UiTheme.build()
	mouse_filter = Control.MOUSE_FILTER_STOP
	_rng.randomize()
	for f: Dictionary in ArtLibrary.fighters():
		_ids.append(str(f["id"]))

	var margin := MarginContainer.new()
	margin.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	for side in ["left", "right"]:
		margin.add_theme_constant_override("margin_" + side, 60)
	margin.add_theme_constant_override("margin_top", 30)
	margin.add_theme_constant_override("margin_bottom", 30)
	add_child(margin)
	var page := VBoxContainer.new()
	page.add_theme_constant_override("separation", 16)
	margin.add_child(page)
	page.add_child(_label("CREATE YOUR FIGHTER", 44, UiTheme.TEXT_DIM))

	var main := HBoxContainer.new()
	main.add_theme_constant_override("separation", 40)
	main.size_flags_vertical = Control.SIZE_EXPAND_FILL
	page.add_child(main)

	# Left: the fighter you're making, its name, and poses to try it in.
	var left := VBoxContainer.new()
	left.custom_minimum_size = Vector2(560, 0)
	left.add_theme_constant_override("separation", 14)
	main.add_child(left)
	_preview = ArtPreview.new()
	_preview.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_preview.framed = false
	_preview.replay = true
	left.add_child(_preview)
	var poses := HBoxContainer.new()
	poses.alignment = BoxContainer.ALIGNMENT_CENTER
	poses.add_theme_constant_override("separation", 8)
	left.add_child(poses)
	var group := ButtonGroup.new()
	for pose: Array in POSES:
		var chip := Button.new()
		chip.text = str(pose[0])
		chip.toggle_mode = true
		chip.button_group = group
		chip.button_pressed = pose[1] == "idle"
		chip.focus_mode = Control.FOCUS_NONE
		chip.custom_minimum_size = Vector2(104, 70)
		chip.add_theme_font_size_override("font_size", 26)
		chip.pressed.connect(func() -> void: _preview.clip = str(pose[1]))
		poses.add_child(chip)
	_name_edit = LineEdit.new()
	_name_edit.placeholder_text = "FIGHTER'S NAME"
	_name_edit.max_length = 12
	_name_edit.alignment = HORIZONTAL_ALIGNMENT_CENTER
	_name_edit.custom_minimum_size = Vector2(0, 88)
	_name_edit.add_theme_font_size_override("font_size", 38)
	_name_edit.text_changed.connect(func(_text: String) -> void: _refresh())
	left.add_child(_name_edit)

	# Right: one strip per piece.
	var right := VBoxContainer.new()
	right.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right.add_theme_constant_override("separation", 10)
	main.add_child(right)
	for row: Array in ROWS:
		var strip := PartStrip.new()
		strip.title = str(row[0])
		strip.part = str(row[1])
		strip.ids = _ids
		strip.size_flags_vertical = Control.SIZE_EXPAND_FILL
		strip.custom_minimum_size = Vector2(0, 150)
		strip.changed.connect(_refresh)
		right.add_child(strip)
		_strips.append(strip)

	var bottom := HBoxContainer.new()
	bottom.alignment = BoxContainer.ALIGNMENT_CENTER
	bottom.add_theme_constant_override("separation", 30)
	page.add_child(bottom)
	var back := _button("Back", 220)
	back.pressed.connect(func() -> void: cancelled.emit())
	bottom.add_child(back)
	var random := _button("Surprise me", 340)
	random.pressed.connect(_randomize)
	bottom.add_child(random)
	_save = _button("Fight as ...", 620)
	UiTheme.make_primary(_save)
	_save.pressed.connect(func() -> void: saved.emit(mix_id()))
	bottom.add_child(_save)


## Opens on the player's saved creation, or on a copy of the fighter they have now.
func open(current: String) -> void:
	var m := ArtLibrary.mix_parts(GameSettings.custom)
	if m.is_empty():
		m = ArtLibrary.mix_parts(current)
	if m.is_empty():
		var base := current if ArtLibrary.is_fighter(current) else "shahn"
		m = PackedStringArray([base, base, base, base, ""])
	for i in 4:
		_strips[i].select(maxi(0, _ids.find(m[i])))
	_name_edit.text = m[4]
	show()
	_refresh()


func mix_id() -> String:
	return ArtLibrary.make_mix(_strips[0].picked(), _strips[1].picked(), _strips[2].picked(), _strips[3].picked(), _name_edit.text)


func _randomize() -> void:
	for strip in _strips:
		strip.select(_rng.randi_range(0, _ids.size() - 1))
	_refresh()


func _refresh() -> void:
	var id := mix_id()
	_preview.fighter_id = id
	_save.text = "Fight as %s" % ArtLibrary.fighter_name(id)


func _unhandled_key_input(event: InputEvent) -> void:
	if visible and event.is_action_pressed("ui_cancel"):
		cancelled.emit()
		get_viewport().set_input_as_handled()


func _draw() -> void:
	draw_rect(Rect2(Vector2.ZERO, size), Color(0.07, 0.06, 0.1))


func _button(text: String, width: float) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.custom_minimum_size = Vector2(width, 110)
	b.add_theme_font_size_override("font_size", 42)
	return b


func _label(text: String, font_size: int, color: Color) -> Label:
	var l := Label.new()
	l.text = text
	l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	l.add_theme_font_size_override("font_size", font_size)
	l.add_theme_color_override("font_color", color)
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return l


## One strip of the creator: every fighter's version of a piece, side by side. Drag to slide the
## strip, tap a piece to pick it; the pick has the marigold frame.
class PartStrip:
	extends Control

	signal changed

	const CELL := 150.0
	const LABEL := 170.0 # room on the left for the strip's title

	var title := ""
	var part := "head" # the rig part whose picture stands for each fighter
	var ids: PackedStringArray = []
	var _picked := 0
	var _scroll := 0.0 # pixels the strip has slid
	var _target := 0.0
	var _dragging := false
	var _moved := 0.0

	func _ready() -> void:
		mouse_filter = Control.MOUSE_FILTER_STOP
		clip_contents = true

	func _notification(what: int) -> void:
		if what == NOTIFICATION_RESIZED and not ids.is_empty():
			select(_picked) # keep the pick in the middle when the strip gets its size
			_scroll = _target

	func picked() -> String:
		return ids[_picked] if _picked < ids.size() else "shahn"

	## Picks piece i and slides it to the middle.
	func select(i: int) -> void:
		_picked = clampi(i, 0, ids.size() - 1)
		_target = _clamp_scroll(_picked * CELL + CELL / 2.0 - (size.x - LABEL) / 2.0)
		queue_redraw()

	func _clamp_scroll(s: float) -> float:
		return clampf(s, 0.0, maxf(0.0, ids.size() * CELL - (size.x - LABEL)))

	func _process(delta: float) -> void:
		if not is_visible_in_tree():
			return
		if not _dragging and absf(_scroll - _target) > 0.5:
			_scroll = lerpf(_scroll, _target, minf(1.0, delta * 12.0))
			queue_redraw()

	func _gui_input(event: InputEvent) -> void:
		var button := event as InputEventMouseButton
		if button != null and button.button_index == MOUSE_BUTTON_LEFT:
			if button.pressed:
				_dragging = true
				_moved = 0.0
			else:
				_dragging = false
				if _moved < 12.0 and button.position.x > LABEL:
					var i := int((button.position.x - LABEL + _scroll) / CELL)
					if i >= 0 and i < ids.size():
						select(i)
						changed.emit()
			accept_event()
			return
		var motion := event as InputEventMouseMotion
		if motion != null and _dragging:
			_moved += absf(motion.relative.x)
			_scroll = _clamp_scroll(_scroll - motion.relative.x)
			_target = _scroll
			queue_redraw()
			accept_event()

	func _draw() -> void:
		var font := get_theme_default_font()
		draw_rect(Rect2(Vector2.ZERO, size), Color(0.05, 0.045, 0.07, 0.9))
		var pad := 8.0
		for i in ids.size():
			var x := LABEL + i * CELL - _scroll
			if x + CELL < LABEL or x > size.x:
				continue
			var cell := Rect2(x + pad, pad, CELL - pad * 2.0, size.y - pad * 2.0)
			var on := i == _picked
			draw_rect(cell, Color(UiTheme.ACCENT, 0.22) if on else Color(1, 1, 1, 0.04))
			if on:
				draw_rect(cell, UiTheme.ACCENT, false, 4.0)
			var tex := _picture(ids[i])
			var box := Rect2(cell.position + Vector2(8, 6), Vector2(cell.size.x - 16, cell.size.y - 40))
			if tex != null:
				var k := minf(box.size.x / tex.get_width(), box.size.y / tex.get_height())
				var s := Vector2(tex.get_width(), tex.get_height()) * k
				draw_texture_rect(tex, Rect2(box.position + (box.size - s) / 2.0, s), false)
			draw_string(font, Vector2(cell.position.x, cell.end.y - 10), ArtLibrary.fighter_name(ids[i]),
				HORIZONTAL_ALIGNMENT_CENTER, cell.size.x, 22, UiTheme.ACCENT if on else UiTheme.TEXT_DIM)
		# The title sits over the left end, on its own dark block.
		draw_rect(Rect2(0, 0, LABEL, size.y), Color(0.07, 0.06, 0.1))
		draw_string(font, Vector2(0, size.y / 2.0 + 12), title, HORIZONTAL_ALIGNMENT_CENTER, LABEL, 34, UiTheme.TEXT)

	func _picture(id: String) -> Texture2D:
		var spec: Dictionary = ArtLibrary.character(id).get("parts", {}).get(part, {})
		var textures: Dictionary = spec.get("textures", {})
		var file: Variant = textures.get(str(spec.get("texture", "normal")), null)
		return ArtLibrary.character_texture(id, str(file)) if file != null else null
