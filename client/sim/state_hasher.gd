class_name StateHasher
extends RefCounted
## FNV-1a 64-bit over 8-byte little-endian values, bit-identical to StateHasher.cs. GDScript has
## no unsigned ints, so 64-bit values are held as signed ints with the same bits; multiplication
## wraps around mod 2^64 just like C#'s ulong.

const OFFSET_BASIS := -3750763034362895579 # 14695981039346656037 as signed 64-bit
const PRIME := 1099511628211

var _hash := OFFSET_BASIS


## A 64-bit value as is (C# Add(ulong) / Add(long)).
func add_u64(v: int) -> void:
	var h := _hash
	for i in 8:
		h = (h ^ ((v >> (i * 8)) & 0xFF)) * PRIME
	_hash = h


## A 32-bit int, zero-extended like C#'s Add(int) => Add((ulong)(uint)value).
func add_int(v: int) -> void:
	add_u64(v & 0xFFFFFFFF)


func add_bool(b: bool) -> void:
	add_u64(1 if b else 0)


## PRIME to the power n, wrapping like the hash does.
static func prime_power(n: int) -> int:
	var r := 1
	for i in n:
		r *= PRIME
	return r


## A run of zero values, as one multiplication: a zero byte only multiplies the hash by PRIME, so
## `count` zero values multiply it by prime_power(count * 8). Pass that product.
func add_zeros(product: int) -> void:
	_hash *= product


func value() -> int:
	return _hash


## A 64-bit value as 16 upper-case hex digits, as C#'s ToString("X16") prints the ulong.
static func hex(v: int) -> String:
	return "%08X%08X" % [(v >> 32) & 0xFFFFFFFF, v & 0xFFFFFFFF]
