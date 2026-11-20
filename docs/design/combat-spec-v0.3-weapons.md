# Combat spec v0.3: weapons, as built

This spec updates `combat-spec-v0.2-weapons.md`, which was approved on 2026-11-12. It records what was built on 2026-11-18 and where the build differs from v0.2. The user asked for three things:

- weapon spawns fast enough for a very fast-paced game;
- every weapon with its own ability, so guns shoot and the ball rolls;
- each fighter slightly better with their own weapon.

The code is in two places: the C# reference in `src/Rangbaazi.Sim/Weapons.cs` and `SimulationWeapons.cs`, and the GDScript port in `client/sim/weapons.gd`, `prop.gd` and `simulation.gd`. The golden traces check that both behave the same. Every number below is a first guess, to be tuned in playtests.

## What changed from v0.2

| Topic | v0.2 | Built (v0.3) |
|---|---|---|
| First box | 5 s | **1.5 s** |
| Box interval | 8–12 s | **2.5–4.5 s** |
| Boxes and loose weapons on stage at once | 3 | **3 + 1 per 2 fighters, up to 8** (4 in a duel, 8 in a full room) |
| Drop height | 2 m below the blast zone top | **6 m above a platform**, so a box lands in about half a second |
| An untouched box or loose weapon vanishes after | 15 s | **12 s** |
| A dead fighter's weapon comes back after | 2 s | **1 s** |
| Weapons | 6 | **7**: the six, plus the **Bow** |
| Own-weapon bonus | not in v0.2 | **+10% damage** with your own weapon (the GDD B4 "weapon affinity" number) |
| Belan sweet spot | the head box | **bonking someone mid-attack** (the head box covered almost every hit) |
| Bat sweet spot | hitbox x 1.3–1.8 | **the tip: the target's near edge 1.3 m or more away** |
| Shotgun sweet spot | hitbox x 0.4–1.6 | **point blank: the target's near edge within 1.2 m**; beyond 3 m it does 10 |
| Bat, shotgun, ball speed | 16 / 12 / 20 startup | **12 / 12 / 14 startup**, with shorter recoveries |
| Picking up | any time | **not during your own attack**, so a weapon can't change in the middle of a move |
| Fist flurry | in v0.2 | **built 2026-11-19**, as v0.2 specifies (`MatchRules.Flurry`; on in practice, duels and rooms) |
| Winner nicknames by weapon | in v0.2 | **not built yet** (see the end) |

Everything else in v0.2 is built as written:

- touch to take and touch to swap;
- the swapped-out weapon is free for anyone, and ignores whoever dropped it for 1 s;
- you keep your weapon when you're hit;
- one attack per weapon, on either button;
- Up + Heavy stays the recovery;
- Drop weapon throws your weapon, once, and it's gone;
- an empty weapon is thrown by the attack buttons;
- everything can hit the fighter who threw it;
- the release grace;
- parrying a projectile destroys it;
- the bat bats projectiles back at 1.25× speed;
- sweet spots do 1.25× damage and base knockback, with the maximum hit-stop.

## The weapons and their abilities

| Weapon | Ability | Supply | Sweet spot | Owner (+10%) |
|---|---|---|---|---|
| Belan | A quick bonk that dizzies (+10 hitstun) | 12 hits | Bonking someone mid-attack dizzies 6 frames longer | Akshat, the chef |
| Baseball Bat | A two-handed swing that bats projectiles back | 8 hits | The tip launches flat at 20°: the "six" | Shubham, the gully slugger |
| Bowling Ball | Rolls along the ground through everyone, then lies there for anyone to roll again | never runs out | "Strike!": every fighter after the first | Shan, the street brawler |
| Shotgun | A short blast that kicks the shooter back | 4 shells | Point blank | Salik, the robber |
| Uzi | Fires every 5 frames while held. You can walk at half speed while firing. Bullets chip without freezing the target | 30 rounds | 6 hits in a row on one target: 3 damage, and the stream holds them | Rishabh, the tech gunner |
| Cherry Bombs | Lobbed, with a 50-frame fuse. A bomb bursts on touching anyone but its thrower; the 2.4 m blast hits the thrower too | 3 bombs | A direct hit | Ankit, the clown |
| Bow | A fast arrow that dips slightly and pierces everyone in its path | 6 arrows | A long shot: 20+ frames of flight, about 11 m | Suhani, the rooftop archer |

**The Bow (GDD B5, "Bow & arrows: charge for pierce").** It has no charge, because each weapon has one attack, so every arrow pierces.

- Startup 10, recovery 14.
- 10 damage, base knockback 0.15, growth 0.0024, launched at 20°, +2 hitstun.
- The arrow flies at 0.55 m/frame and dips by 0.004 m/frame², for up to 50 frames.
- Arrows ignore platforms.

**Owners.**

- Only 7 of the 31 fighters own one of these weapons. The rest get theirs as their own weapons are added; the art pack gives each fighter three reward weapons from its 100.
- The bonus is set by the fighter's character. Practice uses it for both fighters, and rooms use it for every player and CPU.
- 1v1 duels have weapons but no bonus yet, because the two players don't send each other their characters.

## Throwing, controls and online

- **Throw** (Drop weapon) is:
  - U on the keyboard (Numpad 4 for player 2);
  - LB on a gamepad;
  - a new Throw button on the touch controls.
- **Rooms:** STATE frames now carry each fighter's weapon and supply, plus every box, loose weapon and projectile on the stage, at 12 bytes each.
  - The arena protocol is version 2. Clients send `v=2`.
  - An older client is told to refresh the page or update the app, so it never misreads the frames.
- **Versions:** `Simulation.SIM_VERSION` is 4 (3 added weapons, 4 the fist flurry).

## Added 2026-11-19

**The record of hits.** Every hit now records the weapon it came from and whether it was a sweet spot (`Fighter.LastHitWeapon`, `LastHitSweet`, `HitCount`). The match also keeps each fighter's damage, KOs and sweet spots per weapon (`GameState.WeaponStats`, with fists as weapon 0). A KO is credited to the weapon of the last hit.

- **Sweet-spot callouts:** the game shouts the sweet spot over the fighter it landed on: BONK!, SIX!, STRIKE!, POINT BLANK!, ON TARGET!, DIRECT HIT!, LONG SHOT!
- **Weapon nicknames:** built as v0.2 specifies.
  - The winner's main weapon is the one that did the most of their damage, if that is at least 40% of it. Otherwise they are an all-rounder.
  - They are a master with 3 sweet spots and a KO with it.
  - Bow words: Arrow, Bullseye, Longbow, Quiver.

**Arena hazards.** Each arena's three hazards (from the art pack's layouts) run on a shared 10-second clock: 7 s idle, 0.75 s of warning, 1 s active, then cooling down. Each hazard runs 200 frames ahead of the one before it. A hazard's phase is a pure function of the match frame, so hazards add nothing to the rollback state.

The pack's 48 hazard types are grouped into three behaviours (`tools/gen_arena_stages.py` holds the mapping):

| Kind | Examples | While active |
|---|---|---|
| Launch (19) | spring, bounce pad, steam, lift | Flings a fighter standing in it into the air at 0.36 m/frame. No damage |
| Hit (36) | shutter, crusher, laser, burner, bell | Hits everyone in it every 20 frames: 8 damage, launched up and away at 60°. Credits nobody with a KO |
| Push (38) | gust, conveyor, magnet, oil, mud | Carries everyone in it 0.07 m/frame towards the middle of the arena. No damage |

- A zone is as wide as the layout says and 2 m tall, on its platform.
- A dodging fighter is untouched.
- Hazards are on in practice and rooms (`MatchRules.Hazards`). Duels use the graybox stage, which has none.
- The slippery and sticky types (oil, mud, ice, slime) are pushers for now; real slipping and slowing would need movement changes.

**The CPU and low steps.** The CPU now jumps after an opponent standing 1 m or more above it (it was 1.5 m). Before, it stood still under anyone on a step between 1 and 1.5 m up.

**Controls.** One key or button presses Light and Heavy together for the fist flurry: I on the keyboard (Numpad 5 for player 2), B on a gamepad, and on touch the small button, which is Combo empty-handed and Throw with a weapon.

**Versions:** `Simulation.SIM_VERSION` is 5, and the arena protocol is version 3 (STATE carries each fighter's last hit and main weapon).

**Screen-edge ring-outs** (asked for "like Chaos Faction"). The camera never shows past the 32 m of painted arena: its widest view is exactly the arena's width, so the screen is always full of arena, and it pans up and down to keep fighters in view. The side blast zones moved in from 26 m to **17.2 m**: a fighter is out once their body (0.8 m wide) has gone off the side of the screen by its own width. Top and bottom are unchanged. In a four-bot test this means a KO about every 5 s, at 23–81% Josh. `Simulation.SIM_VERSION` is 6.

## Added 2026-11-20: five weapons with new behaviours

The user asked for more of the art pack's 100 weapons, and only ones that do something the first seven don't. These five are in crates with the same odds as the rest. None has an owner yet. Every number is a first guess.

| Weapon (pack key) | What it does | Supply | Sweet spot |
|---|---|---|---|
| Boomerang (w055) | Flies 10 m out in 40 frames, stops, and flies back to its thrower's hand. It passes through everyone and can hit each fighter once on each leg. 8 damage | 4 throws | "Comeback!": a hit on the way back |
| Trampoline (w007) | Put down at the feet, it lies for 10 s and bounces up (0.42 m/frame) anyone touching it who isn't already rising. No damage | 3 pads | none |
| Mines (w070) | Planted at the feet. Armed after 0.75 s, a mine bursts on the first fighter to touch it, its planter included. One nobody finds bursts by itself after 15 s. 16 damage, 2.4 m blast. Planting is quick: 6 startup, 8 recovery | 3 mines | "Stepped on it!": the fighter who set it off |
| Homing Missile (w068) | Flies at 0.2 m/frame and turns a little each frame (0.008 m/frame²) towards the nearest fighter who isn't its owner. It passes through platforms, bursts on touching anyone but its owner, and bursts by itself after 4 s. 15 damage, 2.4 m blast | 2 missiles | A direct hit |
| Black-Hole Bomb (w098) | Lobbed like a cherry bomb, with a 110-frame fuse. It does not burst on touch. For the last 80 frames it drags everyone within 4.5 m sideways towards it at 0.11 m/frame (just under run speed, so running away gets you out), its thrower too; then it bursts: 20 damage, 4.2 m blast, launched at 70° | 2 bombs | none |

- **The boomerang in the hand.** Throwing it empties the hand; the boomerang carries the throws it has left. Its thrower catches it on the way back if their hands are empty. If it is not caught within 2.5 s, or its thrower is knocked out, it drops and lies there for anyone, with the throws it has left. After the last throw it comes back empty, and the next attack throws it away for good.
- **The owner's grace.** A trampoline doesn't bounce, and a mine doesn't burst on, the fighter who put it down until they have stepped off it once.
- **The bat.** A batted boomerang becomes the batter's and comes back to them. A batted missile chases everyone but the batter. Trampolines and mines can't be batted or parried.
- **Bursts.** A mine, a missile and a black hole burst the way a cherry bomb does: everyone in the box is hit, the owner included, and launched away from its centre.
- **The CPU** fires the boomerang from up to 8 m, the missile from 3–14 m and the black hole from 2.5–6 m. It uses trampolines and mines at fist range.
- **No new state.** The five use the fields a prop already had (`Timer` is a mine's arming time, a pad's life, a hole's fuse and a boomerang's leg), so nothing was added to the checksum.
- **Rooms.** Each prop in a STATE frame is now 13 bytes: the new last byte is its timer (capped at 255), so every screen can show a mine arming and a black hole starting to pull. The arena protocol is version 4.
- **Versions:** `Simulation.SIM_VERSION` is 7. Two new golden traces (`newer_weapons_random_4p`, `newer_weapons_bots_4p`) hand every fighter one of the five every 90 frames, so each is checked against the C# reference.
- **Look and sound.** Sprites are cut from the pack by `tools/cut_weapons.gd` (ids 8–12) and sounds by `tools/import_audio.py`. A burst draws a flash and a ring as wide as its blast.

### Tuned 2026-11-21

Measured, not played: four Hard CPUs for a minute on Rooftops and the graybox, six matches each, with one of them kept armed with a single weapon. The measure is the damage one crate's worth of a weapon does. The older weapons land between about 20 (Belan, bat) and 75 (bow), with the shotgun and cherry bombs near 40.

| Weapon | Before | Change | After |
|---|---|---|---|
| Boomerang | 62 per crate, 5.8 KOs a minute | 9 → 8 damage | 55, 4.2 KOs |
| Mines | 28 per crate, and the planter fell most of all | 14 → 16 damage, base knockback 0.19 → 0.20, planting 8/12 → 6/8 frames | 38 |
| Homing Missile | 37 per crate, but 10 hits a minute on its own shooter | The CPU no longer fires it from under 3 m. The missile is unchanged | 4 hits a minute on the shooter |
| Black-Hole Bomb | 15 per crate | 16 → 20 damage, base knockback 0.22 → 0.25, growth 0.0028 → 0.0030, pull 0.08 → 0.11 m/frame, blast 3.6 → 4.2 m, 1 → 2 bombs | 28 |
| Trampoline | no damage, by design | unchanged | |

- A wider pull (5.5 m) was tried for the black hole and dropped: it caught the thrower more than anyone else.
- The CPU still walks back onto its own mines.
- The same measure shows the bow and the bowling ball well ahead of the other older weapons in the CPU's hands. They were not changed here.

## Not built yet

These come from v0.2 and the GDD:

- weapon swing animations from the art pack (the game borrows a fist move's poses for now);
- hit reactions drawn on the fighter (dizzy stars, soot);
- hazards that slip, slow or break (they push or hit for now);
- the other 88 weapons;
- match settings to turn weapons or hazards off.
