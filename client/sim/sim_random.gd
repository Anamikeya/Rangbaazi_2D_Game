class_name SimRandom
extends RefCounted
## SplitMix64, bit-identical to SimRandom.cs. Its whole state is one 64-bit int kept in
## GameState.random_state (so rollback rewinds it); this class wraps a copy of it.

const GOLDEN := -7046029254386353131 # 0x9E3779B97F4A7C15
const MIX1 := -4658895280553007687   # 0xBF58476D1CE4E5B9
const MIX2 := -7723592293110705685   # 0x94D049BB133111EB

var state: int


func _init(seed: int) -> void:
	state = seed


## Unsigned (logical) right shift of a 64-bit value.
static func ushr(v: int, n: int) -> int:
	return (v >> n) & ((1 << (64 - n)) - 1)


func next_u64() -> int:
	state += GOLDEN
	var z := state
	z = (z ^ ushr(z, 30)) * MIX1
	z = (z ^ ushr(z, 27)) * MIX2
	return z ^ ushr(z, 31)


func next_u32() -> int:
	return ushr(next_u64(), 32)


## Uniform int in [min_inclusive, max_exclusive), multiply-shift like the reference.
func range_int(min_inclusive: int, max_exclusive: int) -> int:
	assert(max_exclusive > min_inclusive)
	var span := max_exclusive - min_inclusive
	return min_inclusive + ushr(next_u32() * span, 32)


## True with probability numerator / denominator.
func chance(numerator: int, denominator: int) -> bool:
	return range_int(0, denominator) < numerator
