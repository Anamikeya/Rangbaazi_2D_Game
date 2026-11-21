# Rooms v1: teams, the big-room view, the UI/UX audit, polish

This spec answers four requests from the user, made on 2026-11-21:

- "create team in rooms like max 4 in each";
- "polish the view for 15 players: the level should be bigger and can take the camera little far, but the whole level should be always visible";
- "audit the UI/UX and fix it";
- "finally polish the game".

It merges three working designs (teams, the big-room view, a UI/UX audit of 58 screenshots on desktop and phone) and one review of them. Each review point and what was decided is in the Review notes at the end. The standing UI rules apply everywhere: sliders, carousels and visible chips; never cycling buttons or drop-downs. The targets are the web and Android phones (the Xiaomi, landscape, touch).

It builds on combat spec v0.4 (`combat-spec-v0.4-all-weapons.md`), which is being built in the same tree. That build has already added `MatchRules.Teams` / `TeamOf`, `Simulation.Allies`, ally skips in `ResolveHits`, `ShotHits`, `TripMine`, `Explode` and `SteerMissile`, `SparringBot.NearestOpponent` skipping allies, the GDScript mirror (`match_rules.gd` `teams` / `team_of`, `simulation.gd` `allies`), `MatchRules.Room(stage, minutes, signatures, teams)`, and the `all_weapons_teams_6p` golden. Don't start batch 1 below until that build's current batch has landed and rewritten its goldens.

Code is referred to by function name, not line number: the files are still moving.

## What changes, at a glance

| Topic | Today | v1 |
|---|---|---|
| Room modes | Free-for-all only | **Free-for-all, or 2, 3 or 4 teams of at most 4** |
| Team count | — | at least what the room needs: 1–8 members 2 teams, 9–12 three, 13–15 four |
| Friendly fire | — | off (v0.4 already); plus no ring-out credit to a teammate, and a bat never returns a teammate's shot |
| Win rule, team match | — | the team with the most KOs, then the fewest falls; the STATE winner is that team's MVP |
| Someone leaves mid-match | their fighter stands idle | **the CPU plays it** to the end, in every mode |
| Level for 9+ fighters | the normal 32 m floor | **1.5× as wide (48 m floor)**, 15 spawn points |
| Room camera | follows "you" (up to 2.1× zoom) | **the whole level, always**; it may pull back to 0.85 of the fit, never zooms in |
| Phone camera | the floor under the touch buttons; top platform cut off at 20:9 | the floor sits on the line just above the move pad and Kick/Jump; the fit uses height too |
| Room HUD | top-5 list over the arena, P-number markers | one strip of face chips grouped by team, team totals, head markers |
| Result | a banner over the running arena | a result panel ranking everyone, the touch controls hidden |
| Android back | quits the game from any screen | closes the top overlay; never leaves a room without asking |

## 1. Teams

Teams stay **out of `GameState`**. A fighter's team never changes during a match, so it is a per-match rule sent with the roster (v0.4 section 3.4). With `Teams` null or all 0, `Allies(a, b) == (a == b)`, so nothing in this section changes a free-for-all match or its goldens.

### 1.1 Constants

`server/Rangbaazi.Relay/Arena/ArenaProtocol.cs`, `ArenaLimits`:

- `MaxTeams = 4`, `MinTeams = 2`, `MaxTeamSize = 4`.
- `static int TeamsNeeded(int members) => Math.Max(MinTeams, (members + MaxTeamSize - 1) / MaxTeamSize)`: 1–8 members need 2, 9–12 need 3, 13–15 need 4. Four teams of four hold 16, so a full room always fits.

`client/net/room_session.gd`: `MAX_TEAMS := 4`, `TEAM_SIZE := 4`, and `static func teams_needed(members)` with the same arithmetic.

### 1.2 Server state

- `ArenaPlayer`: `public int Team { get; set; }` (0 = none; 1 to 4).
- `ArenaRoom`: `public int TeamCount { get; private set; }`. **0 means free-for-all**; 2 to 4 means teams. `bool TeamsMode => TeamCount > 0`.
- `int Size(int team)` and `int People(int team)` count the members on that team.

### 1.3 Lobby rules (`ArenaRoom`, lobby only unless stated, all deterministic)

- **`SetTeams(int n)`** (the host's one chip row):
  - `n == 0`: free-for-all; every `Team = 0`.
  - Otherwise `TeamCount = Clamp(n, TeamsNeeded(Members.Count), MaxTeams)`. Members with no team, or on a team above `TeamCount`, are placed with `PlaceNew`: people first in join order, then CPUs. Then `BalanceBots()`.
- **`PlaceNew(ArenaPlayer p)`**: the team with the fewest members, then the fewest people, then the lowest number. If every team has 4 and `TeamCount < 4`, `TeamCount` rises by 1 first.
- **`bool PickTeam(ArenaPlayer p, int team)`**:
  - false for a team outside `1..TeamCount`, or `p`'s own team;
  - if `Size(team) < 4`: `p.Team = team`;
  - else if that team has a CPU: its last-joined CPU swaps with `p` (it takes `p`'s old team);
  - else false (four people). Then `BalanceBots()`.
- **`BalanceBots()`**: while the biggest team has more than 1 member over the smallest and has a CPU, its last-joined CPU moves to the smallest team (the lowest number on ties). People are never moved by it.
- `SetBots(count)`: in teams mode each new CPU gets `PlaceNew`, and `TeamCount` rises to `TeamsNeeded` if it must.
- `AddPerson`: in the lobby in teams mode, `PlaceNew` then `BalanceBots()`. **Mid-match** the joiner gets `Team = 0` and watches; they are placed at `ReturnToLobby`.
- `RemovePerson`: in the lobby, `BalanceBots()` after the remove. Mid-match unchanged (`Left = true`).
- `ReturnToLobby()`: after `RemoveAll(Left)`, in teams mode, `PlaceNew` every member with `Team == 0`, then `BalanceBots()`.
- **`bool CanStart(out string reason)`**: in teams mode at least 2 teams must have someone on them, else "Put someone on a second team to start." Free-for-all is unchanged.
- **`Start(seed)`**: `int[]? teams = TeamsMode ? <Fighters' Team, in a for loop> : null`, then `Rules = MatchRules.Room(stage, Minutes, signatures, teams)`. Fighters stay in join order.
- The host role passes on as it does today; the team count is kept.

### 1.4 Leavers are played by the CPU (every mode)

In `ArenaRoom.Tick`:

```csharp
inputs[i] = p.IsBot || p.Left ? SparringBot.Think(State, Rules, i, CpuLevel.Normal) : p.TakeInput();
```

This changes no sim code. Fix the `ArenaPlayer.Connection` doc comment, which says a leaver stands idle. The lobby message marks the player `left`, and clients tag that fighter "CPU" (`main.gd` `_refresh_room` sets `_arena.tags[i]`).

### 1.5 Requests and errors (`ArenaServer.Request`)

- `{"type":"teams","count":0|2|3|4}`: host, lobby. Calls `SetTeams`, then `BroadcastLobby`.
- `{"type":"team","team":1..4}`: anyone, lobby, teams mode. Calls `PickTeam`. On false: `Error("team_full", "That team is full.")`.
- `"start"`: if `!CanStart(out r)`, `Error("teams_needed", r)` and don't start. This is a server guard; the client already disables START (1.8).
- Add `"teams" or "team"` to the `not_allowed` arm. `ArenaRequest.NumberKeys` gains `"team"` (`"count"` is already there).
- `team_full`, `teams_needed` and `not_allowed` never end the session: `RoomSession._on_text` has `const NOTICE_CODES := ["not_allowed", "team_full", "teams_needed"]`, which set `notice` and `notice_tick` instead of calling `_end`.

The wire fields are listed once, in section 5.

### 1.6 Friendly fire (both sims)

Already skipping allies, from v0.4: `ResolveHits` (and everything carried by a hit), `ShotHits`, `TripMine`, `Explode` (still hurts its owner), `SteerMissile`, `SparringBot.NearestOpponent`.

Added here:

1. **`CheckBlastZone`** (C#) / `_check_blast_zone` (GDScript): a ring-out credits `LastHitBy - 1` only if it is not an ally of the fighter who fell (`!Allies(rules, f.LastHitBy - 1, index)`). The fall still counts. This covers scoring whatever path set the credit, including any zone or cloud rule the weapons build settles on.
2. **The bat return in `ShotHits`**: a batter skips a shot whose owner is a teammate (`p.Owner >= 0 && b != p.Owner && Allies(rules, b, p.Owner)`). A batter's own shot keeps today's rule.
3. **`SparringBot.ShouldDodge`**: `if (i == self) continue;` becomes `if (Simulation.Allies(rules, self, i)) continue;`, so a CPU doesn't dodge a teammate's swing. Identical with no teams.

Not changed: **the black hole's `Pull`** drags teammates too, as v0.4's batch 1 notes say (it is not a hit and sets no credit). Zones and the cloud cough for teammates are the weapons build's call: this spec adds one line to v0.4 section 9 (Open points) instead of amending it (section 5.4).

### 1.7 Scoring and the win rule (C# first, then the GDScript port line for line)

`src/Rangbaazi.Sim/Simulation.cs`:

- `MatchRules.HasTeams()`: a loop, true if any `Teams[i] != 0`. GDScript `match_rules.gd` `has_teams()`.
- **`public static int TimedTeamWinner(Fighter[] fighters, MatchRules rules)`**:
  - each fighter's team leader is the lowest index `j` with `Allies(rules, i, j)`; a team-0 fighter is its own team;
  - KOs and falls are summed per leader (`stackalloc int[n]` twice; n ≤ 16);
  - the best team has the most KOs, then the fewest falls; if the best two are level on both, the result is -1 (a draw);
  - otherwise the result is that team's **MVP**: the most KOs, then the fewest falls, then the lowest index.
- `CheckMatchOver`, timed branch: `state.WinnerIndex = rules.HasTeams() ? TimedTeamWinner(state.Fighters, rules) : TimedWinner(state.Fighters);`
- The stock branch is not changed: rooms are always timed and nothing else sets teams.
- Raw team totals, no per-head adjustment. `BalanceBots` keeps CPU-filled teams within 1, and the lobby warns when people make them uneven.
- GDScript `client/sim/simulation.gd`: `timed_team_winner(fighters, rules)` and `_check_match_over`.
- No new `Fighter` or `GameState` field: the checksum, `copy_from` and `GameStateCodec` are unchanged.

Team totals for the HUD and the result are presentation, so they live in `client/game`, not the sim: a new `client/game/standings.gd` (`class_name Standings`, static functions) with `team_kos(state, rules, team)`, `team_falls(...)`, `teams_ranked(state, rules) -> Array` (rank order by the same rule as `TimedTeamWinner`) and `fighters_ranked(state) -> Array` (KOs, then falls, then index).

### 1.8 Lobby UI (`client/game/room_screen.gd`)

**Left column**, top to bottom:

1. A fixed top bar: **Leave room** at the top left (it can never be pushed off), then the title.
2. The share row (UI/UX fix 7): the code in large type and **Copy link**.
3. **One chip row**, `ChoiceRow.make("", ["Free-for-all", "2 teams", "3 teams", "4 teams"], ...)`, height 88. Chips below `teams_needed(members)` are disabled. Guests see the row lit but locked, so everyone can see the mode.
4. A `ScrollContainer` with `SIZE_EXPAND_FILL` holding either:
   - in teams mode, the **`TeamBoard`**; or
   - in free-for-all, a **player chip grid**, 3 columns of 5: face, name, and a "host" / "you" / "CPU" tag.

**New `client/game/team_board.gd`** (`class_name TeamBoard extends GridContainer`, 2 columns, `signal team_picked(team: int)`):

- One card per team, about 350 × 210 at 1920 × 1080. The whole card is a flat `Button`, so a tap anywhere picks it.
- Card: `BOX_FILL` background, a 10 px left band in the team colour, a 4 px border all round on your own team.
- Header: the team symbol, the team name in its colour, and "3/4".
- Up to 4 rows at 28 px: "Ravi (host)", "Neha (you)", "CPU 2". An open slot shows a dim "open".
- A full card's footer: "Tap to take a CPU's place" if it has a CPU, else a dim "FULL".

**`client/game/choice_row.gd`** gains `set_enabled(index: int, on: bool)` and `set_locked(on: bool)`. Disabled chips are greyed and ignore taps.

**Right column** (host box): the arena card with "Change arena" under it, the minutes slider, the CPU slider, START. In teams mode START is disabled while fewer than 2 teams are in use.

**Status line** (one place for every reason):

- host: "Tap a team to join it. Slide to set the match, then start.";
- uneven people: "Teams are uneven (4 v 3).";
- one team in use: "Put someone on a second team to start.";
- when a team chip is disabled: "4 a team at most: 9+ players need 3 teams, 13+ need 4.";
- a `notice` ("That team is full.") for 2 s;
- a watcher mid-match: "You're watching: you'll get a team in the next match."

The middle fighter preview gets a 6 px ring in your team colour. `show_lobby`'s change key adds `session.team_count` and every player's `team` and `left`.

Signals `teams_changed(count)` and `team_picked(team)`, connected in `main.gd` next to `minutes_changed`, call new `RoomSession.set_teams(count)` and `pick_team(team)`.

`RoomSession` also gains `team_count`, `my_team()`, and from `start`: `fighter_teams: PackedInt32Array`, `fighter_ids: PackedInt32Array` (to map the lobby's `left` to fighters) and `fighter_left(i) -> bool`. `_begin_match` builds `MatchRules.room(stage, minutes, PackedInt32Array(), fighter_teams if team_count > 0 else PackedInt32Array())`.

### 1.9 Team colours (`client/game/team_style.gd`, new, `class_name TeamStyle`)

| Team | Name | Colour | Symbol |
|---|---|---|---|
| 1 | BLUE | `#3d7bff` | circle |
| 2 | ORANGE | `#ff9a1a` | triangle |
| 3 | PINK | `#d02a8c` | square |
| 4 | ICE | `#6ee7ff` | diamond |

- `COLORS[0]` is white (no team). `static func draw_glyph(canvas, centre, radius, team)` draws the symbol as polygons with a 2 px black rim, with no font glyphs.
- Checked for colour blindness (Machado 2009): the smallest CIE76 difference between two team colours is 72.6 normal, 41.6 protan, 36.3 deutan, 32.0 tritan. Contrast on the dark HUD plate is at least 4.1:1 (Pink). The palette avoids gold (Guts, weapon words, crate markers) and red (`#ff5a4f`, Josh danger). The symbol, the dark rim and the outline mean colour is never the only cue.
- **Free-for-all** keeps `PLAYER_COLORS`, with two near-duplicates replaced: sand `#c9a26b` and ice `#7fdbff` become two hues picked to clear the rest by the same CIE76 check (the batch records them).

`client/game/arena_view.gd`: a new `_color_of(i)` returns `TeamStyle.COLORS[rules.team_of(i)]` when `rules.has_teams()`, else `PLAYER_COLORS[i % size]`. It replaces every `PLAYER_COLORS[...]` lookup. Where the colour shows:

- **floor disc**: alpha 0.75 (was 0.55) with a 2 px dark rim, in every mode;
- **name tag**: the team symbol inside the pill (section 2.5);
- **off-screen markers**: section 2.5;
- **KO feed**: the killer's name in their colour;
- **the winner's pose**: `FighterPainter.Result.WON` when `Simulation.allies(rules, i, state.winner_index)`, so the whole winning team celebrates (`team_of(-1)` is 0, so a draw is safe);
- **hidden in a cloud**: you and your teammates drawn faintly, as v0.4 2.8 wants; the teams sent in `start` make it possible.

Until batch 3 builds the HUD strip, batch 1 shows team totals as a stop-gap: one line under the clock plate, the four totals at 28 px in team colours with their symbols. The winner banner reads "BLUE TEAM WINS!" (symbol and colour) or "DRAW", with "MVP <name>" and the existing nickname under it.

## 2. Big-room level and camera

### 2.1 What was measured

A scratch renderer ran 15 hard CPUs under room rules at a 1600 × 900 window (1920 × 1080 logical) and a 1200 × 540 window (2400 × 1080 logical, the Xiaomi at about 15.5 px/mm). Fighter art is 2.57 m tall, the hurtbox 1.8 m.

| View (Rooftops, 15 CPUs) | px per metre | Art height, physical px | Fighters on screen, avg / min |
|---|---|---|---|
| Today, follow camera, 1920 | 72–99 | 154–212 | 10.1 / 3 |
| Today, follow off, 1920 | 60 | 128 | 13.9 / 11 |
| Today, follow camera, phone | 81–117 | 207–299 | 11.2 / 4 |
| **Big (48 m), 1920** | 40 | 103 | **14.4 / 11** |
| **Big, phone with touch** | 46 | 118 | **14.4 / 11** |

The minimum of 11 is a KO'd fighter flying past the side for a few frames. Problems today: the follow camera zooms into "you"; fighters 9–15 spawn inside fighters 1–7 (8 spawn points, `index % 8`); crowd tags vanish on light skies; the top-5 list covers the arena; the touch buttons cover the floor; the HUD hides the top platform on the phone.

Every arena has the same frame: a 32 m floor (platform 0 at -16..16, top y = 0), 15 platforms with the highest top at 10.5 m, art from -1.75 m to 16.25 m, 90.25 m of walkable length (11.3 m a fighter with 8, 6.0 m with 15; 9.0 m with 15 on the big level).

### 2.2 When a level is big

`StageSize.Big` is used for **9 or more fighters**: `StageScaling.SizeFor(n) => n >= 9 ? Big : Normal`. The layouts carry 8 spawn points and were made for 8. With teams of at most 4, 9+ also means at least 3 teams. The server decides once, in `ArenaRoom.Start`, from `Fighters.Count`. Duels and local matches (at most 8) never change.

### 2.3 How the stage grows (both sims, no new state)

The layout is **stretched in x only, by 3/2**, as a pure function of (stage id, size). A uniform scale would put vertical gaps past the 5.5 m double-jump reach; hand-made platforms would mean redesigning 31 layouts. Under the stretch the widest horizontal gap to reach any platform grows from 1.9 m to 2.85 m, well inside a jump, and the CPUs climbed every tier in the renders.

C#, `src/Rangbaazi.Sim/StageDefinition.cs` (hand-written; the generated `ArenaStages.cs` is not touched):

```csharp
public enum StageSize : byte { Normal = 0, Big = 1 }
// StageDefinition gains: public StageSize Size { get; init; } = StageSize.Normal;
public static class StageScaling
{
    public const int BigFromPlayers = 9;
    public const int Num = 3, Den = 2;
    public static StageSize SizeFor(int players) => players >= BigFromPlayers ? StageSize.Big : StageSize.Normal;
    public static Fix ScaleX(Fix x) => Fix.FromRaw(x.Raw * Num / Den);   // truncates toward zero, as GDScript int / does
    public static StageDefinition Big(StageDefinition s);                // below
    public static readonly IReadOnlyList<StageDefinition> BigAll;        // built once from ArenaStages.All with a for loop
}
// Stages.ById(string id, StageSize size = StageSize.Normal)
```

`Big(s)`:

- `Id` the same, `Size = Big`.
- **Platforms**: `new FixRect(ScaleX(p.MinX), p.MinY, ScaleX(p.MaxX), p.MaxY)`.
- **Hazards keep their width**, on a scaled centre: `c = (MinX.Raw + MaxX.Raw) / 2 * 3 / 2`, `half = (MaxX.Raw - MinX.Raw) / 2`, zone `c - half .. c + half`, y unchanged. The hazard art still fits, because its platform grew around the same scaled centre.
- **Spawn points**: 15, 3 m apart on the 48 m floor, at y = 2, in an order that spreads any first n (9 to 15) across it: `k = 0, 14, 7, 3, 11, 1, 13, 5, 9, 2, 12, 6, 8, 4, 10`, `x = -21 + 3k` (the first 9: -21, 21, 0, -12, 12, -18, 18, -6, 6).
- **Blast zone**: x ±25.2 (`Fix.FromRatio(±126, 5)`), y -10 to 22.

`Simulation.Spawn` (`index % SpawnPoints.Count`), `SparringBot` (platform 0 edges), crate placement (by platform width) and the blast checks work unchanged.

GDScript, `client/sim/stage_definition.gd`: `const SIZE_NORMAL := 0`, `SIZE_BIG := 1`, `var size := SIZE_NORMAL`, `static func size_for(players)`, `static func big(base)` with exactly `p.min_x * 3 / 2`, `(z.min_x + z.max_x) / 2 * 3 / 2`, `(z.max_x - z.min_x) / 2` and `Fx.ratio(126, 5)`, cached in `static var _big_cache`, and `static func by_id(stage_id, size := SIZE_NORMAL)`.

`add_to()` (the stage hash) is unchanged: the big numbers hash differently on their own. `arena_stages.gd` and `tools/gen_arena_stages.py` don't change. Nothing goes into `GameState`, the checksum or `copy_from`.

### 2.4 The camera (`client/game/arena_view.gd`)

**In rooms of any size, the whole level is always visible.** The camera never zooms in and never pans, including on the result. Outside rooms, the "Follows the fight" setting keeps working, with its zoom cap lowered from 2.1 to 1.4; its "whole arena" alternative uses the same fit as rooms.

One function, used by rooms and by the non-follow mode:

```
half_w      = rules.stage half width (16 normal, 24 big)
top         = 14.0 m                       # top platform 10.5 + a fighter + its tag
bottom      = 0.0 if touch else -1.5
band        = 90 logical px                # the HUD band
floor_line  = TouchControls.floor_clear_y(size) if touch else size.y   # screen y of world `bottom`
fit         = min(size.x / (2 * half_w), (floor_line - band) / (top - bottom))
lo          = 0.85 * fit                   # "a little farther", at most
target      = fit
if the highest active fighter's head (y + 2.8) is above the visible top:
    target  = max(lo, min(fit, (floor_line - band) / (head - bottom)))
ppm eases toward target (lerp 0.05 a frame), never above fit; x is centred
origin.y    = floor_line + bottom * ppm
```

- **`TouchControls.floor_clear_y(size)`** (new): the smallest top of the move pad, Kick and Jump (738 at 1080 today). Under touch, world y = 0 sits on that line, so the floor and everyone standing on it are above the thumbs. When the buttons move or change size (UI/UX fixes 5 and 20), the camera reads the new value with no change of its own.
- **16:9 desktop, big**: width-limited at 40 px/m; 23 m of height above the floor is visible, so launches show against the sky.
- **20:9 phone, big, touch**: (738 − 90) / 14 = 46 px/m, 0.92 of the width fit. Fighters launched above the visible top briefly use the edge markers. That is accepted.
- **Normal level, 8 players, phone**: height-limited; the "out" zones show at the sides.
- The fighter-size floor (art ≥ 103 logical px at ppm ≥ 40) holds on **16:9 and wider**. At 4:3 (1440 × 1080) a big level fits at 30 px/m, art about 77 px. The tag, marker and weapon minimums below are therefore set in pixels, not from ppm.
- `ARENA_HALF_WIDTH`, `ART_TOP` and `ART_BOTTOM` become values derived from `rules.stage.size`. `_watch_bursts`' `absf(x) < 17.0` becomes `< half_w + 1`.
- `client/game/settings_screen.gd`: the camera chips read "Follows the fight (not in rooms)" / "Whole arena".

### 2.5 Painted art on the big level

The painted layers are 1280 × 720 at 40 px/m. The near layer has "bookend" buildings at both sides, so stretching or repeating would look wrong. **Split and mirror**: each layer is cut at its centre and an insert e = 640 px (for 1.5×) goes between the halves. In big-level px:

- `[0, 640]` → `[0, 640]`;
- `[640 − e/2, 640]` **mirrored** → `[640, 640 + e/2]`;
- `[640 − e/2, 640]` → `[640 + e/2, 640 + e]`;
- `[640, 1280]` → `[640 + e, 1280 + e]`.

Every seam joins identical columns and the bookends stay at the true edges. Mirrored pieces use `draw_set_transform(Vector2(right, 0), 0, Vector2(-1, 1))` and `draw_texture_rect_region`, with the source rect scaled by `tex.width / layer.w` (the near layer is drawn at 1280 × 680). `_level_point` uses the centre `640 * k`. Verified with no visible seam on Rookie Rooftops, Gully Stadium and Grand Finale. Platforms already draw wider correctly (cap + tiled middle); hazards draw at native size on the scaled centre.

Outside the painting (replaces `_draw_edge_fade`):

- **Past the painted sides** (when the screen is wider than the level): each layer's outer columns mirrored outward, dimmed by a 1.2 m gradient into `Color(0.05, 0.045, 0.07, 0.62)`: the "out" zone.
- **Above the art top**: the arena's `skyTop` colour, with a 1.5 m fade of the painting into it.
- **Below the art bottom**: the arena's `ground` colour, darkening 50% downwards.
- `tools/import_art.py` writes `skyTop` (the mean of the far layer's top opaque row) and `ground` (the mean of the foreground strip) into each `arena.json`. Several `sky` colours don't match the painting today (Gully Stadium shows a beige band over a blue sky). The view falls back to `sky` and `sky.darkened(0.55)` when the keys are missing.

### 2.6 Readability when zoomed out

Logical px; physical = logical × min(w / 1920, h / 1080). On the Xiaomi they are equal.

- **Name tag** (one spec for every mode with more than 2 fighters):
  - a pill: fill `Color(0.05, 0.045, 0.07, 0.88)`, a 3 px border in `_color_of(i)`, and in teams the team symbol (radius 12) inside at the left;
  - text white, turning to DANGER red by Josh; font 26 on touch, 22 on desktop; your own 30 with a white border;
  - content: "YOU 45%" for you; with more than 6 fighters, teammates "Name(7) 45%" (the name cut to 7 characters) and everyone else "45%"; with 6 or fewer, "Name 45%" for all;
  - drawn after the front art layers;
  - declutter: placed in order (yours first); one that overlaps a placed tag moves up by its height + 2 px, at most 3 times, with a 2 px leader line to its fighter;
  - clamped 6 px inside the screen edges.
- **Chat bubble**: above the speaker's tag stack, never on it.
- **Weapons**: the held sprite at max(length × ppm, 40 px); shot lines ≥ 3 px; pebbles ≥ 5 px radius; bullet trails ≥ 18 px; "word" effects ≥ 22 px; KO burst reach ≥ 60 px. The weapon label under the feet is removed (the player box or your HUD chip carries it).
- **Lobby**: a "BIG ARENA" chip beside the arena card once `size_for(members) == SIZE_BIG`. It is not a control.

## 3. UI/UX fixes, ranked by severity

The room HUD (fix 1), the result panel (fix 2) and the camera items are new designs; the rest are fixes of what is there. "Covered" means the fix is specified in section 1 or 2.

| # | Sev. | Screen | Problem | Fix | Files |
|---|---|---|---|---|---|
| 1 | High | Room HUD, 15 fighters | A top-5 list covers the left of the arena; you can't tell who is who; no "you" panel; the KO feed sits behind Chat; markers say "P10" and stack. | **The chip strip** (3.1), head markers (3.2), the feed at y ≥ 134. | `arena_view.gd`, `standings.gd`, `main.gd` |
| 2 | High | Room result | A banner over the running arena, zoomed on the winner; no standings for 15; no countdown; touch controls on top. | **`result_screen.gd`** (3.3). | `result_screen.gd` (new), `main.gd`, `arena_view.gd` |
| 3 | High | Android back on Settings, the fighter circle, the arena picker, the creator | Back quits the whole game; Settings doesn't close on Esc either. | `main.gd` `_notification(GO_BACK)` and `ui_cancel` go through one `_close_top_overlay()`: creator (cancel, back to the circle), circle, arena picker, settings, help, chat, panel. Quit only from the bare menu, with the "Quit? No / Yes" chips (fix 26). `settings_screen.gd` handles `ui_cancel` (`hide()`, `closed.emit()`). | `main.gd`, `settings_screen.gd`, `character_circle.gd`, `arena_picker.gd` |
| 4 | High | Back in a room lobby or match | Back leaves the room at once, even with a picker open; a host ends the room for everyone. | The same chain. With nothing open: in a match, open the match panel; in the lobby, show "Leave the room? Stay / Leave" chips. | `main.gd`, `room_screen.gd` |
| 5 | High | Match with touch controls | Fighters, crates and the floor sit under the buttons on arena stages. | Covered: `floor_clear_y` (2.4). Also tighten the right cluster's vertical spread in `PAD_BUTTONS` so Heavy and Dodge cover less of the right-hand platforms. | `arena_view.gd`, `touch_controls.gd` |
| 6 | High | Touch controls | Labels vanish over light skies (13% white fill, 80% white text, no outline). | Fill `Color(0.05, 0.045, 0.07, 0.38)`; pressed fill the accent at 0.6; labels drawn with `draw_string_outline` (size 6, black at 0.7) under `draw_string`. | `touch_controls.gd` |
| 7 | High | Room lobby, sharing | No Copy or Share; the code is small dim text that can't be selected. | The code at 64 px, a primary **Copy link** next to it emitting `copy_requested`, wired in `main.gd` to the existing copy path (`_on_copy`, `WEB_COPY_JS`, `_sync_web_copy`), then "Copied!". On Android, the system share sheet if the engine offers one, else copy. | `room_screen.gd`, `main.gd` |
| 8 | High | Room lobby, 15 players | The list pushes "Leave room" off the bottom. | Covered: the fixed top bar and the scroll container (1.8). | `room_screen.gd` |
| 9 | High | Match menu, room host | "End match for everyone" has no confirmation; "Leave match" really leaves the room; "Quit game" sits next to it. | "End match for everyone" opens chips "End the match? No / Yes" (Yes in the error colour). The leave button reads "Leave room" in rooms. Quit is removed from the in-match panel. | `main.gd` |
| 10 | High | 1v1 duel, connecting and waiting | A grey graybox, dead touch controls, no Cancel, nothing moving. | Before RUNNING, draw `_menu_rules()` (the player's arena) as the backdrop; a centred panel with the code in large type, Copy link, **Cancel** and animated dots; touch only in RUNNING. The opponent's real fighter needs a duel handshake change: not in this pass. | `main.gd` |
| 11 | Med. | Touch controls when they do nothing | They show while watching, waiting and on results. | `_update_touch`: needs `_my_fighter() >= 0`, ONLINE needs `_phase_is("RUNNING")`, and not `match_over`. | `main.gd` |
| 12 | Med. | Auto play | A toggle whose state is its own label (a cycling button); 31 px tall on the phone; next to the clock. | Removed from the top bar. The match panel gets an "Auto play: Off / On" `ChoiceRow`, 96 px tall on touch; an "AUTO" badge shows on your HUD chip (rooms) or player box while it is on. | `main.gd`, `arena_view.gd` |
| 13 | Med. | Practice result | Only "<name> WINS!" and a secondary-styled "Play again"; no way to change fighter or arena; the name even when you won. | Reuses `result_screen.gd` with 2 rows: "YOU WIN!" / "YOU LOSE", a primary **Play again**, then "Change fighter", "Change arena", "Menu". The hint reads "R = play again   Esc = menu". | `main.gd`, `result_screen.gd` |
| 14 | Med. | Room lobby, guest | Guests see none of the settings; half the screen is empty. | Read-only chips in the host box's place ("10 min", "12 CPUs", "3 teams") and a larger waiting line with animated dots. | `room_screen.gd` |
| 15 | Med. | Room lobby, host controls | Slider grabbers are ~22 px on the phone; no end labels; the arena picture isn't obviously a button. | Grabber 84 virtual px; slider height 96 with min and max labels at the ends; a "Change arena" button under the arena card. | `ui_theme.gd`, `room_screen.gd` |
| 16 | Med. | Match HUD, practice and duel, phone | Box names 16 px, "KO 0" 13, "GUTS" 10; the "ALIVE 2/2" box is useless in a 1v1; boxes 72% opaque. | In-arena text ×1.5 under touch, 24 px minimum; no ALIVE box when n == 2; `BOX_FILL` alpha 0.88; the weapon name and ammo only in the player box. | `arena_view.gd` |
| 17 | Med. | Touch: Throw / Combo / Kick | One small button is Combo empty-handed and Throw when armed, at the hardest spot for the thumb; Kick's label turns into the weapon's verb. | A fixed **Combo** button (always Kick + Heavy); a separate **Throw** next to Kick that appears only when armed, with the weapon icon and gold ring. Kick keeps "Kick", with the verb as a small caption. Both moved down and right within thumb reach. | `touch_controls.gd` |
| 18 | Med. | Touch: Up and Down | 15 px arrows; the Down zone is 35 px tall at the edge, so the recovery is hard. | Half-moon zones the width of the pad, arrows ≥ 50 virtual px, the Down zone ≥ 100 px tall (`MOVE_FROM_CORNER.y` raised). | `touch_controls.gd` |
| 19 | Med. | Practice match menu | No settings, fighter or arena; "Player 2: A friend" offered on phones where P2 has no controls. | Add "Settings" (over the panel), "Change fighter", "Change arena". Hide the P2 row under `_prefer_touch` with no gamepad. | `main.gd` |
| 20 | Med. | Settings | Developer options shown to players; no touch size or opacity; heading style differs. | "Show hitboxes" and "Debug info" only when `OS.is_debug_build()`. New chips "Touch buttons: Small / Medium / Large" and "Opacity: Low / Medium / High" (`GameSettings.touch_scale`, `touch_alpha`, read by `touch_controls.gd`). Title "SETTINGS" in the pickers' 44 px heading style. | `settings_screen.gd`, `game_settings.gd`, `touch_controls.gd` |
| 21 | Med. | Quick chat | No title or close; your fighter stands still while it is open. | A compact strip of 6 phrases along the top under the HUD band, titled "Quick chat", with a Close chip; the touch controls stay live under it. | `main.gd` |
| 22 | Med. | Menu errors | A 28 px red line far from the code box; no next action; a lost room drops you on the menu. | A banner card under the join row with buttons by error code: `full` "Host a room"; `old_client` "Refresh" (web, `location.reload()`) or "Update" (Android, `OS.shell_open` to the store listing); `closed` / lost "Rejoin <code>". The relay sends code `old_client` for the out-of-date refusal. | `menu_screen.gd`, `main.gd`, `server/Rangbaazi.Relay/RelayApp.cs` |
| 23 | Med. | Room connecting, status lines | White text over a bright sky; no Cancel. | A rounded dark plate (`UiTheme.PANEL`) behind `status_big` / `status_small`, animated dots, and a **Cancel** HUD button while connecting (room and duel). | `arena_view.gd`, `main.gd` |
| 24 | Med. | Fighter circle | Back-row fighters ~15 px with 16 px tap circles; only the front one is named; the ring runs under the buttons. | Minimum scale 0.55, tap radius ≥ 60; a horizontal carousel of face chips (all 31 and "Create") above the buttons; ring radius 0.42 of the width, moved up; the hint above the front fighter. | `character_circle.gd` |
| 25 | Med. | Character creator | MOVES shows the same faces as HEAD; pose chips 35 px; "Fight as Custom" with no name; the keyboard covers it. | MOVES cells as a looping "jab" clip labelled "moves of <name>"; pose chips ≥ 88 tall (font 32), cell names ≥ 26; the default name is the head fighter's name plus a suffix; the menu's compact mode while the name box has focus on a phone. | `character_creator.gd` |
| 26 | Low | No-cycling rule | `_on_level_pressed` (dead) cycles the CPU level; F2 / F3 / F4 cycle with no on-screen twin (Guts has no control); the "Tap again to quit" label swap. | Delete `_on_level_pressed`; add a "Guts: Off / On" row to the practice panel next to the CPU level; keep the F-keys as hidden extras; replace the two-tap quit with "Quit? No / Yes" chips. | `main.gd`, `ui_theme.gd`, `menu_screen.gd` |
| 27 | Low | How to play | Text only; on touch the real buttons are hidden behind it. | On touch, the controls stay visible (dimmed) under a transparent help layer with callouts next to each button (`TouchControls.point_for`); the controls text 32 px at full colour, using the drawn button names. | `main.gd` |
| 28 | Low | Main menu | The name field has no label; 11–13 px subtitles on the phone; translucent disabled Join; Quit smaller than Settings; CPU skill only in the pause menu. | A "YOUR NAME" caption; subtitle and footer ≥ 30; Quit at 44 like Settings; an opaque disabled fill (`PANEL.darkened(0.3)`); CPU skill chips (Easy / Normal / Hard) on the arena picker when it opens for practice. | `menu_screen.gd`, `ui_theme.gd`, `arena_picker.gd` |
| 29 | Low | Arena picker | Only the painting; no position; a 25 px slider on the phone. | Draw the platforms and hazards scaled into the card; an "n / 31" label; slider 96 tall. | `arena_picker.gd` |
| 30 | Low | Effects at the edges | Pick-up words cut off at the edge; the KO burst clamped over the top-left buttons. | Clamp a word's x so its measured width stays 20 px inside; clamp the KO burst below `hud_top_inset`. | `arena_view.gd` |

### 3.1 The room HUD strip (fix 1)

One layout for every room, free-for-all or teams, replacing the top-5 list and batch 1's stop-gap line.

- **The strip** runs from `BAR_MARGIN` to `size.x − (2 × MENU_MARGIN + 2.6 × MENU_BUTTON)` (310 px clear for Chat and the menu), within the 90 px band. `main.gd` sets `arena_view.hud_right_inset` to include Chat, and a new `hud_top_inset = MENU_MARGIN + MENU_BUTTON + 10` (134).
- **The clock plate**, 170 × 62, sits at the strip's centre.
- **Teams**: the groups in team order (stable, not by rank), the first `ceil(n / 2)` left of the clock. Each group starts with a **total plate**, 96 wide: the team colour band, the symbol and the team's KOs at 36 px; the leader's plate has a 3 px white border; a "+1" pops on it when its total rises. Then one chip per member.
- **Free-for-all**: chips in fighter order, the first `ceil(n / 2)` left of the clock.
- **A chip**: 58 px tall, width `clamp((free − plates) / (n + 0.5), 72, 150)`; the cached head (3.2) at 40 px, the KO count at 26 px, a 7 px left edge in `_color_of(i)`, a 4 px bottom bar coloured by Josh danger; dimmed while KO'd or watching; "CPU" small for a leaver.
- **Your chip** is 1.5× wide, with a white border, your Josh % at 26 px, your KOs, and the weapon icon with its ammo. It replaces the audit's "you" box.
- **When chips don't fit at 72** (narrower than 16:9): in teams, other teams collapse to their total plates and only your team keeps chips; in free-for-all, you plus the most KOs that fit, then a "+n" chip.
- **The KO feed** stays at the right, from y = `hud_top_inset`, killer and victim in their colours.

### 3.2 Off-screen markers and the head cache (fix 1)

- At match start, `arena_view.gd` builds one head texture per fighter: the painter drawn once into an `ImageTexture` (or `ArtLibrary.character_texture` for the head part). Chips and markers draw it with one `draw_texture_rect`.
- **A marker**: the cached head in a disc with a 3 px rim in `_color_of(i)` and, in teams, the team symbol. Markers within `2 × MARKER_RADIUS` of each other spread along the edge. Markers stay below `hud_top_inset`, and crate markers clear the touch buttons and the help/menu buttons.

### 3.3 The result panel (fix 2, fix 13)

New `client/game/result_screen.gd`, on its own CanvasLayer, shown by `main.gd` when `state.match_over`:

- **Rooms, free-for-all**: everyone ranked (`Standings.fighters_ranked`): place, face, name, KOs, falls; the winner on top, your row highlighted. Two columns when n > 8.
- **Rooms, teams**: the banner "BLUE TEAM WINS!" (symbol, colour) or "DRAW: BLUE and ORANGE level on 14 KOs and 9 falls"; "MVP <name> "<nickname>"" (the nickname is `Nicknames.for_winner(state, main_weapons[winner])`, unchanged, since `winner_index` is the MVP); then the teams in rank order (`Standings.teams_ranked`), each a 72 px row (symbol, name, "14 KOs  9 falls") with its members and their KOs under it.
- **A countdown bar** to the lobby, from the server's return time.
- **Practice**: 2 rows and the buttons of fix 13.
- The arena keeps running behind it at the whole-level view; the winners take the WON pose with a ring in their colour. `arena_view.gd`'s old banner and `over_hint` are removed in rooms and practice.

## 4. Polish

Done last, once batches 1 to 4 are in. Each is small and visible.

1. **Motion**: chips, notices and banners ease in (0.15 s); the team "+1" pop; START pulses gently when it can be pressed; the result rows slide in one by one.
2. **Sound**: chip taps, team pick, "Copied!" and the result use the existing UI sounds in `sound_director.gd`; a team win plays the existing win sting for everyone on the winning team, the lose sting for the rest. No new audio.
3. **Spawn-in**: a 0.3 s flash ring in the fighter's colour on spawn and respawn, so 15 fighters entering are easy to tell apart.
4. **Remembered choices**: the host's last team chip and minutes in `GameSettings` (`room_teams`, `room_minutes`), applied when they host the next room.
5. **Copy**: one voice across screens ("Leave room", "Change arena", "Copy link", "Copied!"); headings in the 44 px dim style everywhere.
6. **Performance**: with 15 CPUs on a big level, measure frame time on the Xiaomi (APK) and in the web export on a laptop browser. Target 60 fps on the phone; if it falls short, the first cuts are the chip faces (fall back to initials) and the mirrored side art.
7. **Tools**: `tools/shot_weapons.gd` gains `--teams` and `--big`, used to save review frames at 1920 × 1080, 2400 × 1080 and 1440 × 1080.
8. **Docs**: `server/Rangbaazi.Relay/README.md` (Arena rooms: requests and fields), `docs/tech/web-client.md` (the camera rule), and a dated build-notes section at the end of this spec.
9. **Tuning**: a playtest pass with the review frames: the spawn order, the 0.85 pull-back, the tag declutter, chip widths, the team palette in real arenas (Ice against light skies especially). Record what changed in the build notes.

## 5. Versions and wire changes (done once)

### 5.1 The version rule

HEAD has arena protocol **4** and `SIM_VERSION` **7**; the 5 and 8 exist only in the uncommitted v0.4 build. So:

- **Before batch 1 merges**, check what the live relay runs (a v=5 client to the live link is refused by an older relay). If it is **below 5**, teams and stage size **ride protocol 5 and SIM_VERSION 8**: no bump, and the `ArenaProtocol` doc comment for 5 adds "teams and stage size in lobby and start; in a team match the STATE winner is the winning team's MVP". The `SIM_VERSION` comment adds "team wins, big stages".
- **If 5 has already been deployed**, batch 1 sets protocol **6** and `SIM_VERSION` **9** (C# `Simulation.SimVersion`, GDScript `SIM_VERSION`, `ArenaLimits.ProtocolVersion`, `room_session.gd` `PROTOCOL_VERSION`) with that comment, once.
- Batches 2 to 5 never change the numbers. **Batches 1 and 2 go to the live relay together** (no deploy between them), so no client of one protocol number sees two message shapes.
- `NetProtocol.VERSION` (duels) stays 1. Nothing here touches the rollback socket.
- `tools/deploy-aws.ps1` changes paid resources: ask the user before running it.

### 5.2 Requests (client to server)

| Request | Who, when | Effect |
|---|---|---|
| `{"type":"teams","count":0\|2\|3\|4}` | host, lobby | `SetTeams` (0 = free-for-all) |
| `{"type":"team","team":1..4}` | anyone, lobby, teams mode | `PickTeam`; `team_full` on refusal |

### 5.3 Messages (server to client)

- **lobby** (`ArenaMessages.Lobby`): `"teams": TeamCount` (0 in free-for-all); per player `"team": p.Team` and `"left": p.Left`. No `size` and no `team_size`: the client works them out (`size_for(players.size())`, `TEAM_SIZE`).
- **start** (`ArenaMessages.Start`): `"teams": TeamCount`, `"size": "normal" | "big"`, and per fighter `"team": Rules.TeamOf(i)`. Spectators joining mid-match get it too. `ArenaRoom.Start` builds `Stages.ById(StageId, StageScaling.SizeFor(Fighters.Count))`.
- **The client** (`RoomSession._begin_match`): `size = msg.get("size", "normal")`; `teams` and each `team` default to 0. Only a present but unknown `size` ends the session with "This game is too old for the room: update it." A missing field means an older server: free-for-all on a normal stage.
- **STATE**: bytes unchanged. In a team match the winner byte (header byte 10) is the winning team's MVP, or -1 for a draw. Say so in the `EncodeState` summary.
- **Errors**: `team_full`, `teams_needed` (notices, the session stays); `old_client` for the out-of-date refusal in `RelayApp.cs` (it ends the session, as today, with its own code).

### 5.4 The note for combat spec v0.4

One line added to v0.4 section 9 (Open points), for the weapons build to settle in its batch 4: "Zones and the cloud cough hit teammates (2.8, 'a puddle is a puddle'), but 1.6 says statuses never land on a teammate: which?" Whatever it decides, the `CheckBlastZone` guard (1.6) keeps teammates from scoring off each other. This line is the only edit outside this spec's own files, and it goes in with batch 1.

## 6. Tests

All green at the end of every batch: `dotnet test Rangbaazi.sln`, the GDScript suite (`run_tests.gd`), the relay end-to-end tests, `tools/godot.ps1 smoke`.

### 6.1 Sim, C# and GDScript twins

- `tests/Rangbaazi.Sim.Tests/RoomRulesTests.cs` and `client/tests/test_teams.gd` (new, registered in `run_tests.gd`), the same cases:
  - `Team_kos_add_up_and_the_winner_is_the_best_teams_mvp`;
  - `Level_team_kos_go_to_fewer_falls_then_a_draw`;
  - `Mvp_ties_go_to_the_lower_index`;
  - `With_no_teams_the_timed_winner_is_unchanged` (null and all-zero `Teams` match `TimedWinner` over random KOs and falls);
  - `A_teammate_never_gets_ring_out_credit` (`LastHitBy` an ally, pushed past the blast zone: KOs unchanged, falls +1);
  - `A_bat_does_not_return_a_teammates_shot`.
- `SparringBotTests`: `Bot_never_dodges_a_teammates_swing`.
- `DeterminismTests`: `A_four_team_room_of_fifteen_on_a_big_stage_is_deterministic_and_rolls_back` (15 bots, teams [1,1,1,1,2,2,2,2,3,3,3,3,4,4,4], copy at frame 600, advance both, compare checksums).
- `StageScalingTests` (C#; the GDScript stage-hash test covers the arithmetic):
  - platforms x × 1.5, y unchanged; hazards the same width, centre × 1.5;
  - 15 distinct spawns over platform 0, ≥ 3 m apart, the first 9 spanning ≥ 40 m;
  - blast ±25.2: a fighter at x = 25.3 is KO'd, at 25.1 not;
  - `Stages.ById(id, Normal)` equals today's stage; `SizeFor(8) == Normal`, `SizeFor(9) == Big`;
  - every platform is reachable (a vertical step ≤ 5.5 m with a horizontal gap ≤ 4 m from some lower platform).

### 6.2 Goldens

- Batch 1 rewrites `all_weapons_teams_6p` (the win rule and the dodge rule change it).
- Batch 2 appends 31 `"<id>@big"` hashes to `arena_stages.json` (`GoldenTraceTests.ArenaStageHashes` iterates `ArenaStages.All`, then `StageScaling.BigAll`; the GDScript test uses `StageDefinition.by_id(id, SIZE_BIG)`), and adds **`room_big_teams_15p`**: big Rooftops, room rules, teams [1,1,1,1,2,2,2,2,3,3,3,3,4,4,4], weapons and hazards on, bots, about 2,460 frames. The scenario record gains `Size`; the JSON gains `"stage_size": "big"`, read by `test_golden_traces.gd` (`test_room_big_teams_15p`).
- Each batch writes its goldens in one `RANGBAAZI_WRITE_GOLDENS=1` pass, only after the weapons build's current batch has rewritten its own, and checks that the **14 no-teams goldens** are byte-identical: `random_2p`, `random_4p`, `bots_easy_vs_normal`, `bots_normal_vs_hard`, `bots_hard_vs_hard_no_guts`, `weapons_random_4p`, `weapons_bots_4p`, `newer_weapons_random_4p`, `newer_weapons_bots_4p`, `room_bots_6p`, `room_random_15p`, `all_weapons_random_4p`, `all_weapons_bots_4p`, `all_weapons_room_6p` (and `weapon_table.json`).

### 6.3 Relay (`tests/Rangbaazi.Relay.Tests/ArenaTeamTests.cs`, new, on `ArenaRoom` and `ArenaServer`)

- `Host_sets_teams_and_the_lobby_carries_the_count_and_each_team`
- `Free_for_all_lobby_sends_teams_0_and_team_0`
- `Guests_cannot_set_teams`
- `A_fifth_pick_of_a_team_of_people_is_refused_with_team_full_and_the_connection_stays`
- `Picking_a_team_full_with_a_cpu_swaps_the_cpu_out`
- `Joiners_and_cpus_fill_the_smallest_team_and_the_9th_and_13th_raise_the_count`
- `Team_count_never_drops_below_what_the_room_needs`
- `Lowering_the_count_rehomes_members_people_first`
- `Bots_rebalance_but_people_never_move`
- `Start_needs_two_teams_in_use`
- `Start_message_and_rules_carry_each_fighters_team` (`room.Rules.TeamOf(i) == Fighters[i].Team`)
- `A_mid_match_joiner_watches_and_gets_a_team_back_in_the_lobby`
- `A_leaver_is_played_by_the_cpu_and_marked_left` (free-for-all and teams)
- `Start_with_9_is_big_and_with_8_normal` (and `"size"` in the message)
- `Team_state_frame_winner_is_the_mvp` (`EncodeState` byte 10)
- `An_old_client_is_told_to_update_with_code_old_client` (for the version below the current one)

### 6.4 Client

- `client/tests/test_protocol.gd`: crafted lobby and start JSON through `RoomSession._on_text`: `teams`, `team`, `left`, `size`; a missing `size` is normal; `team_full` doesn't end the session.
- `client/game/scene_smoke.gd`:
  - **whole level visible**: a 15-fighter big room and an 8-fighter normal room at 1920 × 1080, 2400 × 1080, 1280 × 720, 1728 × 1080 and 1440 × 1080, with and without touch: platform 0 and every platform's screen rect inside the viewport, and under touch platform 0's top at or above `floor_clear_y`;
  - the lobby at 15 members: "Leave room" and START inside the viewport, desktop and phone sizes;
  - the result panel shows with the touch controls hidden;
  - Android back from Settings, the circle and the picker closes them and doesn't quit; in a room it opens the panel or the leave chips.
- **End to end**, `client/tests/room_teams_e2e.gd` (`-- --relay ws://127.0.0.1:8080`):
  1. the host sets 2 teams and adds 2 CPUs; the guest picks team 2;
  2. both lobbies show 2 v 2 with the guest on 2;
  3. start: both sessions' `rules.team_of` match the lobby, and the host's and guest's fighters are not allies;
  4. the guest leaves; the host's lobby shows `left: true` and that fighter keeps moving;
  5. the match ends: the winner byte is a fighter on the team with the most KOs by `Standings.team_kos`, or -1 on a tie;
  6. everyone is back in the lobby.

Friendly fire itself is checked in the sim tests (v0.4's and 6.1); STATE carries no last-hit byte for the end-to-end test to read.

## 7. Build batches

Sequential. Each is one engineer, end to end, with the C# reference, the GDScript port and every test green before the next starts. The hard rules in CLAUDE.md apply: sim changes go into both sims, goldens are rewritten with `RANGBAAZI_WRITE_GOLDENS=1`, scenes only read state.

### Batch 1: teams

- **Server**: `server/Rangbaazi.Relay/Arena/ArenaProtocol.cs` (limits, `TeamsNeeded`, the version per 5.1, the lobby and start fields, the `EncodeState` note), `ArenaRoom.cs` (1.2–1.4), `ArenaServer.cs` (1.5), `RelayApp.cs` (`old_client`), `README.md`.
- **Sim**: `src/Rangbaazi.Sim/Simulation.cs` (`HasTeams`, `TimedTeamWinner`, `CheckMatchOver`, `CheckBlastZone`, the bat loop in `ShotHits` if it lives there, `SimVersion` per 5.1), `SimulationWeapons.cs` (the bat loop if it lives there), `Diagnostics/SparringBot.cs` (`ShouldDodge`).
- **GDScript sim**: `client/sim/match_rules.gd`, `simulation.gd`, `sparring_bot.gd`.
- **Client**: `client/net/room_session.gd`, `client/game/room_screen.gd` (1.8, including the fixed Leave and the scroll container), `team_board.gd` (new), `team_style.gd` (new), `standings.gd` (new), `choice_row.gd`, `arena_view.gd` (`_color_of`, discs, the symbol on tags, the WON pose, the stop-gap totals, the banner), `main.gd` (signals, `left` tags).
- **Tests**: `RoomRulesTests.cs`, `SparringBotTests.cs`, `DeterminismTests.cs` (the 15-player team test on a normal stage until batch 2 makes it big), `GoldenTraceTests.cs` and `all_weapons_teams_6p.json`, `tests/Rangbaazi.Relay.Tests/ArenaTeamTests.cs` (new), `client/tests/test_teams.gd` (new), `run_tests.gd`, `test_protocol.gd`, `room_teams_e2e.gd` (new).
- **Docs**: the v0.4 section 9 line (5.4).
- **Done when**: a host can run a 4-team room of 15 over the local relay; colours, totals and the team win show; a leaver is played by the CPU.

### Batch 2: big level and camera

- **Sim**: `src/Rangbaazi.Sim/StageDefinition.cs` (`StageSize`, `StageScaling`, `Stages.ById(id, size)`); `client/sim/stage_definition.gd`.
- **Server**: `ArenaRoom.cs` (`Start` picks the size), `ArenaProtocol.cs` (`"size"` in start).
- **Client**: `room_session.gd` (`size`), `arena_view.gd` (the derived stage frame, `_fit_ppm` and the camera of 2.4, split-and-mirror art, out zones, `skyTop` / `ground`, the tag spec and declutter, the chat bubble, weapon minimums, the follow cap 1.4), `touch_controls.gd` (`floor_clear_y`), `settings_screen.gd` (the camera chip wording), `room_screen.gd` (the "BIG ARENA" chip), `scene_smoke.gd`.
- **Tools and assets**: `tools/import_art.py` writes `skyTop` and `ground`; rerun it with the packs in CLAUDE.md and keep only the `arena.json` key additions if anything else differs.
- **Tests**: `StageScalingTests.cs` (new), `DeterminismTests.cs` (the team test on the big stage), `GoldenTraceTests.cs` with `arena_stages.json` (+31) and `room_big_teams_15p.json`, `test_golden_traces.gd`, `ArenaTeamTests.cs` (the size cases), `test_protocol.gd`, `scene_smoke.gd` (whole level visible).
- **Done when**: a room of 9+ plays on the 48 m level; on 16:9, 20:9 with touch and 4:3, the whole level is always on screen and the floor clears the thumbs. Batches 1 and 2 can then be deployed together (ask first).

### Batch 3: UI/UX fixes, the match screens

Fixes 1, 2, 5 (the cluster), 6, 9, 11, 12, 13, 16, 17, 18, 30.

- **Files**: `client/game/arena_view.gd` (the HUD strip 3.1, head cache and markers 3.2, the feed, the practice HUD sizes, the edge clamps, the old banner removed), `standings.gd`, `result_screen.gd` (new), `main.gd` (insets, Auto play chips, `_update_touch`, the end-match confirm, "Leave room", practice result buttons), `touch_controls.gd` (fill and outline, Combo and Throw, Up and Down zones, the tighter cluster), `scene_smoke.gd` (result panel, touch hidden; the whole-level case still passes with the moved buttons).
- **Done when**: a 15-player room on the phone reads at a glance, the result ranks everyone, and the touch controls never cover a result or a dead match.

### Batch 4: UI/UX fixes, menus, lobby and back

Fixes 3, 4, 7, 10, 14, 15, 19 to 29.

- **Files**: `client/game/main.gd` (`_close_top_overlay`, back in rooms, the duel waiting panel, the practice panel, quick chat strip, help callouts, error actions, Cancel while connecting), `room_screen.gd` (Copy link, guest chips, slider labels, "Change arena", the leave chips), `ui_theme.gd` (grabber, disabled fill, the quit confirm), `menu_screen.gd` (error banner, name caption, sizes, quit), `settings_screen.gd`, `game_settings.gd` (`touch_scale`, `touch_alpha`), `touch_controls.gd` (reads them), `character_circle.gd`, `character_creator.gd`, `arena_picker.gd`, `client/tests/test_protocol.gd` (the `old_client` code), `scene_smoke.gd` (back handling, the 15-member lobby).
- **Done when**: no screen quits the game on Android back, nothing leaves a room without asking, every error offers its next step, and no control left in `client/` cycles.

### Batch 5: polish

Section 4, items 1 to 9.

- **Files**: `client/game/arena_view.gd`, `result_screen.gd`, `room_screen.gd`, `main.gd`, `sound_director.gd`, `game_settings.gd`, `tools/shot_weapons.gd`, `server/Rangbaazi.Relay/README.md`, `docs/tech/web-client.md`, this spec (build notes). No sim change.
- **Done when**: review frames at three sizes look right, the phone holds its frame rate in a 15-CPU big room, and an APK is built for the user to try on the Xiaomi.

## Review notes

One review of the merged designs (2026-11-21). Each point and what was decided.

| Point | Decision |
|---|---|
| Bump protocol and `SIM_VERSION` once, shared, folding into 5/8 | **Accepted.** One rule in 5.1: ride 5/8 if the live relay is below 5, else 6/9 once in batch 1. Batches 1 and 2 deploy together. |
| Default a missing `size` to normal; drop lobby `size` and `team_size` | **Accepted.** The client computes both; `size` stays in start. |
| The black-hole `Pull` change contradicts v0.4 | **Accepted.** Dropped with its test; `Pull` stays as built. |
| Raise zones and cloud as a v0.4 question, don't amend it | **Accepted.** One line in v0.4 section 9 (5.4). The `CheckBlastZone` guard and the bat rule stay. |
| The two team palettes conflict; marigold looks like gold | **Accepted.** `TeamStyle` everywhere through `_color_of`; disc alpha 0.75 with the rim; FFA keeps `PLAYER_COLORS` with the two near-duplicates replaced. |
| Three room HUDs; pick one | **Accepted.** The chip strip grouped by team, total plates, your wide chip; batch 1 has a small stop-gap line so it ships on its own. |
| The strip runs under Chat, menu and Auto play | **Accepted.** The strip stops 310 px short; `hud_right_inset` includes Chat; Auto play moves to the panel as chips; the feed starts at 134. |
| The 16% lift doesn't clear the touch controls | **Accepted.** `TouchControls.floor_clear_y`, world 0 on that line under touch; 46 px/m on 20:9. |
| Tags too small on the phone; red on pink | **Accepted.** 26 / 22 / 30 px, a neutral dark fill, the colour as the border, the symbol inside. |
| One result panel; no zoom on the winning team | **Accepted.** Only `result_screen.gd`; rooms never zoom; winners pose with a ring. |
| `team_kos` / `team_falls` have no C# twin | **Accepted.** They live in `client/game/standings.gd`; `TimedTeamWinner` stays in both sims. |
| Merge the two 15-player goldens; one rewrite | **Accepted in part.** One golden, `room_big_teams_15p`, and the list of 14 unchanged goldens. One pass per batch rather than one in all, because batch 1 must leave `all_weapons_teams_6p` green before batch 2 exists. |
| Cut the teams extras (pile-on, stock-branch end, Even teams, two chip rows) | **Accepted.** All four cut; one chip row and one `teams` request. |
| Let the CPU play leavers in free-for-all too | **Accepted.** `p.IsBot \|\| p.Left` in every mode. |
| FFA keeps the list that pushes Leave off screen | **Accepted.** Both views in one scroll container; Leave in a fixed top bar; the caption moved to the status line. |
| Fighter sizes aren't guaranteed on 4:3 or 16:10 | **Accepted.** The guarantee says 16:9 and wider; 1440 × 1080 and 1728 × 1080 are in the smoke test; minimums are in pixels; the strip collapses when chips don't fit. |
| Use a cached head texture | **Accepted.** One texture per fighter, built at match start (3.2). |
| Merge the marker specs | **Accepted.** One marker (3.2). |
| The camera fit belongs outside rooms too | **Accepted.** One `_fit_ppm` for rooms and the non-follow mode; the settings chip says "not in rooms"; the follow cap is 1.4. |
| Don't change the duel handshake | **Accepted.** The real opponent fighter waits for a separate netcode change; the rest of fix 10 goes in. |
| Fix the e2e assertion and stale line numbers | **Accepted in part.** Code is named by function only. The proposed check (the guest's last-hit byte in STATE) is **rejected**: `GameStateCodec` sends no last-hit byte. The e2e test checks the rules, the leaver and the winner byte; friendly fire is covered by the sim tests. |
| Small copy fixes ("BIG ARENA", one signal for START) | **Accepted.** "BIG ARENA" with no metres; START disabled with the reason in the status line only. |

Open questions from the designs, decided here:

| Question | Decision |
|---|---|
| Uneven teams (4/4/4/3): raw KOs, per head, or a 16th CPU? | **Raw totals.** The CPU balance keeps them within 1; a 16th fighter would change the room limit of 15 and the server's tick cost. |
| Team names | **BLUE, ORANGE, PINK, ICE.** Plain words, readable at a glance, and original. |
| Remember the host's mode; a team-only quick chat? | **Remember it** (polish item 4). **No team chat** in v1: quick chat is a shared phrase list, and a second channel needs a B10 design note. |
| How wide is the big level: 1.5× or 1.25×? | **1.5× (48 m).** It fits a 20:9 phone exactly and gives 9 m a fighter with 15; at 1.25× it is 7.5 m, close to today's crowding. |
| Rooms of 8 or fewer: whole level too? | **Yes.** The user asked for the whole level always; a normal level fits at 46–64 px/m. |
| Spawn order: spread, or teammates side by side? | **Spread.** It works for any 9 to 15 and doesn't reorder fighters on the server. |
| Draw fighters larger than their metres on desktop? | **No.** Hits would be harder to read; the pixel minimums for tags and weapons cover readability. |
| High launches above the phone's visible top | **Accepted** as edge markers for a moment; the lift stays. |
| `skyTop` and `ground` from `import_art.py` | **Yes**, with fallbacks in the view (2.5). |

## Build notes

### Batch 1: teams (2026-11-21)

**Built.**

- **Server.** `ArenaLimits.MaxTeams`, `MinTeams`, `MaxTeamSize`, `TeamsNeeded`; `ArenaPlayer.Team`; `ArenaRoom.TeamCount`, `TeamsMode`, `Size`, `People`, `SetTeams`, `PlaceNew`, `PickTeam`, `BalanceBots`, `CanStart`, and the team rules in `AddPerson`, `RemovePerson`, `SetBots`, `ReturnToLobby` and `Start` (1.2 to 1.3). Leavers are played by the CPU in every mode (`p.IsBot || p.Left`, 1.4). Requests `teams` and `team`, the `teams_needed` guard on `start`, `team_full`, and `"team"` in `ArenaRequest.NumberKeys` (1.5). The lobby carries `teams` and each member's `team` and `left`; start carries `teams` and each fighter's `team` (`Rules.TeamOf(i)`). `RelayApp` refuses an old client with code `old_client`. The doc comments of protocol 5 and `SimVersion` 8 say what rides on them (5.1: the live relay runs protocol 3, so no bump). README updated.
- **Both sims, line for line.** `MatchRules.HasTeams` / `has_teams`, `Simulation.TimedTeamWinner` / `timed_team_winner` and its use in `CheckMatchOver`; the ring-out guard in `CheckBlastZone` (`!Allies(rules, LastHitBy - 1, index)`, identical to `!= index` with no teams); a bat never returns a teammate's shot (`ShotHits`, in `SimulationWeapons.cs` and `simulation.gd`); `SparringBot.ShouldDodge` skips allies.
- **Client.** `RoomSession`: `MAX_TEAMS`, `TEAM_SIZE`, `teams_needed`, `team_count`, `my_team`, `team_sizes`, `team_people`, `set_teams`, `pick_team`, `fighter_teams`, `fighter_ids`, `fighter_left`, `NOTICE_CODES` with `notice` / `notice_tick` / `current_notice`, and `_begin_match` building the rules with the teams (a missing `teams` or `team` is a free-for-all). New `TeamStyle` (names, colours, polygon symbols with a dark rim), `Standings` (`team_kos`, `team_falls`, `teams_ranked`, `fighters_ranked`) and `TeamBoard` (one flat-button card per team). `ChoiceRow.set_enabled` / `set_locked`. `RoomScreen`: the fixed top bar with Leave room, the mode chip row (greyed below `teams_needed`, locked but lit for guests), the scroll container with the team cards or a free-for-all grid of player chips (face, name, host / you / CPU), the team ring on your fighter, START disabled with one team in use, and the one status line. `ArenaView`: `_color_of(i)` everywhere a player colour was read, the floor disc at 0.75 with a 2 px dark rim, the team symbol beside a fighter's label, the whole winning team in the WON pose, the stop-gap team totals under the clock, and the team banner ("ORANGE TEAM WINS!" with its symbol, "MVP <name>", the nickname line; "DRAW"). `main.gd` connects `teams_changed` / `team_picked` and tags a leaver's fighter "CPU".
- **Tests.** C#: the six `RoomRulesTests` cases of 6.1 plus a 15-CPU four-team room ending on its best team's MVP; `SparringBotTests.Bot_never_dodges_a_teammates_swing`; `DeterminismTests.A_four_team_room_of_fifteen_is_deterministic_and_rolls_back` (normal stage until batch 2); new `ArenaTeamTests` (18: the lobby rules on `ArenaRoom`, the requests, notices and messages end to end, the leaver in both modes, the winner byte, `old_client`). GDScript: new `test_teams.gd` (the 6.1 twins and the dodge case), three `test_protocol.gd` cases (lobby and start with teams and `left`, an older server's messages, notices that keep the session). New `client/tests/room_teams_e2e.gd`. Goldens: only `all_weapons_teams_6p.json` changed; the 15 no-teams traces (the 14 of 6.2 and `big_room_props_15p`), `arena_stages.json` and `weapon_table.json` are byte-identical to the copies taken before the batch.

**Differs from the spec.**

- **Free-for-all colours.** Sand `#c9a26b` and ice `#7fdbff` became cocoa `#997a54` and magenta `#f224f2`: each is at least 48.0 (CIE76) from every other colour of the palette, where ice was 23.4 from sky. Two other pairs of the 14 kept colours are closer than that (16.0 at the closest); the spec only asked for these two.
- **The uneven warning** shows when two teams differ by 2 or more members, not by 1: with 7, 9, 10, 11, 13, 14 or 15 members a difference of one can't be avoided, and "Teams are uneven (4 v 3)" was on screen for every full room. A difference of two only happens when people chose it, which is what the warning is for.
- **`team` requests** for the team you are on are ignored; any other refusal (four people, or a team the host has just removed) is `team_full`, so a race with the host never closes a guest's connection. A `teams` count of 1 or above 4 is clamped like any other, not refused.
- **`ARENA_MATCH_SECONDS`** (new `RelayOptions.ArenaMatchSeconds`, default 0): a local relay can shorten every match so `room_teams_e2e.gd` sees one end and checks the winner byte. Against a normal relay the script ends the match itself and says the winner check was skipped. Not for production.
- **`RoomSession.new(..., dial := false)`** makes a session that never connects, for the protocol tests.
- The share row (code at 64 px and Copy link, fix 7) is left to batch 4 as section 7 says; the lobby keeps today's code and link line. The chip row's font is 28 so the four chips fit the left column at 1920 x 1080.

**Checked.** `dotnet test Rangbaazi.sln`, the GDScript suite, both scene smokes and `tools/godot.ps1 smoke`; against a local relay `relay_e2e.gd`, `room_e2e.gd` and `room_teams_e2e.gd` (with `ARENA_MATCH_SECONDS=40`: 15 on four teams of 4/4/4/3, 2 v 2 with the guest on team 2, the leaver's fighter moving under the CPU, the winner byte the best team's MVP, back to the lobby without the leaver). Review frames at 1600 x 900 and 1200 x 540 with touch (the team lobby as host and guest, the team-full notice, the free-for-all chip grid, a 15-player team match with totals, a leaver tagged CPU, "ORANGE TEAM WINS!" with the MVP, a 2 v 2 draw) were looked at in the scratchpad.

**Left for later batches.** The big level and camera, `"size"` in start and its client check (batch 2); the HUD strip, head markers, the result panel and removing the stop-gap totals and the top-5 list (batch 3); Copy link, guest chips, slider labels, and the lobby at 4:3 and the 15-member lobby smoke check, not looked at in this batch (batch 4); remembered team choice, team stings and motion (batch 5).

### Batch 2: big level and camera (2026-11-21)

**Built.**

- **Both sims, line for line, no new state.** `StageSize` (Normal, Big), `StageDefinition.Size`, `StageScaling` (`BigFromPlayers` 9, `SizeFor`, `ScaleX`, `Big`, `BigAll`) and `Stages.ById(id, size)` in `StageDefinition.cs`; `SIZE_NORMAL` / `SIZE_BIG`, `size`, `size_for`, `big` (cached in `_big_cache`) and `by_id(id, size)` in `stage_definition.gd`. The stretch, the hazard centres, the 15 spawns in the spread order and the ±25.2 m blast zone are exactly 2.3's arithmetic. `GameState`, the checksum, `copy_from`, `add_to`, `ArenaStages` and the generator are untouched.
- **Server.** `ArenaRoom.Start` plays `Stages.ById(StageId, StageScaling.SizeFor(Fighters.Count))`; `start` carries `"size": "normal" | "big"`. Protocol 5 / `SimVersion` 8 unchanged (their comments already said "stage size"). README updated.
- **Client.** `RoomSession._begin_match` reads `size` (missing: normal; unknown: ends with "This game is too old for the room: update it."). `ArenaView`: `place_camera(size)` (shared by `_draw` and the smoke test), one `fit_ppm` and `_fit_camera` for every room and for the "Whole arena" setting (centred, never panned, never closer than the fit, pulled back towards 0.85 of it while a head is above the top, the floor kept on `_floor_line`); the follow camera (outside rooms only) capped at 1.4 and framed above the thumbs under touch; `_half_w` / `_level_k` from the stage size; split-and-mirror layers (`_layer_piece`), the outer columns mirrored into the out zone, and `_draw_beyond_art` (skyTop above with the 1.5 m fade, ground below darkening by half, the out zone dimmed over 1.2 m), replacing `_draw_edge_fade`; the pill name tags of 2.6 (`tag_text`, `_queue_tag`, `_draw_tags` after the front art, yours first, the declutter with leader lines, 6 px inside the screen, the team symbol inside, the combat word or CPU on top); chat bubbles above the tag; the weapon minimums (held 40 px, bullet and arrow trails 18 px long and 3 px thick, pebbles 5 px, bolt tails 18 px, words 22 px, the KO burst 60 px at its widest). `TouchControls.floor_clear_y(view)` (738 at 1080). `BurstWatch.bursts` takes the level's half width + 1 (the view and `SoundDirector.arena_half`). Settings: "Follows the fight (not in rooms)" / "Whole arena". The lobby shows a "BIG ARENA" chip by the arena's name from 9 members.
- **Art.** `tools/import_art.py` writes `skyTop` and `ground` into each `arena.json`, worked out by the new `tools/arena_colours.gd` (Godot decodes the WebP layers; this machine's Python has no image library). Rerun with round-brawler-v6 and the v7 delta: the only change in `client/assets` is the two keys added at the end of the 31 `arena.json` (checked: same JSON otherwise, same bytes up to the new keys).
- **Tests.** C#: new `StageScalingTests` (the 31 stretches and hazards, the 15 spawns, the blast zone at 25.3 / 25.1 m both sides, sizes and `ById`, every platform reachable on all 31 big levels); `DeterminismTests.A_four_team_room_of_fifteen_on_a_big_stage_is_deterministic_and_rolls_back`; `ArenaTeamTests.Start_with_9_is_big_and_with_8_normal` (and `size` in the message, back to normal at 8). Goldens: `arena_stages.json` gains the 31 `"<id>@big"` hashes; new `room_big_teams_15p.json` (big Rooftops, room rules, four teams, 15 bots, weapons and hazards, 2,460 frames; `"stage_size": "big"` is written only for a big trace, so the others keep their bytes). The 15 no-teams traces and `weapon_table.json` are byte-identical to the copies taken before rooms v1; `all_weapons_teams_6p` (batch 1's) is rewritten unchanged by the same sim, as both twins still pass it. GDScript: `test_room_big_teams_15p`, the stage hashes at both sizes, `test_the_start_size_picks_the_level`. `scene_smoke.gd`: `whole_level_problem` plays a 15-fighter four-team big room and an 8-fighter normal room for 720 frames (one fighter lifted 19 m for 120 of them) through ten views (1920 x 1080, 2400 x 1080, 1280 x 720, 1728 x 1080, 1440 x 1080, each with and without touch) and fails on any frame where a platform leaves the screen under the HUD band, the zoom leaves 0.85..1 of the fit, or under touch the floor's top is below `floor_clear_y`. `room_teams_e2e.gd` checks the 15-player match is on the big level (a fighter more than 17 m out in the server's frames) and the 2 v 2 on the normal one.

**Differs from the spec.**

- **Screen shake** in the whole-level view jolts upwards only: a sideways or downward shake pushed the level's edge 8 px off a 16:9 screen, or the floor onto the thumbs.
- **The HUD band** in `fit_ppm` is the view's own `_bar_bottom` (90 in rooms, as the spec says; the stats bar's height in practice with "Whole arena").
- **Pill tags** are used in rooms of any size and whenever more than 2 fight; duels and practice keep "P1" / "P2". A combat word (GUTS!, STUN) or a leaver's "CPU" sits on top of the pill.
- **Your weapon label** under your feet stays in rooms until batch 3's HUD chip carries it; outside rooms it is gone (the player box has it).
- **`Stages.ById("graybox", Big)`** is null: rooms play arena stages only.
- **skyTop / ground** come from a Godot helper that `import_art.py` runs (set `GODOT` if it isn't the default install); without Godot the keys are left out and the view falls back to `sky`.

**Checked.** `dotnet test Rangbaazi.sln`, the GDScript suite, both scene smokes, `tools/godot.ps1 smoke`; against local relays `relay_e2e.gd`, `room_e2e.gd` and `room_teams_e2e.gd` (with `ARENA_MATCH_SECONDS=40` the team match ended on the best team's MVP; on a normal relay all three pass, the winner check skipped). Frames of the real `ArenaView` looked at in the scratchpad: 15 on big Rooftops at 16:9 (40 px/m) and on the phone with touch (46 px/m, pulling back to 40), big Gully Stadium at 4:3, big Grand Finale at 16:10 (no seam), 12 on big Grand Finale at 4:3 with touch, 9 on big Frost Factory at 1280 x 720, 8 on normal Gully Stadium on the phone (out zones at the sides), practice with the follow camera on the phone (the floor above the thumbs), and the lobby's BIG ARENA chip and the settings wording.

**Left for later batches.** The HUD strip, head markers and result panel still to come (batch 3): the top-5 list and KO feed overlap the arena's top-left and top-right corners at 20:9, and the right touch cluster (Heavy, Dodge, Combo) still covers the right-hand platforms (fix 5's tighter cluster). In a pile of five or more, a few tags can still overlap after the three moves the declutter allows (tuning in batch 5). The `docs/tech/web-client.md` camera rule and `shot_weapons.gd --big` are batch 5's.

### Batch 3: UI/UX fixes, the match screens (2026-11-21)

**Built** (fixes 1, 2, 5, 6, 9, 11, 12, 13, 16, 17, 18 and 30; no sim, wire or golden change).

- **The room HUD strip (3.1), `arena_view.gd`.** `_draw_room_hud` draws one layout for every room from the static `ArenaView.hud_strip(width, right_inset, teams, you, kos)`: the clock plate (170 x 62) at the strip's centre, or as near as the two sides allow; in teams each team's group in team order (a total plate with the colour band, symbol and KOs, the leader's edged in white, "+1" rising under it when its total goes up), the first half of the groups left of the clock; in a free-for-all a chip per fighter in fighter order. A chip has a 7 px edge in `_color_of(i)`, the cached head, the KOs, a Josh bar along the bottom (its length and colour), is dimmed while the fighter is out, and says "CPU" for a leaver. Your chip is wider, edged in white, with your Josh %, your KOs on the corner of your head, the weapon's icon and ammo, and an AUTO badge while auto play is on. When the chips don't fit, other teams shrink to their plates (only yours keeps its chips) or, in a free-for-all, you and the most KOs that fit, then "+n". The top-5 list, batch 1's stop-gap totals and the room banner are gone. `main.gd` sets `hud_right_inset` to 2 x 20 + 2.6 x 104 in rooms (Chat and the menu) and the new `hud_top_inset` to 134; the KO feed starts under it, killer and victim each in their colour.
- **Head cache and markers (3.2).** `ArenaView.head_texture_for(id)` takes the head part of a fighter's rig (one texture per fighter, rebuilt when the cast changes); chips, markers and the result panel draw it. A marker is the head in a dark disc with a 3 px rim in the fighter's colour and, in teams, the symbol; yours is placed first and ringed in white; markers within 2 radii spread along their edge. Fighter and crate markers keep below `hud_top_inset` and, under touch, above `TouchControls.floor_clear_y` (so off the buttons).
- **The result panel (3.3, fixes 2 and 13), new `result_screen.gd`** on its own CanvasLayer, as tall as what it holds, under the HUD band. Rooms, free-for-all: everyone ranked (`Standings.fighters_ranked`, shared places on a tie), face, name, KOs, falls, the winner's row gold, yours lit, two columns past 8. Teams: "ORANGE TEAM WINS!" with its symbols, or "DRAW: BLUE and ORANGE level on 14 KOs and 9 falls"; "MVP <name> <nickname>"; the teams in rank order as cards (two columns for three or four), each member with face and KOs. A bar counts down to the lobby. Practice: "YOU WIN!" / "YOU LOSE" (a name with two people at one keyboard), both fighters, a primary Play again, Change fighter, Change arena, Menu, and "R = play again   Esc = menu" off touch. The winners keep celebrating (the view's animation frame counts on while the state stands still) with a pulsing ring in their colour. The arena's banner and `over_hint` now draw only in a duel (`show_banner`).
- **Practice HUD (fix 16).** `BOX_FILL` at 0.88; no ALIVE box with two fighters; under touch the boxes 1.4 times bigger and their type 1.5 times (never under 24 px), and the P1 / YOU / CPU labels over heads 1.5 times; the weapon's name beside its icon and ammo in the player box, cut to fit; an AUTO badge by your name. The weapon label under the feet is gone in every mode.
- **Edges (fix 30).** A pop-up word is kept whole 20 px inside the sides; the KO burst stays under `hud_top_inset` and, under touch, above the floor line.
- **Touch controls (fixes 5, 6, 17, 18), `touch_controls.gd`.** Dark fill `Color(0.05, 0.045, 0.07, 0.38)`, the accent at 0.6 when pressed, outlined labels (6 px, black at 0.7). A fixed Combo (Kick and Heavy at once); a separate Throw beside Kick, shown and hit only while armed, with the weapon and a gold ring; Kick always says "Kick", armed with the weapon over it and its verb as a 28 px caption. Up and Down are half-moons the width of the pad with 54 px arrows; the Down zone runs to the screen's bottom edge (128 px at 1080). Layout (from the bottom-right corner): Kick (210, 210) r128, Jump (520, 150) r116, Heavy (250, 440) r80, Dodge (490, 385) r80, Combo (70, 420) r64, Throw (352, 66) r56: the cluster's top is now 560 at 1080 (Throw's was 456), and Heavy and Dodge sit 15 to 30 px lower. `TouchControls.covered_rects` lists what the controls cover.
- **`main.gd` (fixes 9, 11, 12).** `_update_touch` shows the controls only while `_fighting()` (a fighter of ours, practice not over, a duel RUNNING and not over, a room PLAYING and not over), and `_refresh` calls it every tick, so they go the moment a match ends; watching, waiting, connecting and results have none. Auto play left the top bar for "Auto play: Off / On" chips (96 tall) in the match panel, shown while there is a fight to take over. "End match for everyone" swaps to "End the match?  No / Yes, end it" (Yes in the error colour), asked again each time the panel opens. The leave button reads "Leave room" in rooms. Quit is no longer in the in-match panel. The arena keeps its touch layout while the panel or the result covers the controls, so it doesn't jump; a watcher's view uses the whole screen.
- **`room_session.gd`.** `RESULT_TICKS` (15 s, as the relay's `ArenaLimits.OverFrames`), `over_tick`, `note_over()` and `result_left()` for the countdown.
- **Tests.** New `client/tests/test_hud.gd` (10): the strip fits every smoke window's logical size and two narrower strips, for 2 to 15 fighters in a free-for-all and 2 to 4 teams, as host, last fighter or watcher, inside the strip and the HUD band, nothing overlapping, chips at least `CHIP_MIN`, yours wider; a full four-team room shows all 15 chips on every window; the two collapsed layouts; the touch buttons never overlap and stay on screen; Combo fixed and Throw only while armed; the Up / Down half-moons (the Down zone at least 100 px, to the screen's edge, arrows 50 px or more); the floor line above the pad, Kick and Jump; the countdown; the ranking's tie order. `test_presentation.gd` checks each weapon's verb fits the caption's chord (1.55 radii). `scene_smoke.gd` / `main.gd`: after the practice run, the scene ends the practice match on a phone layout and plays a 15-fighter four-team big room with no server behind it (drawn with the strip for a few frames each): the result panel shows with the touch controls hidden in both, the controls are on while the room's match runs and off while watching, the panel sits under the HUD band at all five window sizes with practice's buttons on screen, and nothing logs an error; the whole-level case passes with the moved buttons.

**Differs from the spec.**

- **Chip sizes.** `CHIP_MIN` is 58 (spec 72), the total plate 84 wide (spec 96) and the gap 4: with 72 and 96 a four-team room of 15 collapsed at 1920 x 1080 (the spec's own sum gives 66 px a chip before gaps). The KO count sits over the right of the head on a narrow chip. Your chip is 1.5 times a chip but at least 150 px (`YOU_MIN`), so your Josh, KOs and weapon fit.
- **When the strip collapses.** The project stretches with "expand", so no window is narrower than 1920 logical px (a 4:3 window is 1920 x 1440, a 16:10 one 1920 x 1200): at today's sizes every room shows every chip, on the phone at 90 to 100 px a chip. The collapse rules are built and unit-tested on 1440 and 1728 px strips for a future HUD that is narrower (spec 2.4's "1440 x 1080" logical sizes don't occur either; the camera's smoke sizes were already logical).
- **The countdown** counts `RoomSession.RESULT_TICKS` from the first frame seen over, a copy of the relay's 15 s, instead of a return time from the server: no wire change.
- **"The arena keeps running behind it":** the relay stops sending frames once the match is over, so the state stands still; the view keeps the poses moving by counting its own draws.
- **The move pad** sits 10 px higher (`MOVE_FROM_CORNER.y` 230 to 240) so the Down zone is 128 px; `floor_clear_y` is 728 instead of 738 (46.3 to 45.6 px/m on the phone's big level).
- **The KO feed** is drawn by the arena, so the room result panel (1560 px wide) covers part of it on a 1920 screen.

**Checked.** At the end: `dotnet test Rangbaazi.sln` (Sim 629, Relay 114, Net 53, Social 33), the GDScript suite (101), `-- --smoke` and `-- --smoke --all-weapons`, `tools/godot.ps1 smoke`; against a local relay on port 8095, `relay_e2e.gd`, `room_e2e.gd` and `room_teams_e2e.gd` (winner check skipped), and against one with `ARENA_MATCH_SECONDS=40` on 8096 `room_teams_e2e.gd` with the winner check (both relays stopped afterwards). The 16 no-teams golden files are byte-identical to the copies taken before rooms v1 (no golden was rewritten in this batch). Before and after frames from a copy of the audit script at 1920 x 1080 and at 2400 x 1080 with touch (15-player room HUD in a free-for-all and in four teams, auto play, the match panel with the end-match question, a KO, both room results and a 4-player team result, watching; practice HUD, armed, paused, auto, result), plus 1440 x 1080, were looked at in the scratchpad.

**Left for later.** Motion (chips and rows easing in, the "+1" pop's ease), sounds on the result and a team win (batch 5). The tag declutter in piles (batch 5). Fixes 3, 4, 7, 10, 14, 15 and 19 to 29 (batch 4), including the duel's waiting panel and `_on_level_pressed`. On the phone the KO burst can land on the Heavy / Combo buttons' area (it is kept above the floor line only). Not tried on the Xiaomi or in the web export.

### Batch 4: UI/UX fixes, menus, lobby and back (2026-11-21)

**Built** (fixes 3, 4, 7, 10, 14, 15 and 19 to 29; no sim, wire or golden change).

- **Back (fixes 3, 4), `main.gd`.** Android's back (`NOTIFICATION_WM_GO_BACK_REQUEST`), Esc and a pad's Back all call `_on_back`. It first tries `_close_top_overlay`, which closes the first thing open in this order: the creator (back to the circle), the circle, the arena slider, Settings (`SettingsScreen.close`), How to play, quick chat, the match panel, the lobby's "Leave the room?", the menu's "Quit?". With nothing open:
  - a running practice opens its panel (pause), and a finished one goes to the menu;
  - a running duel opens the panel; a duel that is waiting or over goes to the menu;
  - a room that is still connecting is cancelled; the lobby asks "Leave the room?  Stay / Leave"; a room's match opens the panel;
  - the bare menu asks "Quit?  No / Yes".

  A once-a-frame guard drops a press delivered twice. The pickers no longer handle `ui_cancel` themselves.
- **Leaving a room always asks.** The lobby's Leave room button asks the same question in a card over the lobby. The match panel's "Leave room" swaps to "Leave the room?  Stay / Leave" (`UiTheme.confirm_row`, which is also used by "End the match?" and "Quit?").
- **Lobby (fixes 7, 14, 15), `room_screen.gd`.**
  - The fixed top bar: Leave room, the code at 64 px, and a primary Copy link (`copy_requested`). `main.gd` sends it through the existing copy path (`_on_copy`, `WEB_COPY_JS`, now also installed for rooms). `_sync_web_copy` follows whichever copy button is on screen; the label turns to "Copied!".
  - A one-line note on how friends join.
  - "Change arena" under the arena card (host).
  - Sliders 96 tall with their ends labelled (10 / 15, 0 / 14). The theme's grabber is 84 px (`UiTheme.GRABBER`).
  - Guests get "THE HOST'S MATCH": read-only chips ("12 min", "6 CPUs", "3 teams" / "Free-for-all", `UiTheme.info_chip`) and a 40 px "Waiting for the host to start" with moving dots.
- **The wait panel (fixes 10, 23), `main.gd` `_refresh_wait`.** While a duel connects, waits or starts, and while a room connects, the player's own arena (`_menu_rules`) stands behind a centred panel. It shows a title with moving dots, a line saying what is happening, the code at 110 px, Cancel, and (a duel waiting) Copy link. The status lines that remain (connection interrupted, a duel's end, watching) sit on a rounded dark plate (`ArenaView.status_plate`). The touch controls already showed only in a running match (batch 3).
- **Menu errors (fix 22), `menu_screen.gd`.** A banner card under the join row shows the message and the buttons from `MenuScreen.error_actions(code, room, web)`:
  - `full`, `no_room`, `taken`: "Host a room";
  - `old_client` (and the start's too-old `size` / `stage`): "Refresh" on the web (`location.reload()`) or "Update" in the app (`OS.shell_open` to the Play listing);
  - `closed`: "Rejoin <code>";
  - `connect` / `timeout`: "Try again";
  - every banner also has "OK".

  `_go_to_menu(message, code, room)` carries the code. The relay already sent `old_client` (batch 1).
- **Main menu (fixes 26, 28).** A "YOUR NAME" caption over the name box. The subtitle, the card subtitles and the footer are 30 px. Quit is as big as Settings (44 px), and a tap swaps it for "Quit?  No / Yes"; the two-tap "Tap again to quit" is gone. The disabled fill is opaque (`UiTheme.DISABLED_FILL`).
- **Practice panel (fixes 19, 26).** The rows are Resume; How to play | Settings (Settings opens over the panel, in every mode); Restart | Change fighter | Change arena; Player 2; CPU skill; "Guts: Off / On" (it restarts the match, as F4 does); Auto play; Leave match. The Player 2 row is hidden under touch with no pad connected (it stays while "A friend" is picked, so it can be undone). `_on_level_pressed` is deleted. F2 / F3 / F4 stay as hidden extras.
- **Settings (fix 20).** "SETTINGS" in the pickers' 44 px dim heading (`UiTheme.heading`, now used by every picker too). New "Touch buttons: Small / Medium / Large" and "Opacity: Low / Medium / High" rows: `GameSettings.touch_scale` (0.85 / 1 / 1.15) and `touch_alpha` (0.55 / 1 / 1.8), saved under `[touch]`. "Show hitboxes" and "Debug info" show only in debug builds (`SettingsScreen.developer_rows`), and in a release build their saved values are ignored. Done is primary.
- **Touch size and opacity, `touch_controls.gd`.** Every radius and every distance from the corners is multiplied by `scale_k()`; `radius_of(i)` and `move_radius()` give the scaled sizes. `floor_clear_y` and `covered_rects` follow the scale, so the camera's floor line moves with the buttons. Fills and rings are multiplied by the opacity, never past opaque. A `frozen` state (shown, dimmed, pressing nothing) is used under How to play.
- **Quick chat (fix 21).** A strip under the top-right buttons: "Quick chat", the 16 phrases in a row that slides (about six in view), and Close. The full-screen backdrop is gone. Your fighter keeps its keys and touch controls while the strip is open, and opening the panel closes the strip.
- **How to play (fix 27).** On touch the card moves up (`HelpCallouts.card_bottom`). The real controls stay under it, dimmed and asleep, and new `HelpCallouts` draws a labelled callout with a leader line beside each button, in the buttons' own words (Move, Up + Heavy, Down, Kick, Jump, Heavy, Dodge, Combo; Throw when armed). Callouts never overlap and follow Settings' size. On keys the list is 32 px at full colour and uses the same button names ("Esc pause").
- **Fighter circle (fix 24).** The minimum scale is 0.55 and the tap radius at least 60 px. The ring is 0.42 of the width each side, raised to 0.47 of the height. A new `FaceStrip` carousel above the buttons holds a chip per fighter (head and display name) and "Create": drag it to slide, tap a face to bring that fighter forward. It follows the front fighter. The hint now sits above the front fighter.
- **Creator (fix 25).** The MOVES strip draws each fighter throwing a jab on a loop, labelled "moves of <name>". The pose chips are 88 tall with 32 px type (`UiTheme.compact` padding, a wider column), and the cell names are 26 px. With no name typed, a creation is called after its head's fighter plus " Jr" (`CharacterCreator.NAME_SUFFIX`, in `ArtLibrary.fighter_name` too, so old unnamed saves no longer say "Custom"); the box's placeholder shows it. While the name box has the focus on a phone, only the preview and the box show, in the top 42 % (as the menu does).
- **Arena picker (fixes 28, 29).**
  - The cards are 720 x 405 and show the stage's platforms (dark slabs with a light top) and hazards (outlined red zones) to scale over the painting (`ArenaPicker.stage_rect`).
  - "n / 31" sits at the slider's end; the slider is 96 tall.
  - Opened for practice (from the menu, the result or the panel), it shows "CPU skill: Easy / Normal / Hard" chips (`skill_picked`).
- **No cycling control (fix 26).** None is left in `client/`. Every choice is a chip row or a slider. The only `% size()` step left on a setting is the F3 key, a hidden extra beside its chips.
- **Tests.**
  - New `client/tests/test_menus.gd` (9 tests): every menu error code has a next step; the relay's `old_client` code reaches the banner's Update; touch buttons at all three sizes on three screens never overlap, stay on screen, keep Up / Down and the Down zone, and bigger ones raise the floor line; opacity fades and never passes opaque; frozen controls press nothing; the help callouts name every button, stay on screen, never overlap, and leave the card at least 44 % of the height; unnamed creations use the head's display name (akmal gives "Simran Jr", artha gives "Chandan ..."); the picker's floor spans the card at the art's y 0; no control cycles (source checks).
  - `test_mix.gd` updated for the new default name. The existing `test_protocol.gd` `old_client` case stays.
  - `scene_smoke.gd` / `main.gd`, after the result checks:
    - back in a room's match opens the panel and back again closes it;
    - on the menu, back closes Settings, the circle, the arena slider and the creator (to the circle, then the circle), and on the bare menu only asks "Quit?" (a second press takes the question away);
    - in a host's lobby of 15, back asks "Leave the room?" without leaving, and a second press takes the question away;
    - the same lobby, laid out in four teams and as a free-for-all at all five smoke window sizes, keeps Leave room, Copy link and START on screen.

**Differs from the spec.**

- **Android share sheet (fix 7).** Godot 4.7 offers no share intent to GDScript, so Copy link copies on Android as it does elsewhere.
- **Back while a room connects** cancels the wait, because you aren't in a room yet. Back on a finished practice or duel goes to the menu, as the result's hint says ("Esc = menu"); a running practice's back opens the pause panel instead of leaving.
- **The leave question** is a card over the lobby rather than chips in the top bar (they didn't fit beside the code and Copy link), and it is asked by the Leave room button too, not only by back. The match panel's Leave room asks as well.
- **The lobby's top bar** holds the code and Copy link, so there is no separate "Room K7QPM" title. The "ARENA" caption over the card is dropped so the host's column fits 1080: the card is 620 x 250, and START is 116 tall.
- **Settings handles Esc** through `main.gd`'s chain (`close()`), not its own `ui_cancel` handler, so there is one chain. The same goes for the circle, creator and picker.
- **Quick chat** keeps all 16 phrases in a sliding row instead of a cut-down six. The server carries phrase numbers, and dropping ten would change what people can say.
- **Help callouts** omit Throw unless armed, since the button is hidden until then. The card's bottom is the higher of half the screen and the top callout minus 12 px; at Large it can overlap the card by a few pixels.
- **Touch size factors** are 0.85 / 1 / 1.15. At Large the floor line is 675 at 1080 (about 41.8 px/m on the phone's big level).

**Checked.** At the end:
- `dotnet test Rangbaazi.sln`: Sim 629, Relay 114, Net 53, Social 33.
- The GDScript suite: 110 passed.
- `-- --smoke` and `-- --smoke --all-weapons`, both PASS with the new back and lobby lines.
- `tools/godot.ps1 smoke`: PASS.
- Against a local relay on port 8097 (started, then stopped): `relay_e2e.gd`, `room_e2e.gd` and `room_teams_e2e.gd` all PASS, with the winner check skipped.
- The 16 no-teams golden files are byte-identical to the copies taken before rooms v1; no golden was rewritten.
- Frames from a copy of the audit script at 1920 x 1080 and at 2400 x 1080 with touch were looked at in the scratchpad: the menu, the quit question, the three error banners, the arena picker, the circle, the creator, Settings, How to play with callouts, the practice panel, the duel connecting / waiting / ended, a room connecting, the host's lobby of 4, of 15 in teams and of 15 as a free-for-all, the guest's lobby (free-for-all and teams), the leave question, quick chat in a room of 15, the room panel and its leave question.

**Left for later.** Motion, sounds, remembered choices, the tag declutter and the other polish items (batch 5). The status plate is drawn by the arena, so a long duel end message on a narrow screen wraps nowhere; it is clamped to the screen's width. Not tried on the Xiaomi or in the web export: the web copy path for the lobby's Copy link, Android's back button and the phone keyboard's compact modes need a device check.

### Batch 5: polish (2026-11-21)

**Built** (section 4, items 1 to 9; no sim, wire, version or golden change).

- **Motion (1).** `ArenaView.ease_in` is the one curve (eased out over 0.15 s, `EASE_FRAMES` 9). At a match's start the HUD strip's clock, plates and chips drop in outwards from the clock (`strip_drop`; all in place by frame 24, and a mid-match joiner sees them still). A team's plate bumps and its "+1" pops out bigger than it settles (`plate_bump`, `plus_one_pop`). A KO feed line slides in from the right; "READY?" and a duel's "P1 WINS!" ease in; the status plate fades in when its words change (moving dots don't count). `ResultScreen`: the panel fades in, then the rows (or team cards) slide in from the right one by one, the winner's first (`row_slide`, all in by about 1 s). `RoomScreen`: START pulses (a little bigger and brighter, one beat in 1.6 s) while it can be pressed; the status line, new free-for-all chips and new team cards fade in.
- **Sound (2), from the existing sounds.** Every button already clicked. New: your own team pick landing plays "pickup"; "Copied!" a higher "pickup" (desktop, Android and the web's in-tap copy). At the result `SoundDirector.result_sting` gives the winner's jingle (the existing cheer) to the winning side (in teams the whole winning team) and to anyone watching or two people at one keyboard, and the lose sting to the rest.
- **Spawn-in (3).** An 18-frame (0.3 s) flash ring in the fighter's colour (its team's in teams) for every fighter at the start and on every respawn, at least 64 px across however far out the camera is. It replaces the old 26-frame respawn ring.
- **Remembered choices (4).** `GameSettings.room_teams` (0, 2, 3, 4) and `room_minutes` (10 to 15), saved under `[room]` when the host taps a mode chip or slides the minutes (`remember_room`). A host's next room asks for those minutes in its address and, once the lobby says they are the host, sends the remembered `teams` (the server still raises it to what the room needs). Checked against a real relay: a host with 4 teams and 11 minutes remembered got a lobby of 4 teams and an 11-minute clock.
- **Copy (5).** The labels already said "Leave room", "Change arena", "Copy link", "Copied!" everywhere. The match panel's title ("PAUSED" / "MATCH MENU") and How to play's now use `UiTheme.heading` (44 px, dim); quick chat's one-row strip is titled "QUICK CHAT" in the heading's colour.
- **Performance (6).** New `client/game/frame_meter.gd`: while a room's match is on, `main.gd` prints a line every 5 s in every build, with nothing on screen, e.g. `[perf] room of 15 fighters, big level, 2400x1080 window (2400x1080 logical), Windows: 16.7 ms a frame on average (60.0 fps), slowest 17.8 ms, arena drawing 3.65 ms` ("arena drawing" is `ArenaView._draw`'s CPU time, `last_draw_usec`). On the phone: `adb logcat -c`, then `adb logcat -s godot:I | findstr /L "[perf]"`; on the web, the browser console.
  - Profiling the draw showed the 15 fighters' keyframe sampling (dictionary look-ups every frame) as half of it. `FighterPainter` now keeps each clip frame's plan (`_plan`: draw order, poses, transforms, textures, rects; the springs are still applied live) and finds its spring parts once, and the floor discs' outlines are one unit ellipse moved by a transform. Arena drawing went from about 5.3 to 3.7 ms on this desktop, the fighters from 2.7 to 1.7 ms.
  - Measured with 15 CPUs on big Rookie Rooftops, four teams, against a local relay: the Windows desktop at 1920 x 1080 and 2400 x 1080 (with touch) ran at 60.0 fps in every 5-s window after the first, with 3.7 to 4.8 ms of arena drawing. The web export (single-threaded, built as `docs/tech/web-client.md` says), in the desktop app's Chromium pane at 1600 x 900, ran at 59.7 to 60.0 fps after the first window (52.6 while loading), with 5.0 to 5.8 ms of drawing.
  - The phone is not measured: the APK is built (`build/android/rangbaazi.apk`, release, debug key) but not installed. If it falls short, the next cuts are the spec's: chip faces to initials, then the mirrored side art.
- **Tools (7).** `tools/shot_weapons.gd` gains `--teams[=N]` (fighters spread over the teams as a lobby fills them), `--big`, `--size=WxH`, `--touch`, `--lift` (fighter 1 held 19 m up for two seconds) and up to 15 fighters. A room is drawn with main.gd's insets and a 40-s clock, so the result is in the frames; the file names carry the size; it prints a rough frame time.
- **Docs (8).** `server/Rangbaazi.Relay/README.md`: the STATE line (34 bytes a fighter, then the props) and a table of the arena fields. `docs/tech/web-client.md`: rooms with teams and the big level, the camera rule, the HUD, the frame-rate line and how to read it, the review-frame command. This entry.
- **Tuning (9), from the review frames.** The frames: 15 on four teams on the big level (Rookie Rooftops at 1920 x 1080 and at 2400 x 1080 with touch, Water-Tank Terrace at 1440 x 1080), the real game at 2400 x 1080 with touch against the relay, and the result panels.
  - **Spawn order**: the 15 spread along the floor with the teams interleaved; nothing to change.
  - **The pull-back**: at 16:9 a fighter 19 m up is still on screen (23 m of height); at 20:9 with touch the view eases back and the head marker shows at the top. Kept at 0.85.
  - **Tag declutter**: a crowded tag now takes the nearest free spot among up to three levels up and one tag-width aside at each level (`ArenaView.place_tags`, `TAG_MOVES`), else the spot it overlaps least, with a leader line edged in black. Piles of 5 and 9 never overlap; a pile of 15 uses at least 12 spots.
  - **Chip widths**: unchanged (58 px at 1920), but other fighters' chips no longer show "0": their KO count appears once they have one. A "0" over every face was clutter.
  - **The team palette in real arenas**: ICE reads on the light skies (Rookie Rooftops, Water-Tank Terrace) because tags and plates are dark pills edged in the colour, and the floor discs keep their dark rim. No colour changed.
- **Tests.** New `client/tests/test_polish.gd` (6 tests):
  - the motion curves: they start at rest and settle, the strip's order and landing, the plate bump, the "+1" overshoot, the result rows in order and in by 1.5 s, START's beat;
  - tags in piles of 5, 9 and 15 and at the screen's edges;
  - the result sting for practice, watching, a draw and every fighter of a four-team room;
  - the remembered choices kept to the lobby's chips and slider, and the minutes in the room's address;
  - 15 spawn rings in team colours, and one on a respawn;
  - the frame meter's windows and arithmetic.

**Differs from the spec.**

- **There was no lose sting**, so it is made from the KO and landing sounds pitched down (`lose_sting`): still no new audio. A draw plays neither sting.
- **The team pick sound** plays when your own pick lands in the lobby (within 3 s of the tap), not on the tap, which already clicks. Being moved by the host or the server is silent.
- **The phone's frame rate** is not measured in this batch: the APK needs the user's approval on the phone to install. Desktop and web hold 60 fps.
- **`shot_weapons.gd`'s file names** now include the window size (`shot_1920x1080_frame_40.png`).
- **Quick chat's title** stays at 34 px, as the strip is one row; it takes the heading's colour and capitals.

**Checked.** At the end:
- `dotnet test Rangbaazi.sln`: Sim 629, Relay 114, Net 53, Social 33.
- The GDScript suite: 116 passed.
- `-- --smoke` and `-- --smoke --all-weapons`: PASS. `tools/godot.ps1 smoke`: PASS.
- Against a local relay on port 8099 (started, then stopped): `relay_e2e.gd`, `room_e2e.gd` and `room_teams_e2e.gd` PASS (the winner check skipped).
- No golden was rewritten; the 16 no-teams golden files are byte-identical to the copies taken before rooms v1.
- The web export and the Android APK (`--export-release Android ../build/android/rangbaazi.apk`, debug key) built.

**Left.**
- Measuring the 15-CPU big room on the Xiaomi (install the APK, read `adb logcat`).
- One long frame (190 to 300 ms) shows up in some 5-s windows on the desktop. It is probably a weapon's sprite or sound loading the first time it appears; preloading them is not done.
- In a pile of more than 12, some tags still overlap.
- Not tried on the phone: the motion and sounds, Android's back, the web's Copy link.

### Review fixes and As built (2026-11-21)

One review of batches 1 to 5 raised 19 points. 17 were real and are fixed. Of the other 2, one was a status summary and one (the phone's frame rate) needs the phone (below). No sim, wire, version or golden change: protocol 5 and `SIM_VERSION` 8 as before, and the 16 no-teams golden files are byte-identical to the copies taken before rooms v1.

**Fixed.**

- **Refusals reach the game (high).** Godot's WebSocketPeer goes from CONNECTING to CLOSED in one poll and drops the relay's `error` text, so every refusal read as "Couldn't reach the game server" with Rejoin.
  - `RoomSession` now reads the close too (`refusal_code`: the reason when it is a known code, else the status 4000 to 4005; a 4000 "bad room code" is no refusal). It ends with the relay's own code and words (`REFUSALS`). A socket seen CLOSING waits up to 2 s for its close code.
  - `OnlineSession.closed_message` does the same for duels (4000, 4001, 4002).
  - The relay's old-client close reason is now `old_client`, the code itself (an older relay's "old client" still maps). `busy` gets "Try again" on the menu banner.
  - Checked: the reviewer's probe now prints `old_client` and `full`. `room_teams_e2e.gd` has a new step: a 16th joiner, an unknown code and a client one version behind end with `full`, `no_room` and `old_client` and the relay's messages. `test_protocol.gd` covers the mapping.
- **Touch controls off the floor (high, and the Up-zone point).**
  - Every control drawn at rest now sits in the band under the floor line (`BAND_HEIGHT` 352 at Medium, the move pad's top). From the bottom-right corner: Kick (240, 170) r116, Jump (560, 118) r100, Heavy (420, 272) r74, Dodge (700, 272) r74, Combo (76, 292) r56, Throw (396, 64) r50. Before, they were Kick r128, Jump r116, Heavy and Dodge r80 (reaching y 560) and Combo r64.
  - The Up half-moon is drawn only while held. At rest, its arrow sits in the notch at the top of the pad. The Up zone's hit area above the pad is unchanged.
  - `floor_clear_y` is now the top of everything drawn (`drawn_circles`). It is still 728 at 1080 at Medium, so the camera and the phone's 45.6 px/m big level are unchanged.
  - How to play's callouts follow: they sit above the buttons, Kick's "Kick: attack" between Heavy and Combo, and Up's at the arrow.
  - `test_hud.gd` checks every button and the Up arrow are under the line at every size. A new check covers all 31 arenas, normal and big, at four screens and all three touch sizes: no standing spot on any platform (at waist height, 0.9 m) is inside a drawn control. The count is 0; the review counted 93 to 124 of 465 platforms before.
- **The KO feed covered the top right of the level (medium).**
  - Under touch, the feed moves into the band under the floor line, between the move pad and the buttons (`TouchControls.free_band`, at least 269 px wide at Large on 1920). It is right-aligned and fades in, and its type shrinks if a line is too wide.
  - Without touch, it stays at the top right under the buttons on a lighter plate (0.5). A line goes to 35 % while a fighter's body is behind it.
  - The Chat and menu buttons over the level's top-right corner are left as they are. Measured at 2400, 1920 x 1080 and 1920 x 1440: no standing spot on any of the 62 levels has a head or tag under them. Only a fighter launched or jumping there passes under them, which the spec already accepts for high launches.
- **The word over a tag (low).** GUTS!, STUN and a leaver's CPU now lead the text inside the pill at the pill's size ("CPU 42%", in gold or light blue), so they are placed with the tag.
- **A watcher on a phone (low).** New `ArenaView.touch_text` (`_prefer_touch` or a handheld device): tags and in-arena labels keep the touch sizes when the layout has no controls.
- **FFA draw (low).** Under "DRAW", a line says who drew: "CPU 5 and CPU 7 level on 3 KOs and 2 falls" (three or more as "A, B and C"; `ResultScreen.draw_line_for`).
- **Showing through (low).** The result panel's fill is 0.96, and the arena draws no name tags while the result shows (`hide_tags`). The match-menu button is hidden while the lobby is on screen.
- **The practice nickname flickered (high).** A practice match no longer advances once it is over; the view keeps the winner moving. The nickname is worked out once per finished match (`Nicknames.Kept`). Practice, rooms and duels use it; a duel's state goes on counting frames behind the result.
- **The painted sky (medium).**
  - `tools/arena_colours.gd` now takes `skyTop` from the fullest coarse colour bin (3 bits a channel) of the far layer's top 8 opaque rows, leaving out 15 % of the width at either side (the bookends). Before, it was the top row's mean.
  - Rerun on the 31 arenas; only `skyTop` changed in their `arena.json`. For example: Farmyard Frenzy #6aa479 to #51c2fa (sky blue), Blast Quarry #5b3755 to #0a1756 (navy), Neon Greenhouse #708bcf to #35308a, Laughing Ring #379561 to #1ec9fd.
  - Frames of all 31 big levels were looked at: the band above now continues the painting's top. World Duel Dome's band is the brown of the arch along its top edge. It continues the painting, though the sky inside the arch is blue.
  - `import_art.py`'s colour step needs absolute paths; run as a script, it has them.
- **Team plates (medium).** Past 9, the team symbol shrinks (radius 9) and the total's type fits the rest (34 px for one digit, about 30 for two, 24 for three), so it never runs into the symbol. Chip KO counts sit in a small dark badge at the chip's top-right corner (22 px).
- **Leader lines across your tag (low).** Tags draw in two passes: every leader line first, then the pills, yours last.
- **Practice keys hint (low).** The hint sits 8 px under the buttons' real bottom; the buttons move up by its height when it shows.
- **The strip touching Chat (low).** The room strip stops 12 px before Chat (`HUD_GAP`). 16 px collapsed a four-team room of 15 at 1920 px; `test_review_fixes.gd` checks that 12 keeps every chip.
- **Cut names (low).** A teammate's crowded tag keeps names of up to 8 letters whole, which covers every display name in the cast. Longer names are cut to 7 letters and "…" (`short_tag_name`).
- **P1 / P2 labels (low).** When two fighters are closer than a label's width, the labels are pushed apart, each to its fighter's side (`_label_x`).
- **shot_weapons.gd (low).** A room now shows main.gd's result panel (ResultScreen, layer 3) once it is over, so its "over" frame has the result.

**Rejected or left.**

- **The phone's frame rate** is still not measured. The rebuilt APK (`build/android/rangbaazi.apk`, release, debug key, 2026-11-21 16:22) needs the user's approval on the Xiaomi to install.
- **The status summary** (fixes 1 to 30) asked for nothing beyond the points above.
- **Chat and menu buttons over the corner:** see the feed point above. The fit's band stays 90 px: a 124 px band would shrink the phone's big level by 5 %, only for fighters in mid-air.

**Measured.** Arena drawing with 15 hard CPUs on four teams on big Rookie Rooftops, windowed with vsync (the reviewer's harness):

| Window | Arena drawing | Frame rate | Worst frame |
|---|---|---|---|
| 2400 x 1080, touch | 3.7 to 5.1 ms | 60.0 fps in every 5-s window (p95 16.8 ms) | 17.6 ms |
| 1920 x 1080 | 3.7 to 5.0 ms | 60.0 fps | 17.0 ms |

This is the same as before the fixes (4.3 to 5.1 ms in the review). The APK is 64.6 MB.

**Checked.**

- `dotnet test Rangbaazi.sln`: Sim 629, Relay 114, Net 53, Social 33.
- The GDScript suite: 125, with the new `test_review_fixes.gd`.
- `-- --smoke` and `-- --smoke --all-weapons`; `tools/godot.ps1 smoke`.
- Against a local relay on port 8101 (started, then stopped): `relay_e2e.gd`, `room_e2e.gd` and `room_teams_e2e.gd`, with the refusal step; the winner check was skipped.
- Frames from a copy of the reviewer's audit script at 2400 x 1080 with touch and at 1920 x 1080 were looked at: the 15-player team HUD, the leaver, a KO, watching, the host's lobby of 15, the room and practice results, the FFA draw, how to play, and practice armed.

**Not done.**

- The phone check: frame rate, motion and sounds, Android back, the compact keyboard modes.
- The web Copy link in a real browser.
- A pickup word (for example "BOXING GLOVES!") can still sit over a tag in a pile, and tags in piles of more than 12 can still overlap.

### Final round (2026-11-21)

Three checkers looked at the finished game and raised 23 points; the orchestrator added one (the sky over the big level). 20 are fixed, 1 is rejected, and 2 are fixed only in part (see "Still open"). No sim change: protocol 5 and `SIM_VERSION` 8 as before, no golden rewritten. The 16 no-teams golden files are byte-identical to the copies taken before rooms v1, and `all_weapons_teams_6p`, `arena_stages` and `room_big_teams_15p` are unchanged in this round.

**Fixed.**

- **CPUs no longer lock friends out (high), relay.** `ArenaRoom.IsFull` now counts people, not members.
  - Someone joining a lobby of 15 takes the last-joined CPU's place. In teams they are placed and the CPUs rebalanced as usual.
  - Mid-match a joiner of a full room watches. At the lobby they take a CPU's place.
  - The host's CPU count is "up to" (`BotsWanted`): `RefillBots` brings a CPU back when a person leaves the lobby, or when the match's leavers are dropped.
  - Only 15 people make a room `full`.
- **A host who leaves the tab keeps the room (high).**
  - Relay: when its last person leaves, a room stops any match, goes back to its lobby and is kept for `RelayOptions.EmptyRoomGrace` (3 min). The sweeper then closes it (`CloseEmptyRooms`). Joining or hosting that code while it waits makes you its host, with its CPUs, teams and minutes.
  - Web: `main.gd` sends the room's keepalive from a page timer every 15 s (`_install_web_keepalive`, `RoomSession.keepalive`), because a hidden tab gets no frames.
  - Menu: a host whose room really has gone gets "Your room K7QPM has closed. Host a new one to play on." with "Host a new room".
  - Checked against a local relay: a host with 6 CPUs dropped the connection, joined the code again as a guest, and was its host with the 6 CPUs.
- **Phone browsers get a soft keyboard (high).** The Web preset's `html/experimental_virtual_keyboard` is `true`; the rebuilt `build/web/index.html` carries `"experimentalVK":true`. Not tried on a phone yet.
- **The sky over the big level (high, and the orchestrator's polish).**
  - The painting's top fades into the sky over 1.5 m in six eased bands (smoothstep), so neither end of the fade shows a line.
  - Above it the sky deepens over 9 m from `skyTop` to `ArenaView.sky_deep` (80 % brightness, a little more saturated), so the band reads as sky, not a flat panel.
  - `tools/arena_colours.gd` gives the three arenas whose painting's top is a canopy or arches (Akhara Arena `siraj`, Archer's Ascent `suhani`, World Duel Dome `avipray`, `PAINTED_SKY`) the painted sky's blue: the fullest bin of sky-blue pixels in the far layer's top 40 %. Only those three `skyTop` values changed (#4dccfa, #4dd8f7, #48bafb); the tool gives the other 28 unchanged.
  - Overtime Office keeps its mauve wall: its "sky" is the windows.
  - No new art, and what is visible of the level is unchanged. The suggested uniformly scaled far layers were not used: they would change the painting the level is drawn on.
  - Contact sheets of all 31 big levels at 16:9 and 16:10 were looked at.
- **Side margin in the whole-level view (medium).** `fit_ppm` fits the level plus `EDGE_MARGIN` (1.8 m) at each end; the mirrored, dimmed out-zone art fills the margin.
  - 16:9 goes from 40 to 37.2 px/m. The phone with touch is height-limited and unchanged (45.6 px/m).
  - A fighter 1 m past either end of the big level is on screen whole at 16:9, 16:10, 4:3 and on the phone (new test). The margin was 1.5 m at first; that test showed it was 0.1 m short.
- **"Try again" for a host (medium).** `MenuScreen.error_actions(code, room, web, hosting)`. A host whose room the server never opened (`busy`, `connect`, `timeout`, or `closed` before any lobby) gets "Try again", which hosts a new room. A guest still rejoins.
- **How-to-play callouts (medium).**
  - Every box keeps 6 px clear of every control drawn on screen (`HelpCallouts.shown_circles`: the pad, the buttons, Throw while armed).
  - Each box takes the nearest free spot: along its own row up to a box's width either way, then a row higher, and so on.
  - Up's box sits above the pad's notch. Dodge's is placed first, so it sits right over Dodge.
  - Checked at every size on three screens, armed and not: no box meets a control, and the card keeps at least 44 % of the height.
- **KO feed between the buttons (low).** `ArenaView.fit_feed_line`: names are cut to 8 letters, then the type shrinks to 20 px at the smallest (it used to go to 16), then the names are cut further, to 3 letters at the shortest. Two 12-letter names fit the band at every size.
- **Duel winners at double speed (low).** `ArenaView.anim_frame`: while the result is up, the frame is the match frame or one more per draw since it ended, whichever is greater, never the sum. The camera's easing uses the same frame.
- **Practice result crop (low).** The camera eases on after the match ends (it used to stop with the state). Once the match is over the follow camera may look 1.5 m past a side (`RESULT_REACH`, into the out zone), so a winner at the edge is seen whole.
- **Touch buttons (low).**
  - Dodge and Heavy swapped: Dodge's centre is 420 px from the right edge (was 700).
  - Combo is r66 (was 56) at (76, 284), and Throw is r56 (was 50). Every gap is 16 px or more, and the floor line is unchanged (728 at Medium).
  - `TouchControls.label_fit` fits each label to its circle's chord, 20 px at the smallest. Combo and Throw no longer run past their rims.
- **Pill tags in every mode (low).** Practice and duels use the pills too ("YOU 6%", "CPU Aastha 36%"); the P1 / P2 two-line labels and `_label_x` are gone.
- **HUD strip headroom (low).** `CHIP_MIN` is 54 (was 58) and `YOU_MIN` 170 (was 150). A four-team room of 15 keeps every chip at 1920 px even 8 px narrower. Your chip has room for the Josh, the weapon and its ammo, and the KOs at the top right.
- **KO badges on faces (low).** On chips under 80 px (`NARROW_CHIP`), the KOs sit at the foot beside the Josh bar, and the head is 6 px shorter.
- **Settings shows through (low).** Its backdrop is opaque. Panels (`UiTheme.PANEL`) are 0.97 and the lobby backdrop is 0.96.
- **The room code (low).** `RoomScreen._fit_title` shrinks the code (64 to 40 px) to fit between Leave room and Copy link. The scene smoke's lobby now uses MWMWM and checks the code is never cut at five sizes.
- **A win on falls (low).** `ResultScreen.tiebreak_line`: "on fewer falls (11 v 12)" under the title when the top two (teams, or fighters in a free-for-all) are level on KOs.
- **A guest's mode chips (low).** Hidden for guests; THE HOST'S MATCH chips say it.
- **Team cards (low).** Each member has their face (32 px) beside their name, in 36 px rows; the cards are 240 tall.
- **Copy link in a local web test (low).** A localhost `?relay=` rides along (`main.gd share_link_for`).
- **Tests that asserted little (low).**
  - `test_review_fixes.gd` reads `MENU_MARGIN`, `MENU_BUTTON` and `HUD_GAP` from `main.gd` and checks the 8 px headroom. It also covers the host cases of `error_actions`.
  - `test_hud.gd`'s overlap test runs at every touch size with `radius_of`.
  - New `test_final_round.gd` (9 tests): callouts against controls; feed lines with long names; `anim_frame`; labels inside their buttons; Dodge's reach and the button sizes; the falls line; the share link; a fighter past the edge; the sky.
  - Relay: `ArenaTeamTests` +4 (a joiner takes a CPU's place and it comes back, in both modes; 15 people fill a room; a mid-match joiner of a full room; the empty room kept for its host, then closed). `ArenaTests.Friends_take_the_cpus_places_and_fifteen_people_refuse_the_sixteenth` replaces the old 15-with-CPUs refusal.
  - `room_teams_e2e.gd`: a 16th joiner of the 2 + 13 CPU room gets in (15 with 12 CPUs), and the CPU comes back when they leave. The refusal step keeps `no_room` and `old_client`.
- **The stale screenshot.** All screens were taken again from this build. In `phone_2400x1080_room_15p_teams_result` the tags are hidden behind the result.

**Rejected.**

- **The Anvil's damage.** Raising its damage or supply is a sim change. `weapon_table.json` and the no-teams traces carry the weapon table, and this round had to keep them byte-identical to the copies taken before rooms v1. It is 1 crate in 100 and nothing breaks. Left for a weapons balance pass that may rewrite those goldens.

**Still open.**

- **Not tried on a device:** the soft keyboard on the Xiaomi's Chrome; the keepalive in a hidden phone tab (Android may freeze the tab; the 3-minute room wait covers that); the phone's frame rate; Android back. The APK (`build/android/rangbaazi.apk`, release, debug key, 64.6 MB) and the web export are rebuilt but not installed or deployed.
- **The duel (1v1) has no wait for a host who drops.** Its relay pairs two people, and a dropped duel ends as before.
- **The practice result** keeps the follow camera's zoom. A winner at the very edge is now in view, but the camera does not ease out to the whole arena.

**Checked.**

- `dotnet test Rangbaazi.sln`: Sim 629, Relay 118, Net 53, Social 33, all passed.
- The GDScript suite: 134 passed, 0 failed.
- `-- --smoke`: PASS (frames 900). `-- --smoke --all-weapons`: PASS (frames 3090). `tools/godot.ps1 smoke`: SMOKE PASS.
- Against a local relay on port 8131: `relay_e2e.gd`, `room_e2e.gd` and `room_teams_e2e.gd` all PASS (the winner check skipped). Against one with `ARENA_MATCH_SECONDS=40` on 8132, `room_teams_e2e.gd` PASS, with the CPU-place step and the winner check. Both relays were stopped afterwards.
- Screens from a copy of the reviewer's audit at 2400 x 1080 with touch and at 1920 x 1080. Contact sheets of the 31 big levels at 16:9 and 16:10.

**Orchestrator's check (2026-11-21).** Reading the relay's room lifecycle after this round: a room kept for its host still counted against `MAX_ROOMS`, so creating rooms and dropping the connection could fill the server for 3 minutes at no cost (before, a room closed when its last person left, so filling it meant holding the connections open). `ArenaServer.Join` now closes the room that has been empty longest when a new host finds the server full (`CloseLongestEmptyRoom`), so kept rooms never keep a new host out. New test `ArenaTeamTests.A_full_server_closes_the_room_empty_longest_to_let_a_new_host_in`; the relay README says so. Rerun afterwards: `dotnet test Rangbaazi.sln` (Relay 119, Sim 629, Net 53, Social 33), the GDScript suite (134), both scene smokes, `tools/godot.ps1 smoke`, and `relay_e2e`, `room_e2e` and `room_teams_e2e` against fresh local relays, normal and with `ARENA_MATCH_SECONDS=40`: all pass.
