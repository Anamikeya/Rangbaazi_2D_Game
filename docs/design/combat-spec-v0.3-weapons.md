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
| Fist flurry and winner nicknames by weapon | in v0.2 | **not built yet** (see the end) |

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
- **Versions:** `Simulation.SIM_VERSION` is 3.

## Not built yet

These come from v0.2 and the GDD:

- the fist flurry (Light + Heavy together while unarmed);
- weapon-based winner nicknames, which need per-weapon damage, KO and sweet-spot counters;
- weapon swing animations from the art pack (the game turns the sprite through the swing for now);
- sweet-spot effects and hit reactions (dizzy stars, soot);
- the other 93 weapons;
- match settings to turn weapons off.
