class_name Prop
extends RefCounted
## One box, loose weapon or projectile (Prop in Weapons.cs). Kept in a fixed array in
## GameState.props. Position is the bottom centre. Fields, copy_from() and add_to() are in the
## C# reference's order.
##
## An empty slot (kind 0) has every field zero, as C#'s default(Prop) does: clear() is the only way
## to empty one. copy_from() and add_to() rely on it, so a table of mostly empty slots costs little
## (client/tests/test_state_coverage.gd checks it over a whole match).

const FIELDS := 17
static var _EMPTY_HASH := StateHasher.prime_power(FIELDS * 8)

var kind := 0           # Weapons.PROP_*
var weapon := 0         # Weapons weapon id
var shot := 0           # Weapons.SHOT_*
var supply := 0         # ammo or durability the weapon carries (a box flung by a swing carries it as a thrown shot)
var owner := 0          # the fighter who fired or threw it, or -1
var owner_clear := false
var pos_x := 0
var pos_y := 0
var vel_x := 0
var vel_y := 0
var grounded := false
var timer := 0          # age of a box or item, fuse of a bomb, arming of a mine or trap, life left of a pad, zone or wall, wait of a pending box; a boomerang's leg
var age := 0            # frames in flight
var hit_mask := 0       # one bit per fighter already hit
var hits := 0
var ignore_fighter := 0 # a box or item: 1 + the fighter who just dropped it, or 0. A shot: swings a breakable prop has taken (its wear)
var ignore_timer := 0   # a box or item: frames left of that. A shot: screen-edge bounces done


## Back to C#'s default(Prop): every field zero.
func clear() -> void:
	kind = 0
	weapon = 0
	shot = 0
	supply = 0
	owner = 0
	owner_clear = false
	pos_x = 0
	pos_y = 0
	vel_x = 0
	vel_y = 0
	grounded = false
	timer = 0
	age = 0
	hit_mask = 0
	hits = 0
	ignore_fighter = 0
	ignore_timer = 0


func copy_from(o: Prop) -> void:
	if o.kind == 0:
		if kind != 0:
			clear()
		return
	kind = o.kind
	weapon = o.weapon
	shot = o.shot
	supply = o.supply
	owner = o.owner
	owner_clear = o.owner_clear
	pos_x = o.pos_x
	pos_y = o.pos_y
	vel_x = o.vel_x
	vel_y = o.vel_y
	grounded = o.grounded
	timer = o.timer
	age = o.age
	hit_mask = o.hit_mask
	hits = o.hits
	ignore_fighter = o.ignore_fighter
	ignore_timer = o.ignore_timer


func add_to(h: StateHasher) -> void:
	if kind == 0:
		h.add_zeros(_EMPTY_HASH) # seventeen zeros
		return
	h.add_int(kind)
	h.add_int(weapon)
	h.add_int(shot)
	h.add_int(supply)
	h.add_int(owner)
	h.add_bool(owner_clear)
	h.add_u64(pos_x)
	h.add_u64(pos_y)
	h.add_u64(vel_x)
	h.add_u64(vel_y)
	h.add_bool(grounded)
	h.add_int(timer)
	h.add_int(age)
	h.add_int(hit_mask)
	h.add_int(hits)
	h.add_int(ignore_fighter)
	h.add_int(ignore_timer)
