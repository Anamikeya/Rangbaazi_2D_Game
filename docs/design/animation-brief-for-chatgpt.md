# Animation brief for ChatGPT

Prompts for the round-brawler-v3 art pack. Paste Prompt 1 first, review what comes back, then use Prompt 2 for each later batch. Prompt 1b fixes the v4 pilot, which ChatGPT built from the first version of Prompt 1. Prompt 4 is for Gemini, which made most of the v5 pack in `gemini/`. It redoes the 27 template movesets and 22 placeholder arenas. The prompts work with either tool. Frame data comes from `client/sim/move_set.gd` and `docs/design/combat-spec-v0.1.md`; if those change, update the table here before sending.

Moves come in two layers: one shared set that every character plays (including the weapon motions: each weapon has its own, the same for every character), and each character's own versions of the moves that show who they are. The work is split three ways. ChatGPT draws the art (faces, split pieces, drawn extras, effects) and drafts each character's key poses. The game adds the live motion itself (springs, leaning, landing squash, hit-stop shake, tumble spin), because it has to react to what actually happens in a match. Final timing and poses are tuned in the game on a phone.

---

## Prompt 1: paste this first

I like the 31 characters you made for RANGBAAZI (round-brawler-v3). Keep the art. I'm not happy with the animation, for the characters or the arenas: everything moves the same way, so nothing feels like it belongs to anyone. I want a player to watch two seconds of any fighter, or any arena, and know whose it is.

### What's wrong now (please don't repeat it)

- `runtime/round-rig.js` has one `pose()` function for all 31 characters. The only per-character difference is a `motionStyle` multiplier (robot: head rotation × 0.45, and so on). That's why Akshat, Simran and Saqlain punch, run and fall the same way.
- 30 of the 31 characters have one neutral face. Only Shan has attack, hurt and KO faces.
- The 12 base clips don't match the game's moves. The game has a jab, four heavies, a ground slam, three kinds of dodge, a parry, Guts and hit-stop (table below).
- Each arena is one still picture. Every map has the same brown platform slabs with text labels, and hazards are coloured bars with a label.

### What stays fixed

- Character art, proportions, the five-part construction, names and weapon art. Don't redraw characters or add bodies or limbs.
- Collision rectangles in `levels/*/level.json`.
- Move timing. Every character uses the same frame data, for fairness. Personality comes from poses, arcs, spacing, holds, secondary motion, faces and effects, not from changing frame counts.
- No root motion. The game moves the fighter: the side heavy's lunge, the up heavy's rise, the slam's plunge and all knockback. Animation moves the parts around the feet origin and never slides the whole character across the stage.
- Animation is visual only. No hitboxes, damage or physics in the data.
- Original content only: no real film titles, dialogue, songs, actors' likenesses or famous actors' signature poses, and no deities.

### The game's moves (60 frames per second)

| Move | Startup | Active | Recovery | Total | Notes |
|---|---|---|---|---|---|
| Jab | 4 | 3 | 8 | 15 | Fast poke, ground or air |
| Neutral heavy | 14 | 4 | 22 | 40 | Big committed hit |
| Side heavy | 16 | 5 | 24 | 45 | The game lunges the fighter forward from frame 10 |
| Up heavy | 6 | 8 | 26 | 40 | The recovery move; the game lifts the fighter about 3.6 m from frame 6 |
| Down heavy | 12 | 4 | 22 | 38 | Low sweep that hits both sides |
| Ground slam (air) | 10 | until landing, max 40 | 14 | up to 64 | Hangs still for 10 frames, plunges, impact on the landing frame |
| Dodge (spot, dash, air) | | | | 24 | Frames 0–5 are the parry window; invincible to frame 17 |
| Stunned after being parried | | | | 36 | The attacker is helpless |

Timing rules:

- Startup is the anticipation, the readable tell. The strike pose lands on the first active frame. Recovery is the follow-through and settle.
- On a hit the game freezes both fighters for 4–10 frames (hit-stop), holding whatever frame is showing. So the first active frame of every attack must be a strong impact pose (stretch, smear) that still looks good frozen. A parry freezes both for 10 frames.

### What the game adds live (don't keyframe these)

Some motion has to react to what actually happens in the match, so the game adds it on top of your clips:

- springs on the split pieces (hats, antennae, ears, drips);
- leaning with running speed and with drift in the air;
- extra squash on landing, scaled by how fast the fighter fell;
- shake during hit-stop;
- spin while tumbling from a big hit, scaled by the knockback.

Leave these out of the clips, or keep them small, so the game doesn't add them twice. Your clips are the key poses: the shapes, arcs and timing that make each character who they are.

### Moves: one shared set, plus each character's own

All 31 characters have the same five pieces at the same size, so any move plays on any character unchanged. Use that, but only where personality matters less. Sharing every move is what made the current pack generic.

**Shared moves**, made once and played by everyone:

- **Movement:** run start; skid to a stop; turn around; ground jump; rising; apex; falling loop; soft landing; hard landing.
- **Getting hit:** impact pose for hit-stop (light and heavy); short hitstun; launch tumble loop for big hits; respawn arrival.
- **Parry:** parry success (the defender's pose during the 10-frame freeze); stunned after being parried (a 36-frame "got caught" loop).
- **Guts:** an overlay on top of whatever the fighter is doing, not a full-body clip, because it starts mid-fight. A "last stand" state for the 3-second countdown (heavy breathing, sweat, gritted face) and a "fired up" flare for the 1-second armor payout.
- **Weapons:** each weapon has its own motions (a bat swings with two hands, a bowling ball rolls underhand, a shotgun kicks and pumps), and every character uses them the same way, so there are no per-character versions. Leave `runtime/weapon-motion.js` as it is this round; we'll convert it to the data format below later.

Characters still look like themselves in shared moves, because the moves swap in each character's own faces and drawn extras and the game adds their springs. Vinayak's visor shows a loading circle while he's stunned; Shan's quiff flattens when he's hit.

**Each character's own moves**, the ones that show who they are:

- **Movement:** idle loop; idle fidget after about 4 seconds idle (2 variants); run loop; air jump (a flip, spin or puff that's theirs).
- **Attacks:** jab, neutral heavy, side heavy, up heavy, down heavy, and ground slam in three parts (hang, plunge loop, impact and recovery).
- **Dodges:** spot dodge, dash dodge, air dodge.
- **Match moments:** select-screen pose, entrance (one generic and one for their own arena), taunt, two victory variants, defeat reaction, KO blast-off.

A character can also replace a shared move when their body works differently, like Chandan's jelly hitstun or Akhmal's floating landing. Most characters need none.

### What "belongs to this character" means

Before animating, write a motion card for each character:

- **Personality** in one line, from their role.
- **Tempo and weight:** snappy or lazy, heavy or floaty, smooth arcs or mechanical snaps with holds.
- **Signature shapes:** the 2 or 3 poses people will remember.
- **Secondary motion:** which pieces lag, bounce or overshoot. Where the existing head art allows it, split one or two pieces (hat, ears, antenna, hood, hair tuft, slime drip) into separate layers with pivots. Fill the gap cleanly; don't redraw the character. Don't keyframe how these pieces move; the game moves them with springs. For each split piece, give a spring setting instead: stiffness and bounce, each from 0 to 1, plus a few words on the feel ("floppy, lags a beat", "stiff, rings after every stop").
- **Faces:** at least six expression heads (neutral, effort, hurt, dizzy/KO, happy, smug), made by editing the existing head so outline weight, palette and proportions match exactly, at the same size and pivot as the current head.
- **Drawn extras:** where moving rigid pieces can't make the shape, draw a replacement that a texture track swaps in: a stretched smear hand for fast strikes, a flattened hat or hair tuft for a hit, a slime body melting into a puddle, a hand coming out of a portal. Same style rules as the faces.
- **Effects:** their own hit spark, dodge trail and landing puff, in their colours. Keep spark size and duration the same across characters so every fighter is equally readable.
- **Foley:** footstep and whoosh sound IDs per character (robot clank, slime squelch, ghost hum), as events in the data.
- **Replaced shared moves:** any shared moves this character needs their own version of, and why. Most need none.

This is the depth I want. Do these four as written, then invent the other 27 at the same depth:

**Shan, Street Brawler.** Confident and a bit cocky, with a boxer's bounce. Idle: bounces on his toes and rolls his shoulders; fidget: flicks his quiff back and cracks his knuckles. The quiff is a split piece on a loose spring, so it bounces a beat late; when he's hit it swaps to a flattened drawing for a few frames. Jab: a crisp boxing jab with the back hand guarding his face. Neutral heavy: a haymaker from way back, front foot stamping. Side heavy: a shoulder charge. Up heavy: a spinning rising uppercut. Down heavy: a low spinning leg sweep. Taunt: beckons with his front hand. Victory: dusts off his collar, then a small hop. KO: windmills away. Effects: orange sneaker-scuff dust and white knuckle sparks.

**Vinayak, Splash Robot.** Mechanical: stepped poses with holds, then snaps, never a smooth arc. Head barely bobs when running; feet move like pistons. The antenna is a split piece on a stiff spring with lots of bounce, so it rings after every stop. Dodge: the visor flickers and he jitters through 2 or 3 offset frames like a glitch. In the shared hurt and stunned moves his faces do the work: static fills the visor when he's hit, and a loading circle when he's stunned. KO: powers down and the visor shrinks to a dot. Taunt: wipes his visor and water drops fly off. Effects: water droplets and steam hiss.

**Chandan, Slime Trickster.** Maximum squash and stretch. Hands drip and re-form. He replaces the shared landing and hitstun: on landing the body splats and pulls back up, and when hit he wobbles like jelly for the whole hitstun. Dodge: melts into a puddle and re-forms a few frames later. Jab: the hand stretches like taffy and snaps back. Taunt: blows a slime bubble that pops. Effects: green splats that stick for a moment.

**Akhmal, Portal Ghost.** Never quite touches the ground; wispy feet trail behind. Moves by drifting, not stepping: the run is a lean and a glide. He replaces the shared jump and landing moves, since he floats. Jab: the hand vanishes into a small portal and comes out in front. Dash dodge: blinks through a portal ring. Up heavy: rises out of a portal. KO: pulled into his own spiral. Effects: lilac portal rings.

### What "belongs to this arena" means

- **Layered art:** far, mid, near and foreground layers as separate transparent images with parallax factors. Nothing that should move is painted into the background.
- **Ambient life:** 8 to 12 looping details per arena that tell the owner's story. In Rookie Rooftops: kites tugging at their strings, laundry flapping in gusts, pigeons that scatter when someone lands nearby, Shan's spare orange sneakers drying on the line, neighbours on far roofs.
- **Platforms as real objects** in that world (water-tank lids, parapets, crates, counter tops), built modular: left cap, tileable middle, right cap, so they fit any rectangle. No text labels in the playfield.
- **Hazards:** art for every state (idle, warning, active, cooldown) and every material reaction. The warning must be readable from shape and motion alone, without colour or sound, and last at least 45 frames. For example, the spring pad's coil visibly compresses before it fires and wobbles after.
- **Reactions to game events.** The game sends these names and the arena only reacts visually: `match_start`, `owner_entrance`, `heavy_hit(x)`, `slam_impact(x)`, `parry(x)`, `guts_start`, `ko(side)`, `owner_victory`. A slam near the laundry shakes the line; when the owner wins at home, the neighbours throw their hands up.
- **Owner presence:** the owner's colours, props and motifs appear in the world, and the crowd or NPCs react differently to the owner than to a visitor.
- **Contrast:** backgrounds stay lower in contrast and saturation than the fighters, and nothing flashes behind them.
- **Phone budget:** about 5 MB of images per arena (WebP), at most 12 ambient loops, and no full-screen per-frame effects. It has to hold 60 fps in a mid-range Android browser.

### Data format

The game is built in Godot and I'll port the animation into it myself, so I need data and art, not engine code.

- One JSON file per move. Shared moves go in `shared/anim/{clip}.json`; a character's own moves go in `characters/{id}/anim/{clip}.json`, using the same clip names. The game plays the character's version when there is one and the shared version otherwise. Use 60 fps with frames numbered from 0, and the coordinate system of `rigs.json` (256 logical frame, origin at the centre of the feet, negative y is up, facing right). Give each part keyframes with x, y, rotation, scaleX, scaleY and an easing from a fixed list (linear, step, in, out, in_out, out_back, out_elastic). Add separate tracks for texture swaps (expression heads, drawn extras), draw-order changes and events (`fx`, `sfx`, `foley`) by frame. A texture swap names a slot, not a file (the `hurt` face, the `hit` hair extra), so a shared move shows each character's own drawing; a character with no drawing for that slot keeps the normal piece. Attack clips also list their startup, active and recovery ranges, which must equal the table.
- Your keyframes are a first draft. I'll tune timing and poses in the game on a phone, so keep each clip to a few clearly named key poses (anticipation, strike, follow-through, settle) rather than a key on every frame.
- Split pieces get their own entries in the character's `rig.json`, with pivots and their spring settings. Drawn extras and expression heads are listed there too, at the same size and pivot as the piece they replace.
- Arenas: `levels/{id}/arena-art.json` listing layers (file, parallax, z order), ambient loops (sprite sheet or keyframes), platform art pieces, hazard state art and event reactions.
- Update the workshop so one generic player plays the JSON (no hand-written code per clip), so what I see is exactly what ships. It can preview the springs with a simple approximation so you can judge the feel; the game's springs are the ones that count. Add a frame-step scrubber with the startup, active and recovery bands drawn on the timeline; a side-by-side view of any four characters doing the same move; a black-silhouette toggle; a phone-size view with the character about 70 px tall; and a way to play any shared move on any character.

### This round: a pilot, not all 31

Doing all 31 at once is how the current pack ended up shallow. This round:

1. Write the motion card for all 31 characters and a one-paragraph identity card for all 31 arenas, for me to review.
2. Make the shared moves first, on a plain grey round body.
3. Fully animate four pilot characters: Shan (human), Vinayak (robot), Chandan (slime) and Akhmal (ghost). All their own moves, any shared moves they replace, their new expressions, split pieces with spring settings, and drawn extras.
4. Fully rebuild one pilot arena: Rookie Rooftops (Shan's).
5. Check your work before handing it over:
   - **Shared-move sheet:** every shared move played on all 31 characters, as one contact sheet. Nothing clips, floats off or looks broken on any body.
   - **Silhouette test:** all four doing the jab, neutral heavy, dodge and victory as black silhouettes side by side. If two look alike, redo one.
   - **Grey-body test:** play each pilot's own moves on a plain grey round body with no face or colours. I should still be able to tell who it is from the motion alone.
   - **Phone test:** at 70 px tall, every attack's tell is readable.
   - **Timing test:** a script checks that every attack's strike pose lands on its first active frame and that frame counts match the table.
   - Record a short video of the four pilots and the arena.

Tell me what you couldn't do or aren't sure about. Don't call it finished if a check failed.

---

## Prompt 1b: fix the pilot (round-brawler-pilot-v4)

Paste this into the same ChatGPT conversation. It's self-contained, because ChatGPT built the v4 pilot from the first version of Prompt 1.

> Thanks, the v4 pilot is a big step up. Keep these exactly as they are: the frame timing (every attack and dodge matches the game), the six faces for each pilot, Chandan's own moves, the Rookie Rooftops arena, `levels/shahn/level.json`, and the 31 motion and arena cards (apart from the card edits below).
>
> The brief changed after you started. This message replaces the "Clips for each character" section and the weapon handling in the brief you have. Please rework the pilot as follows, as round-brawler-pilot-v5.
>
> ### 1. One shared set of moves, plus each character's own
>
> Sharing every move is what made the first pack generic, but some moves don't need personality. These 18 are now **shared**, made once and played by every character. Put them in `shared/anim/{clip}.json`, with the clip names you already use:
>
> `run_start`, `skid_stop`, `turn_around`, `ground_jump`, `rising`, `apex`, `falling_loop`, `land_soft`, `land_hard`, `hit_impact_light`, `hit_impact_heavy`, `hitstun`, `launch_tumble`, `respawn`, `parry_success`, `parried_stun`, `guts_countdown`, `guts_flare`
>
> You can start each one from whichever pilot's version reads best, but it has to look right on every body, so build it on the plain grey body. Shared moves name texture slots, not files (the `hurt` face, the `dizzy` face), so each character shows their own drawing; a character with no drawing for a slot keeps the normal piece. Vinayak's loading ring while stunned works this way.
>
> These 24 stay **each character's own**, in `characters/{id}/anim/{clip}.json`:
>
> `idle`, `idle_fidget_a`, `idle_fidget_b`, `run_loop`, `air_jump`, `jab`, `neutral_heavy`, `side_heavy`, `up_heavy`, `down_heavy`, `ground_slam_hang`, `ground_slam_plunge`, `ground_slam_impact`, `dodge_spot`, `dodge_dash`, `dodge_air`, `select_pose`, `entrance`, `entrance_home`, `taunt`, `victory_a`, `victory_b`, `defeat`, `ko_blastoff`
>
> A character keeps their own version of a shared move only when their body works differently. In the pilot that's Chandan (`land_soft`, `land_hard`, `hitstun`: he splats on landing and wobbles like jelly when hit) and Akhmal (`ground_jump`, `rising`, `apex`, `falling_loop`, `land_soft`, `land_hard`, since he floats). Delete the pilots' other copies of shared moves. The game plays a character's own version when there is one and the shared version otherwise.
>
> ### 2. Weapons are shared
>
> Every character uses weapons the same way, so there's no per-character weapon handling. Each weapon still keeps its own motion: the bat's two-handed swing, the bowling ball's underhand roll and the shotgun's kick and pump stay completely different. Only the per-character layer goes. Delete `weapon_draw`, `weapon_hold`, `weapon_recoil` and `weapon_reload` from the pilots, drop the per-character part of `weapon-handling.json`, and remove the "Weapon handling" line from all 31 motion cards. Leave the v3 weapon motions as they are this round; we'll convert them to this data format later.
>
> ### 3. The game adds the live motion, so don't keyframe it
>
> The game adds these on top of your moves, because they have to react to what actually happens in a match: springs on split pieces, leaning with speed, extra landing squash scaled by fall speed, shake during hit-stop, and spin while tumbling.
>
> - Remove all keyframes on the split pieces (`secondary`, `detail`) from every move. Right now they're keyed in 44 of each pilot's 46 moves.
> - Give each split piece a spring setting in the character's `rig.json` instead: `stiffness` and `bounce`, each from 0 to 1, plus a few words on the feel ("floppy, lags a beat", "stiff, rings after every stop"). Texture swaps on a split piece stay as texture tracks.
> - The workshop can preview the springs with a simple approximation so we can judge the feel; the game's springs are the ones that ship.
> - On the 31 motion cards, change "Secondary parts and pivots" to spring settings, and add a "Replaced shared moves" line (most characters: none).
>
> ### 4. Drawn extras
>
> Where moving rigid pieces can't make a shape, draw a replacement that a texture track swaps in, edited from the existing art like the faces were. For the pilots:
>
> - **Shan:** a stretched smear fist for the jab and heavies; a flattened quiff for when he's hit.
> - **Vinayak:** a smear fist; a glitch frame (an offset, scan-lined copy of his body) for the dodge.
> - **Chandan:** a taffy-stretched hand for the jab; a drawn puddle body for the dodge, instead of only squashing the circle.
> - **Akhmal:** a hand coming out of a portal, for the jab and the up heavy.
>
> ### 5. Shan, Vinayak and Akhmal need much bigger poses
>
> This is the most important fix. Chandan reads clearly. The other three barely move: the body stays upright and still, and one hand travels a short way. At the strike frame, Shan's haymaker, shoulder charge and uppercut look almost the same as his jab, and on the grey body Shan, Vinayak and Akhmal can only be told apart by Akhmal's floating feet.
>
> Push them a lot further. As a rough guide:
>
> - On heavies, the body leans or twists up to about 0.4 radians (25°) and shifts 20–35 px within the frame, while the feet stay mostly planted unless the move is a step, stamp or kick.
> - Anticipation pulls clearly away from the strike: body turned back, striking hand drawn behind the body.
> - On heavies the striking hand ends at least 90 px from the body's centre; on the jab it can be shorter, so the jab is clearly the smallest attack.
> - Humans and ghosts squash and stretch the body between about 0.9 and 1.12. Vinayak stays rigid but snaps between held poses.
>
> Each attack needs its own silhouette, different from the jab and from each other:
>
> - **Shan:** neutral heavy is a haymaker with his body wound right back, then a big forward rotation and a front-foot stamp. Side heavy leads with the body tilted hard forward and the hands trailing. Up heavy stretches him tall, fist above his head, spinning. Down heavy is low and squashed, with one foot sweeping wide.
> - **Vinayak:** a piston punch: the arm locks fully out and the body tilts rigidly behind it. His neutral heavy's strike frame currently has the hand tucked low by his body, which doesn't read as a hit. His glitch dodge doesn't show yet; make the 2–3 offset frames obvious.
> - **Akhmal:** heavies come through portals, with the body leaning hard into the portal and stretching toward it. During his dodge he currently vanishes completely, leaving only a ring; keep a faint outline (about 30% opacity) so players can track him.
>
> Timing differences should show in motion, not just in stills: Vinayak holds and then snaps (step easing), Akhmal drifts and overshoots, Shan bounces.
>
> ### 6. Checks and missing pieces
>
> Keep the checks you already run, and add:
>
> - **Strike-frame sheet:** each pilot's jab and four heavies at their first active frame, side by side, in colour and as silhouettes. No two of one character's attacks may look alike, and the jab must be the smallest.
> - **Shared-move sheet:** every shared move on all 31 characters (the 27 without new faces use their v3 face). Nothing clips, floats off or looks broken.
> - **Grey-body video,** not only stills: each pilot's own moves on the plain grey body. You should be able to tell all four apart from motion alone.
> - **The video the brief asked for:** the four pilots doing every own move, the shared moves on all four, and Rookie Rooftops running for about 20 seconds with its event reactions firing.
> - **Open the workshop in a real browser** through a local web server rather than `file://`, click through every move for every pilot, and report any console errors.
>
> Tell me what you couldn't do or aren't sure about. Don't call it finished if a check failed.

---

## Prompt 2: each later batch, after you approve the pilot

> Good. Apply the same depth to the next batch: **[names]**, and their arenas. The shared moves already exist, so make each character's own moves, plus any shared moves their motion card says they replace. Use the motion cards I approved, with my edits. Run the same four checks, record the same video, and add a silhouette sheet comparing this batch with the four pilots, so no new character copies an earlier one.

Suggested batches, in unlock order:

1. Akshat, Simran, Shraddha, Saqlain, Ayush. Also the pilot owners' arenas: Water-Tank Terrace, Slimeworks, Portal Playground.
2. Abhishek, Shubham, Rajat, Abbas, Devanshu
3. Siraj, Nikhil, Abhipray, Suhani, Aastha
4. Sarthak, Salik, Aman, Tushar, Faiz
5. Faizal, Rishabh, Unnati, Manish, Ankit
6. Harsh and Maaz, with the Rangbaazi Grand Finale arena, which should borrow motifs from every earlier arena.

---

## Prompt 4: redo the template characters and placeholder arenas (Gemini, after v5)

Paste this into the Gemini conversation that made the v5 pack in `gemini/`.

> Thanks. The v5 pack is solid where it got real attention, and the data format and handoff are exactly what the game needs. Keep these as they are: the 18 shared moves, the four pilots (Shan, Vinayak, Chandan, Akhmal) and their overrides, the six faces and drawn extras, the spring settings, the frame timing, the nine painted arenas (Rookie Rooftops, Kitchen Clash, Steel Courtyard, Mohalla Mayhem, Tiffin Terminal, Jugaad Garage, Water-Tank Terrace, Slimeworks, Portal Playground), every `level.json`, and the format in `docs/CLAUDE_HANDOFF.md`.
>
> Two parts of v5 repeat the problem we started with, so they need redoing.
>
> ### 1. The other 27 characters move from one template
>
> Their own moves are one template with small number changes:
>
> - All 27 use the same key timing for `victory_a` and `taunt`; 25 share one `jab` timing and 23 share one `neutral_heavy` timing.
> - Nikhil's and Harsh's `victory_a` head tracks are identical.
> - In `docs/pose-distances.json`, the median distance from each of these characters' strike poses to the nearest other character is 5.5. Shapes that close look the same in play.
> - The moves don't do what the cards say. Akshat's card says his taunt "inspects the opponent like an unfinished dish"; his taunt is the shared template.
>
> Rebuild each character's 24 own moves from their motion card, from scratch, not from a template or from another character's file:
>
> - Every signature beat on a card must appear in the move it names. For each character, add a short table to their card: each beat, the move and frame where it happens, and a still of it.
> - Personality has to show in rhythm, not only in poses. Within the fixed frame data, each character picks their own key timing: where the holds go, how fast the snap is, whether it overshoots. No two characters may share the same key timing for the same move.
> - Use the pilots as the bar for size. Their strike poses are far apart; the template characters' are almost on top of each other.
> - Also fix the one pilot issue the handoff admits: Chandan's neutral heavy and up heavy still have similar tall silhouettes.
>
> ### 2. 22 arenas are placeholders
>
> The nine arenas listed above are painted. The other 22 are flat shapes (24–32 KB of layers each): Skyline Soundstage, for example, is plain purple pillars with orange cones as hazards, far below its v3 painting. Paint each of them to the standard of Kitchen Clash, using the v3 background for that place as reference (`round-brawler-v3/levels/{id}/background.png`):
>
> - painted far, mid, near and foreground layers, full of the owner's props and colours;
> - ambient loops that tell the owner's story;
> - platforms that look like real objects in that world;
> - hazards drawn as the real thing (a speaker, a mixing desk, a strobe rig), never generic cones or bars.
>
> Keep every `level.json` unchanged and stay inside the 5 MB phone budget.
>
> ### 3. Work in batches and stop after each
>
> Doing all 31 at once is how both of these happened. Do one batch at a time: the five characters' own moves, plus their arenas if those are placeholders. Then stop and send it to me for approval before starting the next.
>
> 1. Akshat, Simran, Shraddha, Saqlain, Ayush (moves only; their arenas are already painted)
> 2. Abhishek, Shubham, Rajat, Abbas, Devanshu
> 3. Siraj, Nikhil, Abhipray, Suhani, Aastha
> 4. Sarthak, Salik, Aman, Tushar, Faiz
> 5. Faizal, Rishabh, Unnati, Manish, Ankit
> 6. Harsh and Maaz, plus the Rangbaazi Grand Finale arena, which should borrow motifs from every earlier arena
>
> With each batch, send:
>
> - **Strike sheet:** the batch's jab and four heavies at their first active frame, next to the four pilots and every character already approved, in colour and as silhouettes.
> - **Distance table:** for every own move, each new character's nearest other character in `pose-distances.json` terms. Nothing under 40 (today's median is 5.5).
> - **Timing check:** a script confirming no two characters share the same key timing for the same move.
> - **Card table:** the beat-to-move table from part 1, with stills.
> - **Grey-body video** of the batch's own moves.
> - **Arena sheet:** each repainted arena next to its v3 painting, plus its hazard states.
>
> ### 4. Report honestly
>
> In `docs/REVIEW_STATUS.md`, keep two separate columns: what a script counted (files present, frame counts, budgets) and what was visually reviewed. v5 marked all 31 characters and arenas "Pass" because the files existed, although 27 movesets and 22 arenas were still templates. Don't mark something "Pass" for looks unless you looked at it, and tell me what you're unsure about.
>
> Deliver into a new folder, round-brawler-v6, outside the game's code repository. Don't put `node_modules` or videos inside the game's repository.

---

## Prompt 5: final fixes (Gemini, after round-brawler-v6)

Paste this into the Gemini conversation that finished round-brawler-v6.

> Thanks, round-brawler-v6 is complete and the game is now using it. Keep everything that passes: timing, the 18 shared moves, the faces, springs, drawn extras, every `level.json`, the data format, and the 20 arenas that are fully painted. This last round fixes six things. Deliver as round-brawler-v7, outside the game's code repository, with only the changed files plus an updated `docs/REVIEW_STATUS.md`.
>
> ### 1. Paint the platforms and hazards of 11 arenas
>
> Abhipray, Salik, Manish, Faizal, Sarthak, Rishabh, Faiz, Unnati, Tushar, Aman and Ankit have painted backgrounds but flat placeholder platforms (about 7 KB of platform art per arena, against 21–27 KB in the earlier arenas) and flat hazards (about 35 KB against about 200 KB). Skyline Soundstage's hazards, for example, are plain cyan squares and its platforms plain cyan strips. Paint them to the standard of Overtime Office and Gully Stadium:
>
> - platforms as real objects from that world (speaker tops and truss walkways for Skyline Soundstage), with left cap, tileable middle and right cap;
> - hazards drawn as the real thing in every state (idle, warning, active, cooldown, material reactions).
>
> ### 2. Platforms missing in Laughing Ring and Rangbaazi Grand Finale
>
> In `review/arena-sheet-batch-06.png` neither arena shows any platform except the floor, although `levels/harsh/platforms` and `levels/maaz/platforms` have art. Find out whether the review render or the arena data is wrong, fix it, and show both arenas with every platform drawn.
>
> ### 3. KO blast-off is still the defeat pose
>
> For all 27 non-pilot characters, `ko_blastoff` reuses key poses from `defeat`. They are different moments: a KO is being blasted off the stage (spinning, limbs flung out, the character's own panic); a defeat is the after-match reaction. Make every `ko_blastoff` its own move.
>
> ### 4. Moves that only differ at the checked frame
>
> Every own move passes the 40 minimum at the one frame `pose-distances.json` measures, but not across the whole move. A second into `victory_a`, Harsh and Unnati are only 10 apart; Aman and Tushar, Faiz and Rishabh, and Nikhil and Unnati are under 16. Shubham's and Rajat's jab are the same shape shifted (30 apart with position removed). Measure every 4th frame of every own move, with position removed, against every other character, and fix any pair under 30 for more than a quarter of the move. Leave the wind-up and settle frames close to rest where they must be; this is about the parts that carry personality.
>
> ### 5. Shan and the Portal Ghost still share attack timing
>
> Shan's and the Portal Ghost's (`akmal`) jab and four heavies have identical key-frame timing. Give the Portal Ghost its own drifting, floaty rhythm without changing its poses.
>
> ### 6. Names and two arena owners
>
> - Correct spellings: **Shan** (not Shahn), **Saqlain** (not Saklan) and **Abhipray** (not Avipray).
> - Four fighters have new names. Each keeps its own look and moves:
>
>   | Look (id) | Name now |
>   |---|---|
>   | Portal Ghost (`akmal`) | **Simran** |
>   | cat Disc Duelist (`simran`) | **Aastha** |
>   | Slime Trickster (`chandan`) | **Akhmal** |
>   | Precision Ace (`artha`) | **Chandan** |
>
> - Two arenas change owner. Akhmal (the Slime Trickster) now owns **Steel Courtyard** (`levels/simran`), and Aastha (the cat) now owns **Slimeworks** (`levels/chandan`). Keep both arenas' art and layouts. Change only the owner in labels, review sheets and any owner text. Portal Playground stays the Portal Ghost's (Simran's), and Sightline City the Precision Ace's (Chandan's).
>
> Fix every name a player can see: the `name` in `characters.json`, review sheets, labels and any text drawn into the art. Keep the folder names and ids (`shahn`, `saklan`, `avipray`, `akmal`, `artha`, `simran`, `chandan`) exactly as they are, because the game finds the files by them.
>
> Report honestly as before: what a script counted and what you looked at, in separate columns.

---

## Prompt 3: if a batch comes back samey

> These still feel too alike: **[names]**. Put their idle, run, jab, neutral heavy, dodge and victory side by side as silhouettes and on the grey body, list what's identical, and redo the weaker one until the motion alone tells them apart. Keep the frame counts.
