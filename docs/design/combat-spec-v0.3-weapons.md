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

## Not built yet

These come from v0.2 and the GDD:

- weapon swing animations from the art pack (the game borrows a fist move's poses for now);
- hit reactions drawn on the fighter (dizzy stars, soot);
- hazards that slip, slow or break (they push or hit for now);
- the other 93 weapons;
- match settings to turn weapons or hazards off.
