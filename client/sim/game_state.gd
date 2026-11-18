class_name GameState
extends RefCounted
## Everything that changes during a match (GameState.cs). Rollback = clone() / copy_from(), then
## re-run Simulation.advance. Never put rendering, audio or effects here. New fields must go in
## copy_from() and checksum() in the reference's order.

var frame := 0
var random_state := 0   # SimRandom state (64-bit)
var fighters: Array[Fighter] = []
var match_over := false
var winner_index := -1
## Prop Boxes, loose weapons and projectiles: a fixed array of Weapons.MAX_PROPS.
var props: Array[Prop] = []
var prop_timer := 0      # frames until the next Prop Box drops
var last_box_weapon := 0 # the weapon the last one held


func _init(fighter_count: int = 2, seed: int = 0) -> void:
	random_state = seed
	for i in fighter_count:
		fighters.append(Fighter.new())
	for i in Weapons.MAX_PROPS:
		props.append(Prop.new())


func clone() -> GameState:
	var copy := GameState.new(fighters.size(), 0)
	copy.copy_from(self)
	return copy


func copy_from(o: GameState) -> void:
	assert(o.fighters.size() == fighters.size(), "Fighter count mismatch")
	frame = o.frame
	random_state = o.random_state
	for i in fighters.size():
		fighters[i].copy_from(o.fighters[i])
	match_over = o.match_over
	winner_index = o.winner_index
	for i in props.size():
		props[i].copy_from(o.props[i])
	prop_timer = o.prop_timer
	last_box_weapon = o.last_box_weapon


## Stable 64-bit checksum, equal to the C# GameState.Checksum() for the same state.
func checksum() -> int:
	var h := StateHasher.new()
	h.add_int(frame)
	h.add_u64(random_state)
	h.add_bool(match_over)
	h.add_int(winner_index)
	for f in fighters:
		f.add_to(h)
	h.add_int(prop_timer)
	h.add_int(last_box_weapon)
	for p in props:
		p.add_to(h)
	return h.value()
