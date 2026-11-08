# Combat spec v0.1: the first hero (graybox)

Addendum to GDD B2 ("Combat verbs", "Feel targets") and B4 ("Move-set template"). It covers what the graybox needs for the first blind playtest in GDD C3 ("decide whether the hybrid Josh + Guts model is fun"): the jab, four heavies plus the ground slam, dodge, parry, hit-stop and Guts.

The moves belong to hero 1 of the GDD B4 roster, the medium-weight supercop all-rounder. The code calls the move set `MoveSet.AllRounder`, after the archetype, until the hero's name has been through the same title and trademark check the GDD asks for in C3.

The code is the source of truth, and the tables are a snapshot of it:

- moves: `MoveSet.AllRounder` in `src/Rangbaazi.Sim/MoveData.cs`;
- dodge and parry: `FighterTuning`;
- Guts and hit-stop: `MatchRules` in `Simulation.cs`.

`SpecTests` fails if the frame data, the hitboxes or the KO table here stop matching the code. All numbers are placeholders to tune in playtests.

## Inputs

| Input | Ground | Air |
|---|---|---|
| Light (+ any direction) | Jab | Jab |
| Heavy | Neutral heavy | Neutral heavy |
| ← / → + Heavy | Side heavy (turns to face that way) | Side heavy |
| ↑ + Heavy | Up heavy | Up heavy, once per trip into the air |
| ↓ + Heavy | Down heavy | Ground slam |
| Dodge | Spot dodge | Air dodge, hangs in place |
| ← / → + Dodge | Dash dodge | Air dodge in that direction (8 directions with ↑ / ↓) |

Rules for inputs:

- Only a fresh press counts.
- One action per frame. Dodge beats Heavy, Heavy beats Light, and any of them beats Jump.
- A jump pressed on the same frame as an attack or dodge is ignored and not spent. So Jump + ↓ + Heavy on the ground is the down heavy, not a ground slam.
- ↑ wins over ← / → for heavies, so ↑ + → + Heavy is the up heavy.
- An up heavy that has already been spent does nothing; it never falls back to another heavy.

## Frame data

Frame counting:

- The press is frame 0.
- The hitbox is out on frames `[startup, startup + active)`.
- The fighter can act again on the frame after `total`.
- Hit-stop pauses this count (see below).

| Move | Startup | Active | Recovery | Total | Damage | Base KB | KB growth | Launch | Hitstun bonus | Hit-stop |
|---|---|---|---|---|---|---|---|---|---|---|
| Jab | 4 | 3 | 8 | 15 | 6 | 0.12 | 0.0030 | 45° | 0 | 5 |
| Neutral heavy | 14 | 4 | 22 | 40 | 14 | 0.16 | 0.00225 | 40° | 4 | 9 |
| Side heavy | 16 | 5 | 24 | 45 | 16 | 0.156 | 0.0023 | 30° | 4 | 10 |
| Up heavy | 6 | 8 | 26 | 40 | 10 | 0.17 | 0.0022 | 80° | 2 | 7 |
| Down heavy | 12 | 4 | 22 | 38 | 12 | 0.15 | 0.0018 | 25° | 2 | 8 |
| Ground slam | 10 | up to 40 | 14 | up to 64 | 13 | 0.17 | 0.0021 | 70° | 4 | 8 |

How the special moves behave:

- **Side heavy:** lunges forward at 0.16 m/frame on frame 10. On the ground friction stops it after about 0.7 m. In the air it brakes more slowly and carries about 1.5 m.
- **Up heavy:** the recovery move.
  - It rises at 0.27 m/frame on frame 6, gaining about 3.6 m; an air jump gains about 2.4 m.
  - The fighter can steer during it.
  - It spends the recovery on frame 6 rather than at the press, so starting it from the ground still spends it.
- **Down heavy:** sweeps low on both sides.
- **Ground slam:**
  - It hangs still through startup.
  - Then it plunges at 0.4 m/frame with the hitbox out until it lands, for at most 40 frames.
  - The landing frame still has the hitbox out (the impact), so a slam started just above the floor still hits.
  - The landing frame counts as the slam's last active frame, so its full 14 recovery frames follow. A neutral heavy pressed right after the impact lands before the slammer can act.
  - Using it off the stage is usually a self-KO.
- **Other moves:** attacks lock steering, so an airborne fighter keeps its drift. They don't end on landing; there is no landing lag yet.

Hitboxes are in metres from the fighter's feet, with x measured forward:

| Move | x | y |
|---|---|---|
| Jab | 0.2 to 1.1 | 0.6 to 1.4 |
| Neutral heavy | 0.2 to 1.4 | 0.4 to 1.6 |
| Side heavy | 0.3 to 1.5 | 0.5 to 1.4 |
| Up heavy | −0.3 to 0.9 | 0.8 to 2.4 |
| Down heavy | −1.3 to 1.3 | 0 to 0.7 |
| Ground slam | −0.5 to 0.5 | −0.4 to 0.6 |

The hurtbox is 0.8 m wide and 1.8 m tall.

### Knockback, hitstun, hit-stop

- **Knockback speed:** `base + growth × Josh after the hit`, in m/frame, at the launch angle, away from the attacker. Most moves launch in the direction the attacker faces. Moves whose hitbox also reaches behind (up heavy, down heavy, ground slam) launch away from the side the target is on.
- **Hitstun:** `8 + Josh / 8 + the move's bonus` frames.
- **One hit per move:** a move hits every target it touches on its first connecting frame, and then nobody else.
- **Hit-stop** (GDD B2: 4 to 10 frames scaled by damage) is `clamp(2 + damage / 2, 4, 10)` frames.
  - The attacker and the target freeze for it. Positions, action frames and timers all stop, so it also delays knockback and cooldowns.
  - `Fighter.FrozenByHit` says the freeze came from a hit the fighter took, as opposed to its own hit landing or a parry. The graybox uses it to shake the fighter.
  - A fighter can't hit anyone on a frame it spends frozen, so its hitbox waits for the freeze to end. This matters for an armored fighter who is hit in the middle of an attack.
  - A button pressed during the freeze and still held when it ends counts as a fresh press. A button that was already held when the freeze began does not.

### KO percents

This table is the lowest Josh, before the hit, at which each move takes the stock. `KoChart.MarkdownTable` produces it, and `SpecTests` keeps this copy in sync.

The setup:

- the graybox stage, with Guts off;
- the defender stands at the centre or 1 m inside the edge, with the attacker beside it doing the move once;
- a recovering defender, whenever it can act (including steering during its up heavy), presses off the stage what the CPU would (air jump, then up heavy, then air dodge, steering home), and over the stage drifts toward the centre;
- "no recovery" means the defender does nothing at all;
- a case ends when the defender loses the stock, or stands still on a platform able to act.

<!-- ko-table:start -->
| Move | From the centre | From 1 m inside the edge | From the centre, no recovery |
|---|---|---|---|
| Jab | 142% | 93% | 86% |
| Neutral heavy | 133% | 76% | 68% |
| Side heavy | 112% | 61% | 58% |
| Down heavy | 140% | 82% | 84% |
| Up heavy | 160% | 160% | 160% |
| Ground slam | 178% | 178% | 178% |
<!-- ko-table:end -->

The side heavy is the main ring-out move, and the jab is the slowest way to KO. The up heavy and ground slam KO off the top, late.

## Dodge (GDD B2: 0.3 s invincibility, 1 air dodge, 1 s cooldown)

- **Duration:** a dodge lasts 24 frames.
  - On frames 0 to 17 (0.3 s) attacks pass through: the attack isn't spent and can still hit after the invincibility ends.
  - Frames 18 to 23 can be hit.
- **Movement:**
  - While invincible, the fighter moves in a straight line with no gravity: dash dodges at 0.16 m/frame (about 2.9 m), spot dodges and neutral air dodges stand still.
  - Afterwards an airborne fighter keeps half that speed and falls, and a grounded one slows to a stop.
- **Air dodge:** one per trip into the air. Landing, being hit or a successful parry gives it back.
- **Cooldown:** 60 frames (1 s), counted from the end of the dodge. A dodge cut short by a hit still starts it, and being hit never refunds it. A parry is the only exit with no cooldown.

## Parry (GDD B2: "dodge timed into a hit, 6 frames")

- **The window:** a hit that lands on dodge frames 0 to 5 is parried.
- **The defender:**
  - Takes no damage, and the dodge ends at once with no cooldown. Parrying two attackers on the same frame counts once.
  - Gets the air dodge back, and keeps moving the way a dodge does after its invincible frames: half speed in the air.
  - So a fighter who parries while air-dodging back to the stage can still make it.
- **The attacker:** is **stunned** for 36 frames (0.6 s).
  - It stops moving sideways and stops rising. A parried up heavy drops instead of carrying on up, and an airborne attacker falls.
  - 36 frames is enough to land any heavy, so a parry is a guaranteed punish. It works on every move, including the jab.
- **Hit-stop:** both freeze for the maximum hit-stop (10 frames).
- **Hit and parried on the same frame:** if an attacker is parried by one fighter and hit by another, the hit wins and they are knocked back.
- **Taali:** the GDD's reward for a parry has to wait until Taali exists.

## Guts (GDD B2)

- **When it starts:** a hit that leaves the fighter at **150% or more** starts a 180-frame (3 s) countdown. It happens once per stock: later hits don't restart it, and it never starts twice in a stock.
- **The payout:** if the fighter still has that stock when the countdown ends, they get **30% Josh back** and **60 frames (1 s) of armor**.
- **Armor:** hits still add Josh and hit-stop, but cause no knockback or hitstun, so an attack in progress continues. A parry still stuns an armored attacker.
- **Losing the stock:** cancels a pending Guts. The next stock gets a fresh one.
- **Turning it off:** `MatchRules.Guts` turns the rule off. In the graybox, F4 toggles it and restarts the match, so playtesters can compare.

## Decisions to confirm in playtests

The GDD doesn't specify these. They are the first things to revisit if the playtest says combat feels wrong.

1. **The parry:** the window is the dodge's first 6 frames. A parry ends the dodge with no cooldown, gives back the air dodge, and stuns the attacker for 0.6 s, stopping its rise.
2. **Getting hit:** being hit gives back the air dodge and the up-heavy recovery, but never the dodge cooldown, which a dodge cut short by a hit still starts.
3. **Guts:** the countdown starts on the hit that crosses 150% and isn't restarted by later hits. Armor stops knockback but not Josh, hit-stop or a parry's stun.
4. **Hit-stop:** one formula for every move, and a parry uses the maximum.
5. **Knockback:** heavies use the same model as the jab. Their KO power comes from damage, angle and extra hitstun, plus a modestly higher base.
6. **One action per frame:** an attack or dodge beats a jump pressed on the same frame.
7. **Not yet:** there is no landing lag and no directional influence (DI).

## Not in v0.1

- light strings (3-hit ground, 2-hit air);
- directional and air lights;
- grabs and throws;
- weapon throws;
- wall jump and wall bounce;
- Taali and Signature Scenes;
- team combos;
- landing lag and DI;
- move sets for the other heroes.

## The CPU (graybox sparring bot)

`SparringBot` uses the new verbs so a player practising alone sees them.

- **Recovery:** the CPU recovers with the air jump, then the up heavy, then an air dodge.
- **Defence:**
  - It spot-dodges attacks that are about to reach it, timed to parry.
  - For a ground slam, that means when the plunge will get down to it. It ignores a slam that will land on a platform above it, or that starts below it.
  - It decides once per attack, on the frame it would press to parry, judging where it will be by the time the attack arrives (so it still dodges an attack it is walking into), and waits while the attacker is frozen in hit-stop.
- **Punishes:**
  - A neutral heavy on an opponent it has parried, while enough stun is left for the heavy to land; otherwise a jab.
  - A heavy into the recovery of a whiffed move, if one lands before the opponent can act again: the side heavy if it fits and there's room, otherwise the neutral heavy. That includes a slam that landed and missed.
- **Finishers:** once the opponent is at 70% or more, 1 attack in 3 is a heavy. That is the side heavy when there is room to lunge on the platform it stands on, and the neutral heavy near any edge.

The level changes how it plays:

| Level | Attacks once in reach (avg. wait) | Reaction | Can react in time to | Dodges (and parries) |
|---|---|---|---|---|
| Easy | 1 frame in 16 (~0.25 s) | 10 frames | Neutral, side and down heavies, and slams whose slammer starts at least ~0.8 m above its head. Not the up heavy or the jab. | 1 attack in 4 |
| Normal | 1 frame in 6 (~0.1 s) | 6 frames | Every heavy and slam except the up heavy. Not the jab. | 1 attack in 3 |
| Hard | 1 frame in 2 | 3 frames | Everything, including the jab | 1 attack in 2 |
