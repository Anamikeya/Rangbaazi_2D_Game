class_name Weapons
## The weapon catalogue, the weapons' moves and the props' rules (Weapons.cs; combat spec v0.3,
## docs/design/combat-spec-v0.3-weapons.md). Shared by every fighter. Fix values are Fx ints.

# WeaponId
const NONE := 0
const BELAN := 1
const BASEBALL_BAT := 2
const BOWLING_BALL := 3
const SHOTGUN := 4
const UZI := 5
const CHERRY_BOMBS := 6
const BOW := 7
const BOOMERANG := 8
const TRAMPOLINE := 9
const MINES := 10
const HOMING_MISSILE := 11
const BLACK_HOLE_BOMB := 12
const COUNT := 13 # NONE plus twelve weapons

## Names for drawing and the HUD (presentation only).
const NAMES := ["", "Belan", "Baseball Bat", "Bowling Ball", "Shotgun", "Uzi", "Cherry Bombs", "Bow",
	"Boomerang", "Trampoline", "Mines", "Homing Missile", "Black-Hole Bomb"]

# ShotKind
const SHOT_NONE := 0
const SHOT_ROLL := 1   # the bowling ball rolling; passes through the fighters it hits
const SHOT_BULLET := 2 # an Uzi bullet: straight, fast, through platforms, gone on its first hit
const SHOT_BOMB := 3   # a lobbed cherry bomb with a fuse
const SHOT_ARROW := 4  # a bow's arrow: fast, dips a little, pierces everyone it hits
const SHOT_THROWN := 5 # a thrown weapon: hits once, or lands, and is gone
const SHOT_BOOMERANG := 6 # flies out, slows, and comes back to its thrower's hand, hitting on both legs
const SHOT_PAD := 7     # a trampoline lying on the stage: anyone touching it is bounced up
const SHOT_MINE := 8    # a planted mine: armed after a moment, it bursts on anyone who touches it
const SHOT_MISSILE := 9 # slow, it steers at the nearest fighter who isn't its owner, and bursts
const SHOT_HOLE := 10   # a black-hole bomb: it lands, pulls everyone near it in, then bursts

# PropKind
const PROP_NONE := 0
const PROP_BOX := 1
const PROP_PENDING_BOX := 2
const PROP_ITEM := 3
const PROP_SHOT := 4

const MAX_PROPS := 16

# Prop Boxes: fast, because the game is fast.
const FIRST_BOX_FRAMES := 90
const BOX_INTERVAL_MIN := 150
const BOX_INTERVAL_MAX := 270 # exclusive
const ITEM_LIFE_FRAMES := 720
const RETURN_FRAMES := 60
const NO_SWAP_BACK_FRAMES := 60
const SIGNATURE_PERCENT := 110
const SWEET_PERCENT := 125

const RUN_SPEED_HEAVY_NUM := 4 # the bowling ball: 4/5 speed
const RUN_SPEED_HEAVY_DEN := 5

const ROLL_FRAMES := 180
const BULLET_FRAMES := 20
const UZI_INTERVAL := 5
const BOMB_FUSE := 50
const ARROW_FRAMES := 50
const LONG_SHOT_FRAMES := 20
const SHOTGUN_FAR_DAMAGE := 10
const UZI_STREAK_SWEET := 6

const BOOMERANG_FRAMES := 150 # uncaught by then, it drops and lies there
const PAD_FRAMES := 600       # a trampoline lies for 10 s
const MINE_ARM_FRAMES := 45   # armed 0.75 s after planting
const MINE_FRAMES := 900      # one nobody found bursts by itself after 15 s
const MISSILE_FRAMES := 240   # it bursts after 4 s
const HOLE_FUSE := 110
const HOLE_PULL_FRAMES := 80  # for the end of its fuse it pulls everyone within reach

static var BOX_HALF := Fx.ratio(2, 5)
static var BOX_DROP_HEIGHT := Fx.from_int(6)
static var EDGE_MARGIN := Fx.HALF
static var SHOT_GRAVITY := Fx.ratio(1, 100)
static var ARROW_GRAVITY := Fx.ratio(4, 1000)
static var MAX_FALL := Fx.ratio(30, 100)
static var ROLL_SPEED := Fx.ratio(22, 100)
static var BULLET_SPEED := Fx.ratio(70, 100)
static var BOMB_VX := Fx.ratio(14, 100)
static var BOMB_VY := Fx.ratio(18, 100)
static var BLAST_HALF := Fx.ratio(6, 5)
static var ARROW_SPEED := Fx.ratio(55, 100)
static var THROW_VX := Fx.ratio(32, 100)
static var THROW_VY := Fx.ratio(6, 100)
static var HAND_HEIGHT := Fx.ONE
static var HAND_REACH := Fx.ratio(3, 5)
static var SHOTGUN_FAR := Fx.from_int(3)
static var POINT_BLANK := Fx.ratio(6, 5)
static var BAT_TIP := Fx.ratio(13, 10)
static var SIX_LAUNCH_X := Fx.ratio(9397, 10000)
static var SIX_LAUNCH_Y := Fx.ratio(3420, 10000)
static var BOOMERANG_SPEED := Fx.ratio(50, 100)
static var BOOMERANG_DRAG := Fx.ratio(125, 10000)
static var BOOMERANG_CLIMB := Fx.ratio(12, 100)
static var PAD_LAUNCH := Fx.ratio(42, 100)
static var MISSILE_SPEED := Fx.ratio(20, 100)
static var MISSILE_TURN := Fx.ratio(8, 1000)
static var MISSILE_LEVEL := Fx.ratio(1, 5)
static var HOLE_REACH := Fx.ratio(9, 2)
static var HOLE_PULL := Fx.ratio(11, 100) # a little under run speed: running away just gets you out
static var HOLE_BLAST_HALF := Fx.ratio(21, 10)
static var UZI_SPREAD: PackedInt64Array = [0, Fx.ratio(366, 10000), Fx.ratio(-366, 10000), Fx.ratio(732, 10000), Fx.ratio(-732, 10000)]

# Catalogue: [move id, supply, fires (shot kind), throw damage], by weapon id.
const _CATALOGUE := [
	[MoveData.Id.NONE, 0, SHOT_NONE, 9],
	[MoveData.Id.BELAN_BONK, 12, SHOT_NONE, 6],
	[MoveData.Id.BAT_SWING, 8, SHOT_NONE, 9],
	[MoveData.Id.BALL_ROLL, 1, SHOT_ROLL, 12],
	[MoveData.Id.SHOTGUN_BLAST, 4, SHOT_NONE, 9],
	[MoveData.Id.UZI_FIRE, 30, SHOT_BULLET, 6],
	[MoveData.Id.BOMB_LOB, 3, SHOT_BOMB, 9],
	[MoveData.Id.BOW_SHOT, 6, SHOT_ARROW, 6],
	[MoveData.Id.BOOMERANG_THROW, 4, SHOT_BOOMERANG, 6],
	[MoveData.Id.PAD_PLACE, 3, SHOT_PAD, 9],
	[MoveData.Id.MINE_PLANT, 3, SHOT_MINE, 6],
	[MoveData.Id.MISSILE_FIRE, 2, SHOT_MISSILE, 12],
	[MoveData.Id.HOLE_LOB, 2, SHOT_HOLE, 9],
]

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

static var _moves: Dictionary = {}


## At most this many boxes and loose weapons at once: 3 + 1 per 2 fighters, up to 8.
static func cap(fighters: int) -> int:
	return mini(8, 3 + fighters / 2)


static func move_of(weapon: int) -> int:
	return _CATALOGUE[weapon][0]


static func supply_of(weapon: int) -> int:
	return _CATALOGUE[weapon][1]


static func fires(weapon: int) -> int:
	return _CATALOGUE[weapon][2]


static func throw_damage(weapon: int) -> int:
	return _CATALOGUE[weapon][3]


static func signature_for(character: String) -> int:
	return int(SIGNATURES.get(character, NONE))


static func uzi_spread_at(shot: int) -> int:
	return UZI_SPREAD[shot % UZI_SPREAD.size()]


## Half the width (and half the height) of a shot's box.
static func shot_half(s: int) -> int:
	match s:
		SHOT_ROLL: return Fx.ratio(3, 10)
		SHOT_BULLET: return Fx.ratio(3, 20)
		SHOT_BOMB: return Fx.ratio(1, 5)
		SHOT_ARROW: return Fx.ratio(1, 4)
		SHOT_PAD: return Fx.ratio(9, 20)
		SHOT_MINE: return Fx.ratio(1, 4)
		SHOT_MISSILE: return Fx.ratio(1, 4)
		SHOT_HOLE: return Fx.ratio(1, 4)
	return Fx.ratio(3, 10)


static func is_weapon_move(id: int) -> bool:
	return id >= MoveData.Id.BELAN_BONK


static func _c(v: int) -> int:
	return Fx.ratio(v, 10000)


## A weapon move's frame data, or null.
static func move_for(id: int) -> MoveData:
	if _moves.is_empty():
		_moves[MoveData.Id.BELAN_BONK] = MoveData.make({
			startup = 7, active = 3, recovery = 14,
			damage = 8, base_knockback = Fx.ratio(12, 100), knockback_growth = Fx.ratio(25, 10000),
			launch_x = _c(6428), launch_y = _c(7660), # 50 degrees
			hitstun_bonus = 10, # dizzy
			hitbox_near = Fx.ratio(1, 5), hitbox_far = Fx.ratio(13, 10),
			hitbox_bottom = Fx.ratio(3, 5), hitbox_top = Fx.from_int(2),
		})
		_moves[MoveData.Id.BAT_SWING] = MoveData.make({
			startup = 12, active = 4, recovery = 20,
			damage = 16, base_knockback = Fx.ratio(17, 100), knockback_growth = Fx.ratio(26, 10000),
			launch_x = _c(8192), launch_y = _c(5736), # 35 degrees
			hitstun_bonus = 4,
			hitbox_near = 0, hitbox_far = Fx.ratio(9, 5),
			hitbox_bottom = Fx.ratio(2, 5), hitbox_top = Fx.ratio(8, 5),
		})
		_moves[MoveData.Id.SHOTGUN_BLAST] = MoveData.make({
			startup = 12, active = 2, recovery = 30,
			damage = 18, base_knockback = Fx.ratio(20, 100), knockback_growth = Fx.ratio(28, 10000),
			launch_x = _c(9063), launch_y = _c(4226), # 25 degrees
			hitstun_bonus = 4,
			hitbox_near = Fx.ratio(2, 5), hitbox_far = Fx.ratio(9, 2),
			hitbox_bottom = Fx.HALF, hitbox_top = Fx.ratio(3, 2),
			impulse_frame = 12, lunge_speed = Fx.ratio(-15, 100), # the kick pushes the shooter back
		})
		_moves[MoveData.Id.BALL_ROLL] = MoveData.make({
			startup = 14, active = 0, recovery = 20,
			damage = 14, base_knockback = Fx.ratio(18, 100), knockback_growth = Fx.ratio(24, 10000),
			launch_x = _c(8660), launch_y = _c(5000), # 30 degrees
			hitstun_bonus = 4,
		})
		_moves[MoveData.Id.UZI_FIRE] = MoveData.make({
			startup = 3, active = 0, recovery = 8,
			damage = 2, base_knockback = Fx.ratio(5, 100), knockback_growth = Fx.ratio(5, 10000),
			launch_x = _c(9848), launch_y = _c(1736), # 10 degrees
			hitstun_bonus = -6,
		})
		_moves[MoveData.Id.BOMB_LOB] = MoveData.make({
			startup = 8, active = 0, recovery = 14,
			damage = 12, base_knockback = Fx.ratio(18, 100), knockback_growth = Fx.ratio(25, 10000),
			launch_x = _c(5000), launch_y = _c(8660), # 60 degrees, away from the blast
			hitstun_bonus = 4,
		})
		_moves[MoveData.Id.BOW_SHOT] = MoveData.make({
			startup = 10, active = 0, recovery = 14,
			damage = 10, base_knockback = Fx.ratio(15, 100), knockback_growth = Fx.ratio(24, 10000),
			launch_x = _c(9397), launch_y = _c(3420), # 20 degrees
			hitstun_bonus = 2,
		})
		_moves[MoveData.Id.WEAPON_THROW] = MoveData.make({
			startup = 6, active = 0, recovery = 12,
			damage = 9, base_knockback = Fx.ratio(16, 100), knockback_growth = Fx.ratio(26, 10000),
			launch_x = _c(8192), launch_y = _c(5736), # 35 degrees
			hitstun_bonus = 4,
		})
		_moves[MoveData.Id.BOOMERANG_THROW] = MoveData.make({
			startup = 8, active = 0, recovery = 12,
			damage = 8, base_knockback = Fx.ratio(14, 100), knockback_growth = Fx.ratio(22, 10000),
			launch_x = _c(8192), launch_y = _c(5736), # 35 degrees
			hitstun_bonus = 2,
		})
		_moves[MoveData.Id.PAD_PLACE] = MoveData.make({ # putting a trampoline down: it never hits
			startup = 6, active = 0, recovery = 10,
			launch_x = _c(8192), launch_y = _c(5736),
		})
		_moves[MoveData.Id.MINE_PLANT] = MoveData.make({
			startup = 6, active = 0, recovery = 8,
			damage = 16, base_knockback = Fx.ratio(20, 100), knockback_growth = Fx.ratio(26, 10000),
			launch_x = _c(5000), launch_y = _c(8660), # 60 degrees, away from the blast
			hitstun_bonus = 4,
		})
		_moves[MoveData.Id.MISSILE_FIRE] = MoveData.make({
			startup = 14, active = 0, recovery = 22,
			damage = 15, base_knockback = Fx.ratio(19, 100), knockback_growth = Fx.ratio(27, 10000),
			launch_x = _c(5000), launch_y = _c(8660), # 60 degrees, away from the blast
			hitstun_bonus = 4,
		})
		_moves[MoveData.Id.HOLE_LOB] = MoveData.make({
			startup = 10, active = 0, recovery = 16,
			damage = 20, base_knockback = Fx.ratio(25, 100), knockback_growth = Fx.ratio(30, 10000),
			launch_x = _c(3420), launch_y = _c(9397), # 70 degrees: thrown up out of the hole
			hitstun_bonus = 4,
		})
	return _moves.get(id, null)
