class_name Fx
## Fixed-point numbers as plain ints: raw value = real value x 65536 (Q47.16), bit-identical to
## the C# reference (src/Rangbaazi.Sim/FixedMath/Fix.cs). The sim uses only ints, never floats,
## so every device (browser, phone) computes exactly the same match from the same inputs, which
## rollback netcode depends on. A "Fix" below is such an int; "x int" means multiplied by a whole
## number (no rescaling).

const FRAC_BITS := 16
const ONE := 65536
const HALF := 32768
const ZERO := 0


static func from_int(v: int) -> int:
	return v * ONE


## numerator / denominator, truncated toward zero like C#'s ((Int128)n << 16) / d.
static func ratio(numerator: int, denominator: int) -> int:
	return (numerator * ONE) / denominator


## Fix x Fix. Arithmetic shift, as in the reference.
static func mul(a: int, b: int) -> int:
	return (a * b) >> FRAC_BITS


## Fix / Fix, truncated toward zero.
static func div(a: int, b: int) -> int:
	return (a * ONE) / b


static func floor_to_int(a: int) -> int:
	return a >> FRAC_BITS


static func move_toward(value: int, target: int, max_delta: int) -> int:
	if value < target:
		return mini(value + max_delta, target)
	if value > target:
		return maxi(value - max_delta, target)
	return value


## Fix to float, for drawing only. Never feed the result back into the sim.
static func to_float(a: int) -> float:
	return float(a) / ONE
