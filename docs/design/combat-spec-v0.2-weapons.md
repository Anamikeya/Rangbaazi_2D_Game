# Combat spec v0.2: weapons

Addendum to GDD A4 ("Weapons override fists"), B2 ("Combat verbs": weapon throw; match loop step 2, "Scramble") and B5 ("Weapons, props & power-ups"). It builds on `combat-spec-v0.1.md`. It changes only one v0.1 rule: pressing Light and Heavy together while unarmed (see "The fist flurry"). It covers the first weapon slice: Prop Boxes, picking up, using, throwing and swapping weapons, projectiles, sweet spots, winner nicknames, and six weapons.

**Status: approved 2026-11-12, not yet implemented.** All numbers are first guesses to tune in playtests, as in v0.1. The six weapons' timings come from the art pack's drafts (`round-brawler-v3/weapon-profiles.json`, in seconds), converted to 60 fps and fitted around the v0.1 moves.

## Sources and where they disagree

| Topic | GDD | Art pack (`game-design.md`) | This spec |
|---|---|---|---|
| Fists while armed | Weapons replace your moveset until ammo runs out or you drop them | Fists stay available | The weapon replaces fists: either attack button uses it. Up + Heavy stays the recovery |
| How many weapons you carry | One | One active, one reserve, one utility stack | One |
| Where weapons come from | Prop Boxes every 8–12 s, first touch wins | Personal supply crates (battle royale), public ground loot | Prop Boxes, as in the GDD |
| Roster size | 6 in the vertical slice, 30 at launch | 100 | 6 for this slice, picked from the art pack's 100 |
| Ammo | Ammo or durability; empty weapons can still be thrown | Ammo, durability, charges or cooldowns; 27 weapons reload | Ammo or durability, no reloads |

## The first six weapons

One per GDD B5 class, plus the bowling ball. All six already have art and v3 motions. Each weapon has **one attack**.

| Weapon | GDD class | Art pack family | Attack | Supply |
|---|---|---|---|---|
| Belan | Melee | `overhead` | Quick overhead bonk that dizzies | 12 hits |
| Baseball Bat | Melee | `swing` | Two-handed swing that also bats projectiles back | 8 hits |
| Bowling Ball | Joke | `roll` | Underhand roll along the ground | Never runs out: it becomes a pickup again when it stops |
| Shotgun | Heavy | `firearm` | Short-range blast that pushes you back | 4 shells |
| Uzi | Pistol / SMG | `firearm` | Automatic fire while the button is held | 30 rounds |
| Cherry Bombs | Explosive | `lob` | Lobbed bomb with a fuse | 3 bombs |

## Inputs

The online input already carries `Grab` (bit 8) and `Drop weapon` (bit 9), so the wire format doesn't change. The phone controls need one new button, Throw (GDD: "Flick weapon icon").

| Input | Empty-handed | Holding a weapon |
|---|---|---|
| Light | Jab | The weapon's attack |
| Heavy, ← / → + Heavy, ↓ + Heavy | Heavies and ground slam | The weapon's attack |
| Light + Heavy on the same frame | The fist flurry (below) | The weapon's attack, once |
| ↑ + Heavy | Up heavy | Up heavy, unchanged; the weapon stays in hand |
| Drop weapon | Nothing | Throw the weapon forward (it's gone after the throw) |
| Grab | Kept for fighter grabs later; not needed for weapons | Same |

Input rules:

- **Armed, the buttons are one button.** Light, Heavy or both together all fire the weapon's single attack, once. How hard or how many buttons you press doesn't matter; the weapon's own speed sets the pace. Light versus Heavy only matters for fists.
- **Picking up needs no button:** touching a weapon takes it (see Holding).
- **Priority:** the v0.1 order still holds. Dodge beats attacks, attacks beat Jump, and Drop weapon ranks with the attacks.
- **Fresh presses:** only a fresh press starts an action. The one exception: the Uzi keeps firing while either attack button is **held**.

## The fist flurry

This is new for unarmed fighters. In v0.1, pressing Light and Heavy on the same frame gave the heavy. Now it starts a **flurry**, picked at random from four, with equal odds:

| Roll | The flurry |
|---|---|
| 1 | One small hit: a jab |
| 2 | One big hit: a neutral heavy |
| 3 | A small hit, then a big hit: jab, then neutral heavy |
| 4 | Two big hits: neutral heavy twice |

- **Fairness online:** the pick uses `SimRandom`, so it's the same on both players' screens and exact on rollback. Nobody can choose the result.
- **Chaining:** the second hit starts on the frame after the first one's recovery ends. The flurry commits you: you can't dodge out between the hits.
- **Recovery stays safe:** with ↑ held, both buttons together are still the up heavy, so the flurry never replaces your recovery.
- **Ranked:** a random flurry may not suit ranked play. A match setting can turn it off later, so that both buttons together give the heavy again, as in v0.1.

## Prop Boxes

- **When:** the first box drops 5 s (300 frames) into the match. After that, a new one is due every 8–12 s (a deterministic `SimRandom` roll of 480–719 frames), as in GDD B2.
- **Cap:** at most 3 boxes and loose weapons on the stage at once. While the cap is full, the timer waits.
- **Where:** above a random point on a platform's top (the platform chosen by `SimRandom`, weighted by width, at least 0.5 m from its edges), starting 2 m below the top of the blast zone. It falls under fighter gravity and stops on the first platform it lands on.
- **What:** one of the six weapons, picked uniformly, never the same as the previous box. Match settings that toggle weapons (GDD) are later.
- **Opening:** the box is 0.8 × 0.8 m. The first fighter whose hurtbox touches it takes the weapon, armed or not (see Holding for swapping). If two touch on the same frame, the one closer to the box's centre wins; on an exact tie, the lower player index wins.
- **Expiry:** a box or loose weapon nobody takes vanishes after 15 s (900 frames). It blinks for its last 2 s (presentation only). Anything that leaves the blast zone is gone.

## Holding

- **One weapon at a time.**
- **Touch to take, touch to swap.** Touching a box or a loose weapon takes it, whether you're empty-handed or not. No button is needed. Weapons have no ranking: touching anything swaps, even from a stronger weapon to a weaker one.
- **Swapping:** your old weapon drops where you stand, with whatever ammo or durability it had left, and anyone can take it. You can keep swapping back and forth as often as you like.
- **No instant swap-back:** a weapon you just dropped ignores you for 60 frames (1 s), so you can walk away from it without picking it up again. Anyone else can take it at once.
- **Keep it when hit:** the weapon stays in your hand when you're hit, parried or stunned, and while you dodge.
- **Losing a stock with a weapon:** the weapon comes back in a box. A new Prop Box carrying that weapon drops 2 s (120 frames) later, at a random spot like any other box. It comes back with the ammo or durability it had left, so dying never refills it, and an empty weapon doesn't come back. This box doesn't wait for the normal timer, but it still counts toward the cap of 3.
- **Speed:** the bowling ball slows its carrier to 80% run and air speed. The others don't.
- **Recovery is kept:** ↑ + Heavy is always the v0.1 up heavy, so picking up a weapon never strands you off the stage.
- **No reloads.** At 0 ammo or durability the weapon stays in hand, and the attack buttons throw it, the way Drop weapon does (GDD: "empty weapons can still be thrown").
- **Throwing:** any weapon can be thrown, full or empty, and only once. A thrown weapon hits like a projectile and then disappears, whether it hit someone or landed.

## Using a weapon

Weapon attacks are moves, exactly like v0.1's. They have startup, active and recovery frames, damage, knockback, a launch angle, a hitstun bonus and hit-stop, and they follow v0.1's rules for steering, one hit per move, parries, dodges, Guts armor and hit-stop. Durability counts hits that connect, not swings; ammo counts shots.

Projectiles are new. They follow these rules:

- **A projectile is its own object in the game state,** with an owner, position, velocity, a frame counter and the set of fighters it has already hit.
- **Everything hits everyone, the thrower included.** A projectile can hit the fighter who threw it: your own bomb's blast, your own bowling ball if you jump in front of it, a ball someone picked up and rolled back at you, your own projectile batted back.
- **The release grace:** a projectile can't hit its thrower until it has stopped touching them once, so it doesn't hit them the moment it leaves their hand. Explosions have no grace: a bomb's blast hits everyone in it.
- **One hit per fighter:** a projectile hits each fighter at most once.
- **Hit-stop:** only the target freezes; the thrower is usually far away and keeps acting. Uzi bullets cause no hit-stop at all, or the target would be frozen nearly half the time.
- **Knockback:** it launches the target in the projectile's direction of travel. Explosions launch away from their centre.
- **Parries:** parrying a projectile destroys it. The defender gets the normal parry reward; nobody is stunned.
- **Batting back:** the bat's swing hits back any projectile its active hitbox touches. It becomes the batter's projectile, reversed, at 1.25× speed, and can hit the fighter who first threw it.
- **Platforms:** bullets and blasts ignore platforms. Balls, belans and bombs land on platform tops like fighters (one-way) and fall off edges.

## Frame data

Same counting as v0.1: the press is frame 0, the hitbox is out on frames `[startup, startup + active)`, and for projectiles the release frame is `startup`. Distances are metres from the feet, x forward.

### Melee and blasts

| Attack | Startup | Active | Recovery | Total | Damage | Base KB | KB growth | Launch | Hitstun bonus | Hitbox x | Hitbox y |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Belan bonk | 7 | 3 | 14 | 24 | 8 | 0.12 | 0.0025 | 50° | +10 (dizzy) | 0.2 to 1.3 | 0.6 to 2.0 |
| Bat swing | 16 | 4 | 24 | 44 | 16 | 0.17 | 0.0026 | 35° | 4 | 0 to 1.8 | 0.4 to 1.6 |
| Shotgun blast | 12 | 2 | 40 | 54 | 18 | 0.20 | 0.0028 | 25° | 4 | 0.4 to 4.5 | 0.5 to 1.5 |

- **Belan bonk:** the +10 hitstun is the GDD's "stuns briefly". The target plays the dizzy face.
- **Shotgun blast:** the shooter is pushed back at 0.15 m/frame on frame 12. The 40 recovery frames are the pump.

### Throws and projectiles

| Attack | Startup (release) | Recovery | Projectile | Damage | Base KB | KB growth | Launch | Hitstun bonus |
|---|---|---|---|---|---|---|---|---|
| Bowling Ball roll | 20 | 30 | Rolls at 0.22 m/frame for up to 180 frames, passing through fighters it hits; then lies there as a pickup | 14 | 0.18 | 0.0024 | 30° | 4 |
| Uzi | 3 for the first shot | 8 after release | Bullet every 5 frames while held, 0.70 m/frame for 20 frames; spread cycles 0°, +3°, −3°, +6°, −6° | 2 | 0.05 | 0.0005 | 10° | −6 |
| Cherry Bomb | 8 | 14 | Thrown at (0.14, 0.18) m/frame; explodes 50 frames after release, or at once on touching a fighter other than the thrower | 12 (blast) | 0.18 | 0.0025 | 60°, away from the blast | 4 |
| Throw (any weapon) | 6 | 12 | Thrown at (0.32, 0.06) m/frame under gravity; disappears when it hits or lands | Light 6, medium 9, heavy 12 | 0.16 | 0.0026 | 35° | 4 |

- **Uzi:** hitstun is `8 + Josh/8 − 6` frames, at least 1. Bullets chip away and only knock back at high Josh. You can walk at half speed while firing, and you can't turn. The spread pattern is fixed, not random, so rollback stays exact.
- **Cherry Bombs:** a blast is a 2.4 × 2.4 m box around the bomb, out for 2 frames, and hits everyone in it, the thrower included.
- **Throw weights:** belan and Uzi are light; bat, shotgun and cherry bombs are medium; the bowling ball is heavy.
- **Throws versus the ball's roll:** a thrown weapon is always gone afterwards. The bowling ball's roll is its attack, not a throw, so it comes to rest as a pickup that anyone can take and roll back.

## Hit depth: sweet spots and weapon reactions

Every weapon has a **sweet spot**, a way to hit that rewards a player who's good with it. A sweet-spot hit does 1.25× damage and 1.25× base knockback, always gets the maximum hit-stop (10 frames), and has its own sound and effect. A normal hit is still a hit; the sweet spot is the expert's version.

| Weapon | Sweet spot | Extra on a sweet-spot hit | Reaction on the target |
|---|---|---|---|
| Belan | The end of the pin on the head: hitbox x 0.8 to 1.3, y 1.4 to 2.0 | +6 more frames of dizzy | Dizzy face and circling stars |
| Baseball Bat | The tip of the bat: hitbox x 1.3 to 1.8 | Launches flat at 20° instead of 35°: the ring-out shot (GDD's "hits a six") | Spins as it flies, with a loud crack |
| Bowling Ball | One roll hitting 2 or more fighters ("Strike!") | Every fighter hit after the first counts as a sweet spot | Knocked flat like a pin |
| Shotgun | Point blank: hitbox x 0.4 to 1.6. Beyond 3 m the blast does only 10 damage | | Blown back trailing smoke |
| Uzi | Keeping on target: from the 6th bullet in a row on the same fighter without a miss | Those bullets do 3 damage and drop the hitstun penalty, so the stream holds them | Jittering flinch |
| Cherry Bombs | A direct hit: the bomb touches a fighter and explodes on them | | Sooty face, smoking hair |
| Any throw | Hitting a fighter who's in the air ("Bullseye") | | Knocked out of the air |

- **Exact online:** the sweet-spot boxes are checked against the target's hurtbox on the hit frame, so the result is exact and the same for both players.
- **The Uzi's streak** belongs to the shooter. A bullet that expires without hitting resets it, as does hitting a different fighter.
- **Hitting yourself never counts** as a sweet spot.
- **For animation:** each weapon's hit reaction needs a drawn extra slot on every character (`dizzy` face and `stars`, `sooty` face, `flattened` body for the ball), plus effects for the sweet spot (crack, strike, smoke). These are presentation only: the game raises a `sweet_spot` event with the weapon, and the renderer picks the reaction.

## Nicknames for the winner

A player who's good with a weapon earns a nickname for it. The game counts, for each fighter during the match:

- damage dealt to other fighters with each weapon, including fists;
- KOs credited to each weapon: the weapon of the last hit taken from another fighter in the 5 s before the stock was lost;
- sweet-spot hits with each weapon.

When the match ends, the winner's **main weapon** is the one that did the most of their damage. It needs at least 40% of it; otherwise they're an all-rounder. The winner is a **master** of it if they also landed 3 or more sweet spots and at least 1 KO with it.

### The name generator

Names are generated, not picked from a fixed list, so the same player can earn different names. A name is a **weapon word** plus a **title**, in either order, with an optional connector:

| Main weapon | Weapon words |
|---|---|
| Fists | Mukka, Knuckle, Thappad, Fist |
| Belan | Belan, Rolling-Pin, Bonk, Chapati |
| Baseball Bat | Bat, Home-Run, Sixer, Slugger |
| Bowling Ball | Bowling, Strike, Gutter, Ten-Pin |
| Shotgun | Shotgun, Boomstick, Pump, Buckshot |
| Uzi | Uzi, Trigger, Bullet, Rat-a-tat |
| Cherry Bombs | Pataka, Cherry, Fuse, Firecracker |
| All-rounder | Gully, Mohalla, Jugaad, Chaos |

| Tier | Titles |
|---|---|
| Regular | Wala, Champ, Ace, Bhai, Kid, Specialist |
| Master | Baadshah, Ustaad, Legend, Boss, Maestro, Machine |

- **Examples:** "Home-Run Ustaad", "Sixer Legend", "Bat Wala" (Baseball Bat); "Pataka Baadshah" (Cherry Bombs); "Gully Champ" (All-rounder).
- **The same name for everyone:** the roll uses the match's `SimRandom` stream at the moment the match ends, so both players see the same name.
- **Original content:** every word is original or an everyday word (GDD B8). A short block-list rejects any combination that matches a real film title, song or person, and rolls again.
- **Later weapons:** every new weapon adds its own weapon words, and the title lists are shared.
- **One match only, for now:** this slice shows the nickname for one match. A lasting title, for example after 10 wins with the bat, needs player profiles and saved progress, which come later.

## Game state

New rollback state. Each item is added to `copy_from()`/`CopyFrom()` and to the checksum, in the same order in C# and GDScript:

- **Fighter:**
  - `weapon` (id, 0 = none) and `weapon_supply` (ammo or durability);
  - `flurry_next` (the queued second hit of a fist flurry);
  - `uzi_streak`;
  - `last_hit_weapon` and `last_hit_by`, with `last_hit_timer` (for KO credit);
  - per-weapon match counters `damage_by_weapon[7]`, `kos_by_weapon[7]` and `sweet_spots_by_weapon[7]` (fists plus the six).
  
  The counters live in the rollback state so a rollback never counts a hit twice. Weapon attacks reuse `action = ATTACK` with new `MoveData.Id` values, so `action_frame` drives them exactly like the heavies.
- **GameState:** a fixed array of **16 props**, so copying stays cheap and rollback-safe.
  - Each prop has `kind` (none, box, pending box, loose weapon or projectile), `weapon`, `supply`, `owner`, `owner_clear` (the release grace is over), `pos_x`, `pos_y`, `vel_x`, `vel_y`, `timer`, `bounces` and `hit_mask` (one bit per fighter), plus `ignore_fighter` and `ignore_timer` for the 1 s no-swap-back rule.
  - Also `prop_timer` (frames to the next box) and `last_box_weapon`.
  - A "pending box" is a dead fighter's weapon waiting its 2 s to drop back in.
- **Full array:** if all 16 slots are full, a new projectile replaces the oldest Uzi bullet. Boxes simply wait.
- **Versions:** `Simulation.SIM_VERSION` goes up and the golden traces are rewritten. `NetProtocol.VERSION` stays the same, because the inputs don't change.

## Animation

Animation is presentation only and must fit these frame counts, the same rule as the moves (see `animation-brief-for-chatgpt.md`):

- **One clip set per art pack family** (`overhead`, `swing`, `roll`, `firearm`, `lob`), the same for every character, with per-weapon overrides where needed. For example, the bat and the plank share `swing` but have their own timing.
- **What each set has:** hold idle, the attack, a sweet-spot version of the attack's impact pose, and the throw.
- **Layering:** the weapon clip drives the hands, the weapon and the body's lean. The character's own clips keep driving the feet, so you can swing while running.
- **The weapon is attached** at its grip point in the front hand. Two-handed weapons also put the back hand on a second grip point.
- **Game objects:** projectiles, boxes and pickups are drawn by the renderer from the props array (rolling ball, fizzing fuse, blinking pickup).
- **The fist flurry** reuses the jab and neutral heavy clips, back to back.

## The CPU

- **Empty-handed:** it walks to a box or pickup within 4 m.
- **Armed:** it uses melee weapons where it would use its heavies, at their range. It uses guns when the opponent is level with it and in range, and bombs and the ball when the opponent is 3–8 m away on the same platform. It steps out of the path of a rolling ball, including its own.
- **Empty weapons:** it throws them at the opponent.

## Decided (2026-11-12)

- **No reloads.** An empty weapon gets thrown.
- **Throws are one-time:** any weapon can be thrown, once, and is gone afterwards.
- **Touch to swap:** touching any weapon swaps it, with no ranking. The dropped weapon is free for anyone, and you can swap as often as you like.
- **One attack per weapon:** armed, either button (or both) fires the weapon's single attack once. Light versus Heavy only matters for fists.
- **The fist flurry:** unarmed, both buttons together give a random small/big hit combination.
- **Self-damage:** everything can hit its thrower: bombs, balls, batted-back projectiles.
- **Keep the weapon when hit.** Dying with it sends it back in a Prop Box.
- **Generated nicknames** from the winner's main weapon, with a master tier for sweet spots and a KO.
- **The six weapons:** Belan, Baseball Bat, Bowling Ball, Shotgun, Uzi, Cherry Bombs. The AK-47 can come in a later batch.
- **Flurry odds:** equal, 25% each.

## Not in v0.2

- the other 94 weapons;
- reloads, charged attacks, and status effects other than the belan's dizzy;
- a reserve weapon or utility stack;
- the GDD's power-ups (chai, vada pav, ...);
- weapon affinity (GDD B4: +10% with one class);
- match settings to toggle weapons or the flurry;
- ranked weapon lists;
- lasting nicknames across matches;
- grabbing fighters;
- throwing props;
- wall pins;
- the battle-royale supply crates from the art pack.
