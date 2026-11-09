class_name StageDefinition
extends RefCounted
## Arena data (StageDefinition.cs). Platforms are land-on-top surfaces; platform 0 is the main
## stage. Positions in Fx units, y up.

var id := ""
var platforms: Array[FxRect] = []
var spawn_x: PackedInt64Array = []
var spawn_y: PackedInt64Array = []
var blast_zone: FxRect

static var _graybox: StageDefinition


static func graybox() -> StageDefinition:
	if _graybox != null:
		return _graybox
	var s := StageDefinition.new()
	s.id = "graybox"
	s.platforms = [
		FxRect.new(Fx.from_int(-9), Fx.from_int(-2), Fx.from_int(9), 0),
		FxRect.new(Fx.from_int(-6), Fx.ratio(5, 2), Fx.from_int(-2), Fx.ratio(11, 4)),
		FxRect.new(Fx.from_int(2), Fx.ratio(5, 2), Fx.from_int(6), Fx.ratio(11, 4)),
	]
	# Over open main-stage ground (clear of the floating platforms) so everyone lands at y = 0.
	s.spawn_x = PackedInt64Array([Fx.ratio(-15, 2), Fx.ratio(15, 2), Fx.from_int(-1), Fx.from_int(1)])
	s.spawn_y = PackedInt64Array([Fx.from_int(3), Fx.from_int(3), Fx.from_int(3), Fx.from_int(3)])
	s.blast_zone = FxRect.new(Fx.from_int(-18), Fx.from_int(-10), Fx.from_int(18), Fx.from_int(14))
	_graybox = s
	return s


func add_to(h: StateHasher) -> void:
	for p in platforms:
		for v in [p.min_x, p.min_y, p.max_x, p.max_y]:
			h.add_u64(v)
	for i in spawn_x.size():
		h.add_u64(spawn_x[i])
		h.add_u64(spawn_y[i])
	for v in [blast_zone.min_x, blast_zone.min_y, blast_zone.max_x, blast_zone.max_y]:
		h.add_u64(v)
