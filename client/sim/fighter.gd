class_name Fighter
extends RefCounted
## One fighter's rollback state (Fighter.cs). Plain ints and bools; positions and velocities are
## Fix (see Fx). New fields must be added to copy_from() and add_to(), in the same order as the
## C# reference, or the golden traces and the online checksum stop matching.

enum Action { IDLE, ATTACK, DODGE, HITSTUN, STUNNED, RESPAWNING, ELIMINATED }

var pos_x := 0           # feet, y up
var pos_y := 0
var vel_x := 0
var vel_y := 0
var facing := 1          # +1 right, -1 left
var grounded := false
var air_jumps_left := 0
var josh := 0            # knockback percent, 0..300 (GDD B2)
var stocks := 0
var action: int = Action.IDLE
var move: int = MoveData.Id.NONE   # the move being performed while action is ATTACK
var action_frame := 0    # frames since the current action started
var hitstun_frames := 0  # frames left in HITSTUN or STUNNED
var attack_connected := false
var previous_buttons := 0
var hit_stop := 0        # frames left frozen in hit-stop
var frozen_by_hit := false
var dodge_cooldown := 0
var air_dodge_used := false
var recovery_used := false
var guts_used := false
var guts_timer := 0
var armor_frames := 0
var kos := 0             # ring-outs credited to this fighter (the timed match's score)
var falls := 0           # times this fighter has left the blast zone
var last_hit_by := 0     # 1 + index of the fighter who hit this one last, or 0
var last_hit_timer := 0  # frames left in which a ring-out still credits last_hit_by
var shield := 0          # hidden second wind: damage absorbed before Josh rises (never shown)
var weapon := 0          # the weapon in hand (Weapons), kept when hit
var weapon_supply := 0   # its ammo or durability
var uzi_shots := 0       # Uzi bullets fired in this burst (the spread pattern)
var uzi_streak := 0      # Uzi hits in a row on one target
var uzi_target := 0      # 1 + the fighter the streak is on, or 0
var flurry_next: int = MoveData.Id.NONE # the second hit of a fist flurry, queued; or NONE
var last_hit_weapon := 0 # the weapon the last hit taken came from (0: fists or a hazard)
var last_hit_sweet := false # that hit was a sweet spot
var hit_count := 0       # goes up on every hit taken, so a renderer can tell a new hit


func is_active() -> bool:
	return action != Action.RESPAWNING and action != Action.ELIMINATED


func copy_from(o: Fighter) -> void:
	pos_x = o.pos_x
	pos_y = o.pos_y
	vel_x = o.vel_x
	vel_y = o.vel_y
	facing = o.facing
	grounded = o.grounded
	air_jumps_left = o.air_jumps_left
	josh = o.josh
	stocks = o.stocks
	action = o.action
	move = o.move
	action_frame = o.action_frame
	hitstun_frames = o.hitstun_frames
	attack_connected = o.attack_connected
	previous_buttons = o.previous_buttons
	hit_stop = o.hit_stop
	frozen_by_hit = o.frozen_by_hit
	dodge_cooldown = o.dodge_cooldown
	air_dodge_used = o.air_dodge_used
	recovery_used = o.recovery_used
	guts_used = o.guts_used
	guts_timer = o.guts_timer
	armor_frames = o.armor_frames
	kos = o.kos
	falls = o.falls
	last_hit_by = o.last_hit_by
	last_hit_timer = o.last_hit_timer
	shield = o.shield
	weapon = o.weapon
	weapon_supply = o.weapon_supply
	uzi_shots = o.uzi_shots
	uzi_streak = o.uzi_streak
	uzi_target = o.uzi_target
	flurry_next = o.flurry_next
	last_hit_weapon = o.last_hit_weapon
	last_hit_sweet = o.last_hit_sweet
	hit_count = o.hit_count


func add_to(h: StateHasher) -> void:
	h.add_u64(pos_x)
	h.add_u64(pos_y)
	h.add_u64(vel_x)
	h.add_u64(vel_y)
	h.add_int(facing)
	h.add_bool(grounded)
	h.add_int(air_jumps_left)
	h.add_int(josh)
	h.add_int(stocks)
	h.add_int(action)
	h.add_int(move)
	h.add_int(action_frame)
	h.add_int(hitstun_frames)
	h.add_bool(attack_connected)
	h.add_int(previous_buttons)
	h.add_int(hit_stop)
	h.add_bool(frozen_by_hit)
	h.add_int(dodge_cooldown)
	h.add_bool(air_dodge_used)
	h.add_bool(recovery_used)
	h.add_bool(guts_used)
	h.add_int(guts_timer)
	h.add_int(armor_frames)
	h.add_int(kos)
	h.add_int(falls)
	h.add_int(last_hit_by)
	h.add_int(last_hit_timer)
	h.add_int(shield)
	h.add_int(weapon)
	h.add_int(weapon_supply)
	h.add_int(uzi_shots)
	h.add_int(uzi_streak)
	h.add_int(uzi_target)
	h.add_int(flurry_next)
	h.add_int(last_hit_weapon)
	h.add_bool(last_hit_sweet)
	h.add_int(hit_count)
