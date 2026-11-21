class_name BurstWatch
## Bursts, seen from the state alone (presentation only: the flash in ArenaView, the boom in
## SoundDirector). The sim doesn't record a burst, so this compares a frame's props with the last
## frame's: something that bursts and is gone now (or has turned into the zone, wall or pad it
## leaves) has burst. A room is sent the same props, so it sees the same bursts.
##
## What can burst: a bomb (thrown, lobbed or bounced), a missile, a black hole, a mine (not a trap,
## which has no blast), and a decoy, but a decoy only when its owner is pressing the Crate Detonator
## (one that springs on someone is a punch, not a burst). Matched by flight, weapon and owner within
## 1 m, as the props a room is sent are not in fixed slots.

const NEAR := 1.0
const ARENA_HALF := 17.0 # one that flew out of the arena is gone, not burst (the big level's: 25.0)


## What can burst on the stage now: [x, y, flight, weapon, owner, blast (m), opens], opens being a
## burst with no damage (the Stink Cloud Capsule, a Lota: it opens rather than goes off).
static func snapshot(state: GameState) -> Array:
	var now: Array = []
	for p in state.props:
		if p.kind != Weapons.PROP_SHOT:
			continue
		var row := WeaponTable.ROWS[Weapons.shot_row(p)]
		match p.shot:
			Weapons.SHOT_BOMB, Weapons.SHOT_MISSILE, Weapons.SHOT_HOLE, Weapons.SHOT_DECOY:
				pass
			Weapons.SHOT_MINE:
				if row[WeaponTable.C_BLAST] == 0:
					continue # a trap: it bites, it doesn't burst
			_:
				continue
		now.append([Fx.to_float(p.pos_x), Fx.to_float(p.pos_y), p.shot, p.weapon, p.owner,
			Fx.to_float(row[WeaponTable.C_BLAST]), row[WeaponTable.C_DMG] == 0])
	return now


## The bursts between `before` (last frame's snapshot) and `state`: [x, y, hole, blast (m), opens]
## each. A detonated decoy takes the Detonator's blast. arena_half: the level's half width plus 1 m
## (ArenaView._half_w), past which a shot that is gone flew out rather than burst.
static func bursts(before: Array, state: GameState, arena_half := ARENA_HALF) -> Array:
	var now := snapshot(state)
	var out: Array = []
	for was: Array in before:
		var still := false
		for b: Array in now:
			if int(b[2]) == int(was[2]) and int(b[3]) == int(was[3]) and int(b[4]) == int(was[4]) \
					and absf(float(b[0]) - float(was[0])) < NEAR and absf(float(b[1]) - float(was[1])) < NEAR:
				still = true
				break
		if still or absf(float(was[0])) >= arena_half:
			continue
		var blast := float(was[5])
		var opens := bool(was[6])
		if int(was[2]) == Weapons.SHOT_DECOY:
			var detonator := _detonating(state, int(was[4]))
			if detonator == 0:
				continue # it sprang, ran out or was cleared: no burst
			blast = Fx.to_float(WeaponTable.ROWS[detonator][WeaponTable.C_BLAST])
			opens = false
		out.append([float(was[0]), float(was[1]), int(was[2]) == Weapons.SHOT_HOLE, blast, opens])
	return out


## The row of the Crate Detonator `owner` is pressing now, or 0.
static func _detonating(state: GameState, owner: int) -> int:
	if owner < 0 or owner >= state.fighters.size():
		return 0
	var f := state.fighters[owner]
	if f.action != Fighter.Action.ATTACK:
		return 0
	var row := Weapons.row_of_move(f.move)
	return row if WeaponTable.ROWS[row][WeaponTable.C_USE] == WeaponTable.USE_DETONATE else 0
