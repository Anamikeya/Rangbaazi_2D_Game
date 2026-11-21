# Combat spec v0.4: all 100 weapons

This spec follows `combat-spec-v0.3-weapons.md`. The user asked for every weapon in the art pack: "create all now and complete it". Twelve are built (v0.3). This spec designs the other 88 and the engine change that makes 100 weapons possible. It is a design, written on 2026-11-21 before any of it was built, and revised the same day after two reviews (section 10). Every number is a first guess, to be measured with CPU brawls the way v0.3 was tuned.

The short version:

- **One weapon table.** Every weapon is one row of numbers. The C# sim and the GDScript sim both read the same generated table. The engine has no `if weapon == X` left: it branches on a row's archetype columns.
- **The twelve built weapons become rows 1 to 12** and behave exactly as they do today. The golden traces prove it (section 7, batch 1, step A).
- **88 new rows, ids 13 to 100,** in pack-key order, plus 7 hidden rows (101 to 107) that are parts of other weapons.
- **Eight new `Fighter` fields** hold every status (slow, root, poison or burn, mark, stun immunity and what caused a hold). No weapon has a field of its own. `Prop` gets no new fields. Teams add no state: they come from the match rules.
- **`SIM_VERSION` is 8. The arena protocol is version 5.** Each is set once, in batch 1, with the whole new layout.
- **Six batches**, each finished end to end in both sims: built, drawn, usable by the CPU, with tests and goldens green.
- **Reviewed.** Two reviews changed this spec. Section 10 lists what was accepted and what was not.

Units in this spec: frames at 60 a second; metres; speeds in metres per frame; damage in Josh %. "S/A/R" is startup / active / recovery. A hit is written `damage, base knockback + growth, launch angle, hitstun bonus`, as in `MoveData`.

## 1. One table, one engine

### 1.1 The source and the generator

Adding 88 weapons as 88 enum cases, 88 `MoveData` objects and 88 switch arms, twice, would not hold. Instead:

- **`tools/weapons.json`** is the single source: one object per weapon, holding only the columns that differ from the defaults. Decimals are written as in this spec (`0.17`).
- **`python tools/gen_weapons.py`** reads it and writes, the way `tools/gen_arena_stages.py` writes the stages:
  - `src/Rangbaazi.Sim/WeaponTable.cs`: the `WeaponId` enum (the twelve keep their names and numbers) and `WeaponTable.Rows`, an array of `WeaponRow` indexed by id;
  - `client/sim/weapon_table.gd` (`class_name WeaponTable`): the same rows, as arrays of ints in the same column order;
  - `client/game/weapon_info.gd` (`class_name WeaponInfo`): the presentation columns (section 5). The sims never read it.
- **Fixed point.** The generator turns every decimal into an exact ratio (`Fraction(str(value))`, as the stage generator does) and writes the raw 16.16 integer **truncated towards zero**: `int(Fraction(str(v)) * 65536)`. Not `//` and not `floor`: those round negative numbers the other way, and only negative numbers would show it. That is the value `Fix.FromRatio` and `Fx.ratio` give for the same ratio, so the twelve built weapons keep their exact numbers. The generator tests itself when it starts: -0.15 must give -9830 and -0.0366 must give -2398. A launch angle in whole degrees becomes `round(cos × 10000)` and `round(sin × 10000)` over 10000, which reproduces today's hand-written vectors (35° is 8192 and 5736).
- **Lists and names.** A row holds ints only, in both languages. A `spread` list is written into one shared `SPREADS` array, and the row holds `spreadAt` and `spreadLen`. Every column that is a name (`use`, `flight`, `effect`, `sweet`, `defence`, `spend`, `spreadBy`, `scoop`, `leaves`) is an int constant, generated for both languages from one list in the generator (`WeaponTable.USE_SWING` in GDScript, `WeaponUse.Swing` in C#). So the code that reads a row is the same line in both sims.
- **Flight flags.** The generator also writes the two per-flight flags of section 1.3 (`flies`, `placed`) for both sims.
- **Validation.** The generator stops, writing nothing, on any of these:
  - an unknown column, or a name that is not in its list;
  - an id or a move above 255, or used twice;
  - a `supply` above 127 (a prop's supply is a signed byte in STATE);
  - a repeating row whose `interval` is not smaller than `a + r` (it would never reach its loop frame);
  - `fires`, `thrownAs` or `cycleFirst` (with its `cycleCount` rows) pointing at a row that isn't there;
  - a `spread` without `spreadBy`, `leaves` without a size and a life, `first` together with `both`.
- **A table golden.** `GoldenTraceTests` writes `client/tests/golden/weapon_table.json`: one hash per row over its columns in order, and one over `SPREADS`. A GDScript test hashes its own table and compares, as `arena_stages.json` does for stages. A stale generated file fails a test in whichever sim it is stale in.
- **`built`.** Each row has a `built` flag. Crates only drop built rows, so the table can hold all 107 rows from batch 1 while the engine catches up. A row is marked built only when its rules are in both sims **and** the client can draw it (a placeholder is enough) **and** the CPU has a range for it: section 7.

### 1.2 The columns

Every column has a default (0, false or none), so a row is short. A row is "a weapon"; a few rows are parts of weapons (section 1.5).

| Group | Columns | Meaning |
|---|---|---|
| Identity | `id`, `key`, `name`, `move`, `credit`, `crate`, `weight`, `built` | `move` is the row's `MoveId` byte (section 4). `credit` is the weapon id that hits are recorded under (itself, except for hidden rows). `weight` is its share of crates: 2, or 1 for utility rows (section 4.2) |
| Use | `use` | How the attack is delivered: section 1.3 |
| Frames | `s`, `a`, `r` | Startup, active, recovery, as `MoveData` |
| Hit | `dmg`, `kb`, `g`, `ang`, `hs` | Damage, base knockback, growth, launch angle, hitstun bonus |
| Hit flags | `chip`, `soft`, `first`, `gbreak`, `armorbreak`, `noSelf` | `chip`: no hit-stop (Uzi bullets). `soft`: damage and effect only, no knockback, hitstun or hit-stop. `first`: only the nearest fighter in the box. `gbreak`: goes through a guard and breaks a breakable prop in one swing. `armorbreak`: ignores Guts armor. `noSelf`: its shot never hits its owner |
| Hitbox | `box` (near, far, bottom, top), `both` | As `MoveData`. `both` is `HitsBothSides` |
| Motion | `lungeAt`, `lunge`, `carry`, `walk` | The impulse frame and speed (negative is a kick back). `carry`: run and air speed while holding it, in percent. `walk`: half-speed walking while the attack repeats |
| Supply | `supply`, `spend`, `throw` | `spend`: per fighter hit (`hit`), per use (`use`), or `never`. `throw`: damage when thrown (6, 9 or 12) |
| Repeating | `interval`, `burst`, `burstMax`, `pellets`, `spread`, `spreadBy` | `interval`: frames between shots while held. `burst`: shots fired whether held or not. `burstMax`: most shots in one hold. `pellets`: shots per use. `spread`: a list of upward speeds; `spreadBy` picks the entry: `burst` (by shots in this burst, cycling), `hold` (the same, staying on the last entry), `widen` (the same, then alternating between the last two), `pellet`, or `supply` (by supply left) |
| Shot | `flight`, `vx`, `vy`, `grav`, `life`, `fuse`, `size`, `blast` | The projectile: its flight model (section 1.3), launch speed, gravity, frames of life, its timer's start, the width of its box, the width of its burst. The generator stores half of the last two, as `ShotHalf` and `BlastHalf` are today |
| Shot flags | `pierce`, `maxHits`, `bounces`, `keep`, `edge`, `touch`, `land`, `feet`, `leave`, `rest`, `rehit`, `brk` | Goes through fighters, and fades after `maxHits` of them if set; floor bounces and the percent of speed kept; screen-edge bounces; bursts (or springs) on touch; bursts on landing; starts at the feet; leaves the hand; rests as a loose weapon at the end; hits the same fighter again every N frames; swings it takes before it breaks |
| Shot extras | `turn`, `drag`, `padLaunch`, `pullFrames`, `reach`, `pull` | Missile steering; boomerang drag; a pad's launch speed; the black hole's pull; a speaker's or blink's reach |
| Left behind | `leaves`, `zoneW`, `zoneH`, `zoneLife`, `hides` | What a burst leaves: a zone, a wall or a pad, its size and life; `hides` makes a zone a cloud |
| Spray | `drift`, `lift`, `propDrift`, `armedOnly`, `blow`, `tickNear` | Per frame, to everyone in the box: sideways push (negative pulls), upward float, push on crates and loose weapons; only fighters holding a weapon; turns shots round. `tickNear`: the tick's hit lands only within this distance |
| Defence | `defence`, `batMax`, `batCost` | `bat`, `break`, `guard`, `hold` or `sweep`: section 2.7. `batMax`: a `bat` returns only shots with no blast and at most this much damage. `batCost`: supply a return costs |
| Effect | `effect`, `effectN`, `dotEvery`, `sweetOnly` | What a hit adds: section 1.3. `dotEvery`: frames between poison or burn ticks. `sweetOnly`: the effect is added only by a sweet-spot hit |
| Sweet spot | `sweet`, `sweetArg`, `sweetAng`, `sweetStun`, `farDist`, `farDmg` | The rule and its number; a launch angle and extra hitstun for sweet hits; the shotgun's far damage |
| Props | `scoop` | `fling` or `pull`: what a swing does to crates and loose weapons (hockey stick, rake) |
| Links | `fires`, `thrownAs`, `cycleFirst`, `cycleCount` | Another row whose shot this attack also fires; the row a thrown one becomes; the rows a cycling weapon takes turns through |
| CPU | `cpuMin`, `cpuMax`, `cpuBurst` | The range the CPU fires from (0: used at fist range; -1: thrown), and how many shots or ticks it holds for |

`WeaponRow` in C# is an immutable class with these as `init` properties plus a ready-made `MoveData`. In GDScript a row is one `PackedInt64Array` (lists and names as ints: section 1.1), with named index constants generated beside it (`WeaponTable.C_DMG`, ...), and `MoveData` objects built once at first use, as `Weapons.move_for` does today.

`MoveData` gains two fields, `Effect` and `EffectN`, so that `Hit(MoveData)` carries them into `LandHit`. Fist moves leave them at none.

### 1.3 The archetypes

**Use** (7): how the attack happens.

| Use | What the engine does | Built examples |
|---|---|---|
| `swing` | A melee hitbox on the active frames | Belan, Baseball Bat |
| `blast` | A hit-scan box on the active frames, spent when fired | Shotgun |
| `shoot` | Fires the row's shot on the startup frame; repeats if `interval` is set | Uzi, Bow, Bowling Ball, Mines |
| `spray` | A held stream: the box is out every frame and "ticks" every `a` frames | (new) |
| `blink` | Moves the wielder | (new: Teleporter) |
| `detonate` | Places a rigged crate, or bursts the ones already out | (new: Crate Detonator) |
| `cycle` | Has no attack of its own: each press uses the next of its hidden rows | (new: Jugaad Gauntlet) |

**Flight** (17): how a shot prop behaves. This is `ShotKind`, extended; the first ten keep their numbers.

| # | Flight | Behaviour | Used by |
|---|---|---|---|
| 1 | roll | Along the ground, off ledges, through fighters | Bowling Ball, Chicken, Thunder wave |
| 2 | bullet | Straight, through platforms, gone on its first hit | Uzi and every gun |
| 3 | bomb | Lobbed, lands on platforms, has a fuse, bursts | Cherry Bombs, grenades, pots and capsules, the Chakla, glue blobs |
| 4 | arrow | Straight with its own gravity, through platforms; pierces if `pierce` | Bow, Crossbow, discs, Dark Energy |
| 5 | thrown | A thrown weapon | every weapon |
| 6 | boomerang | Out, stop, back to the hand | Boomerang, Chappal |
| 7 | pad | Lies there and bounces fighters | Trampoline, a thrown Charpai |
| 8 | mine | Arms, then goes off on whoever touches it | Mines, the three traps |
| 9 | missile | Flies and steers by `turn` (0: straight) | Homing Missile, RPG |
| 10 | hole | Lands, pulls, bursts | Black-Hole Bomb |
| 11 | lob | Arcs, lands on platforms, hits on touch, bounces if `bounces` | Snowballs, Net, Cannon, the bouncers |
| 12 | zone | An area on the ground that applies its row's effect to everyone in it | puddle, goo, cloud |
| 13 | wall | A solid block: pushes fighters out, stops shots, breaks | Matka |
| 14 | speaker | Stands and pulses a hit in front of it | Sonic Speaker |
| 15 | gate | One of a linked pair that fighters pass through | Portal Disc |
| 16 | drop | Hangs high, then falls through fighters | Anvil |
| 17 | decoy | Looks like a Prop Box | Decoy Crate, Crate Detonator |

**Flight flags.** Every rule that acts on "a shot" reads one of two generated flags, never a list of flights:

| Flag | Flights | What it means |
|---|---|---|
| `flies` | roll, bullet, bomb, arrow, thrown, boomerang, missile, hole, lob | A bat can return it; a `break` swing destroys it; a `blow` spray turns it; a wall stops it; a guard or a parry destroys it |
| `placed` | pad, mine, zone, wall, speaker, gate, decoy | It counts towards its owner's placed-prop limit. None of the rules above touch it. Only `brk`, `sweep`, a burst or its life ending remove it |

`drop` (the Anvil) has neither flag: it falls through everything, and a dodge or a parry only saves the fighter. The first ten flights keep today's behaviour exactly: the bat already skipped pads and mines.

**Effect** (12): what a hit adds. Section 2.4 has the exact rules.

`slow`, `chill`, `root`, `stun`, `slip`, `poison`, `burn`, `mark`, `disarm`, `reel`, `leech`, `swap`.

**Sweet spot** (11): `midattack` (Belan), `tip` (Bat), `pointblank` (Shotgun), `strike` (Bowling Ball), `streak` (Uzi), `direct` (bombs, mines, missile), `longshot` (Bow), `return` (Boomerang), and three new ones: `airborne` (the target is in the air), `bounced` (the shot has bounced at least once), `back` (the target faces away from the attacker).

- Every sweet spot but `streak` is `Hit.Sweeten()`: 1.25× damage and base knockback and the longest hit-stop, as today. `sweetAng` then replaces the launch angle (the bat's six at 20°) and `sweetStun` adds hitstun (the belan's +6).
- `streak` stays what the Uzi does today: from the Nth hit in a row on one target, the hit does 1 more damage with no hitstun penalty, and no 1.25×. `sweetArg` is N.
- A thrown weapon keeps its own sweet spot (a target in the air), whatever its row says.

### 1.4 The twelve built weapons in the table

These rows reproduce today's constants. Step A of batch 1 changes nothing about how they play; section 1.8 lists what the later steps change on purpose.

| id | Weapon (key) | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|
| 1 | Belan (w011) | 7/3/14 | 8, 0.12 + 0.0025, 50°, +10 | 12 | box 0.2–1.3 × 0.6–2 | thrown 6, sweet: mid-attack, +6 stun | fist range |
| 2 | Baseball Bat (w031) | 12/4/20 | 16, 0.17 + 0.0026, 35°, +4 | 8 | box 0–1.8 × 0.4–1.6 | bats shots back, sweet: tip ≥ 1.3 m → 20° | fist range |
| 3 | Bowling Ball (w006) | 14/0/20 | 14, 0.18 + 0.0024, 30°, +4 | 1 | roll 0.22, life 180, size 0.6 | never runs out, thrown 12, carry 80%, leaves the hand, rests as an item, sweet: 2nd fighter on | 0–12 m |
| 4 | Shotgun (w061) | 12/2/30 | 18, 0.2 + 0.0028, 25°, +4 | 4 | box 0.4–4.5 × 0.5–1.5 | kick 0.15 at frame 12, beyond 3 m: 10, sweet: within 1.2 m | 0–4 m |
| 5 | Uzi (w005) | 3/0/8 | 2, 0.05 + 0.0005, 10°, -6 | 30 | bullet 0.7, life 20, size 0.3 | thrown 6, chip, every 5 f while held, walks, spread 0 0.0366 -0.0366 0.0732 -0.0732 (cycles), sweet: 6 in a row | 0–9 m, holds 8 |
| 6 | Cherry Bombs (w008) | 8/0/14 | 12, 0.18 + 0.0025, 60°, +4 | 3 | bomb 0.14 / 0.18 up, gravity 0.01, fuse 50, size 0.4, blast 2.4 | bursts on touch, sweet: direct | 2.5–6 m |
| 7 | Bow (w054) | 10/0/14 | 10, 0.15 + 0.0024, 20°, +2 | 6 | arrow 0.55, gravity 0.004, life 50, size 0.5 | thrown 6, pierces, sweet: flew 20+ f | 0–15 m |
| 8 | Boomerang (w055) | 8/0/12 | 8, 0.14 + 0.0022, 35°, +2 | 4 | boomerang 0.5, drag 0.0125, life 150, size 0.6 | thrown 6, leaves the hand, sweet: way back | 0–8 m |
| 9 | Trampoline (w007) | 6/0/10 | none | 3 | pad, lasts 600, size 0.9 | launch 0.42 | fist range |
| 10 | Mines (w070) | 6/0/8 | 16, 0.2 + 0.0026, 60°, +4 | 3 | mine, arms in 45, life 900, size 0.5, blast 2.4 | thrown 6, sweet: direct | fist range |
| 11 | Homing Missile (w068) | 14/0/22 | 15, 0.19 + 0.0027, 60°, +4 | 2 | missile 0.2, turn 0.008, life 240, size 0.5, blast 2.4 | thrown 12, bursts on touch, sweet: direct | 3–14 m |
| 12 | Black-Hole Bomb (w098) | 10/0/16 | 20, 0.25 + 0.003, 70°, +4 | 2 | hole 0.14 / 0.18 up, gravity 0.01, fuse 110, size 0.5, blast 4.2 | pulls 0.11 within 4.5 for 80 f | 2.5–6 m |

Each special case in today's engine becomes a column:

| Today, in code | In the table |
|---|---|
| `f.Weapon == BowlingBall` slows the carrier to 4/5 | `carry` 80 |
| `Weapons.Get(w).Move`, `MoveFor(id)`, `IsWeaponMove(id)` | `move`; `Weapons.RowOfMove(move)` is a 256-entry lookup built from the table, null for fist moves and for `WeaponThrow` |
| `f.Move == UziFire` repeats every `UziInterval` while held | `interval` 5 |
| `f.Move == UziFire` walks at half speed | `walk` |
| The switch in `FireShots` | `flight`, `vx`, `vy`, `feet`, `leave`, `spend`, `spread` |
| `ShotHalf(kind)`, `BombFuse`, `PadFrames`, `MineArmFrames`, `HoleFuse` | `size`, `fuse` |
| `RollFrames`, `BulletFrames`, `ArrowFrames`, `BoomerangFrames`, `MissileFrames`, `MineFrames` | `life` |
| `ArrowGravity`, `BoomerangDrag`, `MissileTurn`, `PadLaunch`, `HolePull…` | `grav`, `drag`, `turn`, `padLaunch`, `pull`, `reach`, `pullFrames` |
| `batter.Move == BatSwing` bats shots back | `defence` bat |
| `BlastHalf`, `HoleBlastHalf`, and the burst's `MoveData` | `blast`, and the row's own hit numbers |
| The `p.Shot switch` that picks a shot's `MoveData` | The row of `p.Weapon`; a `thrown` shot uses `WeaponThrow` and the row's `throw` |
| `MeleeWeaponHit` (belan, bat, shotgun) | `spend`, `sweet`, `sweetArg`, `sweetAng`, `sweetStun`, `farDist`, `farDmg` |
| `hit.NoHitStop` for bullets | `chip` |
| `taker.Weapon == BowlingBall` keeps an "empty" ball | `spend` never |
| `RangedReach`, `RangedMin`, `UziBurst` in `SparringBot` | `cpuMax`, `cpuMin`, `cpuBurst` |
| `WEAPON_LENGTH`, `WEAPON_VERBS`, `NAMES`, `SWEET_WORDS`, `WEAPON_WORDS`, `WEAPON_CLIPS`, `KEYS` | `WeaponInfo` (section 5) |

The three Uzi fields on `Fighter` are used by every repeating weapon, so they are renamed in both sims, keeping their place in the checksum: `UziShots` → `BurstShots`, `UziStreak` → `Streak`, `UziTarget` → `StreakTarget`. `BurstShots` goes up only for rows with `interval`, a spray or a held guard, as it does only for the Uzi today.

`WeaponThrow` stays a shared move (move id 14), not a row.

### 1.5 Hidden rows

Seven rows are never in a crate and never in a hand. Each is a part of a weapon, and its hits are recorded under that weapon (`credit`).

| id | Weapon (key) | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|
| 101 | Gauntlet pull | 8/2/14 | 3, 0.18 + 0.0006, 20°, +2 | - | box 0.5–4.5 × 0.6–1.5 | reel, nearest only | - |
| 102 | Gauntlet punch | 4/3/10 | 8, 0.13 + 0.0024, 45° | - | box 0.2–1.3 × 0.6–1.5 | spent per use | - |
| 103 | Gauntlet bolt | 6/0/12 | 5, 0.08 + 0.0012, 15° | - | bullet 0.6, life 20, size 0.3 | - | - |
| 104 | Thunder wave | - | 9, 0.14 + 0.002, 80°, +4 | - | roll 0.3, life 28, size 0.8 | - | - |
| 105 | Thrown gas cylinder | - | 16, 0.2 + 0.0027, 60°, +4 | - | bomb 0.32 / 0.06 up, gravity 0.01, fuse 120, size 0.6, blast 2.4 | bursts on touch, bursts on landing, sweet: direct | - |
| 106 | Thrown charpai | - | none | - | bomb 0.2 / 0.1 up, gravity 0.01, fuse 600, size 0.9 | opens on landing, leaves pad 360 f, launch 0.36 | - |
| 107 | Thrown spear | - | 13, 0.16 + 0.0025, 25°, +4 | - | arrow 0.45, gravity 0.003, life 45, size 0.5 | pierces, sweet: flew 15+ f | - |

- **Rows 101 to 103** are the Jugaad Gauntlet's three attacks. `ArmedMove` picks row `cycleFirst + (cycleCount - WeaponSupply % cycleCount) % cycleCount` and starts that row's move. With 9 presses the order is pull, punch, bolt, three times over: the pull brings them in for the punch. Each press spends one, hit or miss.
- **Row 104** is fired by the Thunder Hammer (`fires`) on its startup frame, beside its own hitbox, so the wave has its own damage.
- **Rows 105 to 107** are what a thrown Gas Cylinder, Charpai and Spear become (`thrownAs`): the throw fires that row's shot instead of a plain `thrown` prop. A crate or loose weapon flung by a Hockey Stick is always a plain `thrown` prop, whatever it holds. A plain `thrown` prop reads none of its weapon's shot columns (`brk`, `edge`, `noSelf` and the rest): its numbers are the throw's, its row's `throw` damage and a 0.6 m box (section 12).

A prop made from a hidden row carries the hidden id in `Prop.Weapon`, so it finds its numbers; hits, KOs, the signature bonus and `LastHitWeapon` use the row's `credit`.

### 1.6 Rules every weapon follows

Everything in v0.2 and v0.3 that is not named here is unchanged: touch to take and swap, one attack per weapon on either button, Up + Heavy stays the recovery, Drop weapon throws, an empty weapon is thrown, everything can hit the fighter who made it once the release grace is over, a parry destroys a projectile, sweet spots are 1.25×, the owner's +10%.

New shared rules:

- **Teams.** `MatchRules.TeamOf(fighter)` gives a team number, 0 for none, the way `SignatureOf` gives a signature weapon. It is part of the rules, not of `GameState`, so it adds no state. `Simulation.Allies(rules, a, b)` is true when `a == b`, or when both have the same team and it isn't 0. Choosing teams (the room lobby, at most 4 a team), team scores and who wins belong to the rooms work. This spec fixes what the sim does with them:
  - a fighter's hits, bursts, ticks, pulses, traps and statuses never land on a teammate, fists included. Shots fly through teammates. A fighter's own bomb still hurts that fighter, as today;
  - wherever a rule leaves out the owner (a speaker, a decoy, a spray, a shot with `noSelf`), it leaves out teammates too;
  - pads, gates and clouds work for everybody, and teammates always see each other in a cloud;
  - with no teams set, `Allies(a, b)` is `a == b` and nothing changes.
- **A safe spot.** `Simulation.SafeSpot(state, rules, point)` is true when the point is at least 2 m inside the blast zone on every side, a platform top lies straight below it within 4 m, and (with hazards on) it is not inside the area of a hazard that is warning or active. The Portal Disc and the Swap Ray use it.
- **Holds only take on the ground.** A root, stun, slip or freeze takes hold only on a grounded fighter. On a fighter in the air the hit lands as an ordinary hit and adds `slow` of the same length instead. A hold ends the moment its fighter leaves the ground (section 2.4). Nobody is ever held while falling.
- **A use that can't happen costs nothing** (it "fizzles"): no free prop slot, no landing spot for a blink, an unsafe spot for a gate. The move's frames play, nothing is spent, and a weapon that would have left the hand stays in it. `AddShot` returns whether it placed the shot, and `FireShots` spends and lets go only then.
- **Prop slots.** `state.Props` is sized when the match is made: `Weapons.PropSlots(fighters)` is min(64, 16 + 4 × fighters). That is 24 in a duel, 32 for four, 64 for a full room; today it is 16 for everyone.
  - The first `Weapons.Cap(fighters) + 2` slots are **box slots**. Prop Boxes, pending boxes and dropped weapons take the first empty slot anywhere, so they find those first. Shots only use the slots after them. So crates keep dropping, and a dead fighter's weapon always comes back, however many shots are out.
  - A shot with no empty slot replaces the oldest `bullet`, as today. With no bullet to replace, it replaces the oldest placed prop **of the fighter who fired it**. With none of those, it fizzles. A shot never removes another fighter's placed prop.
- **Placed props.** A fighter can have at most 4 placed props out (flights with the `placed` flag), or 2 when the match has more than 8 fighters. Placing one more removes their oldest, which vanishes without bursting, a mine too. Two rows are tighter: one speaker and two gates per owner.
- **Breakable props** (`brk`). A swing or a blast takes one swing off every breakable prop in its box that isn't its owner's or a teammate's, on the frame the move connects (section 1.7). At `brk` swings the prop is gone. A row with `gbreak` breaks it at once, and so does any burst that covers it. Sprays never break props. The swings taken are kept in `Prop.IgnoreFighter`.
- **Walls block the line.** A wall is between an attacker and a target when its centre lies between their two X positions and its box overlaps the hitbox. Such a wall stops a swing, a blast, a spray's push and tick, and a speaker's pulse. Bursts ignore walls, and break them.
- **Statuses and Guts armor.** A hit soaked by Guts armor adds its damage and nothing else: no status. Only a row with `armorbreak` (the Wrench) gets past the armor.
- **Dodging.** A dodging fighter is untouched by sprays, zones, traps, pulses and gates, as by hazards. Walls are the exception: a dodge goes through a wall.

### 1.7 One frame of hits

`ResolveHits` stays collect-then-apply: every contact is read from the state before any hit, then all are applied, so two hits on the same frame never depend on fighter index. Guards, `first` and breakable props go into that scheme, not beside it.

1. **Collect.** For each attacker whose swing or blast is active, not frozen and not yet connected (sprays are not here: section 2.5):
   - every breakable prop in the hitbox that isn't the attacker's or a teammate's is a prop contact, walls included;
   - every other fighter in the hitbox who is not a teammate and not behind a wall is a fighter contact. As today, a fighter in the parry window is a parried contact, and a dodging fighter is no contact. New: the contact is **guarded** if, in this pre-hit state, the defender's move is a guard on one of its guarding frames, the attacker is on the side the defender faces (or at the same X), and the attacker's row has no `gbreak`;
   - a row with `first` then keeps only its nearest fighter contact, by horizontal distance, the lower index on a tie. A dodging fighter was never a contact, so it doesn't shield the fighter behind it. A parrying or guarding one does. `first` rows don't touch breakable props other than the wall that stops them.
2. **Apply**, in this order, across all attackers: parries, blocks, prop swings, hits. An attacker with any contact has connected and hits nothing later in that move, as a hit on a fighter works today.

Because each contact's kind is fixed in step 1, a guard knocked away by one attacker still blocks another attacker's hit of the same frame, whatever their indices. Tests run each case twice with the two fighters' indices swapped (section 6).

Shots are resolved later in the frame, in `UpdateProps`, as today, against the state `ResolveHits` left. A guard knocked into hitstun this frame no longer stops a bullet.

A breakable shot (the Chicken, Dark Energy) inside a bat's swing is popped, not returned: the swing is resolved before the bat rule runs.

### 1.8 What changes for the twelve

Step A of batch 1 changes nothing: the goldens prove it. From step B on these are intended changes to how the twelve play, each with a test:

- Mines and Trampolines count towards the placed limit. A fifth placed prop (a third, in a room of more than 8) removes that fighter's oldest, which vanishes without bursting.
- A shot that finds no slot fizzles instead of eating the ammo. A Bowling Ball or Boomerang then stays in the hand.
- Later batches add things that act on the twelve's shots: walls stop them, guards and the Lathi destroy them, the Pankha turns them, the racket returns Uzi bullets.
- Teammates are not hit, when teams are set.
- Crates: 100 weapons instead of 12, with utility rows at half odds.
- The record keeps six weapons a fighter (section 4.3). The main weapon and the master mark are the same as today's for a fighter who used six weapons or fewer, and ties still go to the lower weapon id.

## 2. The 88 weapons

Tables are grouped by archetype. A row is built in its section's batch (section 7), unless its Extras say "built in batch N". "CPU" is the range the CPU fires from. "Size" is the shot's box, "blast" the burst's full width.

### 2.1 Plain swings (batch 2)

No new engine parts beyond the table: frame data, hitbox, lunge, both sides, carry weight, the bat's return, and the sweet-spot rules.

- **Light returns** (`batMax`, `batCost`). The Badminton Racket's `bat` returns only shots with no blast that do 8 damage or less (bullets, darts, stars, pebbles, snowballs, mugs), and each return costs 1 supply. The two bats keep today's rule: any flying shot, for free.
- **`gbreak`** (the Sledgehammer) is read by the guard and breakable-prop rules, which arrive in batch 4. Until then the column does nothing.

| id | Weapon (key) | What it does | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|---|
| 13 | Boxing Gloves (w001) | Faster than a fist jab, with little launch power: a combo tool, not a finisher. Punching someone mid-attack is the sweet spot. | 3/3/7 | 7, 0.13 + 0.0014, 45° | 12 | box 0.2–1.2 × 0.6–1.5 | thrown 6, sweet: mid-attack | fist range |
| 15 | Plank (w003) | A plain swing that breaks after four hits. | 10/4/16 | 12, 0.15 + 0.0024, 40°, +4 | 4 | box 0–1.7 × 0.4–1.6 | - | fist range |
| 24 | Jhaadu (w017) | A wide, low sweep on both sides that sends people sliding off flat, and sweeps away every trap, peel, puddle and patch in its reach. | 9/5/15 | 6, 0.15 + 0.002, 15°, +2 | 10 | box ±2 × 0–0.9 | thrown 6, sweeps ground props away, built in batch 4 | 0–1.7 m |
| 35 | Sil-Batta (w028) | The slowest overhead smash, and nearly the hardest. It comes down close in on both sides at once: the answer to a crowd. | 24/3/28 | 23, 0.22 + 0.003, 65°, +8 | 3 | box ±1.2 × 0–2.2 | thrown 12, carry 85% | fist range |
| 38 | Cricket Bat (w032) | Lofts people up at 60 degrees and bats projectiles back; the tip skies them at 75. | 10/4/18 | 13, 0.17 + 0.0025, 60°, +4 | 8 | box 0–1.6 × 0.3–1.7 | bats shots back, sweet: tip ≥ 1.1 m → 75° | fist range |
| 41 | Badminton Racket (w035) | Very fast, very light hits. Its swing returns light projectiles only (8 damage or less, nothing that bursts), and each return costs one of its 20. | 3/3/6 | 3, 0.08 + 0.0014, 40° | 20 | box 0.2–1.5 × 0.7–1.9 | thrown 6, bats light shots back (costs 1) | fist range |
| 42 | Pitchfork (w036) | A long poke that lifts people straight up. | 9/3/16 | 9, 0.16 + 0.0023, 85°, +4 | 7 | box 0.4–2.2 × 0.7–1.3 | - | 0–1.9 m |
| 45 | Trout (w039) | A floppy slap that leaves people dazed (+8 hitstun). | 6/3/12 | 7, 0.12 + 0.002, 40°, +8 | 9 | box 0.2–1.4 × 0.6–1.6 | - | fist range |
| 48 | Gada (w042) | A slow ground slam that hits both sides. | 22/4/26 | 20, 0.21 + 0.0028, 45°, +6 | 4 | box ±1.8 × 0–1.4 | thrown 12, carry 85% | fist range |
| 49 | Talwar (w043) | A quick slash that steps forward as it cuts. | 7/4/13 | 9, 0.14 + 0.0024, 30°, +2 | 9 | box 0.2–1.8 × 0.5–1.7 | lunge 0.14 at frame 5 | fist range |
| 51 | Katar (w045) | The fastest weapon: a short lunge. A hit from behind is the sweet spot. | 3/2/8 | 5, 0.09 + 0.0018, 35° | 14 | box 0.2–1.1 × 0.7–1.4 | thrown 6, lunge 0.18 at frame 1, sweet: from behind | fist range |
| 52 | Katana (w046) | A draw that dashes about 2 m; the tip is the sweet spot. Long recovery. | 8/3/20 | 11, 0.15 + 0.0023, 25°, +4 | 6 | box 0.3–2 × 0.6–1.5 | lunge 0.26 at frame 5, sweet: tip ≥ 1.4 m | fist range |
| 53 | Broadsword (w047) | A broad, heavy sweep with the longest reach of the swords. | 18/5/24 | 19, 0.2 + 0.0027, 35°, +6 | 5 | box 0–2.2 × 0.3–1.9 | thrown 12, carry 90% | fist range |
| 55 | Trident (w049) | A tall three-pronged thrust, from the knees to above the head, that pushes people away at 40 degrees. The tip skewers them upwards at 70. | 12/4/18 | 12, 0.17 + 0.0025, 40°, +4 | 6 | box 0.5–2.2 × 0.2–2.2 | sweet: tip ≥ 1.6 m → 70° | 0–1.9 m |
| 61 | Sledgehammer (w057) | The hardest hit in the game, flat at 20 degrees for ring-outs, after the longest wind-up. It goes through guards and breaks any wall, speaker, trap or decoy in one swing. | 26/4/30 | 24, 0.23 + 0.0031, 20°, +8 | 3 | box 0.2–1.9 × 0–1.8 | thrown 12, carry 80%, guard break | fist range |

### 2.2 Plain blasts (batch 2)

The shotgun's archetype: a box that hits on its active frames and is spent when fired. `first` (new) keeps only the nearest fighter in the box (section 1.7). That is how a bullet that crosses the stage in one frame is done without a fast projectile skipping past people. The Sniper Rifle's aim line is drawn by the client during its startup; the sim only knows a long startup.

**Shoves.** The Confetti Cannon and the Spring Glove here, and the Charpai, the Parat and the Sonic Speaker later, have a high base knockback and almost no growth: the same shove at any Josh. A shove must never be a kill on a fresh fighter. A test holds all five to that: from 3 m inside a ledge, at 0 Josh, none of them may knock out a fighter who then uses their jump and recovery. The numbers below were cut for it; if the test fails, cut the base again.

| id | Weapon (key) | What it does | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|---|
| 34 | Pressure Cooker (w027) | It whistles for 36 frames, then a steam burst 3.2 m to both sides. | 36/3/24 | 15, 0.21 + 0.0027, 60°, +4 | 2 | box ±3.2 × 0–2.6 | thrown 12 | 0–2.8 m |
| 59 | Sniper Rifle (w053) | A 40-frame aim with a line everyone can see, then an instant shot across the stage that hits the nearest fighter on the line. 10 m or more is the sweet spot. | 40/1/30 | 22, 0.22 + 0.003, 15°, +6 | 3 | box 0.6–30 × 0.9–1.3 | kick 0.1 at frame 40, nearest only, sweet: tip ≥ 10 m | 6–28 m |
| 65 | .357 Magnum (w062) | A hard instant shot to 9 m, after a draw everyone can see, with a big kick and a long recovery. Hitting someone in the air is the sweet spot. | 14/1/34 | 16, 0.19 + 0.0022, 20°, +4 | 4 | box 0.5–9 × 0.8–1.4 | thrown 6, kick 0.18 at frame 14, nearest only, sweet: target in the air | 2–8.5 m |
| 84 | Dhol (w083) | A sound pulse 3 m to both sides that knocks everyone back. Catching someone mid-attack is the sweet spot. | 14/3/20 | 6, 0.16 + 0.0016, 45°, +6 | 5 | box ±3 × 0–2.4 | sweet: mid-attack, +8 stun | 0–2.6 m |
| 86 | Confetti Cannon (w085) | A short cone that shoves everyone in it, whatever their Josh; modest damage. | 12/2/24 | 8, 0.22 + 0.0008, 25°, +2 | 3 | box 0.4–4 × 0.3–1.9 | kick 0.08 at frame 12 | 0–3.5 m |
| 91 | Spring Glove Launcher (w090) | A telegraphed glove on a spring: one long flat shove on the nearest fighter within 6 m. | 16/3/26 (was 22: section 12) | 9, 0.26 + 0.0006, 20°, +4 | 2 | box 0.4–6 × 0.7–1.4 | nearest only | 0–5.5 m |

### 2.3 Plain guns and projectiles (batch 2)

These use the flights that exist (bullet, arrow, boomerang, bomb, missile) with new numbers, plus four small generalisations:

- **Repeating fire.** At frame `s + interval`, the move loops back to `s` if the weapon has supply, `BurstShots` is under `burstMax` (when set), and either an attack button is held or `BurstShots` is under `burst`. The Uzi is `interval` 5 with neither limit: today's rule.
- **Pellets and spread.** A use fires `pellets` shots. Shot k's upward speed is `vy + spread[k]`. With `spreadBy` `burst`, `hold` or `widen`, k is the shots fired in this burst: `burst` wraps round the list (the Uzi), `hold` stays on the last entry, `widen` goes through the list once and then alternates between its last two entries. With `pellet`, k is the pellet's number. With `supply`, k is the supply left, wrapped.
- **Arrows that stop.** An `arrow` without `pierce` is gone on its first hit.
- **Bombs that wait.** A `bomb` without `touch` only bursts when its fuse ends.

The automatic guns are sized against the Uzi (30 × 2): none carries much more than a crate of it.

| id | Weapon (key) | What it does | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|---|
| 14 | Slingshot (w002) | Quick pebbles on a high arc; a long arc (18 frames or more, about 8 m) is the sweet spot. | 8/0/8 | 5, 0.09 + 0.0014, 30° | 10 | arrow 0.45 / 0.08 up, gravity 0.008, life 60, size 0.4 | thrown 6, sweet: flew 18+ f | 2–11 m |
| 16 | Ninja Stars (w004) | Each throw is a fan of three stars. | 4/0/8 | 3, 0.07 + 0.001, 20° | 5 | bullet 0.55, life 18, size 0.3 | thrown 6, 3 at once, spread 0 0.06 -0.06 (one each) | 0–8 m |
| 17 | Stick Grenades (w009) | A high lob on a short fuse. It does not burst on touch: it goes off at the end of its arc, about 6 m out at hand height, in a 3.5 m blast (as built: section 12; designed as a longer throw that lay counting down 75 frames). | 10/0/16 | 26, 0.2 + 0.0027, 60°, +4 | 4 | bomb 0.14 / 0.2 up, gravity 0.01, fuse 40, size 0.4, blast 3.5 | - | 3.5–9 m |
| 19 | Thali (w012) | The plate is held up while it winds up (frames 2 to 6) and blocks from the front. Then it flies flat and stops in the first fighter it hits. | 6/0/10 | 7, 0.11 + 0.0018, 25° | 6 | arrow 0.5, gravity 0.001, life 30, size 0.5 | thrown 6, guards while winding up, built in batch 4 | 0–10 m |
| 22 | Chakla (w015) | A heavy board, lobbed slow and hard. It bursts on whoever it hits or where it comes down, with a 1.6 m shockwave. | 12/0/18 | 10, 0.16 + 0.0024, 60°, +4 | 3 | bomb 0.3 / 0.1 up, gravity 0.01, fuse 120, size 0.6, blast 1.6 | bursts on touch, bursts on landing, sweet: direct, built in batch 3 | 2–8 m |
| 23 | Chappal (w016) | A quick slipper that flies 5 m and comes back to the hand like the boomerang, stinging both ways. | 4/0/8 | 5, 0.09 + 0.0015, 40°, +6 | 6 | boomerang 0.4, drag 0.016, life 100, size 0.5 | thrown 6, leaves the hand, sweet: way back | 0–4.5 m |
| 57 | Crossbow (w051) | One fast, flat, hard bolt that stops in the first fighter it hits, then a 40-frame reload. | 10/0/40 | 14, 0.17 + 0.0026, 20°, +4 | 4 | arrow 0.8, gravity 0.002, life 40, size 0.5 | sweet: flew 12+ f | 2–20 m |
| 66 | Tommy Gun (w063) | Automatic, one bullet every 6 frames, walking as it fires. Its spread widens through a burst and stays wide: only the first few shots of a hold go where it points. | 5/0/12 | 2, 0.05 + 0.0006, 10°, -6 | 36 | bullet 0.65, life 22, size 0.3 | chip, every 6 f while held, walks, spread 0 0.02 -0.02 0.04 -0.04 0.06 -0.06 0.08 -0.08 (then stays at ±0.08) | 0–10 m, holds 10 |
| 67 | AK-47 (w064) | Automatic, harder bullets, but each shot of a burst climbs, so at range a long burst goes over heads. Tap for short bursts. No walking while firing. | 5/0/12 | 4, 0.06 + 0.0007, 10°, -4 | 18 | bullet 0.75, life 24, size 0.3 | chip, every 7 f while held, spread 0 0.015 0.03 0.045 0.06 0.075 (holds the last), sweet: 4 in a row | 0–13 m, holds 4 |
| 68 | Burst Rifle (w065) | One press fires exactly three straight bullets, then a 22-frame pause. All three on one target is the sweet spot. | 6/0/22 | 4, 0.07 + 0.0008, 10°, -2 | 15 | bullet 0.8, life 22, size 0.3 | chip, every 4 f, burst of 3, sweet: 3 in a row | 0–14 m |
| 69 | RPG (w066) | A straight rocket, twice the missile's speed and no steering; a 2.8 m blast. | 16/0/30 | 18, 0.21 + 0.0028, 60°, +4 | 2 | missile 0.4, life 70, size 0.5, blast 2.8 | thrown 12, kick 0.12 at frame 16, bursts on touch, sweet: direct | 4–16 m |
| 77 | Ray Gun (w076) | One straight energy bolt per press, with real hit-stop and knockback. | 5/0/9 | 6, 0.1 + 0.0014, 15° | 10 | bullet 0.6, life 30, size 0.4 | thrown 6 | 0–14 m |
| 80 | Minigun (w079) | Half a second of spin-up, then a bullet every 3 frames. It overheats after 24 shots, and carrying it slows you to 60%. | 30/0/36 | 2, 0.04 + 0.0004, 10°, -6 | 45 | bullet 0.7, life 22, size 0.3 | thrown 12, carry 60%, chip, every 3 f while held, overheats at 24, spread 0 0.03 -0.03 0.06 -0.06 0.09 -0.09 (cycles), sweet: 10 in a row | 0–10 m, holds 24 |
| 93 | Dark Energy (w092) | A big orb that drifts across the stage slower than a fighter runs, through platforms and through up to four fighters, for 4 s. Easy to see. It never hurts its caster, and any swing pops it. | 34/0/24 | 12, 0.16 + 0.0022, 40°, +6 | 2 | arrow 0.12, life 240, size 1 | pierces, fades after 4 hits, breaks in 1, never hits its owner, built in batch 4 | 2–8 m |

### 2.4 Statuses (batch 3)

Eight `Fighter` fields carry every status (section 3). The effects:

| Effect | Rule |
|---|---|
| `slow` N | `SlowFrames` = min(150, `SlowFrames` + N). While it runs the fighter moves at half speed (the exact code paths are below) |
| `chill` N | As `slow` N. Then, if `SlowFrames` ≥ 90 and a hold can take: frozen. That is a stun of 60 frames, `SlowFrames` back to 0, and 210 frames of immunity (so 150 after thawing). Snowballs and the Freeze Ray add up |
| `root` N | A hold. `RootFrames` = N, immunity = N + 90. A rooted fighter can't walk, jump, dodge or use the recovery; it can attack. Each fresh Light or Heavy press cuts 12 frames off |
| `stun` N | A hold. The fighter is put in `Stunned` for N frames, as a parried attacker is: move cleared, sideways speed zero, no rising. Immunity = N + 90. The hit's own knockback is skipped |
| `slip` N | As `stun` N, and the fighter slides the way it faces at 0.30 m/frame, slowing with ground friction: about 2.3 m. If that takes it off a ledge the hold ends there and it can jump |
| `poison` N | `DotFrames` = max(`DotFrames`, N); `DotBy` and `DotWeapon` record who and what. It never stacks. 1 damage a tick, a tick every 30 frames (or the row's `dotEvery`) |
| `burn` N | The same fields, a tick every 15 frames. Which one it is comes from `DotWeapon`'s row |
| `mark` N | `MarkFrames` = max(`MarkFrames`, N). A marked fighter takes 25% more damage from every hit |
| `disarm` | The target's weapon drops at its feet as a loose weapon with the supply it had, ignoring the target for 1 s (the swap rule), and its burst and streak counters are cleared. Nothing happens if it has no weapon, an empty one, or no prop slot is free. The hit that disarms also knocks the target out of whatever move it was in, as any hit does |
| `reel` | The launch is towards the attacker instead of away |
| `leech` | The attacker's Josh goes down by the damage dealt, not below 0 |
| `swap` | The attacker (a shot's owner) and the target change places, with both speeds set to zero, if both spots pass `SafeSpot` (section 1.6). Otherwise nothing but the damage. The shooter's own mines and bombs are not part of the test: swapping someone onto your own mine is the trick |

- **Holds** (root, stun, slip, freeze). A hold takes only on a target who is grounded, not stun-immune and not in Guts armor. It writes the weapon into `StunWeapon`, which is how the client knows what to draw.
  - Stun-immune: the hit is an ordinary hit with the row's knockback.
  - In the air: an ordinary hit with the row's knockback, plus `slow` N. A `chill` only adds its slow: nobody freezes in the air.
  - A hold ends when its time is up, when a hit with knockback lands, or on the first frame the fighter is not grounded (pushed off a ledge, lifted by a Gravity Gun, launched by a hazard). Then `RootFrames` and `StunWeapon` are 0 and a `Stunned` fighter is `Idle` again. A parry's stun has `StunWeapon` 0 and is left alone.
- **Movement, by code path.** The sims move a fighter in several places, so the rule is written for each:
  - slow halves the target speed in `HandleControl` (ground and air, after `carry`), the walk-while-firing speed, and the air-control speed of a move that has it. Lunges, rises and launches are untouched;
  - root, in `HandleControl`: facing still turns; the target sideways speed is 0; Jump, Dodge and Up + Heavy do nothing. In an attack: the walk-while-firing speed is 0, and a lunge or kick impulse is skipped. A blink ends the root;
  - the 12 frames a press takes off a root are taken in `UpdateFighter`, where `pressed` is worked out, before the action switch, so it works mid-attack. A press made during hit-stop counts when the freeze ends, as presses do today.
- **Ticks** (poison and burn) happen in `TickTimers`, which now gets the frame and the fighter's index. While `DotFrames` > 0, a tick lands on every frame where `(state.Frame + index) % period == 0`. The phase doesn't depend on `DotFrames`, so a zone or a flamethrower that refreshes the timer every frame neither stops the ticks nor speeds them up. `TickTimers` doesn't run during hit-stop, so a frozen fighter doesn't tick.
- **A tick is not a hit:** no knockback, hitstun, hit-stop, rage, mark bonus or hit count. It goes through the hidden second wind, can trigger Guts, and adds its damage to `DotBy`'s record under `DotWeapon`.
- **Ticks and the KO.** A tick writes the last-hit record (`LastHitBy`, `LastHitTimer`, `LastHitWeapon`, not sweet) only when no credit is running, or when the running credit is already `DotBy`'s. So poison never takes a KO from whoever launched the victim, and never changes `LastHitWeapon` under a real hit. A fighter's own poison never writes it.
- **Order in `LandHit`:** damage (rage, sweet and signature percent, then the mark's 125%) → second wind → Josh → last-hit record → stats → hit-stop (unless `chip` or `soft`) → Guts → the armor check (skipped by `armorbreak`) → the effect → stop here if `soft` or a hold took → knockback. With no effect, no mark and no `soft`, this is today's order exactly.
- **Timers** count down in `TickTimers`, so they pause during hit-stop like every other timer. `Spawn` clears all eight fields, and so does `CheckBlastZone` when a fighter is knocked out, so a fighter waiting to respawn carries no status.

| id | Weapon (key) | What it does | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|---|
| 25 | Balti (w018) | An overhead bonk that leaves its target soaked: slowed for 1 s. | 13/3/18 | 11, 0.15 + 0.0025, 55°, +4 | 6 | box 0.1–1.3 × 0.6–2.1 | slow 60 | fist range |
| 46 | Net (w040) | Whoever it lands on, standing on the ground, is rooted for 1.5 s: no walking, jumping or dodging. Each attack press cuts 12 frames off. Someone in the air is only slowed. | 12/0/18 | 1, soft | 2 | lob 0.22 / 0.12 up, gravity 0.01, size 0.8 | thrown 6, root 90 | 1.5–6 m |
| 58 | Blow Darts (w052) | Quiet light darts; each adds 1 s of slow. | 5/0/8 | 2, 0.04 + 0.0006, 15°, -4 | 12 | bullet 0.5, life 24, size 0.24 | thrown 6, slow 60, chip | 0–9 m |
| 73 | Venom Dart Launcher (w072) | A dart that poisons: 1 damage every half second for 4 s. | 6/0/12 | 3, 0.06 + 0.0008, 15° | 8 | bullet 0.6, life 26, size 0.3 | thrown 6, poison 240 | 0–12 m |
| 79 | Taser (w078) | A very short arc that stuns for 24 frames instead of knocking back, if the target is on the ground. | 3/2/14 | 5, 0.08 + 0.001, 30° | 5 | box 0.2–1 × 0.8–1.3 | thrown 6, stun 24 | fist range |
| 83 | Colour Powder Pouch (w082) | A puff of colour: everyone in it is marked for 5 s, and a marked fighter takes 25% more damage from every hit. | 6/3/12 | 2, 0.06 + 0.0006, 45° | 5 | box 0.2–2.6 × 0.4–2 | thrown 6, mark 300 | 0–2.3 m |
| 85 | Snowballs (w084) | Arcing throws; each hit adds slow (up to 2.5 s). They never freeze by themselves. | 5/0/9 | 3, 0.06 + 0.001, 40° | 10 | lob 0.28 / 0.14 up, gravity 0.01, size 0.4 | thrown 6, slow 50 | 2–8 m |
| 90 | Glue Gun (w089) | Automatic sticky blobs on a low arc. Each slows whoever it hits and leaves a 1.2 m sticky patch for 3 s that keeps anyone standing in it slow. | 6/0/12 | 1, soft | 12 | bomb 0.3 / 0.1 up, gravity 0.01, fuse 90, size 0.4, blast 0.6 | slow 45, every 10 f while held, walks, bursts on touch, bursts on landing, leaves zone 1.2 × 0.4, 180 f, built in batch 4 | 1–7 m, holds 3 |

### 2.5 Sprays and beams (batch 3)

A spray is a move with startup `s`, a tick length `a` and recovery `r`.

- The box is out on every frame from `s`. At frame `s + a` the move loops back to `s` while an attack button is held and there is supply. Each pass through frame `s` is a **tick**: it spends 1 supply and counts in `BurstShots`.
- A new pass, `Sprays`, runs after `ResolveHits`. For each fighter spraying, and each other active, undodging fighter who is not a teammate, whose hurtbox touches the box and who is not behind a wall (only the nearest if `first`; not one holding a `hold` guard towards the wielder):
  - every frame: `drift` moves the target sideways (the way the wielder faces; negative pulls, and stops pulling inside 1 m), and `lift` sets its upward speed to at least `lift` and takes it off the ground;
  - on tick frames: the row's hit lands, if it has damage or an effect, and if the target is within `tickNear` when that is set. Tick hits never freeze the wielder.
- **Drift on a fighter in the air is capped** at 60% of the tuning's air speed (0.066 m/frame). A grounded fighter gets the row's full drift. So a gust at a ledge slows a recovering fighter but can't hold them out: they still gain 40% of their air speed against it, and have their jump and recovery.
- `propDrift` moves boxes and loose weapons in the box the same way (a pull stops 1 m in front of the wielder). `blow` turns round every shot with the `flies` flag in the box that isn't the wielder's or a teammate's, so it flies away from the wielder; it stays its owner's.
- `ResolveHits` skips spray moves. Sprays never break props. Sprays can be parried only in the sense that a dodging fighter is untouched.

| id | Weapon (key) | What it does | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|---|
| 29 | Chai Kettle (w022) | A short puff of steam that pops people upwards a little, tick after tick. | 6/6/12 | 2, 0.06 + 0.0006, 75°, -2 | 18 | box 0.3–2 × 0.6–2 | chip | fist range, holds 5 |
| 36 | Pankha (w029) | A gust that pushes fighters back (on the ground, almost as fast as they run) and turns other fighters' projectiles around. 1 damage a tick. | 8/4/12 | 1, soft | 24 | box 0.3–4.5 × 0.2–2.2 | pushes 0.12/f, turns shots | 0–4 m, holds 12 |
| 60 | Chainsaw (w056) | Close contact damage, 3 every 5 frames while held, walking at half speed. 20 ticks of fuel. | 10/5/14 | 3, 0.04 + 0.0004, 20°, -2 | 20 | box 0.3–1.5 × 0.5–1.5 | chip, walks | fist range, holds 6 |
| 64 | Magnet Gun (w060) | A 7 m beam that drags fighters who are holding a weapon towards the wielder (slower than they run), and pulls loose crates and weapons in fast, to be taken. A fighter dragged all the way in is clonked upwards each tick. | 8/6/12 | 3, 0.1 + 0.001, 70° (within 1.2 m) | 20 | box 0.4–7 × 0.3–2 | pulls 0.1/f (armed only), pulls crates 0.3/f | 3–6.5 m, holds 10 |
| 72 | Health Leech (w071) | A 4 m tether to the nearest fighter in front: each tick adds 2 Josh to them and takes 2 off the wielder. | 10/6/14 | 2, soft | 20 | box 0.4–4 × 0.6–1.6 | thrown 6, leech, nearest only | 0–3.6 m, holds 8 |
| 78 | Freeze Ray (w077) | Each tick adds chill (slow); about half a second on one target on the ground freezes them solid for 1 s. A thawed fighter can't be frozen again for 2.5 s. Nobody freezes in the air. | 10/4/16 | 1, soft | 30 | box 0.4–5 × 0.7–1.5 | chill 16, nearest only | 0–4.5 m, holds 12 |
| 81 | Flamethrower (w080) | A short cone; each tick does 2 and sets the target burning: 1 damage every quarter second for 1.5 s. | 8/6/16 | 2, 0.04 + 0.0004, 20°, -4 | 24 | box 0.4–3.2 × 0.4–1.7 | burn 90, chip, walks | 0–2.8 m, holds 6 |
| 82 | Pichkari (w081) | A thin 5 m water jet that pushes everyone in it back 0.09 m a frame; 1 damage a tick. | 5/8/10 | 1, soft | 16 | box 0.4–5 × 0.8–1.4 | thrown 6, walks, pushes 0.09/f | 0–4.5 m, holds 8 |
| 95 | Gravity Gun (w094) | A 6 m beam that floats everyone in it upwards and away, and throws loose crates off. 1 damage a tick. At most 1.5 s of beam in a crate, so nobody is held for long. | 12/3/14 | 1, soft | 30 | box 0.4–6 × 0–2.6 | pushes 0.05/f, lifts 0.06/f, pushes crates 0.3/f | 0–5.5 m, holds 14 |

### 2.6 Lobs and bouncers (batch 3)

`lob` is new: it arcs under `grav`, lands on platforms, and hits whoever it touches (gone on that hit unless `pierce`).

- **Floor bounces.** When it lands with bounces left, it leaves the floor again at `keep` percent of the speed it came down with (at least 0.08). With none left, a lob is gone. Bounces done are kept in `Prop.Timer` (a `bomb` with `bounces`, the Grenade Launcher, keeps them in `Prop.Hits`, and after its last bounce lies and slides like any bomb).
- **Screen-edge bounces** (`edge`). The screen's sides are 1.2 m inside the side blast zones. A shot that crosses one, with edge bounces left, is put back on the edge with its sideways speed reversed, and can hit everyone again. The count is kept in `Prop.IgnoreTimer`, which shots never used.
- **`bounced`** is true once either count is above 0.
- **`life`** ends a lob in the air if set.
- **`land`.** A `bomb` with `land` bursts on the frame it lands (the Chakla here; the pots and blobs of batch 4).

| id | Weapon (key) | What it does | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|---|
| 26 | Bathroom Mug (w019) | Rapid light throws that bounce once; a hit after the bounce is the sweet spot. | 3/0/6 | 3, 0.06 + 0.001, 40° | 8 | lob 0.3 / 0.08 up, gravity 0.01, life 60, size 0.4 | thrown 6, bounces 1 (80%), sweet: after a bounce | 0–6 m |
| 28 | Tiffin Stack (w021) | Each toss throws the whole stack: three containers that fan out, each bouncing twice. A hit after a bounce is the sweet spot. | 8/0/12 | 5, 0.1 + 0.0016, 45°, +2 | 3 | lob 0.22 / 0.16 up, gravity 0.01, life 120, size 0.5 | 3 at once, spread 0 0.05 0.1 (one each), bounces 2 (80%), sweet: after a bounce | 2–7 m |
| 30 | Steel Tumbler (w023) | A fast skimming tumbler that skips off the floor, goes through everyone and bounces back once off the side of the screen. | 4/0/8 | 4, 0.06 + 0.0009, 20° | 8 | lob 0.55 / 0.02 up, gravity 0.01, life 80, size 0.4 | thrown 6, pierces, bounces 3 (90%), screen-edge bounce 1, sweet: 2nd fighter on | 0–10 m |
| 40 | Cricket Ball (w034) | Bowled fast at the ground: it pitches once and comes up. A hit after it pitches is the sweet spot. | 6/0/12 | 8, 0.13 + 0.0022, 30°, +2 | 5 | lob 0.5 / 0.04 down, gravity 0.01, life 50, size 0.4 | thrown 6, bounces 1 (90%), sweet: after a bounce | 2–11 m |
| 50 | Chakram (w044) | A flat ring that flies level through everyone and comes back once off the side of the screen; a hit on the way back is the sweet spot. | 8/0/12 | 7, 0.13 + 0.002, 25°, +2 | 4 | arrow 0.5, life 70, size 0.5 | thrown 6, pierces, screen-edge bounce 1, sweet: after a bounce | 0–12 m |
| 70 | Grenade Launcher (w067) | Fast grenades that bounce twice, so they get over and round platforms; they burst on touch or after 80 frames. | 10/0/20 | 13, 0.18 + 0.0026, 60°, +4 | 4 | bomb 0.32 / 0.12 up, gravity 0.01, fuse 80, size 0.4, blast 2.4 | kick 0.08 at frame 10, bounces 2 (70%), bursts on touch, sweet: direct | 4–12 m |
| 71 | Cannon (w069) | A heavy arcing cannonball that goes through everyone in its path. No explosion; huge knockback. | 22/0/30 | 22, 0.24 + 0.003, 45°, +6 | 2 | lob 0.3 / 0.16 up, gravity 0.01, size 0.7 | thrown 12, carry 70%, kick 0.15 at frame 22, pierces | 4–10 m |
| 100 | Rubber Chicken Launcher (w100) | Squeaky chickens that bounce three times; each shot leaves at a different height, so their paths are hard to read. | 8/0/14 | 6, 0.11 + 0.0016, 50°, +2 | 6 | lob 0.26 / 0.1 up, gravity 0.01, life 150, size 0.6 | spread 0 0.08 0.04 (by supply), bounces 3 (80%) | 2–9 m |

### 2.7 Guards, melee tricks and linked rows (batch 4)

**Defence.** The bat's "bats projectiles back" becomes one column with five values:

| `defence` | Rule |
|---|---|
| `bat` | During the active frames, a flying shot in the hitbox, coming towards the swinger, becomes theirs, reversed and 1.25× faster. Today's rule. `batMax` and `batCost` narrow it (the racket: section 2.1) |
| `break` | During the active frames, a flying shot in the hitbox that isn't the swinger's or a teammate's is destroyed, without bursting |
| `guard` | From frame 2 until the startup ends, the fighter guards its front |
| `hold` | As `guard`; and while an attack button is held the move waits on its last startup frame, for up to 90 frames (counted in `BurstShots`). Then the bash comes out |
| `sweep` | During the active frames, every `mine`-flight prop (mines, traps, peels) and every zone that isn't a cloud in the hitbox is removed, whoever owns it, without bursting |

"Flying" means the `flies` flag (section 1.3). No defence touches a placed prop except `sweep`, and nothing touches the Anvil.

**Guarding.** A guard covers whatever comes from the side the fighter faces (or from exactly where it stands). A shot's side is the way it moves (towards the guard's face), not where it is: a fast bullet is often first seen overlapping the guard after it has passed its middle (section 12). A guard with no supply left guards nothing.

- A swing or blast from the front is blocked: no damage, no status. Whether a contact is blocked is decided in the collect pass (section 1.7). The attacker's move counts as connected, the attacker is pushed back at 0.12 m/frame, and both freeze for the shortest hit-stop.
- A flying shot from the front is destroyed, as by a parry. A burst whose centre is in front does not hit the guard.
- Each block costs the guard 1 supply. At 0 the weapon is empty: the guard is down for the rest of the move (the Parat's tray is no longer held up, so the bash comes out), and the next press throws it.
- A row with `gbreak` (the Wrench, the Sledgehammer) ignores guards.
- Sprays, zones and poison ticks are not blocked by `guard`; a `hold` guard also blocks sprays from the front.

**Props in a swing** (`scoop`). On its active frames, a swing with `fling` turns every box and loose weapon in its hitbox into a `thrown` shot owned by the swinger, flying the way they face at the throw speed. It does its weapon's `throw` damage.

- The flung prop carries its supply in `Prop.Supply`. Where a fighter's thrown weapon is gone when it hits or lands, a flung one comes to rest there as a loose weapon holding what it held.
- One that leaves the blast zone comes back in a box a second later, as a dead fighter's weapon does.
- So flinging never takes a weapon out of the match.

With `pull`, each box and loose weapon is moved to 1.2 m in front of the swinger instead.

| id | Weapon (key) | What it does | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|---|
| 20 | Tawa (w013) | A heavy slap. While it winds up (frames 2 to 10) the pan guards the front. | 11/3/18 | 14, 0.16 + 0.0026, 30°, +6 | 7 | box 0.1–1.3 × 0.5–1.7 | guards while winding up, sweet: mid-attack | fist range |
| 21 | Chimta (w014) | A long pinch. A hit with the very tip (1.5 m or more away) knocks the target's weapon out of their hand, onto the floor. | 6/3/12 | 4, 0.08 + 0.0012, 30°, +2 | 6 | box 0.3–2 × 0.8–1.3 | thrown 6, sweet: tip ≥ 1.5 m, disarms on the sweet spot | 0–1.7 m |
| 31 | Parat (w024) | Hold the button to keep the tray up for up to 1.5 s: everything from the front is blocked. Let go to bash. | 8/3/14 | 6, 0.2 + 0.001, 20°, +4 | 8 | box 0.1–1.3 × 0.3–1.8 | hold to guard (90 max) | fist range |
| 33 | Gas Cylinder (w026) | A heavy two-handed swing that slows its carrier. Thrown, it bursts where it lands or on whoever it touches. | 20/4/26 | 20, 0.21 + 0.0029, 30°, +6 | 3 | box 0–1.6 × 0.3–1.7 | thrown 12, carry 75%, thrown as 105 | fist range |
| 37 | Charpai (w030) | A two-handed shove. Thrown, it lands and lies for 6 s as a bounce pad. | 14/5/20 | 13, 0.2 + 0.0012, 20°, +4 | 4 | box 0–1.9 × 0.2–1.6 | carry 85%, thrown as 106 | fist range |
| 39 | Hockey Stick (w033) | A long low sweep. Any crate or loose weapon it touches is flung forward as a thrown weapon. | 9/4/14 | 9, 0.15 + 0.0022, 20°, +2 | 8 | box 0.2–2 × 0–0.8 | props: fling | 0–1.7 m |
| 43 | Rake (w037) | A low sweep that launches people towards the wielder instead of away, and drags loose crates and weapons in. | 10/4/16 | 8, 0.16 + 0.0014, 25°, +4 | 7 | box 0.3–2.3 × 0–0.9 | reel, props: pull | 0–2 m |
| 47 | Lathi (w041) | A staff spin that hits both sides and knocks projectiles out of the air. | 8/5/15 | 8, 0.14 + 0.0022, 35°, +2 | 10 | box ±2.1 × 0.4–1.7 | breaks shots | 0–1.8 m |
| 54 | Spear (w048) | The longest poke. Thrown, it flies flat and goes through everyone. | 9/3/15 | 9, 0.15 + 0.0023, 25°, +2 | 8 | box 0.6–2.5 × 0.8–1.2 | sweet: tip ≥ 1.9 m, thrown as 107 | 0–2.2 m |
| 56 | Cutlass (w050) | A fast cut whose short wind-up (frames 2 to 4) guards the front: swing into an attack to block it and hit back. | 5/3/10 | 6, 0.11 + 0.0022, 40° | 12 | box 0.2–1.6 × 0.5–1.6 | thrown 6, guards while winding up | fist range |
| 63 | Wrench (w059) | A reliable swing that goes straight through a guard and through Guts armor, and breaks any breakable prop in one swing. | 8/3/14 | 11, 0.14 + 0.0024, 40°, +4 | 9 | box 0.2–1.4 × 0.5–1.6 | guard break, armor break | fist range |
| 97 | Jugaad Gauntlet (w096) | Each press does the next of three things, in turn: a pull, a punch, then an energy bolt. Nine presses. | by turn | none | 9 | - | rows 101–103 in turn | 0–1.2 m |
| 99 | Thunder Hammer (w099) | A slow smash that also sends a shockwave 8 m along the ground, through everyone. | 22/4/30 | 18, 0.2 + 0.0028, 60°, +6 | 3 | box 0.2–1.8 × 0–1.8 | spent per use, thrown 12, carry 85%, also fires 104 | 0–6 m |

### 2.8 Traps, zones and walls (batch 4)

**Traps** are the `mine` flight with no blast. After arming, the first undodging fighter to touch one takes the row's hit and effect alone, and the trap is gone. Its owner can trip it once they have stepped off it; a teammate can't. A trap nobody finds is gone after 15 s without bursting.

**What a burst leaves** (`leaves`). When a bomb with `leaves` bursts, on touch, on landing or when its fuse ends, its burst hits as any burst does (with the row's effect, if it has damage), and then the prop is not removed: it becomes the thing it leaves, where it burst, through `Become` (section 3.2), which resets every field. That thing falls to the platform below if it burst in the air. It counts as a placed prop from then on.

- **Zone.** A box `zoneW` wide and `zoneH` high on the ground, for `zoneLife` frames. Every frame, each undodging fighter touching it, its owner included (teammates too: a puddle is a puddle), gets the row's effect in its standing form:
  - `slow` N: `SlowFrames` is at least N;
  - `poison` N: `DotFrames` is at least N, credited to the zone's owner. The ticks keep their own rhythm (section 2.4): standing in the goo for 2 s is exactly 8 damage;
  - `slip` N: a grounded fighter moving faster than 0.05 m/frame, with no stun immunity, slips.
- **Cloud** (`hides`). A zone that hides. A fighter whose middle is inside one is **hidden**: the server marks it in STATE, no screen draws it or its name tag (a player's own fighter and teammates are drawn faintly), the CPU doesn't target it from more than 2 m, and a homing missile doesn't steer at it. `Simulation.IsHidden` works it out from the props; it adds no state.
- **Wall.** A block `zoneW` wide and `zoneH` high for `zoneLife` frames.
  - Each frame, a fighter overlapping it who is not dodging is moved out sideways, towards the nearer side, by at most 0.2 m, and stops moving into it. It is 1.6 m high: a jump clears it.
  - A flying shot touching it stops: bullets, arrows, lobs and thrown weapons are gone; bombs, missiles and holes burst; a rolling shot turns round; a boomerang starts its way back.
  - Swings, blasts, sprays and pulses don't reach past it (section 1.6).
  - It is breakable (`brk` 3).

**Dark Energy** (the row is in section 2.3) uses three columns built here: `maxHits` 4 (the orb is gone after its fourth fighter), `brk` 1 (any swing pops it) and `noSelf` (it passes through its caster and teammates).

| id | Weapon (key) | What it does | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|---|
| 18 | Mouse Trap (w010) | A visible floor trap: it snaps on the first fighter to touch it and roots them for 0.75 s. A swing breaks it. | 5/0/8 | 4, 0.06 + 0.0008, 80° | 3 | mine, arms in 30, life 900, size 0.5 | thrown 6, root 45, breaks in 1 | fist range |
| 27 | Lota (w020) | It bonks whoever it lands on and leaves a 2.6 m puddle for 5 s: anyone running across it slips. | 7/0/12 | 5, 0.09 + 0.0015, 45° | 3 | bomb 0.18 / 0.16 up, gravity 0.01, fuse 90, size 0.4, blast 0.8 | thrown 6, slip 30, bursts on touch, bursts on landing, leaves zone 2.6 × 0.4, 300 f | 2–6.5 m |
| 32 | Matka (w025) | It shatters where it lands (a small burst) and leaves a clay wall 1.6 m high for 4 s that fighters must jump and shots can't pass. Three swings break it. | 12/0/18 | 10, 0.16 + 0.0022, 60°, +4 | 3 | bomb 0.16 / 0.18 up, gravity 0.01, fuse 90, size 0.5, blast 1.8 | bursts on touch, bursts on landing, breaks in 3, leaves wall 1.2 × 1.6, 240 f, sweet: direct | 2.5–6.5 m |
| 74 | Toxic Goo Launcher (w073) | A blob that paints a 2.4 m patch for 5 s. Standing in it keeps you poisoned; the launcher's owner too. | 8/0/16 | 2, soft | 4 | bomb 0.24 / 0.12 up, gravity 0.01, fuse 90, size 0.4, blast 0.8 | poison 90 (a tick every 15 f), bursts on touch, bursts on landing, leaves zone 2.4 × 0.5, 300 f | 2–8 m |
| 75 | Stink Cloud Capsule (w074) | Opens into a 4 m cloud for 6 s. Fighters inside are hidden from every screen, from the CPU and from homing shots, and move slowly while they cough. | 8/0/14 | none | 3 | bomb 0.16 / 0.18 up, gravity 0.01, fuse 60, size 0.4 | thrown 6, slow 10, opens on landing, leaves cloud 4 × 3, 360 f | 2.5–6.5 m |
| 76 | Venom Trap (w075) | A visible, breakable trap that poisons whoever touches it for 5 s. | 6/0/10 | 2, soft | 3 | mine, arms in 40, life 900, size 0.5 | thrown 6, poison 300, breaks in 1 | fist range |
| 89 | Banana Peel (w088) | Whoever steps on it slips: stunned for half a second and sliding the way they faced, off the edge if it is near. A swing breaks it. | 4/0/6 | 1, soft | 5 | mine, arms in 20, life 900, size 0.4 | thrown 6, slip 30, breaks in 1 (section 12) | fist range |

### 2.9 Machines and position tricks (batch 5)

**Chicken.** A `roll` with `rehit`: every 30 frames of its run it forgets who it has hit. It does not rest at the end: it is gone.

**Anvil** (`drop`). Its mark is over the nearest opponent in front of the wielder (at most 8 m ahead and 9 m up or down, not a teammate, not hidden in a cloud), where they will be 40 frames on at the speed they have now, but 2 to 6 m ahead; with nobody to aim at it is 4 m ahead (section 12). The prop appears over its mark, kept at least 2 m inside the side blast lines, and 9 m above the wielder's feet or 2 m under the top blast line, whichever is lower (the rule `PlaceBox` uses), so it is never deleted as it appears. It hangs for 45 frames (counted down in `Prop.Timer`); the client draws its shadow on the first platform below. Then it falls under its own gravity, hitting each fighter it passes through once and popping them upwards, and is gone when it lands. It can hit its owner.

**Sonic Speaker.** It stands where it was put, facing the way its owner faced (kept in `Prop.Supply` as +1 or -1), for 480 frames.

- All speakers pulse together, on the match clock: on every frame where `state.Frame % 45 == 0`. A pulse hits every undodging fighter, other than its owner and teammates, whose hurtbox touches the box 6 m long and 2 m high in front of it, launched the way it faces.
- A fighter takes at most one speaker pulse a frame (from the speaker in the lowest prop slot). So two speakers can never take turns on one fighter: nobody is shoved more than once every 45 frames.
- It does not pulse at a fighter who is in the air with no platform anywhere below them: it can't keep a recovering fighter out.
- One speaker per owner: placing a second removes the first. It is breakable (`brk` 2).

**Portal Disc** (`gate`). Each use opens a gate at the wielder's feet, if that point passes `SafeSpot` (section 1.6). If it doesn't, the use fizzles. So a gate can't be hung over the pit or beside the blast line. A gate floats where it was opened and lasts 600 frames.

- Two gates with the same owner are a pair. A gate with no partner does nothing. If the owner opens a third, their oldest closes.
- An undodging fighter touching a gate that hasn't marked it comes out at the partner: its position becomes the partner's, its speed is set to zero, and its air jump, air dodge and recovery are given back. Both gates then mark that fighter (a bit in `Prop.HitMask`), and each gate clears its mark once the fighter no longer touches it. So nobody bounces between gates.
- Any fighter can use them. Shots don't.

**Decoy Crate** (`decoy`). The prop falls and rests like a Prop Box and is the same size. The weapon it pretends to hold is the last real box's (`LastBoxWeapon`, or the Belan if none has dropped), kept in `Prop.Supply`. The first fighter, other than its owner and teammates, who touches it while able to pick up (active, not attacking, not dodging) takes the row's hit and the decoy is gone. It lasts 20 s. The CPU walks to a decoy that isn't its own as it walks to a box.

**Crate Detonator** (`detonate`). On its startup frame:

- if the wielder owns any `decoy` prop, every one of them bursts with the Detonator's numbers (a 3 m blast, hitting its owner too if they stand in it), and the press spends 1;
- otherwise it puts down a rigged crate: a `decoy` that does not spring on touch. That spends 1 too. Six presses is three crates and three bursts.

**Teleporter** (`blink`). On the startup frame the game looks for a landing spot: the point `d` metres ahead for `d` = 5, 4, 3, 2, on a platform top within 6 m of the wielder's height (the nearest in height; the higher on a tie). With Up held it looks first straight above, then ahead, and only at tops more than 0.5 m above the feet. Up + Heavy is still the recovery, so the upward blink is Up + Light.

- Found: the wielder is put there, standing, with speed zero. Standing on a platform gives back the air jump, air dodge and recovery, as any landing does. A root ends. It spends 1.
- Not found: it fizzles.
- It works in the air and off the stage, so it is also a recovery.

**Swap Ray.** A slow `bullet` with the `swap` effect.

| id | Weapon (key) | What it does | S/A/R | Hit | Supply | Delivery | Extras | CPU |
|---|---|---|---|---|---|---|---|---|
| 44 | Chicken (w038) | A wind-up chicken runs along the ground for 2.5 s, pecking everyone it touches, each of them again every half second. A swing breaks it. | 12/0/14 | 5, 0.09 + 0.0012, 70°, +2 | 2 | roll 0.16, life 150, size 0.6 | hits again every 30 f, breaks in 1 | 2–10 m |
| 62 | Anvil (w058) | Marks the nearest opponent in front, where they are heading (2 to 6 m ahead; 4 m with nobody there); an anvil hangs 9 m above the mark for 0.75 s with its shadow on the ground, then falls through everyone under it and pops them upwards. | 14/0/20 | 24, 0.23 + 0.003, 80°, +8 | 2 | drop, gravity 0.02, hangs 45, size 0.8 | thrown 12, carry 80%, pierces | 3–5 m |
| 87 | Decoy Crate (w086) | Looks exactly like a Prop Box to everyone but its owner. The first other fighter to touch it is punched skywards. Two swings pop it. | 10/0/14 | 14, 0.22 + 0.0026, 75°, +6 | 2 | decoy, life 1200, size 0.8 | springs on touch, breaks in 2 (section 12) | fist range |
| 88 | Crate Detonator (w087) | With none of your rigged crates on the stage, a press puts one down (it looks like a Prop Box and does nothing when touched; two swings pop it). With any out, a press bursts them all, and your Decoy Crates too. | 4/0/10 | 16, 0.2 + 0.0027, 60°, +4 | 6 | decoy, life 1200, size 0.8, blast 3 | thrown 6, breaks in 2 (section 12) | fist range |
| 92 | Teleporter (w091) | Blinks the wielder 5 m the way they face onto a platform (Up + Light: onto a platform above). Never into thin air. | 6/0/10 | none | 3 | blink 5 m | thrown 6 | throws it |
| 94 | Sonic Speaker (w093) | A speaker that stands for 8 s and shoves everyone but its owner's side in the 6 m in front of it, every 0.75 s on the match clock. One per owner. Two swings break it. | 10/0/14 | 3, 0.16 + 0.0006, 25° | 2 | speaker, life 480, size 0.8 | pulses every 45 f, breaks in 2, reach 6 | fist range |
| 96 | Portal Disc (w095) | Each use opens a gate where the wielder stands, if that spot is safe. Two gates link for 10 s: any fighter touching one comes out of the other, standing still. | 8/0/12 | none | 2 | gate, life 600, size 1 | thrown 6 | fist range |
| 98 | Swap Ray (w097) | After a 20-frame warning, a slow bolt. The fighter it hits and the shooter change places, if both spots are safe. | 20/0/20 | 4, soft | 3 | bullet 0.45, life 30, size 0.5 | swap | 3–11 m |

## 3. New state

### 3.1 `Fighter`: eight fields

Added at the end of `Fighter`, after `HitCount`, in this order. The same order is used in `Fighter.AddTo` (C#) and in `copy_from()` and `add_to()` (GDScript). All are ints; `Fighter` stays a plain value type. They are added once, in batch 1 step B, and stay 0 until batch 3 gives them behaviour, so the checksum and the STATE frame change only once.

| # | Field | Meaning |
|---|---|---|
| 1 | `SlowFrames` | Frames of slow left; also the chill that builds towards a freeze |
| 2 | `RootFrames` | Frames rooted |
| 3 | `DotFrames` | Frames of poison or burn left |
| 4 | `DotBy` | 1 + the fighter credited with it, or 0 |
| 5 | `DotWeapon` | The weapon id it is recorded under |
| 6 | `MarkFrames` | Frames marked |
| 7 | `StunImmune` | Frames in which a stun, slip, root or freeze can't take hold |
| 8 | `StunWeapon` | The weapon whose hold (root, stun, slip, freeze) the fighter is in now, or 0 |

`Spawn` clears all eight, and so does `CheckBlastZone` when the fighter is knocked out.

Everything else reuses what exists:

- A freeze, a shock and a slip are `FighterAction.Stunned` with `HitstunFrames`. The client tells them apart by `StunWeapon`, which no later hit or tick overwrites. (`LastHitWeapon` could not do this: a soft tick during a freeze would change it.)
- Guards, blinks, sprays, cycling and hidden fighters need no state: they follow from the move, the frame, the supply and the props.
- Teams need no state: `MatchRules.TeamOf` (section 1.6).
- The three renamed Uzi fields (section 1.4) keep their places.

### 3.2 `Prop`: no new fields, new uses

| Field | New use |
|---|---|
| `Timer` | Frames of life left, counting down, for pad, zone, wall, gate, speaker and decoy (the prop is gone at 0). Hang frames left (drop). Floor bounces done (lob). Unchanged: fuse (bomb, hole), arming (mine, trap), boomerang leg |
| `Age` | Counts up every frame, as today. It ends bullets, arrows, lobs, rolls, missiles, mines and traps at the row's `life`, and times a Chicken's rehit |
| `Hits` | Floor bounces done (a bomb that bounces); fighters hit, for `maxHits` |
| `HitMask` | Fighters a gate has marked |
| `Supply` | A speaker's facing (+1, -1); the weapon a decoy pretends to hold; the supply a flung crate or weapon carries |
| `IgnoreFighter` | On a shot: swings a breakable prop has taken |
| `IgnoreTimer` | On a shot: screen-edge bounces done |

Where a placed prop's life starts: a pad at the row's `fuse` (600 for the Trampoline, as today) or `zoneLife` when it was left by a burst; a zone and a wall at `zoneLife`; a speaker, gate and decoy at `life`.

**One way to change what a prop is.** A prop changes role in four places: a burst leaves a zone, wall or pad; a swing flings a box; a shot comes to rest; a boomerang is caught. Reused fields make a stale value dangerous (a wall starting with the bomb's bounce count; a rested weapon that fighter 3 can never pick up because `IgnoreFighter` held 4 swings). So:

- `Become(ref p, flight, row)` in both sims writes **every** `Prop` field explicitly, and is the only way a prop changes kind or flight;
- `Rest` also clears `IgnoreFighter` and `IgnoreTimer` (they are always 0 on today's shots, so step A's goldens don't move);
- `PropBox` reads its width and height from the row for zones, walls and clouds (`zoneW` × `zoneH`); every other prop keeps today's square.

**The table's size.** `state.Props` has `Weapons.PropSlots(fighters)` entries (section 1.6): 24 to 64. The checksum, `CopyFrom` and `GameStateCodec` use the array's length. Before step B the GDScript cost is measured: 2,000 frames of advance + checksum + `copy_from` with four fighters, at 16 props and at 32. The two figures go into section 9. If a duel's frame costs over 25% more, the per-fighter term drops from 4 to 2 for matches of up to four fighters.

### 3.3 `GameState`: the weapon record, reshaped

`WeaponStats` today is `fighters × weapons × 3` ints. At 108 rows that would be 324 ints a fighter, hashed in every checksum, which is slow in GDScript. It becomes six slots and a total per fighter (section 4.3): 25 ints a fighter, fewer than the 39 of today. It stays where it is in `Checksum()` and `CopyFrom()`. `GameStateCodec` in `src/Rangbaazi.Net` sizes it from `state.WeaponStats.Length`.

During batch 1 step A the record keeps today's shape, sized by its own constant `Weapons.RecordWeapons` = 13 (in `GameState`, `GameStateCodec`, the GDScript `GameState` and the tests), because `Weapons.Count` is already 108 then.

No other `GameState` field is added.

### 3.4 The STATE frame (arena protocol 5)

The whole new layout goes in at once, in batch 1 step B, so protocol 5 means one layout. Fields that have no behaviour yet are sent as 0.

- **Each fighter: 34 bytes** (was 31).
  - Byte 30, the main weapon: 0 to 100, or **127** for an all-rounder (was 15, which is now the Plank), +128 for a master.
  - Byte 31, new, status bits: 1 slowed, 2 rooted, 4 poisoned or burning, 8 marked, 16 stun-immune, 32 hidden in a cloud, 64 guarding.
  - Byte 32, new: `DotWeapon`.
  - Byte 33, new: `StunWeapon`.
- **Each prop: 17 bytes** (was 13).
  - Bytes 12 and 13: its timer as a u16 (was one byte capped at 255, too small for a zone's 300 frames or a gate's 600).
  - Byte 14, new: its owner (255 for none). The client needs it to mark a player's own decoys and gates.
  - Byte 15, new: its supply as a signed byte (a speaker's facing, a decoy's pretended weapon).
  - Byte 16, new: its wear, the swings a breakable prop has taken, so a wall can be drawn cracking.
- A prop's age is not sent. The one thing drawn from it, a speaker's pulse, runs on the match clock, and the frame number is in the header.
- The client fills the `Fighter` it draws from: a status bit sets that status's frames to 1.
- Only live props are sent, as today. A full frame is at most 12 + 16 × 34 + 1 + 64 × 17 = 1,645 bytes, up from 717; a typical busy room sends 20 to 30 props, about 1,000 bytes.
- Teams are not in STATE: a fighter's team never changes during a match, so it belongs with the roster the room already sends (the rooms work).

### 3.5 Versions

- `Simulation.SIM_VERSION` (and the C# constant): **8**, set once in batch 1 step B.
- `ArenaLimits.ProtocolVersion` and `room_session.gd` `PROTOCOL_VERSION`: **5**, set once in batch 1 step B, together with the whole layout of section 3.4. No later batch changes the layout.
- `NetProtocol.VERSION` (duels) stays 1: no message changes, and `SIM_VERSION` is already in the rules hash the two peers compare.

## 4. Ids, crates, the record and nicknames

### 4.1 Ids

- Ids 1 to 12 are the built weapons, unchanged.
- Ids 13 to 100 are the other 88 in pack-key order (w001 is 13, w100 is 100).
- Ids 101 to 107 are the hidden rows.
- `Weapons.Count` is 108 (rows). `Weapons.CrateList` is the built rows with `crate` set, in id order: 100 when the job is done. `Weapons.RecordWeapons` (13) is a separate constant that sizes the record during batch 1 step A (section 3.3).
- **Move ids.** `move` is a column. Rows 1 to 12 keep today's ids (7 to 13 and 15 to 19; `WeaponThrow` stays 14). Every other row's is 7 + its id (20 to 114). Both fit a byte, as STATE needs. The new ones have no names in the `MoveId` enum: the engine only reads a weapon move through `RowOfMove`.

| id | Key | Name | Move id | Batch |
|---|---|---|---|---|
| 1 | w011 | Belan | 7 | built |
| 2 | w031 | Baseball Bat | 8 | built |
| 3 | w006 | Bowling Ball | 10 | built |
| 4 | w061 | Shotgun | 9 | built |
| 5 | w005 | Uzi | 11 | built |
| 6 | w008 | Cherry Bombs | 12 | built |
| 7 | w054 | Bow | 13 | built |
| 8 | w055 | Boomerang | 15 | built |
| 9 | w007 | Trampoline | 16 | built |
| 10 | w070 | Mines | 17 | built |
| 11 | w068 | Homing Missile | 18 | built |
| 12 | w098 | Black-Hole Bomb | 19 | built |
| 13 | w001 | Boxing Gloves | 20 | 2 |
| 14 | w002 | Slingshot | 21 | 2 |
| 15 | w003 | Plank | 22 | 2 |
| 16 | w004 | Ninja Stars | 23 | 2 |
| 17 | w009 | Stick Grenades | 24 | 2 |
| 18 | w010 | Mouse Trap | 25 | 4 |
| 19 | w012 | Thali | 26 | 4 |
| 20 | w013 | Tawa | 27 | 4 |
| 21 | w014 | Chimta | 28 | 4 |
| 22 | w015 | Chakla | 29 | 3 |
| 23 | w016 | Chappal | 30 | 2 |
| 24 | w017 | Jhaadu | 31 | 4 |
| 25 | w018 | Balti | 32 | 3 |
| 26 | w019 | Bathroom Mug | 33 | 3 |
| 27 | w020 | Lota | 34 | 4 |
| 28 | w021 | Tiffin Stack | 35 | 3 |
| 29 | w022 | Chai Kettle | 36 | 3 |
| 30 | w023 | Steel Tumbler | 37 | 3 |
| 31 | w024 | Parat | 38 | 4 |
| 32 | w025 | Matka | 39 | 4 |
| 33 | w026 | Gas Cylinder | 40 | 4 |
| 34 | w027 | Pressure Cooker | 41 | 2 |
| 35 | w028 | Sil-Batta | 42 | 2 |
| 36 | w029 | Pankha | 43 | 3 |
| 37 | w030 | Charpai | 44 | 4 |
| 38 | w032 | Cricket Bat | 45 | 2 |
| 39 | w033 | Hockey Stick | 46 | 4 |
| 40 | w034 | Cricket Ball | 47 | 3 |
| 41 | w035 | Badminton Racket | 48 | 2 |
| 42 | w036 | Pitchfork | 49 | 2 |
| 43 | w037 | Rake | 50 | 4 |
| 44 | w038 | Chicken | 51 | 5 |
| 45 | w039 | Trout | 52 | 2 |
| 46 | w040 | Net | 53 | 3 |
| 47 | w041 | Lathi | 54 | 4 |
| 48 | w042 | Gada | 55 | 2 |
| 49 | w043 | Talwar | 56 | 2 |
| 50 | w044 | Chakram | 57 | 3 |
| 51 | w045 | Katar | 58 | 2 |
| 52 | w046 | Katana | 59 | 2 |
| 53 | w047 | Broadsword | 60 | 2 |
| 54 | w048 | Spear | 61 | 4 |
| 55 | w049 | Trident | 62 | 2 |
| 56 | w050 | Cutlass | 63 | 4 |
| 57 | w051 | Crossbow | 64 | 2 |
| 58 | w052 | Blow Darts | 65 | 3 |
| 59 | w053 | Sniper Rifle | 66 | 2 |
| 60 | w056 | Chainsaw | 67 | 3 |
| 61 | w057 | Sledgehammer | 68 | 2 |
| 62 | w058 | Anvil | 69 | 5 |
| 63 | w059 | Wrench | 70 | 4 |
| 64 | w060 | Magnet Gun | 71 | 3 |
| 65 | w062 | .357 Magnum | 72 | 2 |
| 66 | w063 | Tommy Gun | 73 | 2 |
| 67 | w064 | AK-47 | 74 | 2 |
| 68 | w065 | Burst Rifle | 75 | 2 |
| 69 | w066 | RPG | 76 | 2 |
| 70 | w067 | Grenade Launcher | 77 | 3 |
| 71 | w069 | Cannon | 78 | 3 |
| 72 | w071 | Health Leech | 79 | 3 |
| 73 | w072 | Venom Dart Launcher | 80 | 3 |
| 74 | w073 | Toxic Goo Launcher | 81 | 4 |
| 75 | w074 | Stink Cloud Capsule | 82 | 4 |
| 76 | w075 | Venom Trap | 83 | 4 |
| 77 | w076 | Ray Gun | 84 | 2 |
| 78 | w077 | Freeze Ray | 85 | 3 |
| 79 | w078 | Taser | 86 | 3 |
| 80 | w079 | Minigun | 87 | 2 |
| 81 | w080 | Flamethrower | 88 | 3 |
| 82 | w081 | Pichkari | 89 | 3 |
| 83 | w082 | Colour Powder Pouch | 90 | 3 |
| 84 | w083 | Dhol | 91 | 2 |
| 85 | w084 | Snowballs | 92 | 3 |
| 86 | w085 | Confetti Cannon | 93 | 2 |
| 87 | w086 | Decoy Crate | 94 | 5 |
| 88 | w087 | Crate Detonator | 95 | 5 |
| 89 | w088 | Banana Peel | 96 | 4 |
| 90 | w089 | Glue Gun | 97 | 4 |
| 91 | w090 | Spring Glove Launcher | 98 | 2 |
| 92 | w091 | Teleporter | 99 | 5 |
| 93 | w092 | Dark Energy | 100 | 4 |
| 94 | w093 | Sonic Speaker | 101 | 5 |
| 95 | w094 | Gravity Gun | 102 | 3 |
| 96 | w095 | Portal Disc | 103 | 5 |
| 97 | w096 | Jugaad Gauntlet | 104 | 4 |
| 98 | w097 | Swap Ray | 105 | 5 |
| 99 | w099 | Thunder Hammer | 106 | 4 |
| 100 | w100 | Rubber Chicken Launcher | 107 | 3 |
| 101 | hidden | Gauntlet pull | 108 | 4 |
| 102 | hidden | Gauntlet punch | 109 | 4 |
| 103 | hidden | Gauntlet bolt | 110 | 4 |
| 104 | hidden | Thunder wave | 111 | 4 |
| 105 | hidden | Thrown gas cylinder | 112 | 4 |
| 106 | hidden | Thrown charpai | 113 | 4 |
| 107 | hidden | Thrown spear | 114 | 4 |

### 4.2 Crates

The rule is "any weapon but the last box's". It is written over `CrateList`, so hidden and unbuilt rows are never picked.

- **Step A of batch 1: equal odds**, as today. First box: `CrateList[Random.Range(0, n)]`. Afterwards: `k = Random.Range(0, n - 1)`, plus 1 if `k` is at or past the last box's place in the list. With the twelve built weapons as the list this draws the same numbers from the random stream as today's code and picks the same weapons.
- **From step B: by weight.** `W` is the sum of the weights in the list, leaving out the last box's weapon. `r = Random.Range(0, W)`; walk the list in id order, skipping the last box's weapon, taking each row's weight off `r` until it goes below 0. One draw a box, as before.

**Weights.** A row's `weight` is 2. Twelve **utility rows** have weight 1: Lota, Pankha, Net, Magnet Gun, Stink Cloud Capsule, Colour Powder Pouch, Banana Peel, Glue Gun, Teleporter, Gravity Gun, Portal Disc, Swap Ray. They move people or set them up and do little or no damage themselves, so they come half as often: about one crate in sixteen is one of them.

With 100 weapons a crate holds any one of them about 1 time in 100. A minute of a full room drops about 17 crates, so most matches show 15 or more different weapons. That is the point of the request.

**What a crate should be worth** (checked by measurement, section 7):

- a damage row: roughly 20 to 75 damage for a crate in a Hard CPU's hands, as the twelve do;
- a utility row: the CPU uses it up rather than throwing it away, it does at least 3 "effect events" a crate (a status put on someone else, or someone else moved by it: counted by the measuring script from the state, not by the sim), and it does not do more of them to its wielder than to others.

### 4.3 The record (`WeaponStats`)

Each fighter has 6 slots of 4 ints, `[weapon + 1, damage, KOs, sweet spots]`, then 1 int: all the damage that fighter has done to others, with anything. A slot whose first int is 0 is empty.

- **A hit** on someone else, with credited weapon `w` (0 for fists): add the damage to the total. Use the slot holding `w`. If there is none, take the first empty slot. If none is empty, reuse the slot with the least damage (the lowest slot on a tie), cleared. Add the damage; add 1 sweet spot if it was one.
- **A KO** credited to weapon `w`: add 1 to the slot holding `w`, if there still is one.
- **The main weapon** (`Simulation.MainWeapon`): the slot with the most damage (the lower weapon id on a tie, as today), if that is at least 40% of the fighter's total; otherwise an all-rounder. A master has 3 sweet spots and a KO in that slot. As today.
- A fighter who really used seven weapons loses the least of them from the slots. A main weapon is never the one lost: a slot holding 40% of the total can't be the smallest of six. And because the 40% is taken of the total, not of the six slots, losing a slot doesn't make another weapon look bigger than it was.
- `Simulation.WeaponStat(state, fighter, weapon, stat)` reads a value (0 if the weapon has no slot), for tests and for the measuring script.

### 4.4 Nicknames

`Nicknames.WEAPON_WORDS[id]` becomes `WeaponInfo.words(id)`: four words per weapon, generated from the table (section 5). The code a room sends is the weapon id, 127 for an all-rounder, +128 for a master. The pick from the four words and the six titles is unchanged.

## 5. Presentation from the table

Nothing per weapon is hand-written in `client/game` any more. `WeaponInfo` (generated) has, per id: the pack key, the HUD name, the touch verb, the held length, the sweet-spot shout, the borrowed clip, and four nickname words.

| What | Today | From the table |
|---|---|---|
| Sprite | `KEYS` in `tools/cut_weapons.gd` | The tool reads `tools/weapons.json` for id → key and cuts all 100 from the v3 atlas (a v6 or v7 cut wins where one exists). Hidden rows have no sprite; their props draw their `credit` weapon |
| Sound | 12 lines in `tools/import_audio.py` | A loop over the same file: `weapon_<id>.wav` from `audio/weapons/<key>-primary.wav` |
| HUD name | `Weapons.NAMES` | `WeaponInfo.name(id)`: the pack's name |
| Touch verb | `WEAPON_VERBS` | `WeaponInfo.verb(id)` |
| Held length | `WEAPON_LENGTH` | The pack's `drawWidth` / 80 metres, unless the row gives `length` (the twelve keep theirs) |
| Sweet-spot shout | `SWEET_WORDS` | `WeaponInfo.shout(id)`; empty for weapons with no sweet spot |
| Fighter pose | `WEAPON_CLIPS` by move | `WeaponInfo.clip(id)`: `jab`, `neutral_heavy`, `side_heavy` or `down_heavy`, stretched to the row's startup as today. The painter finds the row by `Weapons.row_of_move(f.move)` |
| Nickname words | `WEAPON_WORDS` | `WeaponInfo.words(id)` |

| id | Key | HUD name | Verb | Shout | Clip | Length | Nickname words |
|---|---|---|---|---|---|---|---|
| 1 | w011 | Belan | Bonk | BONK! | neutral_heavy | 0.95 | Belan, Rolling-Pin, Bonk, Chapati |
| 2 | w031 | Baseball Bat | Swing | SIX! | side_heavy | 1.3 | Bat, Home-Run, Sixer, Slugger |
| 3 | w006 | Bowling Ball | Roll | STRIKE! | down_heavy | 0.6 | Bowling, Strike, Gutter, Ten-Pin |
| 4 | w061 | Shotgun | Blast | POINT BLANK! | jab | 1.15 | Shotgun, Boomstick, Pump, Buckshot |
| 5 | w005 | Uzi | Fire | ON TARGET! | jab | 0.75 | Uzi, Trigger, Bullet, Rat-a-tat |
| 6 | w008 | Cherry Bombs | Lob | DIRECT HIT! | neutral_heavy | 0.55 | Pataka, Cherry, Fuse, Firecracker |
| 7 | w054 | Bow | Shoot | LONG SHOT! | jab | 0.95 | Arrow, Bullseye, Longbow, Quiver |
| 8 | w055 | Boomerang | Throw | COMEBACK! | side_heavy | 0.85 | Boomerang, Comeback, Return, Curve |
| 9 | w007 | Trampoline | Place | - | down_heavy | 1 | Trampoline, Bounce, Spring, Jumper |
| 10 | w070 | Mines | Plant | STEPPED ON IT! | down_heavy | 0.6 | Mine, Booby-Trap, Tripwire, Surprise |
| 11 | w068 | Homing Missile | Launch | DIRECT HIT! | jab | 1.25 | Missile, Rocket, Lock-On, Seeker |
| 12 | w098 | Black-Hole Bomb | Lob | - | neutral_heavy | 0.65 | Black-Hole, Gravity, Vortex, Void |
| 13 | w001 | Boxing Gloves | Punch | COUNTER! | jab | 0.575 | Glove, Jab, Uppercut, Knockout |
| 14 | w002 | Slingshot | Sling | BULLSEYE! | jab | 0.875 | Gulel, Pebble, Sling, Catapult |
| 15 | w003 | Plank | Swing | - | side_heavy | 1.3 | Plank, Timber, Two-by-Four, Splinter |
| 16 | w004 | Ninja Stars | Throw | - | jab | 0.575 | Star, Shuriken, Ninja, Flick |
| 17 | w009 | Stick Grenades | Lob | - | neutral_heavy | 0.575 | Grenade, Countdown, Stick-Bomb, Kaboom |
| 18 | w010 | Mouse Trap | Set | - | down_heavy | 0.575 | Mousetrap, Snap, Cheese, Chuha |
| 19 | w012 | Thali | Fling | - | side_heavy | 0.7 | Thali, Plate, Frisbee, Platter |
| 20 | w013 | Tawa | Slap | CLANG! | side_heavy | 0.875 | Tawa, Pan, Clang, Griddle |
| 21 | w014 | Chimta | Pinch | DISARMED! | jab | 0.875 | Chimta, Tongs, Pinch, Snatch |
| 22 | w015 | Chakla | Hurl | DIRECT HIT! | neutral_heavy | 0.875 | Chakla, Board, Roti, Disc |
| 23 | w016 | Chappal | Fling | COMEBACK! | jab | 0.7 | Chappal, Slipper, Flip-Flop, Mummy's |
| 24 | w017 | Jhaadu | Sweep | - | down_heavy | 1.3 | Jhaadu, Broom, Sweep, Dust |
| 25 | w018 | Balti | Bonk | - | neutral_heavy | 0.875 | Balti, Bucket, Drench, Soak |
| 26 | w019 | Bathroom Mug | Chuck | TRICK SHOT! | jab | 0.575 | Mug, Tumble, Bounce, Trick-Shot |
| 27 | w020 | Lota | Spill | - | neutral_heavy | 0.575 | Lota, Puddle, Slip, Spill |
| 28 | w021 | Tiffin Stack | Toss | TRICK SHOT! | neutral_heavy | 0.575 | Tiffin, Dabba, Lunchbox, Stack |
| 29 | w022 | Chai Kettle | Steam | - | jab | 0.875 | Chai, Kettle, Steam, Cutting |
| 30 | w023 | Steel Tumbler | Fling | RICOCHET! | jab | 0.575 | Tumbler, Glass, Ricochet, Steel |
| 31 | w024 | Parat | Guard | - | side_heavy | 0.875 | Parat, Shield, Tray, Wall |
| 32 | w025 | Matka | Lob | DIRECT HIT! | neutral_heavy | 0.875 | Matka, Clay, Pot, Kulhad |
| 33 | w026 | Gas Cylinder | Swing | - | side_heavy | 0.65 | Cylinder, Gas, LPG, Heavy |
| 34 | w027 | Pressure Cooker | Whistle | - | neutral_heavy | 0.775 | Cooker, Whistle, Seeti, Pressure |
| 35 | w028 | Sil-Batta | Smash | - | neutral_heavy | 0.875 | Sil-Batta, Chutney, Grinder, Stone |
| 36 | w029 | Pankha | Blow | - | jab | 0.875 | Pankha, Fan, Breeze, Gust |
| 37 | w030 | Charpai | Shove | - | side_heavy | 1.3 | Charpai, Cot, Khaat, Shove |
| 38 | w032 | Cricket Bat | Swing | LOFTED! | side_heavy | 1 | Willow, Cover-Drive, Lofted, Century |
| 39 | w033 | Hockey Stick | Scoop | - | down_heavy | 1.3 | Hockey, Dribble, Scoop, Penalty |
| 40 | w034 | Cricket Ball | Bowl | HOWZAT! | side_heavy | 0.575 | Yorker, Bouncer, Googly, Howzat |
| 41 | w035 | Badminton Racket | Smash | - | jab | 0.875 | Racket, Smash, Shuttle, Rally |
| 42 | w036 | Pitchfork | Poke | - | jab | 1.3 | Pitchfork, Prong, Hay, Farm |
| 43 | w037 | Rake | Rake | - | down_heavy | 1.3 | Rake, Garden, Haul, Scrape |
| 44 | w038 | Chicken | Wind Up | - | down_heavy | 0.875 | Chicken, Murgi, Peck, Wind-Up |
| 45 | w039 | Trout | Slap | - | side_heavy | 0.875 | Trout, Fish, Machhli, Slap |
| 46 | w040 | Net | Cast | - | neutral_heavy | 0.875 | Net, Jaal, Tangle, Catch |
| 47 | w041 | Lathi | Spin | - | side_heavy | 1.3 | Lathi, Staff, Danda, Twirl |
| 48 | w042 | Gada | Slam | - | neutral_heavy | 1.3 | Gada, Mace, Pehelwan, Akhada |
| 49 | w043 | Talwar | Slash | - | side_heavy | 1.3 | Talwar, Sabre, Slash, Blade |
| 50 | w044 | Chakram | Spin | OFF THE WALL! | side_heavy | 0.575 | Chakram, Ring, Spin, Halo |
| 51 | w045 | Katar | Lunge | BACKSTAB! | jab | 0.575 | Katar, Dagger, Lunge, Punch-Blade |
| 52 | w046 | Katana | Slash | CLEAN CUT! | side_heavy | 1.3 | Katana, Quick-Draw, Dash, Edge |
| 53 | w047 | Broadsword | Cleave | - | side_heavy | 1.3 | Broadsword, Cleave, Knight, Claymore |
| 54 | w048 | Spear | Thrust | SKEWERED! | jab | 1.3 | Spear, Bhala, Javelin, Skewer |
| 55 | w049 | Trident | Poke | PRONGED! | jab | 1.3 | Trident, Fork, Three-Point, Tide |
| 56 | w050 | Cutlass | Slash | - | side_heavy | 1.3 | Cutlass, Pirate, Parry, Swash |
| 57 | w051 | Crossbow | Shoot | LONG SHOT! | jab | 1.15 | Crossbow, Bolt, Quarrel, Reload |
| 58 | w052 | Blow Darts | Blow | - | jab | 0.575 | Dart, Blowpipe, Phoonk, Whisper |
| 59 | w053 | Sniper Rifle | Snipe | LONG SHOT! | jab | 1.15 | Sniper, Scope, Long-Range, One-Shot |
| 60 | w056 | Chainsaw | Saw | - | jab | 0.875 | Chainsaw, Buzz, Sawdust, Rev |
| 61 | w057 | Sledgehammer | Smash | - | neutral_heavy | 1.3 | Sledge, Hammer, Hathoda, Demolition |
| 62 | w058 | Anvil | Drop | - | neutral_heavy | 0.875 | Anvil, Drop, Thud, Heavy-Metal |
| 63 | w059 | Wrench | Whack | - | side_heavy | 0.775 | Wrench, Spanner, Pana, Mechanic |
| 64 | w060 | Magnet Gun | Pull | - | jab | 1.15 | Magnet, Chumbak, Pull, Polarity |
| 65 | w062 | .357 Magnum | Fire | SKEET! | jab | 1.15 | Magnum, Revolver, Six-Shooter, Hand-Cannon |
| 66 | w063 | Tommy Gun | Fire | - | jab | 1.15 | Tommy, Drum, Chatter, Gangster |
| 67 | w064 | AK-47 | Fire | ON TARGET! | jab | 1.075 | AK, Rifle, Recoil, Full-Auto |
| 68 | w065 | Burst Rifle | Burst | TRIPLE! | jab | 1.15 | Burst, Triple, Tap, Marksman |
| 69 | w066 | RPG | Launch | DIRECT HIT! | jab | 1.15 | RPG, Bazooka, Warhead, Launcher |
| 70 | w067 | Grenade Launcher | Thump | DIRECT HIT! | jab | 1.05 | Thumper, Bloop, Lobber, Popper |
| 71 | w069 | Cannon | Fire | - | jab | 1.15 | Cannon, Tope, Cannonball, Broadside |
| 72 | w071 | Health Leech | Drain | - | jab | 1.15 | Leech, Jonk, Drain, Vampire |
| 73 | w072 | Venom Dart Launcher | Fire | - | jab | 1.15 | Venom, Zeher, Sting, Viper |
| 74 | w073 | Toxic Goo Launcher | Splat | - | jab | 1.15 | Goo, Slime, Keechad, Toxic |
| 75 | w074 | Stink Cloud Capsule | Toss | - | neutral_heavy | 0.575 | Stink, Badboo, Smog, Skunk |
| 76 | w075 | Venom Trap | Set | - | down_heavy | 0.575 | Fang, Snare, Cobra, Ambush |
| 77 | w076 | Ray Gun | Zap | - | jab | 1.15 | Ray, Laser, Zap, Pew-Pew |
| 78 | w077 | Freeze Ray | Freeze | - | jab | 1.15 | Freeze, Baraf, Ice, Chill |
| 79 | w078 | Taser | Zap | - | jab | 0.875 | Taser, Shock, Current, Jhatka |
| 80 | w079 | Minigun | Spin | ON TARGET! | jab | 1.15 | Minigun, Gatling, Spin-Up, Brrrt |
| 81 | w080 | Flamethrower | Burn | - | jab | 1.15 | Flame, Aag, Tandoor, Roast |
| 82 | w081 | Pichkari | Spray | - | jab | 1 | Pichkari, Holi, Squirt, Splash |
| 83 | w082 | Colour Powder Pouch | Puff | - | jab | 0.575 | Gulal, Rang, Colour, Rangeela |
| 84 | w083 | Dhol | Beat | INTERRUPTED! | down_heavy | 0.875 | Dhol, Beat, Bhangra, Thump |
| 85 | w084 | Snowballs | Throw | - | neutral_heavy | 0.575 | Snowball, Frosty, Snowman, Blizzard |
| 86 | w085 | Confetti Cannon | Pop | - | jab | 1.15 | Confetti, Party, Shaadi, Celebration |
| 87 | w086 | Decoy Crate | Place | - | down_heavy | 0.875 | Decoy, Fake, Dhokha, Prank |
| 88 | w087 | Crate Detonator | Detonate | - | jab | 0.575 | Detonator, Button, Remote, Plunger |
| 89 | w088 | Banana Peel | Drop | - | down_heavy | 0.575 | Banana, Kela, Peel, Skid |
| 90 | w089 | Glue Gun | Squirt | - | jab | 1.15 | Glue, Gond, Sticky, Chipku |
| 91 | w090 | Spring Glove Launcher | Punch | - | jab | 1.15 | Spring-Glove, Boing, Sucker-Punch, Jack-in-the-Box |
| 92 | w091 | Teleporter | Blink | - | jab | 0.575 | Teleport, Blink, Gayab, Warp |
| 93 | w092 | Dark Energy | Cast | - | neutral_heavy | 0.575 | Dark-Energy, Orb, Shadow, Eclipse |
| 94 | w093 | Sonic Speaker | Place | - | down_heavy | 0.875 | Speaker, Bass, DJ, Loudspeaker |
| 95 | w094 | Gravity Gun | Lift | - | jab | 1.15 | Anti-Gravity, Float, Zero-G, Levitate |
| 96 | w095 | Portal Disc | Open | - | jab | 0.575 | Portal, Gate, Shortcut, Wormhole |
| 97 | w096 | Jugaad Gauntlet | Jugaad | - | jab | 0.875 | Gauntlet, Jugaadu, Gadget, Tinker |
| 98 | w097 | Swap Ray | Swap | - | jab | 1.15 | Swap, Adla-Badli, Switch, Trade |
| 99 | w099 | Thunder Hammer | Smash | - | neutral_heavy | 1.3 | Thunder, Bijli, Storm, Shockwave |
| 100 | w100 | Rubber Chicken Launcher | Launch | - | jab | 1.15 | Rubber-Chicken, Squeak, Cluck, Honk |

- **Names.** The HUD names are the pack's. Four are real firearm names (Uzi, already shipped; .357 Magnum; Tommy Gun; AK-47). The project's originality rule covers film titles, dialogue, songs, likenesses and deities, not these, so they are kept; changing one is one cell in `tools/weapons.json`.
- **Words.** All nickname words are plain words for the object, in English or Hindi. None is a film, a song or a person.
- **Drawing by archetype, not by weapon** (each archetype in the batch that builds it, as a placeholder at least; the polish is batch 6). `arena_view.gd` draws a shot by its flight, with its weapon's sprite:
  - bullet: a short streak; arrow, lob, thrown, boomerang, roll, missile, drop: the sprite, turned or spun as today's arrow, bomb and ball are;
  - zone: a flat coloured patch (puddle blue, goo green); cloud: a soft opaque cloud in front of the fighters, and the fighters hidden in it are not drawn (a player's own fighter and teammates faintly);
  - wall: a clay block that cracks as it takes swings; speaker and gate: the sprite, with rings on a pulse (the match clock: `frame % 45`) and a swirl on a gate, a pair sharing a colour;
  - drop: a shadow on the platform below while it hangs;
  - decoy: exactly a Prop Box with its pretended weapon; on its owner's screen only, a small mark;
  - a spray: a cone the size of the row's box, coloured by its effect (flame, water, wind, steam, a beam line for `first` sprays);
  - a blast: a muzzle flash the length of the box; the Sniper Rifle's aim line during its startup.
- **Statuses on a fighter:** slowed: a blue tint and slower dust; rooted: a net, or a trap's jaws, by `StunWeapon`; poison: green bubbles; burning: small flames; marked: colour splats; frozen, shocked, slipped: an ice block, sparks, a spin, chosen by `StunWeapon`; guarding: the weapon held out front.
- **Sounds.** A weapon's sound plays when its move starts, as today. Repeating weapons play it on each new shot or tick (the director counts each weapon's bullets, as it does for the Uzi). `boom` plays for every burst.
- **Build size.** 88 more sprites at about 45 KB and 88 more sounds add about 5 MB to the web build and the APK.

## 6. Test plan

Every test below is a C# test in `tests/Rangbaazi.Sim.Tests` unless it says otherwise. The GDScript port is proved by the goldens, as today, and by the GDScript tests named here.

**The table**

- The twelve built rows equal today's constants (one test comparing each row's `MoveData` and numbers with literal expected values copied from v0.3's code before it is deleted).
- Every id fits a byte; every `move` is unique; `RowOfMove` round-trips; `credit` of a crate row is itself; `CrateList` has no hidden row.
- No two crate rows are equal in every gameplay column.
- **Every weapon, one loop:** for each built crate row, a fighter armed with it facing a target at the row's `cpuMax` (or 1 m) presses attack until the weapon is empty or 600 frames pass. Nothing throws, the supply goes down, and the target took damage, a status, or was moved, or a prop exists (per-row expectation derived from the columns).
- `weapon_table.json` matches (C# writes; `client/tests/test_weapon_table.gd` checks the GDScript table and `SPREADS` against it).
- The generator: its rounding self-test (-0.15 is -9830, -0.0366 is -2398), and one bad file per validation rule of section 1.1, each refused.

**One behaviour test per archetype**

| Archetype | Test |
|---|---|
| swing | Hits in its box on its active frames only; `both` hits behind; `spend` hit costs one per fighter hit |
| lunge and carry | The Katana moves about 2 m; a Minigun carrier runs at 60% |
| blast, `first` | The Sniper hits the nearer of two fighters on the line and not the farther; a dodging nearer fighter doesn't shield the farther; a parrying one does; the Shotgun still hits both |
| repeating fire | The Tommy Gun fires every 6 frames while held and stops when released; the Burst Rifle fires 3 on a tap and 3 on a hold; the Minigun stops at 24 |
| pellets and spread | Ninja Stars leave as three props with three upward speeds; the AK's fifth shot of a burst flies higher than its first; the Tommy Gun's 10th and 11th shots are at +0.08 and -0.08, not back at 0 |
| arrow | The Crossbow bolt stops in the first of two fighters; the Bow's arrow goes through both |
| bomb | Stick Grenades don't burst on touch and do at 75 frames; the RPG flies straight past a fighter above it; the Chakla bursts where it lands |
| boomerang | The Chappal returns to the hand with one less |
| bat | The Cricket Bat returns an arrow; the racket returns a bullet for 1 supply and lets an RPG through |
| spray | The Flamethrower ticks every 6 frames while held and spends 1 a tick; the Pichkari moves a grounded fighter 0.09 m a frame and an airborne one 0.066; the Pankha turns an arrow round; the Leech takes Josh off its wielder; `first` sprays touch one fighter; the Magnet Gun's clonk lands only within 1.2 m |
| lob | Snowballs arc and land; a Tiffin toss is three props, each bouncing twice then gone; the Cricket Ball's hit after pitching is sweet; the Chakram comes back off the screen edge once |
| trap | A Mouse Trap is safe while arming, roots the first to touch it, and is gone; a swing breaks it first; a Jhaadu sweep removes it and a Mine without a burst |
| zone | Standing in the goo for 120 frames is exactly 8 damage, and it stops after leaving; the puddle slips a runner and not a walker; a glue patch keeps a fighter slow |
| wall | A fighter walking at a wall doesn't pass; a jump does; a bullet stops; a swing and a Shotgun blast don't reach a fighter behind it; three swings break it and a Sledgehammer breaks it in one |
| pad | A thrown Charpai lies and bounces |
| guard | The Parat blocks a jab from the front, not from behind; a hold lasts at most 90 frames; a block costs 1; the Wrench and the Sledgehammer go through; the Thali blocks while it winds up, then flies |
| defence | The Lathi's spin destroys a bullet; neither the Lathi nor a bat touches a trampoline, a mine, a wall or a hanging anvil |

**Flight flags, table-driven:** for each of the 17 flights, one prop of it meets a bat swing, a Lathi spin, a Pankha gust, a parry, a guard and a wall. The outcome is what the flight's flags say (section 1.3). The ten old flights get today's outcome.

**One frame of hits (section 1.7).** Each of these runs twice, with the two attackers' (or the two defenders') indices swapped, and expects mirrored results:

- two attackers hit one guarding fighter on the same frame, one from the front and one from behind: the front hit is blocked and the back hit lands, either way round;
- a `first` blast with a dodging fighter in front of a standing one hits the standing one;
- a swing whose box holds a trap and a fighter breaks the trap and hits the fighter on the same frame.

**One test per status:** slow halves run speed and runs out; chill freezes at the threshold and not again for 150 frames; root stops walking and jumping and is cut by presses; stun skips knockback; slip slides; poison ticks every 30 frames, never stacks, and gives its owner the KO; burn ticks every 15; mark adds 25%; Guts armor blocks every status and the Wrench gets past it; a respawn clears them all, and so does a KO (nothing is set on a fighter waiting to respawn).

**Holds and movement:**

- a Net, a Taser and a Freeze Ray on a fighter in the air: no hold, the row's knockback, and slow instead;
- a frozen fighter lifted by a Gravity Gun, and a rooted one pushed off a ledge by a Pankha, are free on the frame they leave the ground;
- a rooted fighter with a Katana doesn't dash; a rooted fighter's Up + Heavy does nothing; a slowed fighter firing an Uzi walks at a quarter of run speed;
- a press during a rooted attack still cuts 12 frames.

**Ticks:** a held Flamethrower for 60 frames gives exactly its tick damage plus 4 burn ticks; a fighter in hit-stop on a tick frame doesn't tick; a victim launched by a Sledgehammer and then poisoned by someone else gives the ring-out to the launcher; a fighter who poisoned itself and is knocked out by another gives that other the KO.

**Shoves:** the Confetti Cannon, Spring Glove, Charpai, Parat bash and Sonic Speaker, each on a fighter 3 m inside a ledge at 0 Josh who then jumps and uses the recovery: no KO. The Spring Glove gives the same shove at 0 and at 150 Josh, within 10%.

**Push weapons at a ledge:** a fighter recovering from 4 m outside a ledge against a held Pankha, a held Pichkari, and a Sonic Speaker facing out, gets back each time. Two speakers never shove one fighter twice within 45 frames.

**One test per trick weapon:** Chimta (a tip hit puts the weapon on the floor and its old holder can't take it for 1 s; a close hit doesn't disarm), Rake (the launch is towards the raker; a crate moves in), Hockey Stick (a crate becomes a thrown shot that hits, then lies there as a loose weapon with its supply; one flung off the stage comes back in a box), Gas Cylinder (thrown, it bursts), Spear (thrown, it pierces two), Thunder Hammer (the wave hits beyond the hammer's reach, with its own damage), Jugaad Gauntlet (pull, punch, bolt in order, credited to the gauntlet), Taser, Net, Freeze Ray, Health Leech, Magnet Gun (an armed fighter is dragged, an unarmed one isn't; a crate flies in), Gravity Gun (a fighter rises and drifts away), Stink Cloud (`IsHidden`; a missile ignores a hidden fighter), Matka, Lota, Banana Peel, Glue Gun, Dark Energy (slower than a run; passes through its caster; gone after its fourth fighter; a swing pops it), Chicken (pecks twice in a second), Anvil (hangs 45 frames, then hits under its mark; on the highest platform of the tallest stage it still appears and falls), Sonic Speaker (pulses on the match clock in front only; a second one removes the first; two swings break it), Portal Disc (in one, out the other with speed zero, no bounce back; a third closes the first; over the pit and beside the blast line it fizzles and spends nothing), Decoy Crate (springs on another fighter, never on its owner), Crate Detonator (place, then burst; it bursts the owner's Decoy Crates too), Teleporter (lands on a platform; fizzles over nothing without spending; recovers from off-stage; Up + Light goes up, Up + Heavy is still the recovery), Swap Ray (swaps; fails when either spot is unsafe, including a spot inside a hazard that is warning), Colour Powder.

**Shared rules:**

- placed props: the limit of 4, and of 2 in a 9-fighter match; the oldest vanishes without bursting, a mine included;
- prop slots: with the shot slots full of placed props, another fighter's shot fizzles and spends nothing, a bullet replaces the oldest bullet, and a Bowling Ball that fizzles stays in the hand; boxes still drop and a dead fighter's weapon still comes back;
- `Become`: a bounced Grenade Launcher bomb, a Matka wall, a flung crate and a rested Bowling Ball each have exactly the expected value in every `Prop` field on their first frame;
- the record: a seventh weapon replaces the least; the main weapon and master are as before; a tie goes to the lower weapon id; losing a slot never changes a main weapon that holds 40% or more;
- crates never pick a hidden or unbuilt row, never repeat, reach every built row over a long run, and pick utility rows about half as often;
- teams: with `TeamOf` set, a teammate takes no hit, burst, tick, pulse or trap from a teammate, shots pass through, a pad and a gate still work for them; with no teams the eleven old goldens are unchanged by `Allies`.

**A full room.** Fifteen Hard CPUs under room rules with every built weapon, 3,600 frames: boxes keep dropping throughout, no weapon that leaves the hand disappears on a fizzle, the prop count never passes the table, every fighter's placed props stay within 2, nobody is in a hold while airborne, and the run's checksums are the same twice.

**Rollback and determinism**

- `DeterminismTests`: a match with every status active and one of each new prop on the stage is saved at frame N, advanced 120 frames, restored and advanced again: the checksums match frame by frame. The same for a clone taken mid-spray, mid-guard and mid-burst, and across the frame of each `Become`.
- The same match run twice from the same seed gives the same checksums.
- The existing reflection test (every `Fighter`, `Prop` and `GameState` field moves the checksum and is cloned) covers the eight fields and the new record.
- **`client/tests/test_state_coverage.gd`** (new) does the same for GDScript, where `copy_from()` and `add_to()` are hand-written lists: for every script variable of `Fighter` and `Prop`, set a non-default value and check that the checksum changes, that `copy_from` reproduces it, and that `Prop.clear()` zeroes it; the same for `GameState`, each record entry included. It also runs the save / advance 120 / restore / advance loop over the `all_weapons` scenario, comparing checksums frame by frame.
- `GameStateCodecTests` (Net): a state with statuses and a full prop table, a non-zero value in every reused `Prop` field, round-trips.
- `client/tests/test_rollback.gd`: the GDScript rollback core re-simulates across a frame where a status is applied and a zone is created.
- `DeterminismGuardTests` stay untouched and green.

**Goldens**

- The eleven existing scenarios keep their names.
- Three new ones hand out weapons from `CrateList` in order, so every built weapon is in a trace whatever the crates hold. Fighter `p` is given weapon `CrateList[(f / 90 + p) % n]` at every 90th frame `f`:
  - `all_weapons_random_4p`: random inputs, 90 × n frames (9,000 at 100 weapons), so each weapon is held four times. Fighters 0 and 2 hold Light for the first 60 frames after each hand-out, so every row fires (the longest startup is 40);
  - `all_weapons_bots_4p`: Hard, Normal, Hard and Easy CPUs, the same length, which also checks the GDScript CPU picks the same buttons with every weapon;
  - `all_weapons_room_6p`: six CPUs under room rules on Rooftops (hazards, rage, second wind), a weapon every 45 frames.
- They are rewritten in every batch as `CrateList` grows, with `RANGBAAZI_WRITE_GOLDENS=1`, and `client/tests/test_golden_traces.gd` replays them. Batch 2 notes how long the GDScript suite takes; if the three new traces push it past two minutes, the random and bot traces drop to 60 frames a weapon.

**Rooms and scenes**

- `ArenaTests` (Relay): the frame sizes, the main-weapon byte (127, and a weapon above 15), every status bit, `DotWeapon`, `StunWeapon`, and a prop of each new flight with its u16 timer, owner, supply and wear. `client/tests/test_protocol.gd` decodes the same bytes.
- `room_e2e.gd`: a room match with weapons runs to the end on protocol 5.
- The scene smoke gets a second mode, `-- --smoke --all-weapons`: the match hands every crate weapon to its CPUs in turn (one every 30 frames) and fails on any script error. Every batch runs it, so a weapon the client can't draw fails that batch.
- A `shot_weapons.gd` run per batch, to look at the batch's weapons on screen.

## 7. Batches

Six batches, in order. Each one ends with: `dotnet test Rangbaazi.sln` green, the goldens rewritten, the GDScript tests green, the scene smoke green in both modes. Nothing is committed, pushed or deployed.

**A batch leaves the game playable.** A row is marked `built`, and so drops from crates, only when three things are true: its rules are in both sims with tests, the client draws it (a placeholder is enough: the weapon's sprite at the prop's box, a plain tint for a status), and the CPU has a range or rule for it. The drawing and the CPU rule for an archetype are done in the batch that adds the archetype. If the job stopped after any batch, what is in the crates works, shows and is used.

### Batch 1: the table, the engine, the shared state (no new weapons)

Keys: the twelve built ones (w011, w031, w006, w061, w005, w008, w054, w055, w007, w070, w068, w098).

- **Step A: the refactor, proved.** Write `tools/weapons.json` with all 107 rows from section 8 (only rows 1 to 12 `built`), the generator (rounding self-test, validation, `SPREADS`, name constants, flight flags), the generated files, and the table golden. Move both sims onto the table: every case in section 1.4, the renamed Uzi fields, the crate pick over `CrateList` with equal odds, a row-driven `PropBox`, `Rest` clearing the two ignore fields. `WeaponStats` keeps today's shape, sized by `Weapons.RecordWeapons` = 13. Leave the prop table at 16, `SIM_VERSION` at 7, and `AddShot` spending as it does today. **Gate: all eleven golden traces are byte-for-byte unchanged**, in C# without rewriting and in GDScript. That is the proof the twelve behave as before; do not go on until it holds.
- **Measure** the GDScript cost of the prop table (section 3.2) and write the two figures into section 9.
- **Step B: everything that changes the state or a frame, once.**
  - `PropSlots`, box slots, the fizzle rule (`AddShot` returns a bool), the placed-prop limit for pads and mines;
  - the eight `Fighter` fields: in the checksum and copy of both sims, cleared in `Spawn` and `CheckBlastZone`, with no behaviour yet;
  - `MatchRules.TeamOf` and `Simulation.Allies`, used by `ResolveHits`, `ShotHits`, `Explode` and `TripMine`;
  - the six-slot record with its total, `WeaponStat`, `MainWeapon`; the weighted crate pick;
  - the whole STATE layout of section 3.4 on both ends, protocol 5, the main-weapon byte (127) in `nicknames.gd`, `GameStateCodec`;
  - `SIM_VERSION` 8.
  - The goldens change here for the first time, for these reasons and nothing else.
- **Tests:** the table and generator tests; the slot, fizzle, placed-limit, record, crate and team tests; `test_state_coverage.gd`; `ArenaTests` and `test_protocol.gd` for the new layout; the three `all_weapons_*` goldens (twelve weapons long for now); the `--all-weapons` smoke mode.

Leaves for batch 2: the table and generator; an engine with no weapon ids in it; `Hit` and `MoveData` ready to carry an effect; all the state and the wire layout the later batches need, so none of them touches a checksum order or a frame size.

**Built (2026-11-21).** Both steps are in, in both sims; section 11 says what was built and where it differs from the text above. Step A's gate held: after the refactor, and before any of step B, the eleven golden traces were byte-for-byte what was committed (C#, not rewritten) and the GDScript replay passed.

### Batch 2: the plain weapons, and everything drawn from the table (31)

Keys: w001, w002, w003, w004, w009, w016, w027, w028, w032, w035, w036, w039, w042, w043, w045, w046, w047, w049, w051, w053, w057, w062, w063, w064, w065, w066, w076, w079, w083, w085, w090

- **Engine:** the columns of sections 2.1 to 2.3: `first` (in the collect pass of section 1.7), `burst`, `burstMax`, `pellets`, the spread modes, arrows that stop, bombs that wait, a missile that doesn't steer, the new sweet spots, `carry`, lunges, `batMax` and `batCost`.
- **Assets, for all 100 now:** `cut_weapons.gd` and `import_audio.py` read `tools/weapons.json`; cut 100 sprites and import 100 sounds (after the import, restore the tracked `.wav.import` files and run Godot's `--import`, as the project notes say).
- **Presentation from the table:** every per-weapon array in `client/game` reads `WeaponInfo` (held length, pose clip, HUD name, touch verb, shout, nickname words, sound). `arena_view.gd` draws a shot by its flight with its weapon's sprite, and any flight it has no drawing for as that sprite at the prop's box, so nothing later can be invisible. A blast draws a flash the length of its box; the Sniper Rifle draws its aim line.
- **The measuring script** moves into the repo as `tools/measure_weapons.gd`, reading `Simulation.WeaponStat`. Run it on this batch's rows and fix anything plainly broken (a damage row at 0, or far over 75). Fine tuning is batch 6.
- **Tests:** the plain archetype tests, the shove test for the Confetti Cannon and Spring Glove, the goldens.

Leaves for batch 3: 43 weapons in crates, all 100 sprites and sounds in the client, drawing that falls back safely.

**Built (2026-11-21).** The 31 rows are `built` and drop from crates; section 11 says what was built and where it differs from the text above.

### Batch 3: statuses, sprays, lobs (25)

Keys: w015, w018, w019, w021, w022, w023, w029, w034, w040, w044, w052, w056, w060, w067, w069, w071, w072, w077, w078, w080, w081, w082, w084, w094, w100

- **Engine:** `TickTimers` with the frame and index; the new `LandHit` order; the effects `slow`, `chill`, `root`, `stun`, `poison`, `burn`, `mark`, `leech`; the hold rules and the movement rules by code path (section 2.4). The `spray` use and the `Sprays` pass, with the airborne drift cap (section 2.5). The `lob` flight, floor and screen-edge bounces, `bounced`, and `land` on bombs (section 2.6).
- **Drawn:** each status on a fighter (a tint, a net, bubbles, flames, splats; ice, sparks or a spin by `StunWeapon`); a spray as a cone or beam the size of its box.
- **CPU:** holds a spray for `cpuBurst` ticks.
- **Tests:** every status, the hold, movement and tick tests, the spray and lob archetypes, the push-at-a-ledge tests for the Pankha and Pichkari, this batch's trick weapons, rollback with statuses.

Leaves for batch 4: effects that any later weapon can name in a column; `soft` hits; the hold rules; 68 weapons in crates.

**Built (2026-11-21).** The 25 rows are `built` and drop from crates (68 weapons); section 11 says what was built and where it differs from the text above.

### Batch 4: guards, melee tricks, traps, zones, walls (24)

Keys: w010, w012, w013, w014, w017, w020, w024, w025, w026, w030, w033, w037, w041, w048, w050, w059, w073, w074, w075, w088, w089, w092, w096, w099

- **Engine:** `defence` guard, hold, break and sweep, decided in the collect pass; `gbreak`, `armorbreak`; the effects `disarm` (with `sweetOnly`), `reel` and `slip`; `scoop` (section 2.7). Hidden rows: `cycle`, `fires`, `thrownAs`, `credit` (section 1.5). `Become`; traps on the `mine` flight; `leaves` with zones, clouds, walls and pads; walls blocking the line; breakable props; `maxHits`, `noSelf`; `IsHidden` (section 2.8).
- **Drawn:** the guard pose; a zone as a flat patch, a cloud in front of the fighters, a wall that cracks with its wear; hidden fighters not drawn (a player's own and teammates faintly).
- **CPU:** never holds the Parat's guard; doesn't target hidden fighters from more than 2 m.
- **Tests:** guard, defence, trap, zone, wall and pad archetypes; the flight-flag table; the one-frame-of-hits tests; `Become`; this batch's trick weapons; the Sledgehammer's guard break (built in batch 2, live now).

Leaves for batch 5: breakable and placed props; a prop that turns into another; hidden rows; 92 weapons in crates.

**Built (2026-11-21).** The 24 rows and the seven hidden rows are `built`, and the 24 drop from crates (92 weapons); section 11 says what was built and where it differs from the text above.

### Batch 5: machines and position tricks (8)

Keys: w038, w058, w086, w087, w091, w093, w095, w097

- **Engine:** `rehit` on a roll; the `drop`, `speaker`, `gate` and `decoy` flights; the `detonate` and `blink` uses; `SafeSpot`; the `swap` effect (section 2.9).
- **Drawn:** the anvil's shadow; a speaker's rings on the match clock; a gate's swirl, a pair sharing a colour; a decoy exactly as a Prop Box, with a small mark on its owner's screen only.
- **CPU:** uses the Teleporter to recover and throws it otherwise; presses the Detonator again once an opponent is within 1.5 m of its crate; walks to a decoy that isn't its own as it walks to a box.
- **Tests:** one per weapon here; the shove and ledge tests for the speaker; the full rollback test with every status and one of each prop; the codec round trip; the full-room test with fifteen CPUs.

Leaves for batch 6: all 100 weapons in crates, correct in both sims, drawn and used.

**Built (2026-11-21).** The 8 rows are `built` and drop from crates: all 100 weapons are in crates now. Section 11 says what was built and where it differs from the text above.

### Batch 6: tuning, polish, rooms end to end (all 100)

Keys: every weapon, w001 to w100.

- **Tuning by measurement:** `tools/measure_weapons.gd` for all 100 with Hard CPUs, against the two criteria of section 4.2 (damage rows 20 to 75 a crate; utility rows used, effective, and not mostly on their wielder). Weapons that hurt their wielder more than others get a `cpuMin`, as the missile did. Changes go in `tools/weapons.json`, then the generator, then the goldens.
- **Polish:** replace the placeholders that are still plain with the drawing of section 5; repeating weapons play their sound per shot or tick; `boom` for every burst.
- **Rooms:** `room_e2e.gd` against a local relay with weapons on, and one run of a 15-CPU room to read the real STATE size per frame; write the figure into section 9.

**Built (2026-11-21).** All 100 weapons measured with Hard CPUs; 31 rows retuned in `tools/weapons.json`: 29 crate weapons and two of the Jugaad Gauntlet's hidden rows (no new engine part, state or frame); the last plain placeholder drawn; a boom and a flash for every burst; the room end-to-end test and a 15-CPU room run against a local relay. Section 9 has the figures, section 11 what was changed and why.
- **Docs:** what was built and tuned, here; v0.3's "not built yet" list; the generator and measuring commands in `docs/tech/web-client.md`.

## 8. The rows as data

This is the table in the form `tools/weapons.json` takes: one object per row, holding the columns that differ from the defaults, under the names of section 1.2. Batch 1 starts by copying it. Where a table above and this list disagree, this list is right. `box` is near, far, bottom, top. `words`, `verb`, `shout`, `clip` and `length` are the presentation columns; a row without `length` uses the pack's `drawWidth` / 80, and a row without `clip` uses `side_heavy`. The defaults that are not zero, false or empty: `ang` 35, `throw` 9, `carry` 100, `pellets` 1, `spend` use, `crate` true, `credit` the row's own id, `weight` 2, `dotEvery` 30 (15 for `burn`). `batch` is the batch that builds the row (section 7); 0 is built already.

```json
[
{"id": 1, "key": "w011", "name": "Belan", "move": 7, "use": "swing", "batch": 0, "s": 7, "a": 3, "r": 14, "dmg": 8, "kb": 0.12, "g": 0.0025, "ang": 50, "hs": 10, "box": [0.2, 1.3, 0.6, 2.0], "supply": 12, "spend": "hit", "throw": 6, "sweet": "midattack", "sweetStun": 6, "verb": "Bonk", "shout": "BONK!", "clip": "neutral_heavy", "words": ["Belan", "Rolling-Pin", "Bonk", "Chapati"], "length": 0.95},
{"id": 2, "key": "w031", "name": "Baseball Bat", "move": 8, "use": "swing", "batch": 0, "s": 12, "a": 4, "r": 20, "dmg": 16, "kb": 0.17, "g": 0.0026, "hs": 4, "box": [0, 1.8, 0.4, 1.6], "supply": 8, "spend": "hit", "defence": "bat", "sweet": "tip", "sweetArg": 1.3, "sweetAng": 20, "verb": "Swing", "shout": "SIX!", "clip": "side_heavy", "words": ["Bat", "Home-Run", "Sixer", "Slugger"], "length": 1.3},
{"id": 3, "key": "w006", "name": "Bowling Ball", "move": 10, "use": "shoot", "batch": 0, "s": 14, "r": 20, "dmg": 14, "kb": 0.18, "g": 0.0024, "ang": 30, "hs": 4, "supply": 1, "spend": "never", "throw": 12, "carry": 80, "sweet": "strike", "flight": "roll", "vx": 0.22, "life": 180, "feet": true, "leave": true, "rest": true, "cpuMax": 12, "verb": "Roll", "shout": "STRIKE!", "clip": "down_heavy", "words": ["Bowling", "Strike", "Gutter", "Ten-Pin"], "size": 0.6, "length": 0.6},
{"id": 4, "key": "w061", "name": "Shotgun", "move": 9, "use": "blast", "batch": 0, "s": 12, "a": 2, "r": 30, "dmg": 18, "kb": 0.2, "g": 0.0028, "ang": 25, "hs": 4, "box": [0.4, 4.5, 0.5, 1.5], "lungeAt": 12, "lunge": -0.15, "supply": 4, "sweet": "pointblank", "sweetArg": 1.2, "farDist": 3, "farDmg": 10, "cpuMax": 4, "verb": "Blast", "shout": "POINT BLANK!", "clip": "jab", "words": ["Shotgun", "Boomstick", "Pump", "Buckshot"], "length": 1.15},
{"id": 5, "key": "w005", "name": "Uzi", "move": 11, "use": "shoot", "batch": 0, "s": 3, "r": 8, "dmg": 2, "kb": 0.05, "g": 0.0005, "ang": 10, "hs": -6, "supply": 30, "throw": 6, "sweet": "streak", "sweetArg": 6, "chip": true, "interval": 5, "walk": true, "spread": [0, 0.0366, -0.0366, 0.0732, -0.0732], "flight": "bullet", "vx": 0.7, "life": 20, "cpuMax": 9, "cpuBurst": 8, "verb": "Fire", "shout": "ON TARGET!", "clip": "jab", "words": ["Uzi", "Trigger", "Bullet", "Rat-a-tat"], "size": 0.3, "spreadBy": "burst", "length": 0.75},
{"id": 6, "key": "w008", "name": "Cherry Bombs", "move": 12, "use": "shoot", "batch": 0, "s": 8, "r": 14, "dmg": 12, "kb": 0.18, "g": 0.0025, "ang": 60, "hs": 4, "supply": 3, "sweet": "direct", "flight": "bomb", "vx": 0.14, "vy": 0.18, "grav": 0.01, "fuse": 50, "blast": 2.4, "touch": true, "cpuMin": 2.5, "cpuMax": 6, "verb": "Lob", "shout": "DIRECT HIT!", "clip": "neutral_heavy", "words": ["Pataka", "Cherry", "Fuse", "Firecracker"], "size": 0.4, "length": 0.55},
{"id": 7, "key": "w054", "name": "Bow", "move": 13, "use": "shoot", "batch": 0, "s": 10, "r": 14, "dmg": 10, "kb": 0.15, "g": 0.0024, "ang": 20, "hs": 2, "supply": 6, "throw": 6, "sweet": "longshot", "sweetArg": 20, "flight": "arrow", "vx": 0.55, "grav": 0.004, "life": 50, "pierce": true, "cpuMax": 15, "verb": "Shoot", "shout": "LONG SHOT!", "clip": "jab", "words": ["Arrow", "Bullseye", "Longbow", "Quiver"], "size": 0.5, "length": 0.95},
{"id": 8, "key": "w055", "name": "Boomerang", "move": 15, "use": "shoot", "batch": 0, "s": 8, "r": 12, "dmg": 8, "kb": 0.14, "g": 0.0022, "hs": 2, "supply": 4, "throw": 6, "sweet": "return", "flight": "boomerang", "vx": 0.5, "life": 150, "leave": true, "drag": 0.0125, "cpuMax": 8, "verb": "Throw", "shout": "COMEBACK!", "clip": "side_heavy", "words": ["Boomerang", "Comeback", "Return", "Curve"], "size": 0.6, "length": 0.85},
{"id": 9, "key": "w007", "name": "Trampoline", "move": 16, "use": "shoot", "batch": 0, "s": 6, "r": 10, "supply": 3, "flight": "pad", "fuse": 600, "feet": true, "padLaunch": 0.42, "verb": "Place", "clip": "down_heavy", "words": ["Trampoline", "Bounce", "Spring", "Jumper"], "size": 0.9, "length": 1.0},
{"id": 10, "key": "w070", "name": "Mines", "move": 17, "use": "shoot", "batch": 0, "s": 6, "r": 8, "dmg": 16, "kb": 0.2, "g": 0.0026, "ang": 60, "hs": 4, "supply": 3, "throw": 6, "sweet": "direct", "flight": "mine", "life": 900, "fuse": 45, "blast": 2.4, "feet": true, "verb": "Plant", "shout": "STEPPED ON IT!", "clip": "down_heavy", "words": ["Mine", "Booby-Trap", "Tripwire", "Surprise"], "size": 0.5, "length": 0.6},
{"id": 11, "key": "w068", "name": "Homing Missile", "move": 18, "use": "shoot", "batch": 0, "s": 14, "r": 22, "dmg": 15, "kb": 0.19, "g": 0.0027, "ang": 60, "hs": 4, "supply": 2, "throw": 12, "sweet": "direct", "flight": "missile", "vx": 0.2, "life": 240, "blast": 2.4, "touch": true, "turn": 0.008, "cpuMin": 3, "cpuMax": 14, "verb": "Launch", "shout": "DIRECT HIT!", "clip": "jab", "words": ["Missile", "Rocket", "Lock-On", "Seeker"], "size": 0.5, "length": 1.25},
{"id": 12, "key": "w098", "name": "Black-Hole Bomb", "move": 19, "use": "shoot", "batch": 0, "s": 10, "r": 16, "dmg": 20, "kb": 0.25, "g": 0.003, "ang": 70, "hs": 4, "supply": 2, "flight": "hole", "vx": 0.14, "vy": 0.18, "grav": 0.01, "fuse": 110, "blast": 4.2, "pullFrames": 80, "reach": 4.5, "pull": 0.11, "cpuMin": 2.5, "cpuMax": 6, "verb": "Lob", "clip": "neutral_heavy", "words": ["Black-Hole", "Gravity", "Vortex", "Void"], "size": 0.5, "length": 0.65},
{"id": 13, "key": "w001", "name": "Boxing Gloves", "move": 20, "use": "swing", "batch": 2, "s": 3, "a": 3, "r": 7, "dmg": 7, "kb": 0.13, "g": 0.0014, "ang": 45, "box": [0.2, 1.2, 0.6, 1.5], "supply": 12, "spend": "hit", "throw": 6, "sweet": "midattack", "verb": "Punch", "shout": "COUNTER!", "clip": "jab", "words": ["Glove", "Jab", "Uppercut", "Knockout"]},
{"id": 14, "key": "w002", "name": "Slingshot", "move": 21, "use": "shoot", "batch": 2, "s": 8, "r": 8, "dmg": 5, "kb": 0.09, "g": 0.0014, "ang": 30, "supply": 10, "throw": 6, "sweet": "longshot", "sweetArg": 18, "flight": "arrow", "vx": 0.45, "vy": 0.08, "grav": 0.008, "life": 60, "cpuMin": 2, "cpuMax": 11, "verb": "Sling", "shout": "BULLSEYE!", "clip": "jab", "words": ["Gulel", "Pebble", "Sling", "Catapult"], "size": 0.4},
{"id": 15, "key": "w003", "name": "Plank", "move": 22, "use": "swing", "batch": 2, "s": 10, "a": 4, "r": 16, "dmg": 12, "kb": 0.15, "g": 0.0024, "ang": 40, "hs": 4, "box": [0, 1.7, 0.4, 1.6], "supply": 4, "spend": "hit", "verb": "Swing", "words": ["Plank", "Timber", "Two-by-Four", "Splinter"]},
{"id": 16, "key": "w004", "name": "Ninja Stars", "move": 23, "use": "shoot", "batch": 2, "s": 4, "r": 8, "dmg": 3, "kb": 0.07, "g": 0.001, "ang": 20, "supply": 5, "throw": 6, "spread": [0, 0.06, -0.06], "pellets": 3, "flight": "bullet", "vx": 0.55, "life": 18, "cpuMax": 8, "verb": "Throw", "clip": "jab", "words": ["Star", "Shuriken", "Ninja", "Flick"], "size": 0.3, "spreadBy": "pellet"},
{"id": 17, "key": "w009", "name": "Stick Grenades", "move": 24, "use": "shoot", "batch": 2, "s": 10, "r": 16, "dmg": 16, "kb": 0.2, "g": 0.0027, "ang": 60, "hs": 4, "supply": 3, "flight": "bomb", "vx": 0.2, "vy": 0.2, "grav": 0.01, "fuse": 75, "blast": 3.0, "cpuMin": 3.5, "cpuMax": 9, "verb": "Lob", "clip": "neutral_heavy", "words": ["Grenade", "Countdown", "Stick-Bomb", "Kaboom"], "size": 0.4},
{"id": 18, "key": "w010", "name": "Mouse Trap", "move": 25, "use": "shoot", "batch": 4, "s": 5, "r": 8, "dmg": 4, "kb": 0.06, "g": 0.0008, "ang": 80, "supply": 3, "throw": 6, "effect": "root", "flight": "mine", "life": 900, "fuse": 30, "feet": true, "brk": 1, "verb": "Set", "clip": "down_heavy", "words": ["Mousetrap", "Snap", "Cheese", "Chuha"], "size": 0.5, "effectN": 45},
{"id": 19, "key": "w012", "name": "Thali", "move": 26, "use": "shoot", "batch": 4, "s": 6, "r": 10, "dmg": 7, "kb": 0.11, "g": 0.0018, "ang": 25, "supply": 6, "throw": 6, "flight": "arrow", "vx": 0.5, "grav": 0.001, "life": 30, "cpuMax": 10, "verb": "Fling", "clip": "side_heavy", "words": ["Thali", "Plate", "Frisbee", "Platter"], "size": 0.5, "defence": "guard"},
{"id": 20, "key": "w013", "name": "Tawa", "move": 27, "use": "swing", "batch": 4, "s": 11, "a": 3, "r": 18, "dmg": 14, "kb": 0.16, "g": 0.0026, "ang": 30, "hs": 6, "box": [0.1, 1.3, 0.5, 1.7], "supply": 7, "spend": "hit", "defence": "guard", "sweet": "midattack", "verb": "Slap", "shout": "CLANG!", "words": ["Tawa", "Pan", "Clang", "Griddle"]},
{"id": 21, "key": "w014", "name": "Chimta", "move": 28, "use": "swing", "batch": 4, "s": 6, "a": 3, "r": 12, "dmg": 4, "kb": 0.08, "g": 0.0012, "ang": 30, "hs": 2, "box": [0.3, 2.0, 0.8, 1.3], "supply": 6, "spend": "hit", "throw": 6, "effect": "disarm", "cpuMax": 1.7, "verb": "Pinch", "clip": "jab", "words": ["Chimta", "Tongs", "Pinch", "Snatch"], "sweet": "tip", "sweetArg": 1.5, "sweetOnly": true, "shout": "DISARMED!"},
{"id": 22, "key": "w015", "name": "Chakla", "move": 29, "use": "shoot", "batch": 3, "s": 12, "r": 18, "dmg": 10, "kb": 0.16, "g": 0.0024, "hs": 4, "supply": 3, "flight": "bomb", "vx": 0.3, "grav": 0.01, "cpuMax": 8, "verb": "Hurl", "clip": "neutral_heavy", "words": ["Chakla", "Board", "Roti", "Disc"], "size": 0.6, "ang": 60, "sweet": "direct", "vy": 0.1, "fuse": 120, "blast": 1.6, "touch": true, "land": true, "cpuMin": 2, "shout": "DIRECT HIT!"},
{"id": 23, "key": "w016", "name": "Chappal", "move": 30, "use": "shoot", "batch": 2, "s": 4, "r": 8, "dmg": 5, "kb": 0.09, "g": 0.0015, "ang": 40, "hs": 6, "supply": 6, "throw": 6, "sweet": "return", "flight": "boomerang", "vx": 0.4, "life": 100, "leave": true, "drag": 0.016, "cpuMax": 4.5, "verb": "Fling", "shout": "COMEBACK!", "clip": "jab", "words": ["Chappal", "Slipper", "Flip-Flop", "Mummy's"], "size": 0.5},
{"id": 24, "key": "w017", "name": "Jhaadu", "move": 31, "use": "swing", "batch": 4, "s": 9, "a": 5, "r": 15, "dmg": 6, "kb": 0.15, "g": 0.002, "ang": 15, "hs": 2, "box": [0, 2.0, 0, 0.9], "both": true, "supply": 10, "spend": "hit", "throw": 6, "cpuMax": 1.7, "verb": "Sweep", "clip": "down_heavy", "words": ["Jhaadu", "Broom", "Sweep", "Dust"], "defence": "sweep"},
{"id": 25, "key": "w018", "name": "Balti", "move": 32, "use": "swing", "batch": 3, "s": 13, "a": 3, "r": 18, "dmg": 11, "kb": 0.15, "g": 0.0025, "ang": 55, "hs": 4, "box": [0.1, 1.3, 0.6, 2.1], "supply": 6, "spend": "hit", "effect": "slow", "verb": "Bonk", "clip": "neutral_heavy", "words": ["Balti", "Bucket", "Drench", "Soak"], "effectN": 60},
{"id": 26, "key": "w019", "name": "Bathroom Mug", "move": 33, "use": "shoot", "batch": 3, "s": 3, "r": 6, "dmg": 3, "kb": 0.06, "g": 0.001, "ang": 40, "supply": 8, "throw": 6, "sweet": "bounced", "flight": "lob", "vx": 0.3, "vy": 0.08, "grav": 0.01, "life": 60, "bounces": 1, "keep": 80, "cpuMax": 6, "verb": "Chuck", "shout": "TRICK SHOT!", "clip": "jab", "words": ["Mug", "Tumble", "Bounce", "Trick-Shot"], "size": 0.4},
{"id": 27, "key": "w020", "name": "Lota", "move": 34, "use": "shoot", "batch": 4, "s": 7, "r": 12, "dmg": 5, "kb": 0.09, "g": 0.0015, "ang": 45, "supply": 3, "throw": 6, "effect": "slip", "flight": "bomb", "vx": 0.18, "vy": 0.16, "grav": 0.01, "fuse": 90, "blast": 0.8, "touch": true, "land": true, "leaves": "zone", "zoneW": 2.6, "zoneH": 0.4, "zoneLife": 300, "cpuMin": 2, "cpuMax": 6.5, "verb": "Spill", "clip": "neutral_heavy", "words": ["Lota", "Puddle", "Slip", "Spill"], "size": 0.4, "effectN": 30, "weight": 1},
{"id": 28, "key": "w021", "name": "Tiffin Stack", "move": 35, "use": "shoot", "batch": 3, "s": 8, "r": 12, "dmg": 5, "kb": 0.1, "g": 0.0016, "ang": 45, "hs": 2, "supply": 3, "sweet": "bounced", "flight": "lob", "vx": 0.22, "vy": 0.16, "grav": 0.01, "life": 120, "bounces": 2, "keep": 80, "cpuMin": 2, "cpuMax": 7, "verb": "Toss", "shout": "TRICK SHOT!", "clip": "neutral_heavy", "words": ["Tiffin", "Dabba", "Lunchbox", "Stack"], "size": 0.5, "pellets": 3, "spread": [0, 0.05, 0.1], "spreadBy": "pellet"},
{"id": 29, "key": "w022", "name": "Chai Kettle", "move": 36, "use": "spray", "batch": 3, "s": 6, "a": 6, "r": 12, "dmg": 2, "kb": 0.06, "g": 0.0006, "ang": 75, "hs": -2, "box": [0.3, 2.0, 0.6, 2.0], "supply": 18, "chip": true, "cpuBurst": 5, "verb": "Steam", "clip": "jab", "words": ["Chai", "Kettle", "Steam", "Cutting"]},
{"id": 30, "key": "w023", "name": "Steel Tumbler", "move": 37, "use": "shoot", "batch": 3, "s": 4, "r": 8, "dmg": 4, "kb": 0.06, "g": 0.0009, "ang": 20, "supply": 8, "throw": 6, "sweet": "strike", "flight": "lob", "vx": 0.55, "vy": 0.02, "grav": 0.01, "life": 80, "pierce": true, "bounces": 3, "keep": 90, "edge": 1, "cpuMax": 10, "verb": "Fling", "shout": "RICOCHET!", "clip": "jab", "words": ["Tumbler", "Glass", "Ricochet", "Steel"], "size": 0.4},
{"id": 31, "key": "w024", "name": "Parat", "move": 38, "use": "swing", "batch": 4, "s": 8, "a": 3, "r": 14, "dmg": 6, "kb": 0.2, "g": 0.001, "ang": 20, "hs": 4, "box": [0.1, 1.3, 0.3, 1.8], "supply": 8, "spend": "hit", "defence": "hold", "verb": "Guard", "words": ["Parat", "Shield", "Tray", "Wall"]},
{"id": 32, "key": "w025", "name": "Matka", "move": 39, "use": "shoot", "batch": 4, "s": 12, "r": 18, "dmg": 10, "kb": 0.16, "g": 0.0022, "ang": 60, "hs": 4, "supply": 3, "sweet": "direct", "flight": "bomb", "vx": 0.16, "vy": 0.18, "grav": 0.01, "fuse": 90, "blast": 1.8, "touch": true, "land": true, "brk": 3, "leaves": "wall", "zoneW": 1.2, "zoneH": 1.6, "zoneLife": 240, "cpuMin": 2.5, "cpuMax": 6.5, "verb": "Lob", "shout": "DIRECT HIT!", "clip": "neutral_heavy", "words": ["Matka", "Clay", "Pot", "Kulhad"], "size": 0.5},
{"id": 33, "key": "w026", "name": "Gas Cylinder", "move": 40, "use": "swing", "batch": 4, "s": 20, "a": 4, "r": 26, "dmg": 20, "kb": 0.21, "g": 0.0029, "ang": 30, "hs": 6, "box": [0, 1.6, 0.3, 1.7], "supply": 3, "spend": "hit", "throw": 12, "carry": 75, "verb": "Swing", "words": ["Cylinder", "Gas", "LPG", "Heavy"], "thrownAs": 105},
{"id": 34, "key": "w027", "name": "Pressure Cooker", "move": 41, "use": "blast", "batch": 2, "s": 36, "a": 3, "r": 24, "dmg": 15, "kb": 0.21, "g": 0.0027, "ang": 60, "hs": 4, "box": [0, 3.2, 0, 2.6], "both": true, "supply": 2, "throw": 12, "cpuMax": 2.8, "verb": "Whistle", "clip": "neutral_heavy", "words": ["Cooker", "Whistle", "Seeti", "Pressure"]},
{"id": 35, "key": "w028", "name": "Sil-Batta", "move": 42, "use": "swing", "batch": 2, "s": 24, "a": 3, "r": 28, "dmg": 23, "kb": 0.22, "g": 0.003, "ang": 65, "hs": 8, "box": [0, 1.2, 0, 2.2], "supply": 3, "spend": "hit", "throw": 12, "carry": 85, "verb": "Smash", "clip": "neutral_heavy", "words": ["Sil-Batta", "Chutney", "Grinder", "Stone"], "both": true},
{"id": 36, "key": "w029", "name": "Pankha", "move": 43, "use": "spray", "batch": 3, "s": 8, "a": 4, "r": 12, "ang": 20, "box": [0.3, 4.5, 0.2, 2.2], "supply": 24, "drift": 0.12, "blow": true, "cpuMax": 4, "cpuBurst": 12, "verb": "Blow", "clip": "jab", "words": ["Pankha", "Fan", "Breeze", "Gust"], "dmg": 1, "soft": true, "weight": 1},
{"id": 37, "key": "w030", "name": "Charpai", "move": 44, "use": "swing", "batch": 4, "s": 14, "a": 5, "r": 20, "dmg": 13, "kb": 0.2, "g": 0.0012, "ang": 20, "hs": 4, "box": [0, 1.9, 0.2, 1.6], "supply": 4, "spend": "hit", "carry": 85, "verb": "Shove", "words": ["Charpai", "Cot", "Khaat", "Shove"], "thrownAs": 106},
{"id": 38, "key": "w032", "name": "Cricket Bat", "move": 45, "use": "swing", "batch": 2, "s": 10, "a": 4, "r": 18, "dmg": 13, "kb": 0.17, "g": 0.0025, "ang": 60, "hs": 4, "box": [0, 1.6, 0.3, 1.7], "supply": 8, "spend": "hit", "defence": "bat", "sweet": "tip", "sweetArg": 1.1, "sweetAng": 75, "verb": "Swing", "shout": "LOFTED!", "words": ["Willow", "Cover-Drive", "Lofted", "Century"]},
{"id": 39, "key": "w033", "name": "Hockey Stick", "move": 46, "use": "swing", "batch": 4, "s": 9, "a": 4, "r": 14, "dmg": 9, "kb": 0.15, "g": 0.0022, "ang": 20, "hs": 2, "box": [0.2, 2.0, 0, 0.8], "supply": 8, "spend": "hit", "scoop": "fling", "cpuMax": 1.7, "verb": "Scoop", "clip": "down_heavy", "words": ["Hockey", "Dribble", "Scoop", "Penalty"]},
{"id": 40, "key": "w034", "name": "Cricket Ball", "move": 47, "use": "shoot", "batch": 3, "s": 6, "r": 12, "dmg": 8, "kb": 0.13, "g": 0.0022, "ang": 30, "hs": 2, "supply": 5, "throw": 6, "sweet": "bounced", "flight": "lob", "vx": 0.5, "vy": -0.04, "grav": 0.01, "life": 50, "bounces": 1, "keep": 90, "cpuMin": 2, "cpuMax": 11, "verb": "Bowl", "shout": "HOWZAT!", "clip": "side_heavy", "words": ["Yorker", "Bouncer", "Googly", "Howzat"], "size": 0.4},
{"id": 41, "key": "w035", "name": "Badminton Racket", "move": 48, "use": "swing", "batch": 2, "s": 3, "a": 3, "r": 6, "dmg": 3, "kb": 0.08, "g": 0.0014, "ang": 40, "box": [0.2, 1.5, 0.7, 1.9], "supply": 20, "spend": "hit", "throw": 6, "defence": "bat", "verb": "Smash", "clip": "jab", "words": ["Racket", "Smash", "Shuttle", "Rally"], "batMax": 8, "batCost": 1},
{"id": 42, "key": "w036", "name": "Pitchfork", "move": 49, "use": "swing", "batch": 2, "s": 9, "a": 3, "r": 16, "dmg": 9, "kb": 0.16, "g": 0.0023, "ang": 85, "hs": 4, "box": [0.4, 2.2, 0.7, 1.3], "supply": 7, "spend": "hit", "cpuMax": 1.9, "verb": "Poke", "clip": "jab", "words": ["Pitchfork", "Prong", "Hay", "Farm"]},
{"id": 43, "key": "w037", "name": "Rake", "move": 50, "use": "swing", "batch": 4, "s": 10, "a": 4, "r": 16, "dmg": 8, "kb": 0.16, "g": 0.0014, "ang": 25, "hs": 4, "box": [0.3, 2.3, 0, 0.9], "supply": 7, "spend": "hit", "effect": "reel", "scoop": "pull", "cpuMax": 2.0, "verb": "Rake", "clip": "down_heavy", "words": ["Rake", "Garden", "Haul", "Scrape"]},
{"id": 44, "key": "w038", "name": "Chicken", "move": 51, "use": "shoot", "batch": 5, "s": 12, "r": 14, "dmg": 5, "kb": 0.09, "g": 0.0012, "ang": 70, "hs": 2, "supply": 2, "flight": "roll", "vx": 0.16, "life": 150, "feet": true, "rehit": 30, "brk": 1, "cpuMin": 2, "cpuMax": 10, "verb": "Wind Up", "clip": "down_heavy", "words": ["Chicken", "Murgi", "Peck", "Wind-Up"], "size": 0.6},
{"id": 45, "key": "w039", "name": "Trout", "move": 52, "use": "swing", "batch": 2, "s": 6, "a": 3, "r": 12, "dmg": 7, "kb": 0.12, "g": 0.002, "ang": 40, "hs": 8, "box": [0.2, 1.4, 0.6, 1.6], "supply": 9, "spend": "hit", "verb": "Slap", "words": ["Trout", "Fish", "Machhli", "Slap"]},
{"id": 46, "key": "w040", "name": "Net", "move": 53, "use": "shoot", "batch": 3, "s": 12, "r": 18, "dmg": 1, "ang": 45, "supply": 2, "throw": 6, "effect": "root", "soft": true, "flight": "lob", "vx": 0.22, "vy": 0.12, "grav": 0.01, "cpuMin": 1.5, "cpuMax": 6, "verb": "Cast", "clip": "neutral_heavy", "words": ["Net", "Jaal", "Tangle", "Catch"], "size": 0.8, "effectN": 90, "weight": 1},
{"id": 47, "key": "w041", "name": "Lathi", "move": 54, "use": "swing", "batch": 4, "s": 8, "a": 5, "r": 15, "dmg": 8, "kb": 0.14, "g": 0.0022, "hs": 2, "box": [0, 2.1, 0.4, 1.7], "both": true, "supply": 10, "spend": "hit", "defence": "break", "cpuMax": 1.8, "verb": "Spin", "words": ["Lathi", "Staff", "Danda", "Twirl"]},
{"id": 48, "key": "w042", "name": "Gada", "move": 55, "use": "swing", "batch": 2, "s": 22, "a": 4, "r": 26, "dmg": 20, "kb": 0.21, "g": 0.0028, "ang": 45, "hs": 6, "box": [0, 1.8, 0, 1.4], "both": true, "supply": 4, "spend": "hit", "throw": 12, "carry": 85, "verb": "Slam", "clip": "neutral_heavy", "words": ["Gada", "Mace", "Pehelwan", "Akhada"]},
{"id": 49, "key": "w043", "name": "Talwar", "move": 56, "use": "swing", "batch": 2, "s": 7, "a": 4, "r": 13, "dmg": 9, "kb": 0.14, "g": 0.0024, "ang": 30, "hs": 2, "box": [0.2, 1.8, 0.5, 1.7], "lungeAt": 5, "lunge": 0.14, "supply": 9, "spend": "hit", "verb": "Slash", "words": ["Talwar", "Sabre", "Slash", "Blade"]},
{"id": 50, "key": "w044", "name": "Chakram", "move": 57, "use": "shoot", "batch": 3, "s": 8, "r": 12, "dmg": 7, "kb": 0.13, "g": 0.002, "ang": 25, "hs": 2, "supply": 4, "throw": 6, "sweet": "bounced", "flight": "arrow", "vx": 0.5, "life": 70, "pierce": true, "edge": 1, "cpuMax": 12, "verb": "Spin", "shout": "OFF THE WALL!", "clip": "side_heavy", "words": ["Chakram", "Ring", "Spin", "Halo"], "size": 0.5},
{"id": 51, "key": "w045", "name": "Katar", "move": 58, "use": "swing", "batch": 2, "s": 3, "a": 2, "r": 8, "dmg": 5, "kb": 0.09, "g": 0.0018, "box": [0.2, 1.1, 0.7, 1.4], "lungeAt": 1, "lunge": 0.18, "supply": 14, "spend": "hit", "throw": 6, "sweet": "back", "verb": "Lunge", "shout": "BACKSTAB!", "clip": "jab", "words": ["Katar", "Dagger", "Lunge", "Punch-Blade"]},
{"id": 52, "key": "w046", "name": "Katana", "move": 59, "use": "swing", "batch": 2, "s": 8, "a": 3, "r": 20, "dmg": 11, "kb": 0.15, "g": 0.0023, "ang": 25, "hs": 4, "box": [0.3, 2.0, 0.6, 1.5], "lungeAt": 5, "lunge": 0.26, "supply": 6, "spend": "hit", "sweet": "tip", "sweetArg": 1.4, "verb": "Slash", "shout": "CLEAN CUT!", "words": ["Katana", "Quick-Draw", "Dash", "Edge"]},
{"id": 53, "key": "w047", "name": "Broadsword", "move": 60, "use": "swing", "batch": 2, "s": 18, "a": 5, "r": 24, "dmg": 19, "kb": 0.2, "g": 0.0027, "hs": 6, "box": [0, 2.2, 0.3, 1.9], "supply": 5, "spend": "hit", "throw": 12, "carry": 90, "verb": "Cleave", "words": ["Broadsword", "Cleave", "Knight", "Claymore"]},
{"id": 54, "key": "w048", "name": "Spear", "move": 61, "use": "swing", "batch": 4, "s": 9, "a": 3, "r": 15, "dmg": 9, "kb": 0.15, "g": 0.0023, "ang": 25, "hs": 2, "box": [0.6, 2.5, 0.8, 1.2], "supply": 8, "spend": "hit", "sweet": "tip", "sweetArg": 1.9, "cpuMax": 2.2, "verb": "Thrust", "shout": "SKEWERED!", "clip": "jab", "words": ["Spear", "Bhala", "Javelin", "Skewer"], "thrownAs": 107},
{"id": 55, "key": "w049", "name": "Trident", "move": 62, "use": "swing", "batch": 2, "s": 12, "a": 4, "r": 18, "dmg": 12, "kb": 0.17, "g": 0.0025, "ang": 40, "hs": 4, "box": [0.5, 2.2, 0.2, 2.2], "supply": 6, "spend": "hit", "cpuMax": 1.9, "verb": "Poke", "clip": "jab", "words": ["Trident", "Fork", "Three-Point", "Tide"], "sweet": "tip", "sweetArg": 1.6, "sweetAng": 70, "shout": "PRONGED!"},
{"id": 56, "key": "w050", "name": "Cutlass", "move": 63, "use": "swing", "batch": 4, "s": 5, "a": 3, "r": 10, "dmg": 6, "kb": 0.11, "g": 0.0022, "ang": 40, "box": [0.2, 1.6, 0.5, 1.6], "supply": 12, "spend": "hit", "throw": 6, "defence": "guard", "verb": "Slash", "words": ["Cutlass", "Pirate", "Parry", "Swash"]},
{"id": 57, "key": "w051", "name": "Crossbow", "move": 64, "use": "shoot", "batch": 2, "s": 10, "r": 40, "dmg": 14, "kb": 0.17, "g": 0.0026, "ang": 20, "hs": 4, "supply": 4, "sweet": "longshot", "sweetArg": 12, "flight": "arrow", "vx": 0.8, "grav": 0.002, "life": 40, "cpuMin": 2, "cpuMax": 20, "verb": "Shoot", "shout": "LONG SHOT!", "clip": "jab", "words": ["Crossbow", "Bolt", "Quarrel", "Reload"], "size": 0.5},
{"id": 58, "key": "w052", "name": "Blow Darts", "move": 65, "use": "shoot", "batch": 3, "s": 5, "r": 8, "dmg": 2, "kb": 0.04, "g": 0.0006, "ang": 15, "hs": -4, "supply": 12, "throw": 6, "effect": "slow", "chip": true, "flight": "bullet", "vx": 0.5, "life": 24, "cpuMax": 9, "verb": "Blow", "clip": "jab", "words": ["Dart", "Blowpipe", "Phoonk", "Whisper"], "size": 0.24, "effectN": 60},
{"id": 59, "key": "w053", "name": "Sniper Rifle", "move": 66, "use": "blast", "batch": 2, "s": 40, "a": 1, "r": 30, "dmg": 22, "kb": 0.22, "g": 0.003, "ang": 15, "hs": 6, "box": [0.6, 30, 0.9, 1.3], "lungeAt": 40, "lunge": -0.1, "supply": 3, "sweet": "tip", "sweetArg": 10, "first": true, "cpuMin": 6, "cpuMax": 28, "verb": "Snipe", "shout": "LONG SHOT!", "clip": "jab", "words": ["Sniper", "Scope", "Long-Range", "One-Shot"]},
{"id": 60, "key": "w056", "name": "Chainsaw", "move": 67, "use": "spray", "batch": 3, "s": 10, "a": 5, "r": 14, "dmg": 3, "kb": 0.04, "g": 0.0004, "ang": 20, "hs": -2, "box": [0.3, 1.5, 0.5, 1.5], "supply": 20, "chip": true, "walk": true, "cpuBurst": 6, "verb": "Saw", "clip": "jab", "words": ["Chainsaw", "Buzz", "Sawdust", "Rev"]},
{"id": 61, "key": "w057", "name": "Sledgehammer", "move": 68, "use": "swing", "batch": 2, "s": 26, "a": 4, "r": 30, "dmg": 24, "kb": 0.23, "g": 0.0031, "ang": 20, "hs": 8, "box": [0.2, 1.9, 0, 1.8], "supply": 3, "spend": "hit", "throw": 12, "carry": 80, "verb": "Smash", "clip": "neutral_heavy", "words": ["Sledge", "Hammer", "Hathoda", "Demolition"], "gbreak": true},
{"id": 62, "key": "w058", "name": "Anvil", "move": 69, "use": "shoot", "batch": 5, "s": 14, "r": 20, "dmg": 24, "kb": 0.23, "g": 0.003, "ang": 80, "hs": 8, "supply": 2, "throw": 12, "carry": 80, "flight": "drop", "grav": 0.02, "fuse": 45, "pierce": true, "cpuMin": 3, "cpuMax": 5, "verb": "Drop", "clip": "neutral_heavy", "words": ["Anvil", "Drop", "Thud", "Heavy-Metal"], "size": 0.8},
{"id": 63, "key": "w059", "name": "Wrench", "move": 70, "use": "swing", "batch": 4, "s": 8, "a": 3, "r": 14, "dmg": 11, "kb": 0.14, "g": 0.0024, "ang": 40, "hs": 4, "box": [0.2, 1.4, 0.5, 1.6], "supply": 9, "spend": "hit", "gbreak": true, "verb": "Whack", "words": ["Wrench", "Spanner", "Pana", "Mechanic"], "armorbreak": true},
{"id": 64, "key": "w060", "name": "Magnet Gun", "move": 71, "use": "spray", "batch": 3, "s": 8, "a": 6, "r": 12, "ang": 70, "box": [0.4, 7.0, 0.3, 2.0], "supply": 20, "drift": -0.1, "propDrift": -0.3, "armedOnly": true, "cpuMin": 3, "cpuMax": 6.5, "cpuBurst": 10, "verb": "Pull", "clip": "jab", "words": ["Magnet", "Chumbak", "Pull", "Polarity"], "dmg": 3, "kb": 0.1, "g": 0.001, "tickNear": 1.2, "weight": 1},
{"id": 65, "key": "w062", "name": ".357 Magnum", "move": 72, "use": "blast", "batch": 2, "s": 14, "a": 1, "r": 34, "dmg": 16, "kb": 0.19, "g": 0.0022, "ang": 20, "hs": 4, "box": [0.5, 9, 0.8, 1.4], "lungeAt": 14, "lunge": -0.18, "supply": 4, "throw": 6, "sweet": "airborne", "first": true, "cpuMin": 2, "cpuMax": 8.5, "verb": "Fire", "shout": "SKEET!", "clip": "jab", "words": ["Magnum", "Revolver", "Six-Shooter", "Hand-Cannon"]},
{"id": 66, "key": "w063", "name": "Tommy Gun", "move": 73, "use": "shoot", "batch": 2, "s": 5, "r": 12, "dmg": 2, "kb": 0.05, "g": 0.0006, "ang": 10, "hs": -6, "supply": 36, "chip": true, "interval": 6, "walk": true, "spread": [0, 0.02, -0.02, 0.04, -0.04, 0.06, -0.06, 0.08, -0.08], "flight": "bullet", "vx": 0.65, "life": 22, "cpuMax": 10, "cpuBurst": 10, "verb": "Fire", "clip": "jab", "words": ["Tommy", "Drum", "Chatter", "Gangster"], "size": 0.3, "spreadBy": "widen"},
{"id": 67, "key": "w064", "name": "AK-47", "move": 74, "use": "shoot", "batch": 2, "s": 5, "r": 12, "dmg": 4, "kb": 0.06, "g": 0.0007, "ang": 10, "hs": -4, "supply": 18, "sweet": "streak", "sweetArg": 4, "chip": true, "interval": 7, "spread": [0, 0.015, 0.03, 0.045, 0.06, 0.075], "flight": "bullet", "vx": 0.75, "life": 24, "cpuMax": 13, "cpuBurst": 4, "verb": "Fire", "shout": "ON TARGET!", "clip": "jab", "words": ["AK", "Rifle", "Recoil", "Full-Auto"], "size": 0.3, "spreadBy": "hold"},
{"id": 68, "key": "w065", "name": "Burst Rifle", "move": 75, "use": "shoot", "batch": 2, "s": 6, "r": 22, "dmg": 4, "kb": 0.07, "g": 0.0008, "ang": 10, "hs": -2, "supply": 15, "sweet": "streak", "sweetArg": 3, "chip": true, "interval": 4, "burst": 3, "burstMax": 3, "flight": "bullet", "vx": 0.8, "life": 22, "cpuMax": 14, "verb": "Burst", "shout": "TRIPLE!", "clip": "jab", "words": ["Burst", "Triple", "Tap", "Marksman"], "size": 0.3},
{"id": 69, "key": "w066", "name": "RPG", "move": 76, "use": "shoot", "batch": 2, "s": 16, "r": 30, "dmg": 18, "kb": 0.21, "g": 0.0028, "ang": 60, "hs": 4, "lungeAt": 16, "lunge": -0.12, "supply": 2, "throw": 12, "sweet": "direct", "flight": "missile", "vx": 0.4, "life": 70, "blast": 2.8, "touch": true, "cpuMin": 4, "cpuMax": 16, "verb": "Launch", "shout": "DIRECT HIT!", "clip": "jab", "words": ["RPG", "Bazooka", "Warhead", "Launcher"], "size": 0.5},
{"id": 70, "key": "w067", "name": "Grenade Launcher", "move": 77, "use": "shoot", "batch": 3, "s": 10, "r": 20, "dmg": 13, "kb": 0.18, "g": 0.0026, "ang": 60, "hs": 4, "lungeAt": 10, "lunge": -0.08, "supply": 4, "sweet": "direct", "flight": "bomb", "vx": 0.32, "vy": 0.12, "grav": 0.01, "fuse": 80, "blast": 2.4, "bounces": 2, "keep": 70, "touch": true, "cpuMin": 4, "cpuMax": 12, "verb": "Thump", "shout": "DIRECT HIT!", "clip": "jab", "words": ["Thumper", "Bloop", "Lobber", "Popper"], "size": 0.4},
{"id": 71, "key": "w069", "name": "Cannon", "move": 78, "use": "shoot", "batch": 3, "s": 22, "r": 30, "dmg": 22, "kb": 0.24, "g": 0.003, "ang": 45, "hs": 6, "lungeAt": 22, "lunge": -0.15, "supply": 2, "throw": 12, "carry": 70, "flight": "lob", "vx": 0.3, "vy": 0.16, "grav": 0.01, "pierce": true, "cpuMin": 4, "cpuMax": 10, "verb": "Fire", "clip": "jab", "words": ["Cannon", "Tope", "Cannonball", "Broadside"], "size": 0.7},
{"id": 72, "key": "w071", "name": "Health Leech", "move": 79, "use": "spray", "batch": 3, "s": 10, "a": 6, "r": 14, "dmg": 2, "ang": 20, "box": [0.4, 4.0, 0.6, 1.6], "supply": 20, "throw": 6, "effect": "leech", "first": true, "soft": true, "cpuMax": 3.6, "cpuBurst": 8, "verb": "Drain", "clip": "jab", "words": ["Leech", "Jonk", "Drain", "Vampire"]},
{"id": 73, "key": "w072", "name": "Venom Dart Launcher", "move": 80, "use": "shoot", "batch": 3, "s": 6, "r": 12, "dmg": 3, "kb": 0.06, "g": 0.0008, "ang": 15, "supply": 8, "throw": 6, "effect": "poison", "flight": "bullet", "vx": 0.6, "life": 26, "cpuMax": 12, "verb": "Fire", "clip": "jab", "words": ["Venom", "Zeher", "Sting", "Viper"], "size": 0.3, "effectN": 240},
{"id": 74, "key": "w073", "name": "Toxic Goo Launcher", "move": 81, "use": "shoot", "batch": 4, "s": 8, "r": 16, "dmg": 2, "ang": 45, "supply": 4, "effect": "poison", "soft": true, "flight": "bomb", "vx": 0.24, "vy": 0.12, "grav": 0.01, "fuse": 90, "blast": 0.8, "touch": true, "land": true, "leaves": "zone", "zoneW": 2.4, "zoneH": 0.5, "zoneLife": 300, "cpuMin": 2, "cpuMax": 8, "verb": "Splat", "clip": "jab", "words": ["Goo", "Slime", "Keechad", "Toxic"], "size": 0.4, "effectN": 90, "dotEvery": 15},
{"id": 75, "key": "w074", "name": "Stink Cloud Capsule", "move": 82, "use": "shoot", "batch": 4, "s": 8, "r": 14, "ang": 45, "supply": 3, "throw": 6, "effect": "slow", "flight": "bomb", "vx": 0.16, "vy": 0.18, "grav": 0.01, "fuse": 60, "land": true, "leaves": "zone", "zoneW": 4.0, "zoneH": 3.0, "zoneLife": 360, "hides": true, "cpuMin": 2.5, "cpuMax": 6.5, "verb": "Toss", "clip": "neutral_heavy", "words": ["Stink", "Badboo", "Smog", "Skunk"], "size": 0.4, "effectN": 10, "weight": 1},
{"id": 76, "key": "w075", "name": "Venom Trap", "move": 83, "use": "shoot", "batch": 4, "s": 6, "r": 10, "dmg": 2, "ang": 45, "supply": 3, "throw": 6, "effect": "poison", "soft": true, "flight": "mine", "life": 900, "fuse": 40, "feet": true, "brk": 1, "verb": "Set", "clip": "down_heavy", "words": ["Fang", "Snare", "Cobra", "Ambush"], "size": 0.5, "effectN": 300},
{"id": 77, "key": "w076", "name": "Ray Gun", "move": 84, "use": "shoot", "batch": 2, "s": 5, "r": 9, "dmg": 6, "kb": 0.1, "g": 0.0014, "ang": 15, "supply": 10, "throw": 6, "flight": "bullet", "vx": 0.6, "life": 30, "cpuMax": 14, "verb": "Zap", "clip": "jab", "words": ["Ray", "Laser", "Zap", "Pew-Pew"], "size": 0.4},
{"id": 78, "key": "w077", "name": "Freeze Ray", "move": 85, "use": "spray", "batch": 3, "s": 10, "a": 4, "r": 16, "dmg": 1, "ang": 20, "box": [0.4, 5.0, 0.7, 1.5], "supply": 30, "effect": "chill", "first": true, "soft": true, "cpuMax": 4.5, "cpuBurst": 12, "verb": "Freeze", "clip": "jab", "words": ["Freeze", "Baraf", "Ice", "Chill"], "effectN": 16},
{"id": 79, "key": "w078", "name": "Taser", "move": 86, "use": "swing", "batch": 3, "s": 3, "a": 2, "r": 14, "dmg": 5, "kb": 0.08, "g": 0.001, "ang": 30, "box": [0.2, 1.0, 0.8, 1.3], "supply": 5, "spend": "hit", "throw": 6, "effect": "stun", "verb": "Zap", "clip": "jab", "words": ["Taser", "Shock", "Current", "Jhatka"], "effectN": 24},
{"id": 80, "key": "w079", "name": "Minigun", "move": 87, "use": "shoot", "batch": 2, "s": 30, "r": 36, "dmg": 2, "kb": 0.04, "g": 0.0004, "ang": 10, "hs": -6, "supply": 45, "throw": 12, "carry": 60, "sweet": "streak", "sweetArg": 10, "chip": true, "interval": 3, "burstMax": 24, "spread": [0, 0.03, -0.03, 0.06, -0.06, 0.09, -0.09], "flight": "bullet", "vx": 0.7, "life": 22, "cpuMax": 10, "cpuBurst": 24, "verb": "Spin", "shout": "ON TARGET!", "clip": "jab", "words": ["Minigun", "Gatling", "Spin-Up", "Brrrt"], "size": 0.3, "spreadBy": "burst"},
{"id": 81, "key": "w080", "name": "Flamethrower", "move": 88, "use": "spray", "batch": 3, "s": 8, "a": 6, "r": 16, "dmg": 2, "kb": 0.04, "g": 0.0004, "ang": 20, "hs": -4, "box": [0.4, 3.2, 0.4, 1.7], "supply": 24, "effect": "burn", "chip": true, "walk": true, "cpuMax": 2.8, "cpuBurst": 6, "verb": "Burn", "clip": "jab", "words": ["Flame", "Aag", "Tandoor", "Roast"], "effectN": 90},
{"id": 82, "key": "w081", "name": "Pichkari", "move": 89, "use": "spray", "batch": 3, "s": 5, "a": 8, "r": 10, "dmg": 1, "ang": 20, "box": [0.4, 5.0, 0.8, 1.4], "supply": 16, "throw": 6, "soft": true, "walk": true, "drift": 0.09, "cpuMax": 4.5, "cpuBurst": 8, "verb": "Spray", "clip": "jab", "words": ["Pichkari", "Holi", "Squirt", "Splash"]},
{"id": 83, "key": "w082", "name": "Colour Powder Pouch", "move": 90, "use": "blast", "batch": 3, "s": 6, "a": 3, "r": 12, "dmg": 2, "kb": 0.06, "g": 0.0006, "ang": 45, "box": [0.2, 2.6, 0.4, 2.0], "supply": 5, "throw": 6, "effect": "mark", "cpuMax": 2.3, "verb": "Puff", "clip": "jab", "words": ["Gulal", "Rang", "Colour", "Rangeela"], "effectN": 300, "weight": 1},
{"id": 84, "key": "w083", "name": "Dhol", "move": 91, "use": "blast", "batch": 2, "s": 14, "a": 3, "r": 20, "dmg": 6, "kb": 0.16, "g": 0.0016, "ang": 45, "hs": 6, "box": [0, 3.0, 0, 2.4], "both": true, "supply": 5, "sweet": "midattack", "sweetStun": 8, "cpuMax": 2.6, "verb": "Beat", "shout": "INTERRUPTED!", "clip": "down_heavy", "words": ["Dhol", "Beat", "Bhangra", "Thump"]},
{"id": 85, "key": "w084", "name": "Snowballs", "move": 92, "use": "shoot", "batch": 3, "s": 5, "r": 9, "dmg": 3, "kb": 0.06, "g": 0.001, "ang": 40, "supply": 10, "throw": 6, "effect": "slow", "flight": "lob", "vx": 0.28, "vy": 0.14, "grav": 0.01, "cpuMin": 2, "cpuMax": 8, "verb": "Throw", "clip": "neutral_heavy", "words": ["Snowball", "Frosty", "Snowman", "Blizzard"], "size": 0.4, "effectN": 50},
{"id": 86, "key": "w085", "name": "Confetti Cannon", "move": 93, "use": "blast", "batch": 2, "s": 12, "a": 2, "r": 24, "dmg": 8, "kb": 0.22, "g": 0.0008, "ang": 25, "hs": 2, "box": [0.4, 4.0, 0.3, 1.9], "lungeAt": 12, "lunge": -0.08, "supply": 3, "cpuMax": 3.5, "verb": "Pop", "clip": "jab", "words": ["Confetti", "Party", "Shaadi", "Celebration"]},
{"id": 87, "key": "w086", "name": "Decoy Crate", "move": 94, "use": "shoot", "batch": 5, "s": 10, "r": 14, "dmg": 14, "kb": 0.22, "g": 0.0026, "ang": 75, "hs": 6, "supply": 2, "flight": "decoy", "life": 1200, "touch": true, "feet": true, "verb": "Place", "clip": "down_heavy", "words": ["Decoy", "Fake", "Dhokha", "Prank"], "size": 0.8},
{"id": 88, "key": "w087", "name": "Crate Detonator", "move": 95, "use": "detonate", "batch": 5, "s": 4, "r": 10, "dmg": 16, "kb": 0.2, "g": 0.0027, "ang": 60, "hs": 4, "supply": 6, "throw": 6, "flight": "decoy", "life": 1200, "blast": 3.0, "feet": true, "verb": "Detonate", "clip": "jab", "words": ["Detonator", "Button", "Remote", "Plunger"], "size": 0.8},
{"id": 89, "key": "w088", "name": "Banana Peel", "move": 96, "use": "shoot", "batch": 4, "s": 4, "r": 6, "dmg": 1, "ang": 45, "supply": 4, "throw": 6, "effect": "slip", "soft": true, "flight": "mine", "life": 900, "fuse": 20, "feet": true, "verb": "Drop", "clip": "down_heavy", "words": ["Banana", "Kela", "Peel", "Skid"], "size": 0.4, "effectN": 30, "weight": 1},
{"id": 90, "key": "w089", "name": "Glue Gun", "move": 97, "use": "shoot", "batch": 4, "s": 6, "r": 12, "dmg": 1, "ang": 45, "supply": 12, "effect": "slow", "soft": true, "interval": 10, "walk": true, "flight": "bomb", "vx": 0.3, "vy": 0.1, "grav": 0.01, "cpuMin": 1, "cpuMax": 7, "cpuBurst": 3, "verb": "Squirt", "clip": "jab", "words": ["Glue", "Gond", "Sticky", "Chipku"], "size": 0.4, "effectN": 45, "fuse": 90, "blast": 0.6, "touch": true, "land": true, "leaves": "zone", "zoneW": 1.2, "zoneH": 0.4, "zoneLife": 180, "weight": 1},
{"id": 91, "key": "w090", "name": "Spring Glove Launcher", "move": 98, "use": "blast", "batch": 2, "s": 22, "a": 3, "r": 26, "dmg": 9, "kb": 0.26, "g": 0.0006, "ang": 20, "hs": 4, "box": [0.4, 6, 0.7, 1.4], "supply": 2, "first": true, "cpuMax": 5.5, "verb": "Punch", "clip": "jab", "words": ["Spring-Glove", "Boing", "Sucker-Punch", "Jack-in-the-Box"]},
{"id": 92, "key": "w091", "name": "Teleporter", "move": 99, "use": "blink", "batch": 5, "s": 6, "r": 10, "supply": 3, "throw": 6, "reach": 5, "cpuMax": -1, "verb": "Blink", "clip": "jab", "words": ["Teleport", "Blink", "Gayab", "Warp"], "weight": 1},
{"id": 93, "key": "w092", "name": "Dark Energy", "move": 100, "use": "shoot", "batch": 4, "s": 34, "r": 24, "dmg": 12, "kb": 0.16, "g": 0.0022, "ang": 40, "hs": 6, "supply": 2, "flight": "arrow", "vx": 0.12, "life": 240, "pierce": true, "cpuMin": 2, "cpuMax": 8, "verb": "Cast", "clip": "neutral_heavy", "words": ["Dark-Energy", "Orb", "Shadow", "Eclipse"], "size": 1.0, "maxHits": 4, "brk": 1, "noSelf": true},
{"id": 94, "key": "w093", "name": "Sonic Speaker", "move": 101, "use": "shoot", "batch": 5, "s": 10, "r": 14, "dmg": 3, "kb": 0.16, "g": 0.0006, "ang": 25, "supply": 2, "flight": "speaker", "life": 480, "feet": true, "rehit": 45, "brk": 2, "reach": 6, "verb": "Place", "clip": "down_heavy", "words": ["Speaker", "Bass", "DJ", "Loudspeaker"], "size": 0.8},
{"id": 95, "key": "w094", "name": "Gravity Gun", "move": 102, "use": "spray", "batch": 3, "s": 12, "a": 3, "r": 14, "ang": 20, "box": [0.4, 6.0, 0, 2.6], "supply": 30, "drift": 0.05, "lift": 0.06, "propDrift": 0.3, "cpuMax": 5.5, "cpuBurst": 14, "verb": "Lift", "clip": "jab", "words": ["Anti-Gravity", "Float", "Zero-G", "Levitate"], "dmg": 1, "soft": true, "weight": 1},
{"id": 96, "key": "w095", "name": "Portal Disc", "move": 103, "use": "shoot", "batch": 5, "s": 8, "r": 12, "supply": 2, "throw": 6, "flight": "gate", "life": 600, "verb": "Open", "clip": "jab", "words": ["Portal", "Gate", "Shortcut", "Wormhole"], "size": 1.0, "weight": 1},
{"id": 97, "key": "w096", "name": "Jugaad Gauntlet", "move": 104, "use": "cycle", "batch": 4, "supply": 9, "cpuMax": 1.2, "verb": "Jugaad", "clip": "jab", "words": ["Gauntlet", "Jugaadu", "Gadget", "Tinker"], "cycleFirst": 101, "cycleCount": 3},
{"id": 98, "key": "w097", "name": "Swap Ray", "move": 105, "use": "shoot", "batch": 5, "s": 20, "r": 20, "dmg": 4, "ang": 45, "supply": 3, "effect": "swap", "soft": true, "flight": "bullet", "vx": 0.45, "life": 30, "cpuMin": 3, "cpuMax": 11, "verb": "Swap", "clip": "jab", "words": ["Swap", "Adla-Badli", "Switch", "Trade"], "size": 0.5, "weight": 1},
{"id": 99, "key": "w099", "name": "Thunder Hammer", "move": 106, "use": "swing", "batch": 4, "s": 22, "a": 4, "r": 30, "dmg": 18, "kb": 0.2, "g": 0.0028, "ang": 60, "hs": 6, "box": [0.2, 1.8, 0, 1.8], "supply": 3, "throw": 12, "carry": 85, "cpuMax": 6, "verb": "Smash", "clip": "neutral_heavy", "words": ["Thunder", "Bijli", "Storm", "Shockwave"], "fires": 104},
{"id": 100, "key": "w100", "name": "Rubber Chicken Launcher", "move": 107, "use": "shoot", "batch": 3, "s": 8, "r": 14, "dmg": 6, "kb": 0.11, "g": 0.0016, "ang": 50, "hs": 2, "supply": 6, "spread": [0, 0.08, 0.04], "flight": "lob", "vx": 0.26, "vy": 0.1, "grav": 0.01, "life": 150, "bounces": 3, "keep": 80, "cpuMin": 2, "cpuMax": 9, "verb": "Launch", "clip": "jab", "words": ["Rubber-Chicken", "Squeak", "Cluck", "Honk"], "size": 0.6, "spreadBy": "supply"},
{"id": 101, "key": "", "name": "Gauntlet pull", "move": 108, "use": "blast", "batch": 4, "s": 8, "a": 2, "r": 14, "dmg": 3, "kb": 0.18, "g": 0.0006, "ang": 20, "hs": 2, "box": [0.5, 4.5, 0.6, 1.5], "effect": "reel", "first": true, "crate": false, "credit": 97},
{"id": 102, "key": "", "name": "Gauntlet punch", "move": 109, "use": "swing", "batch": 4, "s": 4, "a": 3, "r": 10, "dmg": 8, "kb": 0.13, "g": 0.0024, "ang": 45, "box": [0.2, 1.3, 0.6, 1.5], "crate": false, "credit": 97},
{"id": 103, "key": "", "name": "Gauntlet bolt", "move": 110, "use": "shoot", "batch": 4, "s": 6, "r": 12, "dmg": 5, "kb": 0.08, "g": 0.0012, "ang": 15, "flight": "bullet", "vx": 0.6, "life": 20, "size": 0.3, "crate": false, "credit": 97},
{"id": 104, "key": "", "name": "Thunder wave", "move": 111, "use": "shoot", "batch": 4, "dmg": 9, "kb": 0.14, "g": 0.002, "ang": 80, "hs": 4, "spend": "never", "flight": "roll", "vx": 0.3, "life": 28, "feet": true, "size": 0.8, "crate": false, "credit": 99},
{"id": 105, "key": "", "name": "Thrown gas cylinder", "move": 112, "use": "shoot", "batch": 4, "dmg": 16, "kb": 0.2, "g": 0.0027, "ang": 60, "hs": 4, "spend": "never", "sweet": "direct", "flight": "bomb", "vx": 0.32, "vy": 0.06, "grav": 0.01, "fuse": 120, "blast": 2.4, "touch": true, "land": true, "shout": "DIRECT HIT!", "size": 0.6, "crate": false, "credit": 33},
{"id": 106, "key": "", "name": "Thrown charpai", "move": 113, "use": "shoot", "batch": 4, "spend": "never", "flight": "bomb", "vx": 0.2, "vy": 0.1, "grav": 0.01, "fuse": 600, "land": true, "leaves": "pad", "zoneLife": 360, "padLaunch": 0.36, "size": 0.9, "crate": false, "credit": 37},
{"id": 107, "key": "", "name": "Thrown spear", "move": 114, "use": "shoot", "batch": 4, "dmg": 13, "kb": 0.16, "g": 0.0025, "ang": 25, "hs": 4, "spend": "never", "sweet": "longshot", "sweetArg": 15, "flight": "arrow", "vx": 0.45, "grav": 0.003, "life": 45, "pierce": true, "shout": "LONG SHOT!", "size": 0.5, "crate": false, "credit": 54}
]
```

## 9. Open points

- **Balance is measured, not played.** Batch 6 measured all 100 with `tools/measure_weapons.gd -- 6 rooftops` (one armed Hard CPU among four, six one-minute brawls each) and retuned 30 weapons; the review pass (section 12) retuned two more and changed four engine rules. With the final numbers 75 of the 76 damage rows among the 88 do 17.4 to 68.3 a crate on Rooftops, and the twelve keep their v0.3 range (19.8 to 75.5, the Trampoline doing no damage by design); the one outlier is the Anvil (below). The utility rows make 0.8 to 6.2 effect events a crate on others (the Teleporter's effect is on its wielder). Playtests will move these numbers again; the measure is a CPU brawl, not people.
- **The Anvil is still low on Rooftops: 6.5 a crate (28.7 on the Graybox).** The review pass made it aim at the nearest opponent in front, where they are heading (section 12), which took the Graybox from 16.0 to 28.7. On Rooftops its CPU wielder is knocked out 7.2 times a minute (fists: 4.0) and loses 7 of the 10.7 anvils it picks up a minute in those falls, before using them; no aim or CPU range (tried: 2 to 7.5 m, 3 to 7.5, 0 to 7.5, a 1 m nearest mark, no carry penalty, a lead of 0, 40 and 80 frames) moved it past 7. It still drops from crates: taking it out of them (`"crate": false`) would go against "all weapons drop", so that is the user's call.
- **Control weapons in a 15-player room.** Stun immunity stops one fighter being held by one weapon; it is shared across stuns, slips, roots and freezes so that several weapons can't chain either. Holds only take on the ground. Slow and poison have caps instead.
- **The clay wall is the first solid thing in the game.** It only pushes sideways; nobody can stand on it.
- **The prop table in GDScript.** Measured in batch 1 before step B: 2,000 frames of advance + checksum + `copy_from` with four fighters, random inputs, weapons on and a weapon handed out every 90 frames (5.1 props in use a frame), best of six runs on the development PC.
  - As the code stood: **876 ms at 16 props, 1,228 ms at 32 props** (+40%). That is over the 25% this spec set as the limit, and all of it is empty slots: hashing and copying seventeen fields of nothing, sixteen more times a frame.
  - So an empty slot was made cheap instead of making the table smaller. `Prop.add_to` hashes an empty slot as one multiplication (seventeen zero values multiply an FNV hash by a constant) and `Prop.copy_from` skips it. With that: **692 ms at 16 props, 742 ms at 32 props** (+7%), and 16 props is itself a fifth faster than before. The per-fighter term stays 4, as section 1.6 says.
  - The shortcut stands on one rule, written on `Prop`: an empty slot has every field zero, and `clear()` is the only way to empty one. `test_state_coverage.gd` checks it on every frame of a match with every weapon.
- **Bandwidth, measured.** A STATE frame is 12 + 34 per fighter + 1 + 17 per prop in use: 523 bytes for 15 fighters and no props, up to 1,611 with all 64 slots full. **The real figure** (2026-11-21, a room of 15 CPUs on the local relay with every weapon in the crates, 3 minutes, 10,799 frames read by a client): 523 to 659 bytes, **573 on average** (p50 574, p90 608, p99 625), never more than 8 props in use. At 60 frames a second that is about 34 KB/s to each player, well under the 1,000-byte estimate. If rooms on phones suffer, the fighters are the bulk (510 of the 573 bytes), not the prop list.
- **Teams.** The sim side is fixed here (`TeamOf`, `Allies`, section 1.6). The lobby, the roster message, scores and the win rule are the rooms work (`docs/design/rooms-teams-view-ui-v1.md`).
- **Zones and clouds and teammates: decided (2026-11-21, rooms v1).** Zones and the cloud cough affect everyone touching them, their owner and the owner's teammates included ("a puddle is a puddle", 2.8); this is not a hit, so 1.6's "statuses never land on a teammate" covers hits, sprays and shots, not zones. Teammates still never score off each other: `CheckBlastZone` gives a ring-out's KO only to a fighter who is not an ally of the one who fell, whatever set the credit.
- **Owners.** None of the 88 has an owner yet; the pack's reward weapons per fighter can fill `Weapons.SignatureFor` later, with no engine change.
- **Secondary attacks.** The pack gives every weapon a second attack. The game has one attack per weapon; where the second one was the point (the Parat's bash, a thrown Spear, the Charpai as a bounce pad, the Thali's block) it is folded into the one attack or the throw.

## 10. Review notes

Two reviews of the first draft (2026-11-21), one of balance and play, one of engineering. Each point and what was decided.

**Play and balance**

| Point | Decision |
|---|---|
| Portal Disc as a kill trap | Accepted. A gate opens only on a `SafeSpot`, else it fizzles; travellers come out standing still with their jumps back |
| Root, freeze and stun on a falling fighter | Accepted. Holds take only on the ground and end when the fighter leaves it; in the air the hit adds slow and knocks back |
| Push sprays and the speaker holding a fighter off stage | Accepted. Drift on an airborne fighter is capped at 60% of air speed; Pankha 24 ticks, Pichkari 16. The speaker: one per owner, no pulse at a fighter with no ground below. The 90-frame immunity was **not** taken: it needs state. All speakers pulse on the match clock and a fighter takes one pulse a frame, which gives the same guarantee with none |
| .357 Magnum | Accepted: startup 14, supply 4, 9 m, growth 0.0022 |
| Boxing Gloves and Katana | Accepted: gloves growth 0.0014 and supply 12; Katana startup 8 and growth 0.0023 |
| Automatic guns | Accepted: Tommy 2 damage, spread that widens and stays wide (`widen`), no streak; AK 18; Minigun 45, overheats at 24; Burst Rifle 4 damage |
| 32 prop slots, bullets evicting other players' props | Accepted: up to 64 slots by fighter count, box slots kept apart, placed limit 2 in big rooms, a shot never removes another fighter's prop. A global placed cap was **not** added: the per-fighter limit already bounds it (at most 32) |
| Dark Energy | Accepted: 12 damage, 0.16 + 0.0022, fades after 4 hits, any swing pops it, never hits its caster. It moves to batch 4, where breakable props are built |
| Weapons that do no damage | Mostly accepted. Pankha and Gravity Gun do 1 a tick; the Magnet Gun clonks whoever it drags all the way in; Blow Darts 12, Snowballs 10, Tumbler 4 damage × 8, Taser 5, goo ticks every 15 frames. Twelve utility rows drop at half odds and have their own pass criterion. **Not** taken: pulled crates flying as shots. Pulling a crate in to take it is what the gun is for |
| Three "light shot that slows" | Accepted for the Glue Gun: blobs leave sticky patches (it moves to batch 4). **Not** taken: attack presses cutting a glue slow. The sim would need to know which slow is glue; jumping out of the patch is the escape |
| Thali, Chakla | Accepted. The Thali blocks while it winds up and no longer pierces (batch 4). The Chakla is a lobbed board with a 1.6 m shockwave where it lands (batch 3) |
| Trident, Spring Glove, Jhaadu, Wrench | Accepted. Trident: tall box, 40°, tip to 70°. Spring Glove: 6 m, startup 22, supply 2. Jhaadu: sweeps traps, peels and patches away (batch 4). Wrench: through guards and Guts armor, breaks props in one swing |
| Six slow heavies | Accepted. The Sledgehammer goes through guards and breaks props in one swing; the Sil-Batta is the short both-sides slam, the Gada the wide one |
| Badminton Racket | Accepted: light shots only (`batMax` 8, nothing that bursts), 1 supply a return |
| Tiffin Stack | Accepted: each toss is three containers, 5 damage each |
| Fixed shoves at 0 Josh | Accepted: the test is in section 6, and the numbers were cut ahead of it (glove 0.26 at 20°, confetti 0.22 at 25°, charpai 0.2 at 20°, speaker 0.16 at 25°) |
| Swap Ray and hazards | Accepted: `SafeSpot` fails inside a hazard that is warning or active. The shooter's own mines are left out of the test on purpose |
| Teams | Accepted, without new state: `MatchRules.TeamOf` and `Allies` (section 1.6) |
| Gauntlet order; flung boxes; Chimta; walls and blasts; Anvil angle | All accepted: pull, punch, bolt; a flung box keeps its weapon and can't leave the match; the Chimta disarms on the tip only; walls stop swings, blasts, sprays and pulses; the Anvil launches at 80° |

**Engineering**

| Point | Decision |
|---|---|
| Poison and burn ticks under a refreshed timer | Accepted: the tick phase is `(Frame + index) % period`, no new state, with exact-damage tests |
| The full-table rule starving crates and eating weapons | Accepted: `AddShot` returns a bool, fizzles spend nothing, box slots, the 15-CPU test |
| Guards, `first` and breakables in collect-then-apply | Accepted (section 1.7), with index-swapped tests. One change from the suggestion: a swing connects with every fighter **and** prop in its box on that frame, so a trap can't shield the fighter next to it |
| `bat`, `break`, `blow`, walls, guards and parries on placed props | Accepted: the generated `flies` and `placed` flags, with a table-driven test |
| Reused `Prop` fields when a prop changes role; a square `PropBox` | Accepted: `Become`, `Rest` clearing the ignore fields, a row-driven `PropBox` |
| Two STATE layouts under one protocol number | Accepted: the whole layout once, in batch 1 step B, with a u16 timer, owner, supply and wear per prop. A prop's age is **not** sent: the speaker pulses on the match clock instead |
| Batches not finishable on their own | Accepted: a row is `built` only when it is also drawn and usable by the CPU; each archetype's drawing and CPU rule are in its own batch; every batch runs the all-weapons smoke. The old batch 1 is split in two so the refactor's gate ends a batch |
| Root, slow and stun across the movement code paths | Accepted: the rules are written per path (section 2.4); the upward blink is Up + Light |
| A flat int row with lists and names; generator rounding | Accepted: `SPREADS`, generated name constants, truncation towards zero with a self-test, validation |
| KO credit and the stun look sharing `LastHit*` | Accepted: a tick never overwrites a running credit; an eighth field, `StunWeapon`, carries the look; statuses are cleared on a KO as well as on a spawn |
| Changes to the twelve not called out | Accepted: section 1.8, and the record keeps the lower-id tie-break and a total so the 40% is unchanged |
| Checksum and copy coverage only proved in C# | Accepted: `client/tests/test_state_coverage.gd` |
| GDScript cost of the prop table and the goldens | Accepted: measured before step B, the table sized by fighter count. **Not** taken: 120-frame windows for slow rows in the random golden. The two fighters that hold Light do so for 60 frames, and the longest startup is 40 |
| Anvil spawn height, life fields, decoys and the CPU, the codec | Accepted: the anvil's height is clamped like a box's; section 3.2 says which field holds life for each flight; the CPU walks to decoys; step A sizes the record from `RecordWeapons` |

## 11. Build notes

What each batch built, and where it had to differ from the design above. Where this section and an earlier one disagree, this one is what is in the code.

### Batch 1 (2026-11-21)

**The generator and the table**

- `tools/weapons.json` holds the 107 rows of section 8. One thing was added to them: every weapon's `length` (the pack's `drawWidth` / 80) is written into the file, so the generator needs no art pack.
- `python tools/gen_weapons.py` writes `src/Rangbaazi.Sim/WeaponTable.cs`, `client/sim/weapon_table.gd` and `client/game/weapon_info.gd`; `--check` writes nothing and fails if one of them is stale. A row is 100 ints. `built` is true for a row whose `batch` is 0, or that says `"built": true`: a later batch marks its rows built that way.
- `sweetArg` is a distance (fixed point) for `tip` and `pointblank`, and a whole number (frames, or hits in a row) for every other rule.
- Besides the rules of section 1.1 the generator refuses: ids that don't run 1, 2, 3, ... in file order (the table is indexed by id), a move that is a fist move or the throw's (1 to 6, 14), a value of the wrong kind, an angle outside 0 to 90, two names that would be one identifier. `.357 Magnum` is `WeaponId.Magnum357` and `MAGNUM_357`.
- The generator's tests are `python tools/test_gen_weapons.py` (the rounding, and one refused file per rule). `WeaponTableTests` runs it, and `gen_weapons.py --check`, when the machine has Python; without Python those two tests pass without checking, and the table golden still guards the two sims against each other.
- In C# `Weapons.RowOfMove` gives a `WeaponRow` or null. In GDScript `Weapons.row_of_move` gives the row's id, 0 for none, and row 0 is the empty row (carry 100, interval 0, no defence), so the engine reads it without a null check.

**A weapon changed in the middle of its move.** The golden scenarios hand a fighter a new weapon every 90 frames whatever they are doing, so a fighter can be half-way through the Uzi's move with a Boomerang in hand. The old code then fired an Uzi bullet that was credited to the Boomerang, and step A had to do exactly the same. So:

- a move fires the shot of **the move's row** (flight, speed, fuse), and the prop's `Weapon` is the weapon in the hand, as before;
- a shot prop takes its numbers from the row of its `Weapon` when that row flies that way (or leaves that flight behind); otherwise from the first row in the table with that flight (`Weapons.ShotRow`). For the twelve that is the row the old `switch` on the shot kind picked. Hits are credited to `Rows[p.Weapon].Credit`.

Real play never makes such a prop (nobody picks a weapon up during an attack), so this only matters to the test harness, but it is why `ShotRow` is not just `Rows[p.Weapon]`. A hidden row's prop carries the hidden id, as section 1.5 says, and finds its own row.

**Step B, as built**

- `Weapons.PropSlots`, `BoxSlots` (the first `Cap + 2`), `PlacedLimit`. `AddShot` returns whether it placed the shot; a throw fizzles too (the weapon stays in the hand). The oldest of two props is the one with the greater `Age`, the lower slot on a tie.
- The eight `Fighter` fields, after `HitCount`, cleared by `Spawn` and `CheckBlastZone` (`Fighter.ClearStatuses`).
- Teams: `MatchRules.Teams` / `TeamOf`, `Simulation.Allies`, used in `ResolveHits`, `ShotHits`, `TripMine` and `Explode`. Two more places follow the rule "where the owner is left out, so is their team": a homing missile never steers at a teammate, and a CPU never picks a teammate as its opponent. A black hole still drags everybody in, teammates too (like a pad, it is not a hit); its burst leaves them alone. `MatchRules.Room` takes the teams as a fourth argument, and they are part of the GDScript rules' fingerprint.
- The record: six slots and a total, 25 ints a fighter; `Simulation.WeaponStat`, `DamageDealt` and `MainWeapon`. A KO with a weapon the record no longer holds still scores for the fighter, but not in the record.
- Crates by weight. With every weight equal (the twelve are all 2) the weighted pick draws the same number and picks the same weapon as the equal-odds pick did, so the twelve drop exactly as before.
- STATE protocol 5 on both ends (section 3.4). The hidden and guarding bits are sent as 0 until batch 4 gives them a rule. The client's decoder is a static `RoomSession.decode_state`, and the session keeps each fighter's bits in `status_bits` for the two that have no field in `Fighter`.
- `GameStateCodec` sizes come from `PropSlots` and `RecordInts`. `SIM_VERSION` is 8 (`Simulation.SimVersion` in C#).

**What the goldens show.** After step B every checksum is new (the state grew). The buttons the CPUs press are a finer measure: in all ten of the other old traces every frame's buttons are the same as before, weapons included, and in `newer_weapons_bots_4p` they are the same for the first 1,450 frames, until the placed-prop limit or a fizzle first changes something.

**Goldens and tests added**

- `all_weapons_random_4p`, `all_weapons_bots_4p`, `all_weapons_room_6p` as section 6 describes, and a fourth, `all_weapons_teams_6p` (two teams of three under room rules): without it nothing would check the GDScript port of the team rules. In the random trace fighters 0 and 2 hold Light for the 60 frames after a hand-out, keep only Left and Right of their random buttons, and let go of Light for one frame at 0, 20 and 40, so that each is a fresh press: a button that is only held never starts a move.
- `weapon_table.json`; C# `WeaponTableTests` and `WeaponRulesTests` (slots, fizzles, the placed limit, crate weights, teams, statuses, a full room of fifteen, rollback with every weapon), more record tests, `GameStateCodecTests`, `ArenaTests`; GDScript `test_weapon_table.gd`, `test_state_coverage.gd`, and the STATE frame in `test_protocol.gd` (the same bytes as `ArenaTests.SharedFrame`).
- The scene smoke has its second mode, `-- --smoke --all-weapons`.
- `tools/measure_weapons.gd` is the balance script (four Hard CPUs, one armed), reading the record through `Simulation.weapon_stat`: `godot --headless --path client -s ../tools/measure_weapons.gd -- <seeds> <stage>`.

**Left for later batches, on purpose**

- What the game draws per weapon was still hand-written for the twelve in `client/game` and in `tools/cut_weapons.gd` and `tools/import_audio.py`. Batch 2 moved all of it to the table.
- `Become` (section 3.2) arrives with the first prop that changes role (a burst that leaves a zone). `Rest` already clears the two ignore fields, and `PropBox` already reads a zone's or a wall's size from its row.
- `Hit` and `MoveData` carry `Effect` and `EffectN`; nothing reads them.

### Batch 2 (2026-11-21)

The 31 plain swings, blasts and guns of sections 2.1 to 2.3 are `built` (`"built": true` in `tools/weapons.json`), so crates hold 43 weapons. Their rules are in both sims, the client draws them, and the CPU uses them by their `cpuMin`/`cpuMax`/`cpuBurst`.

**The engine, in both sims**

- `first` is decided in `ResolveHits`' collect pass, after the parry and dodge test: a dodging fighter is never a contact, so it shields nobody; a parrying one is a contact and does. Nearest by horizontal distance, the lower index on a tie.
- Repeating fire reads `interval`, `burst` and `burstMax`; a use fires `pellets` shots; the spread modes `burst`, `hold`, `widen`, `pellet` and `supply` are one function (`Spread` / `_spread`). `widen` after the list's end alternates its last two entries, so the Tommy Gun's 10th and 11th shots are +0.08 and -0.08.
- An `arrow` without `pierce` is gone on its first hit; a `bomb` without `touch` bursts only when its fuse ends; a `missile` with `turn` 0 flies straight.
- The sweet rules `airborne` and `back`, with `sweetAng` and `sweetStun`; lunges and kicks from `lungeAt`/`lunge`; `carry`.
- `batMax` and `batCost`: the racket returns only a shot of at most 8 damage with no blast, for 1 supply, and returns nothing when it has none left. A thrown weapon's damage is its row's `throw`.
- `gbreak` is in the table (the Sledgehammer) and read by nothing until batch 4.

**Numbers changed from section 8**

- **Spring Glove Launcher:** base knockback 0.26 → 0.2, growth 0.0006 → 0.0001, hitstun +4 → +2. **Confetti Cannon:** base knockback 0.22 → 0.2. Section 2.2 says to cut the base if the shove test fails; at the spec's numbers a fighter shoved from 3 m inside a ledge could be lost. The lower growth also keeps the glove's shove the same at 0 and 150 Josh, within 10%.
- A presentation column, `shot`, was added (it is in `WeaponInfo`, not the sims): how a row's shot is drawn when its flight's usual drawing would be wrong. `spin` (Ninja Stars), `pebble` (Slingshot), `bolt` (Ray Gun), `rocket` (RPG, whose sprite is the launcher). The generator refuses any other value.

**Assets and presentation**

- `tools/cut_weapons.gd` and `tools/import_audio.py` read `tools/weapons.json`: 100 sprites in `client/assets/weapons` (a v7, then v6, cut wins over the v3 atlas) and 100 `client/audio/sfx/weapon_<id>.wav`. Hidden rows have neither.
- Nothing per weapon is hand-written in `client/game` any more: held length, HUD name, touch verb, shout, nickname words and the borrowed pose clip (`fighter_painter.gd` finds the row by `Weapons.row_of_move`; a throw borrows the jab) all come from `WeaponInfo`.
- Sound: a repeating weapon plays its sound on each shot, read from the fighter's move and frame (the move reaching its release frame, or looping back to it), so a room plays it too. Other weapons play it as the move starts.
- `arena_view.gd` draws a shot by its `shot` look, else by its flight, with its weapon's sprite; any flight it has no drawing for is the weapon's sprite standing in the prop's box, with a faint outline of the box, so a prop a later batch adds is never invisible. A blast draws, from the fighter's move and frame, a flash the length of its box: a cone, a tracer that stops in whoever a `first` blast hits, or a dome for a blast on both sides. A `first` blast with a startup of 30 frames or more (the Sniper Rifle) draws its aim line for everyone during the startup.

**Measured** (`tools/measure_weapons.gd -- 6 rooftops`: one armed Hard CPU among four, 6 one-minute brawls; "a crate" is damage per weapon handed out):

| Weapon | a crate | Weapon | a crate | Weapon | a crate |
|---|---|---|---|---|---|
| Boxing Gloves | 80.9 | Trout | 15.4 | .357 Magnum | 31.5 |
| Slingshot | 40.2 | Gada | 22.7 | Tommy Gun | 58.2 |
| Plank | 18.4 | Talwar | 26.4 | AK-47 | 77.5 |
| Ninja Stars | 38.5 | Katar | 65.8 | Burst Rifle | 59.9 |
| Stick Grenades | 10.6 | Katana | 30.5 | RPG | 26.5 |
| Chappal | 67.1 | Broadsword | 21.7 | Ray Gun | 57.5 |
| Pressure Cooker | 14.7 | Trident | 21.3 | Minigun | 41.0 |
| Sil-Batta | 19.5 | Crossbow | 45.1 | Dhol | 14.1 |
| Cricket Bat | 19.1 | Sniper Rifle | 12.8 | Confetti Cannon | 23.3 |
| Badminton Racket | 51.9 | Sledgehammer | 6.7 | Spring Glove Launcher | 10.8 |
| Pitchfork | 22.3 | | | | |

For scale, in the same run: Belan 21.7, Baseball Bat 27.1, Shotgun 39.4, Uzi 65.1, Bow 75.5. No row is at 0 and none is far over 75 (the gloves and the AK-47 are a little over), so nothing was changed for balance in this batch. For batch 6: the slow heavies and the long-startup blasts (Sledgehammer, Sniper Rifle, Spring Glove, Stick Grenades, Dhol, Pressure Cooker, Trout) are low, and their wielders fall 6 to 7 times a minute against 4 with fists: the CPU commits to a long startup within reach of the others. Only the RPG hurt its own wielder (3 hits a minute).

**Tests.** `tests/Rangbaazi.Sim.Tests/PlainWeaponTests.cs`: swings (active frames only, both sides, spend per hit, the Plank's four hits, mid-attack, from behind, tip angles), lunge and carry (the Katana's dash, the Minigun's 60%, kicks), blasts and `first` (nearest of two, a tie, a dodger doesn't shield, a parrier does, the Shotgun hits both, both-sides blasts, the Magnum's airborne sweet spot), repeating fire (Tommy Gun held and released, Burst Rifle on a tap and a hold, Minigun spin-up and overheat at 24), pellets and spread (three stars, the AK's climb, the Tommy Gun's 10th and 11th shots, the Minigun's cycle), arrows (the Crossbow stops in the first; its and the Slingshot's sweet spots), bombs (Stick Grenades wait for the fuse; the RPG flies straight past a fighter above it), the Chappal's return with one less, bats (the Cricket Bat returns an arrow; the racket a bullet for 1, not an RPG, and nothing when empty), and the shove tests (Confetti Cannon and Spring Glove from 3 m inside a ledge at 0 Josh on two stages, no KO; the glove the same at 0 and 150 Josh). The goldens were rewritten with 43 weapons (3,870 and 3,930 frames).

**GDScript suite time.** The whole `run_tests.gd` takes about 26 s on the development PC; the four `all_weapons` traces about 9.5 s of it. Well under two minutes, so the traces stay at 90 frames a weapon.

### Batch 3 (2026-11-21)

The 25 rows of sections 2.4 to 2.6 are `built`, so crates hold 68 weapons. Their rules are in both sims, the client draws them, and the CPU uses them by their range and holds a spray for its `cpuBurst` ticks.

**Statuses, in both sims**

- `TickTimers` takes the state and the fighter's index. It counts slow, mark, stun immunity and root down (a root that runs out clears `StunWeapon`), then ticks poison or burn when `(Frame + index) % dotEvery == 0` and counts `DotFrames` down. When `DotFrames` reaches 0, `DotBy` and `DotWeapon` are cleared too, so the client stops drawing it. `dotEvery` comes from the row of `DotWeapon` (row 0's is 30, so a stray value never divides by zero).
- A tick (`DotTick`) is 1 damage through the second wind, can trigger Guts, goes into `DotBy`'s record, and writes the last-hit record only when no credit is running, when the running credit is already `DotBy`'s, or when it is the victim's own (a fighter's own bomb is no KO credit, so it doesn't block the poisoner's). A fighter's own poison never writes it.
- `LandHit` follows section 2.4's order. `Hit` carries `Soft` and `ArmorBreak` beside `NoHitStop`, all three set from the row by `Hit.Flags(row)` for swings, blasts, shots, bursts and spray ticks. A hit with knockback ends any hold.
- Effects (`ApplyEffect`): slow and chill add up to 150; chill freezes at 90 on a fighter who can be held (60 frames, slow back to 0, 210 frames of immunity); root and stun take only on a grounded, non-immune, unarmored fighter, and in the air add slow of the same length; poison and burn keep the longer timer and the newest poisoner; mark keeps the longer; leech takes the damage dealt (after the mark, before the second wind) off the attacker's Josh, not below 0. A stun hold on a fighter late in a dodge costs the dodge's cooldown, as a hit does.
- **Holds end** when their time is up, when a hit with knockback lands, and in a new check at the end of the frame (`FreeIfAirborne`, after hazards, before the blast zone): a hold on a fighter who is not grounded ends there. A stun or freeze ends with the fighter `Idle`. A parry's stun (no `StunWeapon`) is left alone; so is a parry stun on a rooted fighter, whose root still ends normally.
- **Movement** is as section 2.4 says, path by path. The root's 12-frame cut is one cut per frame with a fresh Light or Heavy press (both on one frame count once).

**Sprays**

- `UpdateAttack` loops a spray from `s + a` back to `s` while a button is held and it has supply; `FireShots` spends 1 and counts `BurstShots` on each pass through `s`. `ResolveHits` skips spray moves.
- The `Sprays` pass reads every active stream (its box, the wielder's x and facing, whether this is a tick frame) before any of them acts, so two wielders spraying each other don't depend on their indices.
- `armedOnly` leaves an unarmed fighter untouched by the stream altogether: no drag and no clonk.
- A pull (`drift` or `propDrift` below 0) stops exactly 1 m in front of the wielder: it moves a fighter or a crate no nearer than that, and not at all once it is there.
- The parts that need batch 4 are not in the pass yet: walls blocking the stream, and a `hold` guard blocking it from the front.

**Lobs and bouncers**

- `lob` falls under its row's gravity, hits on touch (gone on that hit unless `pierce`), and on landing bounces (counted in `Timer`) or is gone. A bounce keeps the horizontal speed. A `bomb` with `bounces` counts them in `Hits` and bounces only when it lands from the air; after the last it slides like any bomb. `land` sets the fuse to 0, so the burst happens in `ShotHits` on the frame it lands.
- Screen-edge bounces work for any flight with `edge` (the Chakram is an arrow). The bounce clears `HitMask`, so it can hit everyone again, its thrower included.
- `bounced` is a floor bounce of a lob, a bounce of a bomb, or any edge bounce.

**Numbers changed from section 8**

- **Steel Tumbler:** damage 4 → 3, supply 8 → 5. **Chakram:** supply 4 → 3. Measured at their section 8 numbers they did 161 and 94 a crate: a tumbler that pierces, skips and comes back off the screen's edge hits everyone twice a throw. After the change: 80 and 68.

**Measured** (`tools/measure_weapons.gd -- 6 rooftops <ids>`, which now also takes a comma list of ids; "a crate" is damage per weapon handed out; the utility rows have weight 1 and are not judged by damage):

| Weapon | a crate | Weapon | a crate | Weapon | a crate |
|---|---|---|---|---|---|
| Chakla | 26.2 | Chakram | 68.3 | Freeze Ray | 24.1 |
| Balti | 12.5 | Blow Darts | 24.5 | Taser | 26.6 |
| Bathroom Mug | 26.8 | Chainsaw | 19.8 | Flamethrower | 47.1 |
| Tiffin Stack | 35.4 | Magnet Gun (utility) | 3.9 | Pichkari | 11.9 |
| Chai Kettle | 16.5 | Grenade Launcher | 32.1 | Colour Powder Pouch (utility) | 8.9 |
| Steel Tumbler | 80.0 | Cannon | 32.2 | Snowballs | 25.9 |
| Pankha (utility) | 18.6 | Health Leech | 12.0 | Gravity Gun (utility) | 18.7 |
| Cricket Ball | 38.3 | Venom Dart Launcher | 37.9 | Rubber Chicken Launcher | 31.3 |
| Net (utility) | 4.5 | | | | |

For batch 6: the Balti, Health Leech, Pichkari and Chai Kettle are low (12 to 17), and their CPU wielders fall 5 to 7 times a minute. The weapons that come back at their thrower hurt it: the Steel Tumbler 48 hits a minute (144 Josh), the Chakram 12.5 (88), the Chakla 8.7 (93), the Grenade Launcher 5 (66). Each still does far more to the others.

**Drawing and sound.** A spray is a cone the size of its box, or for a `first` row a beam that stops in the nearest fighter, coloured by what it does (flame, ice, leech, pull, lift, wind, water, sparks, steam), with puffs carried along it on the match clock. Over a fighter: slowed tints the painted fighter blue (a blue box over a plain one); marked, splats of colour; poison, green bubbles; burn, small flames (by `DotWeapon`'s row); and by `StunWeapon`'s effect an ice block (freeze), sparks (stun), a spin (slip) or a net (root, and anything else). A lob is drawn as its spinning sprite. A spray plays its sound on every tick, as repeating guns do on every shot.

**Tests.** `tests/Rangbaazi.Sim.Tests/StatusWeaponTests.cs`: a test per status (slow, the Balti, chill and the freeze with its immunity, the Net's root and presses, the Taser's stun, poison every 30 frames that never stacks and credits the poisoner, burn every 15, mark, the Colour Powder, Guts armor against every status with the Wrench getting past, the Health Leech); holds in the air for the Net, Taser and Freeze Ray; a frozen fighter lifted by a Gravity Gun and a rooted one pushed off a ledge by a Pankha, free the frame they leave the ground; a rooted Katana that doesn't dash and a rooted Up + Heavy that does nothing; a slowed Uzi walking at a quarter of run speed; the Flamethrower held for 60 frames (9 ticks of 2 and 4 burn ticks: exactly 22); no tick in hit-stop; a launcher keeping the KO over a later poison, and a fighter's own poison never taking it; the spray archetype; the Pichkari's 0.09 and 0.066; the Pankha turning an arrow; a `first` spray; the Magnet Gun (armed dragged, unarmed not, clonk only within 1.2 m, a crate pulled to 1 m); the Gravity Gun; the Pankha and Pichkari held at a ledge for the whole recovery, on two stages and from two heights; Snowballs, the Tiffin's three bouncing twice, the Cricket Ball, the Chakram's edge bounce and sweet way back, the Steel Tumbler, the Grenade Launcher's two bounces, the Chakla bursting where it lands, the Cannon piercing two, the Rubber Chicken's spread; the CPU holding a spray for its burst. `DeterminismTests` has a rollback test with every status running and a save taken mid-spray. `client/tests/test_rollback.gd` re-simulates the GDScript rollback core across frames where poison, chill and a freeze are applied, with the opponent's input 6 frames late. `WeaponTableTests`' every-weapon loop counts a status or a spray's push as doing something, and arms the target for an `armedOnly` row. `WeaponTests`' rollback brawl uses seed 81: with 68 weapons in the crates, seed 77 handed the bots only swings. The goldens were rewritten with 68 weapons.

**The ledge tests' stages.** Every arena's floor runs to the screen's edge (16 m), so "4 m outside a ledge" would be past the blast line there. The push-at-a-ledge test runs on the Graybox and on a test stage with a 16 m floor inside an arena's blast lines.

### Batch 4 (2026-11-21)

The 24 rows of sections 2.7 and 2.8 and the hidden rows 101 to 107 are `built`, so crates hold 92 weapons. Their rules are in both sims, the client draws them, and the CPU uses them by their range.

**One frame of hits (section 1.7), in both sims**

- `ResolveHits`' collect pass records four kinds of contact: hit, parried, blocked, prop. Applied in that order across all attackers: parries, blocks, prop swings, hits.
- **Prop contacts.** Every breakable prop in the box that isn't the attacker's side's (`Weapons.Breakable`: a shot whose row has `brk`, and for a row that `leaves` something, only the thing it left: a flying Matka is not breakable, its wall is). Fists count as swings. A prop contact counts as the move connecting.
- **Walls.** A fighter whose hurtbox is in the box is dropped when a wall stands between (`Simulation.WallBetween`: any wall, its centre strictly between the two x positions, its box overlapping the attack's box).
- **Guards.** `Simulation.Guarding(f)`: the move's row has `guard` or `hold`, and `2 <= ActionFrame < startup`. So the Thali guards on frames 2 to 5 and throws on 6 (section 2.3 said 2 to 6). `FacesToward(f, x)`: `x` on the side it faces, or at its x. A contact is blocked when the defender isn't parrying, the attacker's row has no `gbreak`, and the defender is guarding towards the attacker, all read before anything is applied.
- **`first` rows** keep their nearest fighter contact as before, and take no other prop than the nearest wall in the box, when no fighter stands before it.
- `ApplyBlock`: connected, the attacker's sideways speed set to 0.12 away from the guard, both frozen for `MinHitStop`, the guard's weapon loses 1.

**Defence**

- `hold` (the Parat): in `UpdateAttack`, reaching the startup frame with a button held and `BurstShots` under 90 sends the move back a frame and counts one. The 91st frame lets the bash out.
- A guard destroys a flying shot from the front in `ShotHits` (before a bomb's touch), for 1 supply. A bomb or missile is destroyed only where it would have burst on touch (a bomb that waits for its fuse lies there). A burst whose centre is in front doesn't hit the guard, also for 1 supply. **Every block costs 1**: a swing's, a shot's or a burst's (the spec named only swings).
- `break` (the Lathi), in `ShotHits`: a flying shot of another side in the active box is gone, without bursting. `sweep` (the Jhaadu): every `mine`-flight prop and every zone that doesn't hide, in the active box, whoever owns it.
- A hold guard turns a spray from its front away (`SprayTouches`), and a wall between turns it away for any target. Both were left out of batch 3.
- **The black hole keeps today's rule**: it pulls and bursts and never meets a fighter, so neither a parry nor a guard reaches it (its burst is still blocked from the front). The flight-flag test says so.

**Effects**: `disarm` drops the weapon as a loose weapon (a box-or-item slot) ignoring its holder for 60 frames; `sweetOnly` clears the effect from a hit that isn't sweet (swings, shots and bursts alike); `reel` turns the launch round; `slip` is a stun hold plus 0.3 m/frame sideways the way the fighter faces.

**Scoop.** `SwingProps` runs at the start of `UpdateProps`: on every active frame of a `fling` swing each box or loose weapon in its box becomes a `thrown` shot of the swinger's (through `Become`) carrying its supply. A thrown shot with supply is a flung one: wherever a thrown weapon is gone (it hits, lands, is parried, guarded, broken or stopped by a wall) a flung one rests as a loose weapon (`EndShot`), and one that leaves the blast zone turns into a pending box, back a second later. `pull` sets the prop 1.2 m in front.

**Hidden rows.** `ArmedMove` picks the cycling row by the supply left. `FireShots` spends a `swing` row with `spend use` on its startup frame (the Thunder Hammer and the Gauntlet's punch; no older swing has it), fires a `fires` row's shot from the hand (at the feet for a `feet` row), and throws a `thrownAs` row's shot instead of a plain thrown prop. A hidden row's shot carries the hidden id (`Shoot` uses the row's id when its `credit` isn't itself, else the weapon in the hand as before); hits use `credit`.

**Become** (`Simulation.Become` / `_become`) writes every field: kind (an item for no flight), flight, supply 0, owner -1 for an item (a shot keeps its owner), owner-clear off, speed 0, timer the row's `zoneLife` for a zone, wall or pad, else 0, and age, hit mask, hits and both ignore fields 0; weapon, position and grounded are kept. `Rest` is `Become` to an item plus the supply. The callers: a burst that `leaves`, a fling, a rest.

**Traps, zones, walls**

- A trap is a `mine` with no blast: on touch its row's hit, as a burst's from where it lies, then gone; after its life, gone. The owner trips it once off it; a teammate never.
- `Explode`: hits only if the row has damage (the cloud capsule and the thrown charpai only open); breaks every breakable prop the blast covers that isn't its side's (the spec said "any burst"; a burst sparing its own side's walls keeps a Matka from breaking the thrower's other walls); then, for a row that `leaves`, makes room under the owner's placed limit and becomes the zone, wall or pad in the same slot.
- Zones, walls and pads fall to the platform below and count down their life in `MoveShot`. Zone touch (`ZoneTouch`): slow and poison as section 2.8; slip on a grounded fighter faster than 0.05 m/frame with no stun immunity and no Guts armor, not already in hitstun or a hold.
- **A wall** pushes out (0.2 a frame at most, the speed into it zeroed), stops another side's flying shots (`StopAtWall`: bursts burst, rolls turn round if moving at it, a boomerang going out turns back and one coming back drops, anything else ends). **Changed from section 2.8: a shot passes its own side's walls.** Measured with every wall stopping every shot, the CPU threw Matkas into its own wall at point blank: 18 self-hits a minute, 185 Josh. With the change: 6.3 and 65, the Chakla's pattern. Swings, blasts and sprays are still stopped by any wall.
- Dark Energy: `noSelf` skips its owner's side; `maxHits` ends it after its fourth fighter; `brk` 1 makes any swing pop it.
- `Simulation.IsHidden`: the fighter's middle inside a zone whose row `hides`. The missile skips hidden targets; the CPU skips a hidden one more than 2 m away (squared distance over 4).
- The server now sends the hidden (32) and guarding (64) bits. The client works both out from the state it draws, so a room draws them from its STATE frames too.

**Numbers changed from section 8**

- **Charpai:** base knockback 0.2 → 0.18. At 0.2 the Charpai's swing knocked a fresh fighter out from 3 m inside a ledge on Rooftops (the shove test of section 2.2 now covers the Charpai and the Parat too).
- Presentation only: two new `shot` looks, `orb` (Dark Energy) and `wave` (the Thunder wave), and `spin` for the Thali's plate.

**Drawn.** A guard holds its weapon up in front with a pale arc over the side it covers. A zone that doesn't hide is a flat patch behind the fighters (a puddle blue, goo green, glue amber), fading in its last second; a cloud is soft puffs over its whole box in front of the fighters. A wall is a clay block with a crack more for each swing it has taken. A trap is its sprite, faded while it arms (a mine keeps its light). A hidden fighter, its weapon and its markers are not drawn, except at 35% for the viewer's own side (you, your teammates; with nobody tagged, every fighter that isn't a CPU). Staged screenshots were checked in the scratchpad (`b4_10.png`, `b4_30.png`, `b4_34.png`).

**CPU.** It never holds the Parat's guard (it holds a button only for a repeating weapon or a spray), and doesn't target a hidden fighter beyond 2 m.

**Measured** (`tools/measure_weapons.gd -- 6 rooftops <ids>`; "a crate" is damage per weapon handed out; utility rows have weight 1 and are not judged by damage):

| Weapon | a crate | Weapon | a crate | Weapon | a crate |
|---|---|---|---|---|---|
| Mouse Trap | 9.4 | Matka | 25.6 | Spear | 30.8 |
| Thali | 38.0 | Gas Cylinder | 11.5 | Cutlass | 44.4 |
| Tawa | 69.9 | Charpai | 21.7 | Wrench | 18.3 |
| Chimta | 16.3 | Hockey Stick | 21.1 | Toxic Goo Launcher | 29.7 |
| Jhaadu | 15.4 | Rake | 21.8 | Stink Cloud Capsule (utility) | 2.5 |
| Lota (utility) | 14.3 | Lathi | 29.4 | Venom Trap | 14.4 |
| Parat | 29.4 | Dark Energy | 27.0 | Banana Peel (utility) | 5.1 |
| Glue Gun (utility) | 8.8 | Jugaad Gauntlet | 17.5 | Thunder Hammer | 32.9 |

(Charpai, Matka and the rest measured at their final numbers; the Matka row is after the own-wall change.) No row is at 0 or over 75. For batch 6: the Mouse Trap (9.4), Gas Cylinder (11.5), Chimta, Jhaadu and Venom Trap are low. Weapons that burst where they land hurt their thrower as the Chakla does: the Lota 9.7 hits a minute (53 Josh), the Matka 6.3 (65). The traps catch their own setters now and then (Banana Peel 6.8 slips a minute, Venom Trap 3.3, Mouse Trap 2.3): the owner can trip their own trap once off it, as section 2.8 says. A CPU chasing someone behind a wall walks into it until the wall breaks or runs out: it only jumps after someone standing higher.

**Tests.** `tests/Rangbaazi.Sim.Tests/GuardTrapWeaponTests.cs`: the guard (the Parat blocks a jab from the front and not from behind; its hold lasts 90 frames and lets go at once on release; a block costs 1 and an empty guard is thrown next; the Wrench and the Sledgehammer go through; the Thali blocks while winding up and then flies; a guard destroys a bullet from the front and ignores a burst in front), defences (the Lathi destroys bullets on both sides; neither the Lathi nor a bat touches a pad, a mine or a wall beyond the swing a wall takes), the table-driven flight-flag test (all 17 flights against a bat, a Lathi, a Pankha, a parry, a guard and a wall), the three one-frame tests each run with the indices swapped, `Become` (a Matka wall, a flung crate, a rested Bowling Ball and a bounced Grenade Launcher bomb, every field), traps (arming, rooting, gone; a swing breaks one; one nobody finds goes without bursting; the Jhaadu sweeps a peel, a mine and a goo patch but not a cloud), zones (exactly 8 damage for 120 frames in the goo, 6 more after leaving and then none; the puddle slips a runner, not someone standing; glue keeps the slow up), walls (walking doesn't pass, a jump does; a bullet stops, an own-side bullet passes; a Spear and a Shotgun don't reach past one; three swings or one Sledgehammer break it), the thrown Charpai's pad, and the trick weapons (Chimta, Rake, Hockey Stick with its three cases, Gas Cylinder, Spear, Thunder Hammer, the Gauntlet's order and credit and its pull's reel, the Stink Cloud with the missile and the CPU, the capsule opening, Matka, Lota, Banana Peel at a ledge, Glue Gun, Dark Energy), the CPU and the Parat, the batch's rows in the crates. `PlainWeaponTests`' shove test covers the Charpai and the Parat. `DeterminismTests` rolls back across a held guard, traps, a puddle, a cloud and the frames where bursts become a wall and a patch. `ArenaTests` checks the hidden and guarding bits. `client/tests/test_rollback.gd` re-simulates the GDScript rollback core across the frames where a goo patch and a Matka wall are left. The goldens were rewritten with 92 weapons.

### Batch 5 (2026-11-21)

The 8 rows of section 2.9 are `built`, so crates hold all 100 weapons. Their rules are in both sims, the client draws them, and the CPU uses them. No state was added and no frame changed: `SIM_VERSION` stays 8, the arena protocol stays 5.

**The engine, in both sims**

- **Rehit (the Chicken).** A `roll` whose row has `rehit` clears its hit mask every time its age is a multiple of it, so it pecks whoever it runs with again every 30 frames. Without `rest` it is gone at the end of its life. Its `brk` 1 makes any swing pop it, a bat's too (the swing is resolved before the bat rule).
- **`drop` (the Anvil).** `Simulation.DropSpot`: 4 m ahead, clamped 2 m inside the side blast lines, at the lower of 9 m above the feet and 2 m under the top blast line. Its fuse (45) is the hang, counted down in `Timer`; the frame it appears is the first of the 45. Then it falls under its row's gravity, lands on platforms like any prop and is gone on landing. While falling (`Timer` 0) `DropOn` gives each fighter it passes through its row's hit once (the hit mask), its owner too after the release grace, never its owner's teammates. A dodge or a parry only saves the fighter: nothing destroys it, a guard doesn't block it. It launches away from its own middle (by position, as it has no sideways speed).
- **`speaker` (the Sonic Speaker).** Its life (480) is in `Timer`, its facing in `Supply`. It falls to the platform below like a pad. The pulse period is the row's `rehit` (45), on the match clock (`state.Frame % rehit == 0`). `Pulse` hits every undodging fighter but its owner's side whose hurtbox touches the box from its centre `reach` (6 m) the way it faces and 2 m up from its base, launched the way it faces; a wall in between stops it; a fighter in the air with no platform top anywhere below (`Simulation.TopBelow`) is left alone. One pulse a fighter a frame: `ShotHits` keeps a local mask, so the speaker in the lowest slot gets them. `brk` 2.
- **Per-flight limits.** `Weapons.FlightLimit`: one speaker and two gates an owner. `AddShot` removes the owner's oldest of that flight first, then makes room under the placed limit as before.
- **`Simulation.SafeSpot`.** 2 m inside the blast zone on every side, a platform top straight below within 4 m (`TopBelow`), and, with hazards on, no hazard that is warning or active (`WarnAt` up to `CooldownAt`) overlapping a fighter-sized box standing at the point. **Changed from section 1.6:** "inside the area of a hazard" is read as "a fighter standing there would touch it", since a fighter is what comes out there; a point exactly on a zone's edge is no use as a test.
- **`gate` (the Portal Disc).** It opens at the wielder's own position (feet), only on a `SafeSpot`, else the use fizzles and spends nothing. It floats there (no gravity) for 600 frames in `Timer`. `PassGate`: the partner is the other gate with the same owner; with none it does nothing. An undodging fighter touching it whose bit isn't in its `HitMask` is put on the partner's position with speed zero, its air jump, air dodge and recovery back, and grounded only if the partner sits exactly on a platform top; both gates then set its bit, and a gate clears the bit of every fighter not touching it. **Added:** the opener's release grace (`OwnerClear`) applies to a gate, so a fighter opening the second gate while standing in it is not sent straight back to the first.
- **`decoy` (the Decoy Crate and the rigged crate).** A prop the size of a box (`size` 0.8), falling and resting like a box, with its life (1200) in `Timer` and `Weapons.DecoyLooks` (the last box's weapon, else the Belan) in `Supply`. A row with `touch` springs (`Spring`) on the first fighter by index who isn't on its owner's side and could take a box (active, not attacking, not dodging): its row's hit, launched up and away from it, and it is gone. The Detonator's rigged crate is a decoy of the Detonator's own row, which has no `touch`, so it never springs.
- **`detonate` (the Crate Detonator).** On the startup frame: if the wielder owns any decoy, each is given the Detonator's id (so `ShotRow` gives the Detonator's numbers and the record credits it) and `Explode`d: a 3 m burst that hits its owner too. Otherwise it places a rigged crate at its hand's x, at its feet. Either spends 1; a crate with no slot fizzles.
- **`blink` (the Teleporter).** `Simulation.BlinkSpot` tries `reach` (5 m), 4, 3 and 2 m ahead (with Up, straight above first) for a platform top within 6 m of the feet, the nearest in height and the higher on a tie; with Up only tops more than 0.5 m above the feet. Found: the wielder stands there with speed zero, its jumps and recovery back, a root ended, 1 spent. Not found: it fizzles. Up is read on the startup frame (the fighter's stored buttons for that frame), so Up must still be held then.
- **`swap` (the Swap Ray).** `ApplyEffect` swaps the two fighters' positions and grounded flags and zeroes both speeds, if the attacker is in play and both spots pass `SafeSpot`; otherwise only the damage (the row is `soft`).

**The CPU**

- **Teleporter** (`cpuMax` -1, "throws it"): off the stage and falling below the stage top, it presses Up + Light, before its air jump, when `BlinkSpot` finds a spot from where it is, facing home; it holds Up through the move. On the stage it throws the Teleporter (Drop weapon) at an opponent lined up within 5 m, and never presses Light with it.
- **Crate Detonator:** with no crate out it is used at fist range (it puts one down). With crates out it never presses at fist range, on the ground or in the air, and presses again only when an opponent is within 1.5 m of one of its crates **and it is clear of every one of its crates by the blast's half-width plus 1 m**. That condition is added to section 2.9's: measured with the spec's rule alone the CPU burst crates it stood beside, 3.3 self-hits a minute (53 Josh); with it, none.
- **Decoys:** an empty-handed CPU walks to a decoy that isn't its side's as to a box (`NearestItem`). On a rigged crate, which never springs and can't be taken, it waits until the crate is burst or runs out.

**Drawn** (`client/game/arena_view.gd`): a decoy is drawn by the same code as a Prop Box (`_draw_crate`) with the weapon in its `Supply`, behind the fighters, and in the off-screen crate pointers too; on the screen of its owner's side (`_own_side`) a red dot sits above its corner (with a white spark on a rigged crate). A speaker is its sprite facing its way, with three arcs spreading out over its reach for 15 frames after each pulse (the match clock) and a faint arc between. A gate is a turning swirl in its owner's colour, so a pair shares one, faint while it has no partner and fading in its last second. The anvil is its sprite, and its shadow on the first platform top below it, darker and smaller as it nears. Staged screenshots were checked in the scratchpad (`shots_b5/b5_12.png`, `b5_18.png`, `b5_40.png`).

**Measured** (`tools/measure_weapons.gd -- 6 rooftops <ids>`; "a crate" is damage per weapon handed out; utility rows have weight 1 and are not judged by damage):

| Weapon | a crate | self-hits a minute | wielder's falls a minute |
|---|---|---|---|
| Chicken | 18.7 | 0.2 | 6.2 |
| Anvil | 10.5 | 0.2 | 5.0 |
| Decoy Crate | 18.2 | 0 | 5.7 |
| Crate Detonator | 34.7 | 0 | 3.3 |
| Teleporter (utility) | 5.2 | 0 | 1.3 |
| Sonic Speaker | 16.7 | 0 | 4.0 |
| Portal Disc (utility) | 2.6 | 0 | 5.2 |
| Swap Ray (utility) | 12.6 | 0 | 3.3 |

For scale, in the same run: fists 4.0 falls a minute, Belan 21.7 a crate, Shotgun 39.4. No row is at 0 or over 75, so no numbers were changed. For batch 6: the Anvil (10.5), Sonic Speaker, Decoy Crate and Chicken are low; the Anvil's CPU fires it at 3 to 5 m and most targets have walked off the mark in its 45-frame hang. A CPU holding the Teleporter falls 1.3 times a minute against 4 with fists: the blink recovery works.

**Tests.** New `tests/Rangbaazi.Sim.Tests/MachineWeaponTests.cs`: the Chicken (two pecks in a second, gone at the end; a bat pops it), the Anvil (hangs 45 frames, then hits both fighters under its mark at 80 degrees and is gone on landing; on the highest platform of the tallest arena it still appears inside the blast zone and falls), the Sonic Speaker (pulses only on frames that are multiples of 45, in front only, never at its owner; a second removes the first; two jabs break one; two speakers never shove one fighter twice within 45 frames, the lower slot's pulse counts; never at a teammate; not at someone in the air over the pit, but over the stage yes; a fighter recovering from 4 m outside a ledge against one facing out gets back, on the Graybox from two heights and on a 16 m test floor), the Portal Disc (through one gate and out of the other standing still with its jumps back, no bounce back, the mark cleared on stepping off; a lone gate does nothing; a third closes the first and the opener isn't sent back; over the pit, 6 m up and beside the blast line it fizzles and spends nothing), the Decoy Crate (looks like the last box's weapon, a box's size, falls and rests; its owner walks over it, another fighter is punched up and the decoy is gone; the Belan before any box; 20 s; a teammate, a dodger and an attacker don't spring it), the Crate Detonator (a rigged crate nobody springs; the next press bursts it and its owner's Decoy Crate with the Detonator's numbers, not another fighter's; it hits its owner standing in it), the Teleporter (5 m ahead; fizzles over nothing without spending; recovers from below the stage with its jumps back; Up + Light onto the platform above; Up + Heavy is still the recovery; a blink ends a root), the Swap Ray (swaps, soft; refused beside the blast line for either fighter; refused when the target stands in a hazard that is warning, done while it is idle), `SafeSpot` (heights, the pit, the blast line, each hazard phase, hazards off), and the CPU (blinks back from off the stage; throws the Teleporter on it; presses the Detonator with an opponent by its crate, not otherwise, and not while it stands by one of its crates; walks to another's decoy, not its own). `PlainWeaponTests`' shove test covers the Sonic Speaker from 3 and 5.5 m on two stages. `DeterminismTests` has the full rollback test: every status running, one prop of each of the 17 flights and the props about to change role (a Matka and a Lota that land and leave a wall and a puddle, a Bowling Ball about to rest, a box a Hockey Stick flings), saved every 10 frames over the first 160, advanced 120, loaded into a state that has moved on and replayed, checksum for checksum, and the same start twice. `GameStateCodecTests` now checks that its full prop table has all 17 flights, with a speaker's -1 facing, a decoy's weapon, a gate's marks and an anvil's hang. The full-room test (fifteen Hard CPUs, room rules, every weapon, 3,600 frames) also checks one speaker an owner and nobody held while in the air.

Earlier tests changed for this batch: the full-room test allows a box or loose weapon flung by a swing (a thrown shot carrying its supply) in a box slot, which batch 4's fling does by design and which this batch's weapon list first made happen in that run; the every-weapon loop counts a Decoy Crate sprung on the target at once and a blink that moves its wielder; the flight-flag table test gives a speaker, gate or decoy its row's life (their fuse is 0). The goldens were rewritten with 100 weapons (`all_weapons_*` are 9,000 frames, the room ones 9,060), and the GDScript replay matches all fifteen.

### Batch 6 (2026-11-21)

Tuning, polish, rooms and documents, over all 100. No engine part, state or frame changed: `SIM_VERSION` stays 8 and the arena protocol 5.

**The measure** (`tools/measure_weapons.gd`) now counts what section 4.2 asks of the utility rows, from the state, not the sim:

- **Effect events.** With hazards off, no boxes and the other three on fists, every status and every shove comes from fighter 0's weapon. An event on a fighter is a frame where it takes a hit credited to the weapon, a status timer goes up (slow, root, mark, poison from fighter 0, a new hold), or it moves more than 0.02 m beyond its own speed (a drag, a gate, a swap, a wall; frames in hit-stop are skipped). One fighter counts at most one event every 30 frames, so a stream held on someone for two seconds is about four events. Printed per crate on the other three, and on the wielder (its own hits and statuses only: its own blink or gate trip is the point of those weapons).
- **How each crate left the hand:** used up (it left empty; an empty weapon is thrown by the attack buttons too), thrown with supply left, or lost in a fall.
- For a row with `armedOnly` (the Magnet Gun) the other three are kept holding a Belan, or the gun has nobody to work on. In that run fighter 0 sometimes swaps the gun for a Belan box that a fallen fighter's weapon came back in (2.2 times a minute, printed as "thrown"); the CPU never throws it.
- Fists are the baseline, as before. A measured row depends only on its own numbers (the CPUs are deterministic and only fighter 0 is armed), so rows can be measured alone and in parallel: all 100 take about three minutes in eight processes.

**Changed** (`tools/weapons.json`, then the generator and the goldens). Damage rows below about 20 a crate were raised by damage first, then supply, then startup; the two of the 88 over 75 were cut by supply, and the AK-47 at 77.5 too; utility rows under 3 events by supply. Measured before and after, a crate:

| Weapon | Change | Before | After |
|---|---|---|---|
| Boxing Gloves | supply 12 to 10 | 80.9 | 54.5 |
| Plank | damage 12 to 16 | 18.4 | 20.8 |
| Stick Grenades | damage 16 to 22, supply 3 to 4, blast 3 to 3.5 m, fuse 75 to 50 | 10.6 | 19.9 |
| Mouse Trap | damage 4 to 8, supply 3 to 4 | 9.4 | 20.9 |
| Chimta | damage 4 to 6 | 16.3 | 24.6 |
| Jhaadu | damage 6 to 8 | 15.4 | 21.7 |
| Balti | damage 11 to 17, startup 13 to 11 | 12.5 | 26.2 |
| Chai Kettle | damage 2 to 3 a tick | 16.5 | 26.5 |
| Steel Tumbler | supply 5 to 4 | 80.0 | 50.1 |
| Gas Cylinder | damage 20 to 24, startup 20 to 15 | 11.5 | 20.5 |
| Pressure Cooker | damage 15 to 21, startup 36 to 28, supply 2 to 3 | 14.7 | 21.8 |
| Chicken | damage 5 to 6 a peck | 18.7 | 25.3 |
| Trout | damage 7 to 9 | 15.4 | 24.5 |
| Sniper Rifle | damage 22 to 28, startup 40 to 32 (its kick, `lungeAt`, moved with it) | 12.8 | 25.2 |
| Sledgehammer | damage 24 to 28, startup 26 to 16, recovery 30 to 24 | 6.7 | 18.6 |
| Anvil | damage 24 to 26, supply 2 to 3, size 0.8 to 1.0 | 10.5 | 6.3 (section 9) |
| Wrench | damage 11 to 12 | 18.3 | 20.9 |
| AK-47 | supply 18 to 16 | 77.5 | 65.0 |
| Health Leech | damage 2 to 3 a tick, supply 20 to 24 | 12.0 | 24.4 |
| Venom Trap | damage 2 to 7 | 14.4 | 22.5 |
| Pichkari | damage 1 to 2 a tick | 11.9 | 25.4 |
| Dhol | damage 6 to 8, supply 5 to 6 | 14.1 | 22.4 |
| Decoy Crate | damage 14 to 16 | 18.2 | 21.8 |
| Spring Glove Launcher | damage 9 to 16, supply 2 to 4 (its shove numbers unchanged) | 10.8 | 17.8 |
| Sonic Speaker | damage 3 to 4 a pulse | 16.7 | 24.8 |
| Jugaad Gauntlet | punch 8 to 10, bolt 5 to 7 (hidden rows 102 and 103) | 17.5 | 20.1 |
| Net (utility) | supply 2 to 7, size 0.8 to 1.0 | 1.4 events | 3.7 |
| Magnet Gun (utility) | none: now measured with armed targets | 0.4 events | 4.8 |
| Colour Powder Pouch (utility) | supply 5 to 7 | 2.8 events | 4.3 |
| Banana Peel (utility) | supply 4 to 5 | 2.7 events | 3.1 |
| Swap Ray (utility) | supply 3 to 6 | 2.9 events | 3.6 |

- **No `cpuMin` was added.** No weapon hurts its wielder more than the others: the worst are the Cherry Bombs (184 Josh a minute on the thrower against 445 on others), the Mines (147 against 411) and the Steel Tumbler (121 against 1,195). Among the utility rows the Glue Gun touches its wielder most (2.6 events a crate against 6.2 on others).
- Tried and put back: the Anvil with a 30-frame hang, a faster fall, no carry penalty or a 3.5 to 4.5 m CPU range (no better than noise); Stick Grenades at 26 damage with the 75-frame fuse (10.6: the fuse, not the damage, was the limit).
- Left as they are, a little under 20: Sil-Batta 19.5, Cricket Bat 19.1, Chainsaw 19.8, and the Bowling Ball 19.8 (one of the twelve). The Trampoline does no damage, by design (v0.3). The Bow (75.5) is one of the twelve and was left. None of the twelve changed.
- **The Teleporter** is the one utility row the CPU throws (39.7 of 41.8 crates, by section 2.9's rule: on the stage it throws it; off the stage it blinks back, and its wielder falls 1.3 times a minute against 4 with fists). Its effect is on its wielder by design, so section 4.2's test for it is the recovery, which it passes.
- The tests that pinned a tuned number now read it from the table (`Weapons.Get(...).Damage`, `.Supply`, `.Startup`, `.Fuse`), so later tuning moves only the goldens. The new `WeaponTableTests.EveryLungeAndKickStillLandsInItsMove` checks every kick still comes on the frame its shot leaves (the Sniper Rifle's would not have, when its startup moved). The 0-Josh shove tests and the ledge tests are unchanged and pass at the new numbers. The goldens were rewritten.

**Final measure** (`-- 6 rooftops`, all 100; damage rows: damage a crate; utility rows (u): effect events a crate on the others, and on the wielder):

| Weapon | a crate | Weapon | a crate | Weapon | a crate |
|---|---|---|---|---|---|
| Belan | 21.7 | Sil-Batta | 19.5 | RPG | 26.5 |
| Baseball Bat | 27.1 | Pankha (u) | 4.2 fx (0.0 own) | Grenade Launcher | 32.1 |
| Bowling Ball | 19.8 | Charpai | 21.7 | Cannon | 32.2 |
| Shotgun | 39.4 | Cricket Bat | 19.1 | Health Leech | 24.4 |
| Uzi | 65.1 | Hockey Stick | 21.1 | Venom Dart Launcher | 37.9 |
| Cherry Bombs | 38.1 | Cricket Ball | 38.3 | Toxic Goo Launcher | 29.7 |
| Bow | 75.5 | Badminton Racket | 51.9 | Stink Cloud Capsule (u) | 3.1 fx (0.7 own) |
| Boomerang | 55.0 | Pitchfork | 22.3 | Venom Trap | 22.5 |
| Trampoline | 1.4 | Rake | 21.8 | Ray Gun | 57.5 |
| Mines | 37.9 | Chicken | 25.3 | Freeze Ray | 24.1 |
| Homing Missile | 37.5 | Trout | 24.5 | Taser | 26.6 |
| Black-Hole Bomb | 28.5 | Net (u) | 3.7 fx (0.0 own) | Minigun | 41.0 |
| Boxing Gloves | 54.5 | Lathi | 29.4 | Flamethrower | 47.1 |
| Slingshot | 40.2 | Gada | 22.7 | Pichkari | 25.4 |
| Plank | 20.8 | Talwar | 26.4 | Colour Powder Pouch (u) | 4.3 fx (0.1 own) |
| Ninja Stars | 38.5 | Chakram | 68.3 | Dhol | 22.4 |
| Stick Grenades | 19.9 | Katar | 65.8 | Snowballs | 25.9 |
| Mouse Trap | 20.9 | Katana | 30.5 | Confetti Cannon | 23.3 |
| Thali | 38.0 | Broadsword | 21.7 | Decoy Crate | 21.8 |
| Tawa | 69.9 | Spear | 30.8 | Crate Detonator | 34.7 |
| Chimta | 24.6 | Trident | 21.3 | Banana Peel (u) | 3.1 fx (0.5 own) |
| Chakla | 26.2 | Cutlass | 44.4 | Glue Gun (u) | 6.2 fx (2.5 own) |
| Chappal | 67.1 | Crossbow | 45.1 | Spring Glove Launcher | 17.8 |
| Jhaadu | 21.7 | Blow Darts | 24.5 | Teleporter (u) | 0.8 fx (0.0 own) |
| Balti | 26.2 | Sniper Rifle | 25.2 | Dark Energy | 27.0 |
| Bathroom Mug | 26.8 | Chainsaw | 19.8 | Sonic Speaker | 24.8 |
| Lota (u) | 3.6 fx (1.3 own) | Sledgehammer | 18.6 | Gravity Gun (u) | 4.0 fx (0.0 own) |
| Tiffin Stack | 35.4 | Anvil | 6.3 | Portal Disc (u) | 3.5 fx (0.0 own) |
| Chai Kettle | 26.5 | Wrench | 20.9 | Jugaad Gauntlet | 20.1 |
| Steel Tumbler | 50.1 | Magnet Gun (u) | 4.8 fx (0.0 own) | Swap Ray (u) | 3.6 fx (0.0 own) |
| Parat | 29.4 | .357 Magnum | 31.5 | Thunder Hammer | 32.9 |
| Matka | 25.6 | Tommy Gun | 58.2 | Rubber Chicken Launcher | 31.3 |
| Gas Cylinder | 20.5 | AK-47 | 65.0 |  |  |
| Pressure Cooker | 21.8 | Burst Rifle | 59.9 |  |  |

**Drawn and heard**

- **Bursts** are worked out in one place, `client/game/burst_watch.gd` (`BurstWatch`), from the state alone, so a room sees them too: a bomb, missile or black hole that is gone, a mine with a blast (not a trap, which bites), and a decoy only when its owner is pressing the Crate Detonator (a sprung Decoy Crate is a punch). A bomb that becomes its zone, wall or pad has burst. `ArenaView` draws the flash (purple for a black hole, a pale puff for a burst with no damage, which only opens) and `SoundDirector` plays `boom` for every burst, louder for several on one frame, and a soft high pop for one that only opens. Before, the boom counted bombs, mines, missiles and holes on the stage, so a trap or a mine that ran out boomed and a detonated crate didn't.
- **A trap's jaws.** A fighter rooted by a `mine`-flight row (the Mouse Trap) has steel jaws shut on its feet; any other root keeps the net. That was the last status still drawn as a placeholder.
- Everything else in section 5 was already drawn by the batch that built it (zones by colour, the cloud, the cracking wall, speaker rings, gate swirls, the decoy mark, statuses, spray cones by effect, blast flashes; repeating weapons and sprays play their sound per shot or tick). **Not drawn:** "slower dust" for a slowed fighter: the game draws no running dust to slow, so slow stays a blue tint.
- `client/tests/test_presentation.gd` (new): every crate weapon has a HUD name, a touch verb that fits the Light button at its font size, four distinct one-word nickname words, a pose clip, a held length, a sprite and a sound; every weapon id (and its master code) gives a winner a nickname from its words; and `BurstWatch`: a bomb that moves is not a burst and one that is gone is, with its blast; a trap and a sprung decoy never burst; a detonated decoy bursts with the Detonator's blast; a capsule that becomes its cloud only opens. Staged screenshots were checked (scratchpad `shots_b6/b6_12.png`, `b6_18.png`), and `shot_weapons.gd` brawls of 4 and of 8.

**Rooms.** `room_e2e.gd` against a local relay passed (5 fighters, a weapon box arrived, protocol 5). A host with 14 CPUs played 3 minutes on the local relay for the STATE size in section 9.

## 12. Review of the build, and as built

A review of the finished 88-weapon build (2026-11-21) raised thirteen points. Each was checked against the code (read, or reproduced with a test) before anything changed. Where this section and an earlier one disagree, this one is what is in the code. `SIM_VERSION` stays 8 and the arena protocol 5: no state, field or frame layout changed.

### 12.1 Fixed in both sims

| Point | Real? | What changed | Test |
|---|---|---|---|
| A guard was hit by fast shots fired straight at its front about 1 time in 7 | Yes: a bullet at 0.7 to 0.8 m a frame is often first seen overlapping the guard after it has passed the guard's middle, and was then judged by its position to come from behind | `Simulation.ShotFromFront` / `shot_from_front`: a shot's side is the way it moves (towards the guard's face); with no sideways speed, where it is. Bursts are still judged by their centre | C#: every 5 cm of one frame's travel for the Uzi and the Crossbow, from the front (all blocked) and from behind (all hit). GDScript: the same sweep |
| A guard at 0 supply went on blocking for free | Yes | `Guarding` is false at 0 supply (unless the row's `spend` is `never`); the Parat's tray is no longer held up at 0, so the bash comes out at once | C# and GDScript: a Tawa at 1 blocks one bullet and is hit by the next; a Parat at 1 bashes at once after its last block |
| Thrown and flung weapons took on their row's shot columns (`brk`, `edge`, `noSelf`) | Yes: `ShotRow` fell back to the weapon's own row for a `thrown` prop, so a flung Mouse Trap, Venom Trap, Chicken, Dark Energy or Speaker crate could be broken (and left the match), a thrown Chakram or Steel Tumbler bounced off the screen edge, and a thrown Dark Energy never hit its thrower | `ShotRow` gives a `thrown` prop the empty row 0. Its damage was already its row's `throw` and its box `ThrownHalf` | C# and GDScript: those five flung weapons are not breakable and survive a Belan swing and a Cherry Bomb burst; a thrown Chakram and Steel Tumbler fly on past the screen edge; a thrown Dark Energy hits its thrower |
| The Sledgehammer couldn't break Banana Peels, Decoy Crates or rigged crates | Yes: those rows had no `brk` | Banana Peel `brk` 1 (like the other two traps); Decoy Crate and the Detonator's rigged crate `brk` 2 (like the speaker: two swings, so a decoy can be popped but not by one stray jab; a burst of the other side breaks them too) | C# and GDScript: the Sledgehammer breaks a peel, two traps, both decoys, a speaker and a wall in one swing; two Belan swings pop a decoy |
| The Anvil missed: it always fell 4 m ahead, about 89 frames after the press | Yes | `DropSpot(state, rules, index)`: the mark is over the nearest opponent in front (at most 8 m ahead and 9 m up or down, not a teammate, not hidden in a cloud), at their x plus their speed times 40 frames, kept 2 to 6 m ahead; with nobody to aim at, 4 m ahead as before. 40 frames was measured against 0 and 80 (Graybox 28.7, against 23.3 and 28.1) | C# and GDScript: the mark for a standing target, the nearer of two, a target walking away and walking in, fist range, 7.5 m, 9 m, behind, past a teammate, and someone hidden in a cloud |

### 12.2 Table changes (`tools/weapons.json`, then `gen_weapons.py`)

| Weapon | Change | Rooftops a crate, before → after | Graybox |
|---|---|---|---|
| Stick Grenades | vx 0.2 → 0.14, fuse 50 → 40, damage 22 → 26. It now goes off at the end of its arc, about 6 m out at hand height, so its 3.5 m blast covers the 3.5 to 9 m the CPU throws it from; before, it slid to about 9.7 m and caught nobody nearer than 6 m | 19.9 → 17.4 | 7.8 → 60.6 |
| Spring Glove Launcher | startup 22 → 16 (shove numbers unchanged; it has no lunge, so the kick test is unaffected) | 17.8 → 31.0 (its wielder's falls 6.8 → 2.7 a minute) | 30.0 → 32.2 |
| Banana Peel, Decoy Crate, Crate Detonator | `brk` 1, 2, 2 (above) | peel 3.1 → 3.05 effect events; Decoy Crate 21.8 → 21.8; Detonator 34.7 → 24.6 (its rigged crates can now be broken before they go off) | |

The Stick Grenades change alters the weapon's character (section 2.3 said it lies there counting down); its row there is updated. `PlainWeaponTests.AStickGrenadeCountsDownItsFuseFromTheThrow` now checks it is about 6 m out and still in the air when its fuse ends.

### 12.3 Golden traces and GDScript tests

| Point | Real? | What changed |
|---|---|---|
| The all-weapons goldens never fired several rows (the Teleporter's blink, the Gauntlet's bolt, the three thrown-as rows), and the stock traces ended early (`all_weapons_random_4p` at frame 2,211, `all_weapons_bots_4p` at about 3,500 to 4,150, of 9,000) | Yes, reproduced by counting per row | Both stock traces play with 99 stocks, so nobody is out before the end. `HoldLight` now drives fighters 0 to 2 (it was 0 and 2): a fresh Light press every 30 frames, Up held through every other press (the upward blink), and one of each three neighbouring fighters (who get the same weapon a window apart) throws it from frame 60 of its window with fresh Drop weapon presses. `GoldenTraceTests` counts, frame by frame from the state, what each trace plays, and fails if the all-weapons random trace leaves any built row unplayed (now none of the 107 is), has no blink ahead or upward, or no thrown Charpai pad, or if a 99-stock trace ends early. It checks this both when writing and when comparing |
| No golden reached the placed limit of 2, a full prop table, or a fizzle | Yes (`room_random_15p` never had a placed prop out) | A new golden, `big_room_props_15p`: 15 fighters under room rules on Rooftops, all driven, each handed a weapon every 30 frames from two lists (even fighters: shots that leave nothing behind, with an Uzi for bullets; odd fighters: pads, gates and a decoy in turn with such shots), pressing every 6 frames. Checked while written: 72 placements at the limit of 2, the table full on 81 frames, 6 shots put over a bullet, 10 over the firer's own placed prop, 4 fizzles. Replayed by `test_golden_traces.gd` |
| The GDScript suite had no behaviour tests of its own for these paths | Yes | `client/tests/test_weapon_rules.gd` (14 tests): the review fixes above, and the rarely reached paths: a blink ahead, upward and over nothing; a Chimta tip disarm and a close pinch; the Gauntlet's pull, punch and bolt with the bolt's hidden row and the credit; a thrown Gas Cylinder, Charpai (and its pad's life and launch) and Spear as rows 105 to 107; a shot into a full table that fizzles, keeps a Bowling Ball in the hand, replaces the oldest bullet, or its firer's own oldest placed prop; two placed props in a match of nine. Each mirrors a C# test. Checked with the guard and thrown-prop fixes reverted: their four tests fail |

Disarms are counted in the traces but not required: random play lands a Chimta tip on an armed fighter too rarely (0 in the all-weapons random trace), so the GDScript test covers it. Upward blinks in that trace are few (1 of 6 blinks); the check asks for at least one.

### 12.4 Client

| Point | Real? | What changed |
|---|---|---|
| Burn flames drew a degenerate triangle ("Invalid polygon data, triangulation failed") | Yes, by reading the code: `h` goes to about 1e-7 px when `t` is just under 1 | `_draw_statuses` skips a flame under 0.5 px. `--smoke --all-weapons` passes |
| `room_session.gd`'s comment on `main_weapons` described the old encoding | Yes | It now says: a weapon id (0 is fists), or 127 (`Nicknames.ALL_ROUNDER`), +128 for a master |
| STATE status bits 32 (hidden) and 64 (guarding) are sent but nothing reads them, though the comment said the view did | Yes | The comment now says the view works them out from the decoded state (`is_hidden`, `guarding`), which gives the same answer. The bits are still sent: dropping them changes the frame layout and the protocol to save one `IsHidden` pass per fighter per frame on the server, not worth a protocol bump now |

### 12.5 Not changed

- **The Anvil stays in crates** at 6.5 a crate on Rooftops (section 9). Taking it out would contradict "all weapons drop"; that is the user's call.
- **Upward blinks and the bolt in the goldens rest on few events.** A future scenario change that loses them fails `GoldenTraceTests` with the reason rather than passing silently.

### 12.6 As built

- **Built:** all 100 weapons (88 new, the twelve of v0.3) and the 7 hidden rows, in both sims, drawn, heard, used by the CPU and dropped by crates; `SIM_VERSION` 8, arena protocol 5. Sections 1 to 10 hold, with the differences recorded in section 11 (per batch) and here.
- **Differs from the design:** the Stick Grenades burst in the air at the end of their arc (2.3); the Anvil aims (2.9); a guard judges a shot by its motion and is down at 0 supply (2.7); thrown weapons read none of their row's shot columns (2.7); peels and decoys break (2.8, 2.9); the Spring Glove's startup is 16 (2.2); and the per-batch differences in section 11.
- **Measured balance** (`tools/measure_weapons.gd -- 6 rooftops`, all 100 rows, after this pass): every damage row of the 88 but the Anvil does 17.4 (Stick Grenades) to 68.3 (Chakram) a crate; the Anvil 6.5; the twelve 19.8 to 75.5, the Trampoline none; utility rows 0.8 (Teleporter, whose effect is on its wielder) to 6.2 effect events a crate on others. Against section 11's final table, the rows that moved: Stick Grenades 17.4, Spring Glove 31.0, Parat 29.1 (29.4), Tawa 67.7 (69.9), Chicken 25.1 (25.3), Crate Detonator 24.6 (34.7), Anvil 6.5 (6.3); the rest are unchanged to the decimal.
- **Not done:** the live relay still runs arena protocol 4, so relay and client must be deployed together; nothing was deployed, committed or pushed. The room lobby side of teams (choosing teams, team scores, the team win rule) is the rooms work. "Slower dust" for a slowed fighter is not drawn. None of the 88 has an owner (signature fighter) yet.
