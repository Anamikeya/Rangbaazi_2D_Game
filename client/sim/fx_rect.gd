class_name FxRect
extends RefCounted
## Axis-aligned box in Fix units (see Fx), y up. Mirrors FixRect.cs: the constructor orders the corners.

var min_x: int
var min_y: int
var max_x: int
var max_y: int


func _init(x0: int, y0: int, x1: int, y1: int) -> void:
	min_x = mini(x0, x1)
	max_x = maxi(x0, x1)
	min_y = mini(y0, y1)
	max_y = maxi(y0, y1)


func overlaps(o: FxRect) -> bool:
	return min_x < o.max_x and o.min_x < max_x and min_y < o.max_y and o.min_y < max_y


func contains(x: int, y: int) -> bool:
	return x >= min_x and x <= max_x and y >= min_y and y <= max_y
