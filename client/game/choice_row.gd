class_name ChoiceRow
extends HBoxContainer
## One setting with every choice in view: a label, then a chip per option, the current one lit.
## Used in place of drop-downs and of buttons that cycle through hidden options: one tap picks,
## and nothing has to be opened or remembered.

signal picked(index: int)

var _chips: Array[Button] = []
var _group := ButtonGroup.new()


## A row titled `title` with one chip per option, `selected` lit. `on_pick` gets the index tapped.
static func make(title: String, options: Array, selected: int, on_pick: Callable, label_width := 420.0, height := 88.0) -> ChoiceRow:
	var row := ChoiceRow.new()
	row.add_theme_constant_override("separation", 14)
	if title != "":
		var label := Label.new()
		label.text = title
		label.custom_minimum_size = Vector2(label_width, 0)
		label.add_theme_font_size_override("font_size", 36)
		label.mouse_filter = Control.MOUSE_FILTER_IGNORE
		row.add_child(label)
	for i in options.size():
		var chip := Button.new()
		chip.text = str(options[i])
		chip.toggle_mode = true
		chip.button_group = row._group
		chip.button_pressed = i == selected
		chip.focus_mode = Control.FOCUS_NONE
		chip.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		chip.custom_minimum_size = Vector2(0, height)
		chip.add_theme_font_size_override("font_size", 34)
		chip.pressed.connect(func() -> void:
			row.picked.emit(i)
			on_pick.call(i))
		row.add_child(chip)
		row._chips.append(chip)
	return row


## Lights a chip without calling back (the value changed somewhere else).
func select(index: int) -> void:
	for i in _chips.size():
		_chips[i].set_pressed_no_signal(i == index)


func selected() -> int:
	for i in _chips.size():
		if _chips[i].button_pressed:
			return i
	return -1
