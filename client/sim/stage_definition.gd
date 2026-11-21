class_name StageDefinition
extends RefCounted
## Arena data (StageDefinition.cs). Platforms are land-on-top surfaces; platform 0 is the main
## stage. Positions in Fx units, y up.

var id := ""
var platforms: Array[FxRect] = []
var spawn_x: PackedInt64Array = []
var spawn_y: PackedInt64Array = []
var blast_zone: FxRect
## The arena's hazards (StageDefinition.Hazards): each one's kind (Hazards.LAUNCH, HIT or PUSH), its
## zone, and the way a pusher carries fighters (+1 right, -1 left). On only when rules.hazards is.
var hazard_kinds: PackedInt32Array = []
var hazard_zones: Array[FxRect] = []
var hazard_dirs: PackedInt32Array = []
## SIZE_NORMAL, or SIZE_BIG: the level a room of BIG_FROM_PLAYERS or more plays on (big()).
var size := SIZE_NORMAL

## StageSize and StageScaling (StageDefinition.cs).
const SIZE_NORMAL := 0
const SIZE_BIG := 1
const BIG_FROM_PLAYERS := 9
## The order the 15 big-level spawn slots are used in, so any first 9 to 15 spread over the floor.
const BIG_SPAWN_ORDER := [0, 14, 7, 3, 11, 1, 13, 5, 9, 2, 12, 6, 8, 4, 10]

static var _graybox: StageDefinition
## Arena id -> its big level, built on first use.
static var _big_cache: Dictionary = {}


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


## Rookie Rooftops (Shan's arena), the default room arena (Stages.Rooftops); all 31 arenas are in
## ArenaStages, generated from the art pack's layouts.
static func rooftops() -> StageDefinition:
	return ArenaStages.by_id("rooftops")


## The stage with this id at this size, or null (rooms name their stage and size in the start
## message). The big graybox isn't kept: rooms play arena stages only (Stages.ById).
static func by_id(stage_id: String, stage_size := SIZE_NORMAL) -> StageDefinition:
	if stage_size == SIZE_NORMAL:
		if stage_id == "graybox":
			return graybox()
		return ArenaStages.by_id(stage_id)
	if _big_cache.has(stage_id):
		return _big_cache[stage_id]
	var base := ArenaStages.by_id(stage_id)
	if base == null:
		return null
	var s := big(base)
	_big_cache[stage_id] = s
	return s


## The level for this many fighters (StageScaling.SizeFor): big from BIG_FROM_PLAYERS.
static func size_for(players: int) -> int:
	return SIZE_BIG if players >= BIG_FROM_PLAYERS else SIZE_NORMAL


## The big level (StageScaling.Big, rooms v1 section 2.3): x stretched by 3/2 (truncated toward
## zero), heights kept; hazards as wide as before on a centre moved out by 3/2; 15 spawn points 3 m
## apart at y = 2; the blast zone's sides at +-25.2 m.
static func big(base: StageDefinition) -> StageDefinition:
	var s := StageDefinition.new()
	s.id = base.id
	s.size = SIZE_BIG
	var platforms: Array[FxRect] = []
	for p in base.platforms:
		platforms.append(FxRect.new(p.min_x * 3 / 2, p.min_y, p.max_x * 3 / 2, p.max_y))
	s.platforms = platforms
	var zones: Array[FxRect] = []
	for k in base.hazard_kinds.size():
		var z := base.hazard_zones[k]
		var c := (z.min_x + z.max_x) / 2 * 3 / 2
		var half := (z.max_x - z.min_x) / 2
		zones.append(FxRect.new(c - half, z.min_y, c + half, z.max_y))
	s.hazard_kinds = base.hazard_kinds.duplicate()
	s.hazard_zones = zones
	s.hazard_dirs = base.hazard_dirs.duplicate()
	var xs := PackedInt64Array()
	var ys := PackedInt64Array()
	for k: int in BIG_SPAWN_ORDER:
		xs.append(Fx.from_int(-21 + 3 * k))
		ys.append(Fx.from_int(2))
	s.spawn_x = xs
	s.spawn_y = ys
	s.blast_zone = FxRect.new(Fx.ratio(-126, 5), Fx.from_int(-10), Fx.ratio(126, 5), Fx.from_int(22))
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
	for i in hazard_kinds.size():
		h.add_int(hazard_kinds[i])
		var z := hazard_zones[i]
		for v in [z.min_x, z.min_y, z.max_x, z.max_y]:
			h.add_u64(v)
		h.add_int(hazard_dirs[i])
