# Web and Android client (standard Godot, GDScript)

RANGBAAZI ships on the **web and Android**. Godot 4's C# support can't export to the web ("Projects written in C# using Godot 4 currently cannot be exported to the web", Godot 4.7 docs), so the game is rebuilt in **standard Godot 4.7.2 with GDScript** in `client/`. The C# projects (`src/`, `game/`, `tests/Rangbaazi.Sim.Tests` ...) stay for now as the **reference implementation**: the GDScript sim is checked against them bit for bit, and they'll be removed once the port is complete.

## Layout

```
client/                 standard Godot 4.7.2 project (GL Compatibility renderer: web + Android)
  sim/                  the deterministic sim, a line-for-line port of src/Rangbaazi.Sim (ints only)
  net/                  online play: NetProtocol (messages), RollbackCore (rollback), OnlineSession (relay + match lifecycle)
  game/                 scene: menu, practice vs CPU, online HUD, drawing, keyboard/gamepad/touch input
  tests/                headless tests + golden traces from the C# sim
  export_presets.cfg    "Web" preset (single-threaded: works in every browser, no special headers)
server/Rangbaazi.Relay  WebSocket server (C#, ASP.NET Core): duel rooms (pairs two players, forwards their messages) and
                        arena rooms of up to 15 whose match the server runs with the C# sim
tools/deploy-aws.ps1    builds both and hosts them on AWS (one EC2 t4g.nano; CloudFront + S3 once the account allows it)
```

## Determinism in GDScript

- The sim uses **ints only**: fixed-point Q47.16 as plain 64-bit ints (`Fx`), the same arithmetic as `Fix.cs` (truncating division, arithmetic shifts), SplitMix64 and FNV-1a in signed 64-bit ints with the same bits as C#'s `ulong`. No floats in `client/sim`, ever (`Fx.to_float` is for drawing).
- **Golden traces** prove it: `tests/Rangbaazi.Sim.Tests/GoldenTraceTests.cs` writes `client/tests/golden/*.json` (every frame's inputs and the checksum after it, for random inputs with 2 and 4 players and bot-vs-bot matches at every CPU level, ~12,600 frames). `client/tests/test_golden_traces.gd` replays them and must match every checksum and every bot decision. Rewrite them with `RANGBAAZI_WRITE_GOLDENS=1 dotnet test tests/Rangbaazi.Sim.Tests --filter GoldenTraceTests` only for an intended change, ported to both.
- Gameplay lives only in `client/sim`; scenes read `GameState` and draw it.

## Online

Browsers can't use UDP, so players connect through the **relay** over WebSockets (`wss://<site>/relay/room/<CODE>`). The game and the relay share one address (see Hosting), so the game finds the relay from its own page address. Players only ever connect to the relay, so they never learn each other's IP address, which fixes the privacy problem of direct P2P.

- **Relay** (`server/Rangbaazi.Relay`, README there): rooms of two; `welcome` / `start {seed, slot}` / `peer_left` / `full` / `busy` text messages; binary messages forwarded in order; limits on size (256 bytes), rate, idle time (players send a text keepalive every 20 s); can also serve the game's files (`WEB_ROOT`, with gzip copies of the big ones).
- **Netcode** (`client/net`): the WebSocket is reliable and ordered, so the rollback is simpler than UDP rollback (no acks, resends or redundancy):
  - each side sends one INPUT per frame (its buttons for frame + input delay, default 2), its first `input delay` frames empty;
  - the opponent's input is predicted (repeat the last one); a different real input rolls back to that frame (saved states ring) and re-simulates;
  - prediction limit 8 frames (rollbacks at most 8 deep);
  - time sync: each INPUT carries the sender's frame and measured advantage; the side that runs ahead skips ticks;
  - HELLO with protocol version and a fingerprint of the rules and sim version: a mismatch refuses the match;
  - every 30 frames each side sends the checksum of a frame whose inputs are final; a difference ends the match as a desync on both sides;
  - BYE on leave/rematch/desync; timeouts for a silent or frozen opponent.
- **Tests:** `client/tests/test_rollback.gd` runs two sessions through an in-memory relay (`fake_relay.gd`) with latency and jitter, bots on both sides (in step, replay equal, desync caught, rules refused, leave, rematch, dead link, full room, different input delays). `client/tests/relay_e2e.gd` does the same over real WebSockets against a running relay.

## Rooms of up to 15 (the server runs the match)

The menu's **Host a room** and **Join** use arena rooms (`server/Rangbaazi.Relay/README.md`, "Arena rooms"): the server runs the match with the C# reference sim, and the client (`client/net/room_session.gd`) only sends its buttons and draws the state the server sends every frame, through a 3-frame jitter buffer. Rollback can't scale to 15 players on a phone; this does, and nobody can cheat by changing their own copy of the match. The price is input delay of about one round trip to the server.

- The lobby (`client/game/room_screen.gd`): the host picks 10 to 15 minutes (default 10) and adds CPUs, then starts. `?arena=CODE` in the page's address joins a room.
- The match: Rookie Rooftops, a timed free-for-all with respawns; ring-outs score a KO for the last attacker (5 s window); the most KOs wins, then fewest falls. Comebacks: **rage** (your hits do up to 50% more damage and knockback as your Josh rises to 150%) and a **hidden second wind** (after a respawn the first 20 damage is absorbed). Quick chat: 16 fixed phrases; the server carries only their numbers.
- The winner gets a nickname (`client/game/nicknames.gd`), the same on every screen.
- Settings (`client/game/game_settings.gd`): frame rate 60 / 30, hit shake, hitboxes, debug line, and the player's name, saved in `user://settings.cfg`.
- Test: `client/tests/room_e2e.gd` (host, guest, CPUs, movement, chat, end) against a running server; the server's own tests are `tests/Rangbaazi.Relay.Tests/ArenaTests.cs`.

## Art: fighters and arenas

The art comes from the art pack (`round-brawler-v6`, made with ChatGPT and Gemini; see `docs/design/animation-brief-for-chatgpt.md`):

- **Stages:** `python tools/gen_arena_stages.py <pack>` turns the 31 arena layouts into `src/Rangbaazi.Sim/ArenaStages.cs` and `client/sim/arena_stages.gd` (the same exact numbers; `client/tests/golden/arena_stages.json` checks it). Shan's arena keeps the id `rooftops`. Rooms and practice can play any of them.
- **Art:** `python tools/import_art.py <pack>` copies the fighters' rigs and moves (compact JSON), the shared moves and the arenas' painted layers and platform pieces into `client/assets/` (about 18 MB). Images are `.wbin` files (WebP bytes, renamed so Godot doesn't re-import and enlarge them); `client/game/art/art_library.gd` decodes them. The exports include `*.wbin` (`include_filter`).
- **Drawing:** `client/game/art/fighter_painter.gd` picks each fighter's move from its state and samples the keyframes at the fighter's own action frame (so hit-stop and rollback redraw exactly), swaps faces and drawn extras by slot, and adds springs for hats and antennae. `arena_view.gd` draws the arena's sky, layers and platform art under the sim's platforms.
- **Screens:** the character circle (`character_circle.gd`: drag to spin, tap to bring a fighter forward), the arena slider (`arena_picker.gd`: swipe the cards or drag the bar), and the lobby (`room_screen.gd`: live previews, sliders for the match length and CPUs). Fighters are cosmetic: every one has the same moves and numbers.

## Hosting (AWS, ap-south-1)

`tools/deploy-aws.ps1` builds the web game and the relay and hosts them; re-running it updates everything in place. Godot 4 web builds need **https**: on a plain http page the browser hides features they use (a "secure context", AudioWorklet), so the game won't start.

- **Now: `-Direct`.** This AWS account can't create CloudFront distributions until AWS Support verifies it, so one EC2 t4g.nano does everything: Caddy on ports 80/443 gets a free Let's Encrypt certificate for `<elastic-ip-with-dashes>.sslip.io` (a free DNS name that resolves to the IP) and passes everything to the relay on port 8080, which serves the game files and the relay. Link: **https://13-203-5-192.sslip.io/**. Depends on sslip.io (DNS) and Let's Encrypt staying available; a real domain would remove the first.
- **Later: default mode (CloudFront).** Once the account is verified for CloudFront: the game in a private S3 bucket behind CloudFront (`https://<id>.cloudfront.net/`), `/relay/*` sent to the relay on port 80 with a secret header the relay checks, the security group open to CloudFront only, and Caddy switched off.
- **Resources** (tagged `Project=rangbaazi`): EC2 `rangbaazi-relay` with an Elastic IP, security group `rangbaazi-relay`, IAM role/profile `rangbaazi-relay-role`/`-profile`, buckets `rangbaazi-artifacts-<account>-<region>` (relay binary, game files) and `rangbaazi-web-<account>-<region>` (CloudFront mode), SSM parameter `/rangbaazi/relay-origin-secret`. About 7-8 USD a month (instance, public IPv4, disk).
- **On the server:** systemd services `rangbaazi-relay` and `caddy`; `/opt/rangbaazi/update.sh` fetches the latest relay binary and game files. The deploy script configures it through SSM (no SSH).
- Sign in first: `aws login --profile Admin --region ap-south-1`, then run the script with `$env:AWS_PROFILE='Admin'`.

## Commands

```powershell
$godot = "$env:LOCALAPPDATA\Programs\Godot-4.7.2-standard\Godot_v4.7.2-stable_win64_console.exe"
& $godot --headless --path client --import                       # once, and after adding class_name scripts
& $godot --headless --path client -s res://tests/run_tests.gd    # all GDScript tests (exit 0 = pass)
& $godot --headless --path client -- --smoke                     # scene smoke test (practice, bots)
dotnet run --project server/Rangbaazi.Relay                       # relay on :8080
& $godot --headless --path client -s res://tests/relay_e2e.gd -- --relay ws://127.0.0.1:8080
& $godot --headless --path client -s res://tests/room_e2e.gd -- --relay ws://127.0.0.1:8080
& $godot --headless --path client --export-release Web ../build/web/index.html
$env:AWS_PROFILE='Admin'; ./tools/deploy-aws.ps1 -Direct            # host on AWS, prints the link
```

Local web test: serve `build/web` (e.g. `python -m http.server -d build/web 8060`) and open `http://127.0.0.1:8060/?relay=ws://127.0.0.1:8080&room=TEST` in two tabs. The `relay` parameter only accepts localhost.

## Android

The same client, exported with the **Android** preset in `client/export_presets.cfg`: package `com.artificialinteractive.rangbazi`, landscape, arm64-v8a, an APK built without Gradle. Online play uses the relay address in `client/net/relay_base_url.txt` (the deploy script writes it; `wss://13-203-5-192.sslip.io` today), and the copy button shares a link to the web game on that host, so a friend without the app can join from a browser.

Needs, on this PC: the Android SDK (`%LOCALAPPDATA%\Android\Sdk`), a JDK 17+ and Godot 4.7.2's Android export templates; Godot's editor settings point at the SDK, the JDK and its debug keystore.

```powershell
$g = "$env:LOCALAPPDATA\Programs\Godot-4.7.2-standard\Godot_v4.7.2-stable_win64_console.exe"
# A test build: release (optimized), signed with Godot's debug key. Not for the Play Store.
$env:GODOT_ANDROID_KEYSTORE_RELEASE_PATH = "$env:APPDATA\Godot\keystores\debug.keystore"
$env:GODOT_ANDROID_KEYSTORE_RELEASE_USER = 'androiddebugkey'
$env:GODOT_ANDROID_KEYSTORE_RELEASE_PASSWORD = 'android'
& $g --headless --path client --export-release Android ../build/android/rangbaazi.apk
adb install -r build/android/rangbaazi.apk   # Xiaomi phones: approve the prompt on the phone
```

### Play Store (internal testing)

The **Android Play** preset builds an AAB with Gradle, which needs Godot's Android build template in `client/android/` (gitignored, 200 MB). In a fresh clone, unpack it first:

```powershell
Expand-Archive "$env:APPDATA\Godot\export_templates\4.7.2.stable\android_source.zip" client\android\build
Set-Content client\android\.build_version '4.7.2.stable' -NoNewline; New-Item client\android\build\.gdignore
```

The bundle is signed with the **upload key** in `%USERPROFILE%\.rangbaazi-keys\` (alias `upload`; the password is in `upload-key.txt` there). It never goes in the repo. Back that folder up: Play App Signing holds the real signing key, so a lost upload key can be reset in Play Console, but that takes days.

```powershell
$pw = ((Get-Content "$env:USERPROFILE\.rangbaazi-keys\upload-key.txt") | Where-Object { $_ -like 'password: *' }).Substring(10)
$env:GODOT_ANDROID_KEYSTORE_RELEASE_PATH = "$env:USERPROFILE\.rangbaazi-keys\rangbaazi-upload.jks"
$env:GODOT_ANDROID_KEYSTORE_RELEASE_USER = 'upload'
$env:GODOT_ANDROID_KEYSTORE_RELEASE_PASSWORD = $pw
& $g --headless --path client --export-release "Android Play" ../build/android/rangbaazi.aab
```

Raise `version/code` in the **Android Play** preset before every upload; Play refuses a code it has seen. Upload the AAB under Testing → Internal testing in Play Console. The icons come from `tools/make_icons.gd` (`client/icon.png` is also the 512 px Play Store icon).

## Next

- Port what's left of the C# side (KoChart, spec tests) or drop it, then delete the C# projects.
- Chat: the C# `Rangbaazi.Social` module isn't ported; the web build has no chat yet.
