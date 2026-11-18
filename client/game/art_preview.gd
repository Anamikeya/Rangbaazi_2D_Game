class_name ArtPreview
extends Control
## A small live picture for menus: an arena's painted layers, or a fighter idling. Tapping it
## emits `pressed` (the lobby opens the picker from it).

signal pressed

var arena_id := ""
var fighter_id := ""
var _painter: FighterPainter
var _painter_id := ""
var _frame := 0


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_STOP


func _process(_delta: float) -> void:
	if is_visible_in_tree():
		_frame += 1
		queue_redraw()


func _gui_input(event: InputEvent) -> void:
	var b := event as InputEventMouseButton
	if b != null and b.pressed and b.button_index == MOUSE_BUTTON_LEFT:
		pressed.emit()
		accept_event()


func _draw() -> void:
	var r := Rect2(Vector2.ZERO, size)
	draw_rect(r, Color(0.05, 0.045, 0.07, 0.9))
	if arena_id != "":
		draw_rect(r, Color(str(ArtLibrary.arena(arena_id).get("sky", "#1a1626"))))
		for layer: Dictionary in ArtLibrary.arena(arena_id).get("layers", []):
			if float(layer.get("z", 0)) <= 0.0:
				var tex := ArtLibrary.arena_texture(arena_id, str(layer["file"]))
				if tex != null:
					draw_texture_rect(tex, r, false)
	if fighter_id != "":
		if _painter == null or _painter_id != fighter_id:
			_painter = FighterPainter.new(fighter_id)
			_painter_id = fighter_id
		draw_set_transform(Vector2(size.x / 2, size.y * 0.92), 0.0, Vector2(1, 0.3))
		draw_circle(Vector2.ZERO, size.x * 0.3, Color(UiTheme.ACCENT, 0.4))
		draw_set_transform_matrix(Transform2D.IDENTITY)
		_painter.draw(self, Vector2(size.x / 2, size.y * 0.92), size.y / 300.0, 1, "idle", _frame)
	draw_rect(r, Color(UiTheme.ACCENT, 0.8), false, 4.0)
