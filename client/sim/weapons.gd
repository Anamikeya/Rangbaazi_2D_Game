class_name Weapons
## The props' rules and the numbers every weapon shares (Weapons.cs). A weapon's own numbers are a
## row of the generated WeaponTable (tools/weapons.json; docs/design/combat-spec-v0.4-all-weapons.md):
## the engine reads the row's columns and never asks which weapon it is. Fix values are Fx ints.

# WeaponId: every id is in WeaponTable. The twelve of combat spec v0.3 keep their names here for
# the drawing code; the sim itself uses none of them.
const NONE := 0
const BELAN := WeaponTable.BELAN
const BASEBALL_BAT := WeaponTable.BASEBALL_BAT
const BOWLING_BALL := WeaponTable.BOWLING_BALL
const SHOTGUN := WeaponTable.SHOTGUN
const UZI := WeaponTable.UZI
const CHERRY_BOMBS := WeaponTable.CHERRY_BOMBS
const BOW := WeaponTable.BOW
const BOOMERANG := WeaponTable.BOOMERANG
const TRAMPOLINE := WeaponTable.TRAMPOLINE
const MINES := WeaponTable.MINES
const HOMING_MISSILE := WeaponTable.HOMING_MISSILE
const BLACK_HOLE_BOMB := WeaponTable.BLACK_HOLE_BOMB

## Rows in the table: the empty row 0, the weapons, and the hidden rows that are parts of weapons.
const COUNT := WeaponTable.COUNT
## The match record (GameState.weapon_stats): each fighter has RECORD_SLOTS slots of
## RECORD_SLOT_INTS ints, [weapon + 1, damage, KOs, sweet spots], then one int, all the damage they
## have done to others. A slot whose first int is 0 is empty.
const RECORD_SLOTS := 6
const RECORD_SLOT_INTS := 4
const RECORD_INTS := RECORD_SLOTS * RECORD_SLOT_INTS + 1

# ShotKind: a row's flight (WeaponTable.FLIGHT_*).
const SHOT_NONE := WeaponTable.FLIGHT_NONE
const SHOT_ROLL := WeaponTable.FLIGHT_ROLL           # along the ground, through the fighters it hits
const SHOT_BULLET := WeaponTable.FLIGHT_BULLET       # straight, fast, through platforms, gone on its first hit
const SHOT_BOMB := WeaponTable.FLIGHT_BOMB           # lobbed, with a fuse
const SHOT_ARROW := WeaponTable.FLIGHT_ARROW         # fast, dips a little, pierces if its row says so
const SHOT_THROWN := WeaponTable.FLIGHT_THROWN       # a thrown weapon: hits once, or lands, and is gone
const SHOT_BOOMERANG := WeaponTable.FLIGHT_BOOMERANG # out, slows, and back to its thrower's hand
const SHOT_PAD := WeaponTable.FLIGHT_PAD             # lies on the stage: anyone touching it is bounced up
const SHOT_MINE := WeaponTable.FLIGHT_MINE           # armed after a moment, it bursts on anyone who touches it
const SHOT_MISSILE := WeaponTable.FLIGHT_MISSILE     # steers at the nearest fighter who isn't its owner, and bursts
const SHOT_HOLE := WeaponTable.FLIGHT_HOLE           # lands, pulls everyone near it in, then bursts
const SHOT_LOB := WeaponTable.FLIGHT_LOB             # arcs, lands on platforms, hits on touch, bounces if its row says so
const SHOT_ZONE := WeaponTable.FLIGHT_ZONE         # an area on the ground that applies its row's effect to everyone in it
const SHOT_WALL := WeaponTable.FLIGHT_WALL         # a solid block: pushes fighters out, stops shots, breaks
const SHOT_SPEAKER := WeaponTable.FLIGHT_SPEAKER   # stands and pulses a hit in front of it
const SHOT_GATE := WeaponTable.FLIGHT_GATE         # one of a linked pair that fighters pass through
const SHOT_DROP := WeaponTable.FLIGHT_DROP         # hangs high, then falls through fighters
const SHOT_DECOY := WeaponTable.FLIGHT_DECOY       # looks like a Prop Box

# PropKind
const PROP_NONE := 0
const PROP_BOX := 1
const PROP_PENDING_BOX := 2
const PROP_ITEM := 3
const PROP_SHOT := 4


# Prop Boxes: fast, because the game is fast.
const FIRST_BOX_FRAMES := 90
const BOX_INTERVAL_MIN := 150
const BOX_INTERVAL_MAX := 270 # exclusive
const ITEM_LIFE_FRAMES := 720
const RETURN_FRAMES := 60
const NO_SWAP_BACK_FRAMES := 60
const SIGNATURE_PERCENT := 110
const SWEET_PERCENT := 125

static var BOX_HALF := Fx.ratio(2, 5)
static var BOX_DROP_HEIGHT := Fx.from_int(6)
static var EDGE_MARGIN := Fx.HALF

# Shared by every projectile.
static var SHOT_GRAVITY := Fx.ratio(1, 100)    # props lying or rolling on the stage
static var MAX_FALL := Fx.ratio(30, 100)
static var GROUND_BRAKE := Fx.ratio(2, 100)    # a landed bomb slides to a stop
static var THROW_VX := Fx.ratio(32, 100)
static var THROW_VY := Fx.ratio(6, 100)
static var THROWN_HALF := Fx.ratio(3, 10)      # a thrown weapon's box is 0.6 m
static var HAND_HEIGHT := Fx.ONE
static var HAND_REACH := Fx.ratio(3, 5)
static var BOOMERANG_CLIMB := Fx.ratio(12, 100) # how fast a boomerang rises or sinks to the hand
static var MISSILE_LEVEL := Fx.ratio(1, 5)     # within this height of its target a missile flies level
static var BOUNCE_MIN := Fx.ratio(8, 100)      # a bouncing shot leaves the floor at least this fast
static var SCREEN_INSET := Fx.ratio(6, 5)      # the screen's sides are this far inside the side blast lines
static var PULL_STOP := Fx.ONE                 # a spray's pull stops this far in front of the wielder

# Statuses (combat spec v0.4, section 2.4).
const SLOW_CAP_FRAMES := 150       # slow (and chill) adds up to 2.5 s
const FREEZE_AT := 90              # chill of this much on someone standing freezes them
const FREEZE_FRAMES := 60          # frozen solid for 1 s
const FREEZE_IMMUNE_FRAMES := 210  # and then can't be held for 2.5 s more
const HOLD_IMMUNE_FRAMES := 90     # after a root or stun ends, 1.5 s before another can take
const ROOT_PRESS_FRAMES := 12      # each fresh attack press cuts a root this much
const MARK_PERCENT := 125          # a marked fighter takes 25% more damage
const DOT_DAMAGE := 1              # a poison or burn tick
const AIR_DRIFT_PERCENT := 60      # a spray's drift on someone in the air is capped at this much of air speed

# Guards, traps, zones and walls (combat spec v0.4, sections 2.7 and 2.8).
const GUARD_FROM := 2              # a guard covers the front from this frame of its move to the end of its startup
const HOLD_MAX_FRAMES := 90        # the Parat's guard waits on its last startup frame for at most this long
static var BLOCK_PUSH := Fx.ratio(12, 100)     # a blocked attacker is pushed back this fast
static var SLIP_SPEED := Fx.ratio(30, 100)     # a slip slides the way the fighter faces, then friction stops it
static var SLIP_RUN_SPEED := Fx.ratio(5, 100)  # a puddle slips whoever crosses it faster than this
static var WALL_PUSH := Fx.ratio(1, 5)         # a wall moves a fighter out by at most this much a frame
static var SCOOP_PULL_TO := Fx.ratio(6, 5)     # the rake drags boxes and loose weapons to this far in front
static var HIDDEN_SEEN := Fx.from_int(2)       # the CPU still sees a fighter hidden in a cloud this close

# Machines and position tricks (combat spec v0.4, section 2.9).
static var DROP_AHEAD := Fx.from_int(4)        # the anvil hangs this far in front of its wielder (with nobody to aim at) ...
static var DROP_HEIGHT := Fx.from_int(9)       # ... this high above their feet ...
static var BLAST_MARGIN := Fx.from_int(2)      # ... and at least this far inside the blast lines, as a box is
static var DROP_MIN := Fx.from_int(2)          # its mark is over the nearest opponent in front, where they will be, but no nearer than this ...
static var DROP_MAX := Fx.from_int(6)          # ... and no further than this ahead
static var DROP_SEEK := Fx.from_int(8)         # it aims at an opponent at most this far in front
const DROP_LEAD := 40                          # frames ahead it guesses where they will be (their speed times this)
static var PULSE_HEIGHT := Fx.from_int(2)      # a speaker's pulse is its reach long and this high
static var SAFE_MARGIN := Fx.from_int(2)       # a safe spot is this far inside the blast zone on every side ...
static var SAFE_DROP := Fx.from_int(4)         # ... with a platform top at most this far below it
static var BLINK_MIN := Fx.from_int(2)         # the Teleporter tries its reach, then 1 m less each time, down to this
static var BLINK_HEIGHT := Fx.from_int(6)      # onto a platform top at most this far above or below the feet
static var BLINK_ABOVE := Fx.HALF              # the upward blink only takes tops more than this above the feet

## Each weapon's owner: the fighter who uses it best (+10% damage). The rest of the cast get
## theirs as their own weapons are added.
const SIGNATURES := {
	"akshat": BELAN,        # the chef
	"shubham": BASEBALL_BAT, # the gully slugger
	"shahn": BOWLING_BALL,  # Shan, the street brawler
	"salik": SHOTGUN,       # the robber
	"rishabh": UZI,         # the tech gunner
	"ankit": CHERRY_BOMBS,  # the clown
	"suhani": BOW,          # the rooftop archer
}

static var _crate_list := PackedInt32Array()
static var _move_rows := PackedInt32Array()
static var _flight_rows := PackedInt32Array()
static var _moves: Array[MoveData] = []
static var _throw: MoveData


## At most this many boxes and loose weapons at once: 3 + 1 per 2 fighters, up to 8.
static func cap(fighters: int) -> int:
	return mini(8, 3 + fighters / 2)


## The prop table of a match: 16 slots and 4 more per fighter, up to 64.
static func prop_slots(fighters: int) -> int:
	return mini(64, 16 + 4 * fighters)


## The first slots of the table are kept for boxes, pending boxes and loose weapons: shots only use
## the slots after them, so crates keep dropping and a dead fighter's weapon always comes back,
## however many shots are out.
static func box_slots(fighters: int) -> int:
	return cap(fighters) + 2


## The most placed props (pads, mines: the flights with the placed flag) one fighter can have out:
## 4, or 2 in a match of more than 8. Placing one more removes their oldest.
static func placed_limit(fighters: int) -> int:
	return 2 if fighters > 8 else 4


## Frames a shot starts with in Prop.timer: a speaker, gate or decoy counts its life down there;
## every other flight starts at its row's fuse (a mine's arming, a pad's life, an anvil's hang).
static func start_timer(row: PackedInt64Array) -> int:
	var flight: int = row[WeaponTable.C_FLIGHT]
	if flight == SHOT_SPEAKER or flight == SHOT_GATE or flight == SHOT_DECOY:
		return row[WeaponTable.C_LIFE]
	return row[WeaponTable.C_FUSE]


## The most props of this flight one fighter can have out (one speaker, a pair of gates), or 0 for
## no limit of its own.
static func flight_limit(flight: int) -> int:
	if flight == SHOT_SPEAKER:
		return 1
	return 2 if flight == SHOT_GATE else 0


## The weapon a decoy pretends to hold: the last real box's, or the Belan if none has dropped yet.
static func decoy_looks(state: GameState) -> int:
	return state.last_box_weapon if state.last_box_weapon != NONE else BELAN


static func move_of(weapon: int) -> int:
	return WeaponTable.ROWS[weapon][WeaponTable.C_MOVE]


static func supply_of(weapon: int) -> int:
	return WeaponTable.ROWS[weapon][WeaponTable.C_SUPPLY]


static func signature_for(character: String) -> int:
	return int(SIGNATURES.get(character, NONE))


## The built weapons a crate can hold, in id order.
static func crate_list() -> PackedInt32Array:
	if _crate_list.is_empty():
		for id in WeaponTable.COUNT:
			var row := WeaponTable.ROWS[id]
			if row[WeaponTable.C_BUILT] != 0 and row[WeaponTable.C_CRATE] != 0:
				_crate_list.append(id)
	return _crate_list


## The id of the row whose attack this move is; 0 for the fist moves and for WEAPON_THROW
## (Weapons.RowOfMove, which gives null for those).
static func row_of_move(move: int) -> int:
	if _move_rows.is_empty():
		_move_rows.resize(256)
		for id in range(1, WeaponTable.COUNT):
			_move_rows[WeaponTable.ROWS[id][WeaponTable.C_MOVE]] = id
	return _move_rows[move]


## The flight a burst of this row leaves behind, or FLIGHT_NONE.
static func left_flight(row: PackedInt64Array) -> int:
	match row[WeaponTable.C_LEAVES]:
		WeaponTable.LEAVES_ZONE: return WeaponTable.FLIGHT_ZONE
		WeaponTable.LEAVES_WALL: return WeaponTable.FLIGHT_WALL
		WeaponTable.LEAVES_PAD: return WeaponTable.FLIGHT_PAD
	return WeaponTable.FLIGHT_NONE


## The id of the row a shot prop takes its numbers from: the row of the weapon it carries. A thrown
## weapon (thrown, or flung by a swing) is a plain thrown prop and takes none of its weapon's shot
## columns: it gets the empty row 0, so it can't be broken, bounce off the screen edge or pass its
## thrower because of what it carries; its damage is its row's throw and its size THROWN_HALF. A shot
## whose weapon doesn't fly that way (only a weapon changed in the middle of its move makes one)
## takes them from the first row in the table that does.
static func shot_row(p: Prop) -> int:
	if p.shot == SHOT_THROWN:
		return 0
	var row := WeaponTable.ROWS[p.weapon]
	if row[WeaponTable.C_FLIGHT] == p.shot or (row[WeaponTable.C_LEAVES] != WeaponTable.LEAVES_NONE and left_flight(row) == p.shot):
		return p.weapon
	if _flight_rows.is_empty():
		_flight_rows.resize(256)
		for id in range(WeaponTable.COUNT - 1, 0, -1):
			_flight_rows[WeaponTable.ROWS[id][WeaponTable.C_FLIGHT]] = id
		_flight_rows[WeaponTable.FLIGHT_NONE] = 0
	var by_flight := _flight_rows[p.shot]
	return by_flight if by_flight != 0 else p.weapon


## A shot prop that swings and bursts break (its row's brk): the trap, the orb, or what a burst left
## behind (the Matka's wall). A bomb that will leave a breakable wall is not itself breakable.
static func breakable(p: Prop) -> bool:
	if p.kind != PROP_SHOT:
		return false
	var row := WeaponTable.ROWS[shot_row(p)]
	return row[WeaponTable.C_BRK] > 0 and (row[WeaponTable.C_LEAVES] == WeaponTable.LEAVES_NONE or left_flight(row) == p.shot)


## The weapon a prop's hits, KOs and record are credited to: a hidden row's weapon, else its own.
static func credit_of(p: Prop) -> int:
	return WeaponTable.ROWS[p.weapon][WeaponTable.C_CREDIT]


## Half the width (and half the height) of a shot's box.
static func shot_half(p: Prop) -> int:
	return THROWN_HALF if p.shot == SHOT_THROWN else WeaponTable.ROWS[shot_row(p)][WeaponTable.C_SIZE]


## Throwing the held weapon: the one weapon move that isn't a row. Its damage is the row's throw.
static func weapon_throw() -> MoveData:
	if _throw == null:
		_throw = MoveData.make({
			startup = 6, active = 0, recovery = 12,
			damage = 9, base_knockback = Fx.ratio(16, 100), knockback_growth = Fx.ratio(26, 10000),
			launch_x = Fx.ratio(8192, 10000), launch_y = Fx.ratio(5736, 10000), # 35 degrees
			hitstun_bonus = 4,
		})
	return _throw


## A row's attack as frame data, built from its columns (WeaponRow.MoveData).
static func move_data_of(weapon: int) -> MoveData:
	if _moves.is_empty():
		_moves.resize(WeaponTable.COUNT)
		for id in WeaponTable.COUNT:
			var row := WeaponTable.ROWS[id]
			var m := MoveData.new()
			m.startup = row[WeaponTable.C_S]
			m.active = row[WeaponTable.C_A]
			m.recovery = row[WeaponTable.C_R]
			m.damage = row[WeaponTable.C_DMG]
			m.base_knockback = row[WeaponTable.C_KB]
			m.knockback_growth = row[WeaponTable.C_G]
			m.launch_x = row[WeaponTable.C_LAUNCH_X]
			m.launch_y = row[WeaponTable.C_LAUNCH_Y]
			m.hitstun_bonus = row[WeaponTable.C_HS]
			m.hitbox_near = row[WeaponTable.C_BOX_NEAR]
			m.hitbox_far = row[WeaponTable.C_BOX_FAR]
			m.hitbox_bottom = row[WeaponTable.C_BOX_BOTTOM]
			m.hitbox_top = row[WeaponTable.C_BOX_TOP]
			m.hits_both_sides = row[WeaponTable.C_BOTH] != 0
			m.impulse_frame = row[WeaponTable.C_LUNGE_AT]
			m.lunge_speed = row[WeaponTable.C_LUNGE]
			m.effect = row[WeaponTable.C_EFFECT]
			m.effect_n = row[WeaponTable.C_EFFECT_N]
			_moves[id] = m
	return _moves[weapon]


## A weapon move's frame data: the throw's, or its row's. Null for a fist move.
static func move_for(id: int) -> MoveData:
	if id == MoveData.Id.WEAPON_THROW:
		return weapon_throw()
	var row := row_of_move(id)
	return move_data_of(row) if row != 0 else null
