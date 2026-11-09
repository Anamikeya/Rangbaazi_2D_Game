# RANGBAAZI

*Working title.* A 2D platform brawler set in a loud, masala-cinema world: up to 8 fighters, grab-anything prop chaos, and Taali-powered "Signature Scene" supers. Local play, online rollback, team modes and (later) a 2D battle royale.

- Game design: `RANGBAAZI — Game Design Document.pdf` (not in git; get it from the team and put it at the repo root, where `.gitignore` keeps it out of commits), the combat spec [`docs/design/combat-spec-v0.1.md`](docs/design/combat-spec-v0.1.md) and the chat/voice addendum [`docs/design/B10-chat-and-voice.md`](docs/design/B10-chat-and-voice.md)
- Tech: [`docs/tech/architecture.md`](docs/tech/architecture.md), netcode: [`docs/tech/netcode.md`](docs/tech/netcode.md)

## Web and Android version (`client/`)

The game ships on the **web and Android**, so it is being rebuilt in standard Godot 4.7.2 with GDScript in [`client/`](client/) (Godot can't export C# projects to the web). The C# projects below stay as the reference the GDScript sim is checked against, bit for bit. Details: [`docs/tech/web-client.md`](docs/tech/web-client.md).

- **Play in a browser:** **https://13-203-5-192.sslip.io/** (hosted on AWS by `tools/deploy-aws.ps1`). Menu: *Practice vs CPU*, *Create online room* (share the link it shows), or *Join room* with a code.
- **Controls:** keyboard and gamepad as below (P1 keys), or the on-screen buttons on phones and tablets.
- **Online:** through a small relay server (`server/Rangbaazi.Relay`), so it works from any network and players never see each other's IP address.
- **Tests:** `Godot_v4.7.2-stable_win64_console.exe --headless --path client -s res://tests/run_tests.gd` (standard Godot, not .NET; install it from godotengine.org or the GitHub release).
- **Host it:** `./tools/deploy-aws.ps1 -Direct` (after `aws login`) builds the web game and the relay and puts them on one small AWS server in Mumbai with https (about 7-8 USD a month), then prints the link. Without `-Direct` it uses CloudFront instead, once the AWS account is allowed to. See [`docs/tech/web-client.md`](docs/tech/web-client.md#hosting-aws-ap-south-1).

The rest of this README describes the original C# / Godot .NET graybox.

## Prerequisites

| Tool | Version | Install |
|---|---|---|
| Godot Engine **.NET** edition | 4.7.2 | `winget install --id GodotEngine.GodotEngine.Mono --exact --version 4.7.2` or godotengine.org |
| .NET SDK | 8.0 or newer (repo builds with 9.0) | `winget install Microsoft.DotNet.SDK.10` |
| Git + Git LFS | any recent | `git lfs install` once per machine |

**Don't launch Godot with the `godot` command that winget adds.** It is a symlink, so Godot looks for its .NET assemblies next to the link, fails with `.NET: Assemblies not found` and crashes. Use `tools/godot.ps1` (below), which finds the real executable, or set `GODOT` to the full path of `Godot_v4.7.2-stable_mono_win64_console.exe`.

## Getting started

```bash
git lfs install
dotnet test Rangbaazi.sln
```

Then open the editor and press **F5** to play the graybox scene:

```powershell
./tools/godot.ps1            # open the editor on game/
./tools/godot.ps1 run        # build and run the game, logs in this terminal
```

If your PowerShell blocks local scripts, use `powershell -ExecutionPolicy Bypass -File tools/godot.ps1`.

The C# solution is `Rangbaazi.sln` at the repo root; Godot is pointed at it via `dotnet/project/solution_directory`. The game project also builds without the editor:

```bash
dotnet build Rangbaazi.sln
```

## Smoke test (game in the engine)

`dotnet test` covers the sim and social code. The smoke test also covers the Godot side: two sparring bots (`Rangbaazi.Sim.Diagnostics.SparringBot`) play a 900-frame match through synthetic key presses on the real P1/P2 bindings.

```powershell
./tools/godot.ps1 smoke      # headless, ~1 s; exit code 0 = pass
```

Every button the bots can press, Heavy and Dodge included, goes through Godot input. It passes when every frame's buttons read back exactly as the bots pressed them, the final checksum equals a pure-sim replay of the same script, and the quick-chat key reached the chat hub. It prints one line: `SMOKE PASS frames=900 checksum=<16 hex digits> stocksLost=<n> chatMessages=1`, or `SMOKE FAIL` and the reason. To watch it, record a video (Movie Maker, 60 fps): `./tools/godot.ps1 record -Out "$env:TEMP\smoke.avi"`. Recording opens a real window, so leave the keyboard and gamepads alone until it finishes; they would count as input mismatches.

## Online 1v1

Two players, rollback netcode ([Backdash](https://github.com/Delta3-Studio/Backdash)) over UDP. Each side plays with P1's keys or the first gamepad, whichever side it is.

**Try it on one PC** (you are P1; a bot plays P2 in a second window):

```powershell
./tools/godot.ps1 online-local            # -Bot easy|normal|hard sets P2's level
```

**Two PCs** (same LAN, or reachable through a VPN such as Tailscale or forwarded UDP ports). Both PCs need this repo, Godot 4.7.2 .NET and the .NET SDK, as above. Pick who is P1; each player gives the *other* PC's address:

```powershell
# on PC A (192.168.1.10), as P1
./tools/godot.ps1 online -Peer 192.168.1.20:7000 -Side 1
# on PC B (192.168.1.20), as P2
./tools/godot.ps1 online -Peer 192.168.1.10:7000 -Side 2
```

- The waiting screen shows your own address to give to the other player (`ipconfig` shows it too).
- Windows Firewall asks the first time: allow Godot on **private** networks. If nothing connects, check that the network is set to Private, or allow UDP port 7000 in.
- Options: `-Port` (yours, default 7000), `-Delay 0-7` (input delay in frames, default 2; 1 is fine on a LAN), `-Bot easy|normal|hard` (a bot plays your side).
- The two games must be the same build with the same rules, and one player must be P1 and the other P2; otherwise both screens say why and the match doesn't start.
- **F5** asks for a rematch (the other player presses F5 too). Closing the window tells the other player you left.
- The bottom line shows ping, input delay, rollbacks (last, max, median), and how many checksums have matched the other game. A desync, if one ever happens, ends the match on both screens with the frame it was found at.

Without the script: `Godot_..._console.exe --path game -- --online --port 7000 --peer 192.168.1.20:7000 --side 1 [--delay 2] [--bot normal]`.

Automated check, two headless games with bots over 127.0.0.1:

```powershell
./tools/godot.ps1 online-smoke            # ~20 s; exit code 0 = pass
```

It passes when both games play 900 frames, at least one checksum has been compared and matched, an offline replay of the confirmed inputs gives the same checksum, and both games print the same checksum: `ONLINE SMOKE PASS side=P1 frame=900 checksum=...`.

## Graybox controls

| | P1 (keyboard) | P2 (keyboard) | Gamepad N → player N+1 |
|---|---|---|---|
| Move | A / D | ← / → | Left stick / D-pad |
| Jump (x2) | Space | Numpad 0 | A |
| Light attack (jab) | J | Numpad 1 | X |
| Heavy (+ direction) | K | Numpad 2 | Y |
| Dodge (+ direction) | L | Numpad 3 | RB |
| Aim heavies and dodges | W / S (up / down), A / D | ↑ / ↓, ← / → | Left stick / D-pad |

**Phones and tablets (touch):** in the browser, starting a match goes fullscreen and turns the screen to landscape (Android Chrome; on an iPhone the game asks you to turn the phone). The pad under the left thumb moves left and right. Slide the thumb up off it, or tap the ▲ above it, to hold up (Up + Heavy is the recovery); the ▼ below works the same for down. Under the right thumb are Kick (the light attack) and Jump, with Heavy and Dodge above them.

**Screen:** the bar across the top has a box per player (name, damage %, stocks, KOs, Guts) and how many players are still in; the boxes get narrower as players are added. The ☰ button in the top-right corner opens the menu (Resume, Restart, CPU level, Rematch, Leave); practice pauses while it is open. "Play again" appears once a match is over.

**Combat** (full details, frame data and KO percents: [`docs/design/combat-spec-v0.1.md`](docs/design/combat-spec-v0.1.md)). One action per frame: a jump pressed together with an attack or dodge is ignored.

- **Heavies:** Heavy alone is the neutral heavy. With ← / → it is the side heavy, a lunge that's the main ring-out move. With ↑ it is the up heavy, a rising strike that also works as a recovery once per trip into the air. With ↓ it is a low sweep on both sides, or the ground slam in the air.
- **Dodge:** 0.3 s of invincibility. On the ground, Dodge alone is a spot dodge and with ← / → it dashes. In the air it dashes in any direction, once per trip into the air. After a dodge you wait 1 s before the next.
- **Parry:** dodge just as a hit arrives (the dodge's first 6 frames). You take nothing and get your air dodge back. The attacker is stunned for 0.6 s and stops rising, and you can act at once, which is long enough to land a heavy.
- **Hit-stop:** both fighters freeze for 4-10 frames on a hit, longer for heavier hits.
- **Guts:** once per stock, if a hit leaves you at 150% or more and you keep that stock for 3 s, you get 30% back and 1 s of armor. Armor means hits still add Josh but don't knock you back. A gold bar under your feet fills during the 3 s, and armor shows as a gold outline.

On screen:

- Hitboxes are red.
- A dodging fighter is faded, with a white outline while a hit would be parried.
- A parried fighter is tagged STUN.
- A fighter frozen by a hit it took shakes, including a hit soaked by Guts armor.

**Playing alone:** P2 starts as a CPU sparring bot (labelled "P2 CPU"). While it is on, P2's keys and the second gamepad do nothing.

| Key | Does |
|---|---|
| F2 | CPU players on/off. Off: P2 is a second person again. |
| F3 | CPU level: Easy → Normal → Hard. Once you're in reach, Easy waits about 0.25 s on average before attacking (often less), Normal about 0.1 s, Hard almost at once. The level also sets which attacks the CPU can react to and how often it dodges them, timed to parry. Easy and Normal react only to the slower heavies, not the up heavy or the jab, and dodge 1 in 4 and 1 in 3 of those. Hard reacts to everything, the jab included, and dodges 1 attack in 2. Moving and recovering are equally sharp at every level. |
| F4 | Guts rule on/off (restarts the match), to compare the two in playtests |
| F5 | Restart the match (CPU and Guts settings stay) |
| T | Send a quick-chat line through the local chat hub |

The CPU (`Rangbaazi.Sim.Diagnostics.SparringBot`) is a simple graybox bot, not the real AI. It:

- walks at you and jabs when in reach;
- follows you onto and off platforms;
- dodges and parries some attacks;
- punishes with a heavy when it parries you or you whiff a heavy;
- mixes in heavies once you're at 70% or more;
- recovers with its air jump, then the up heavy, then an air dodge.

The level changes only how fast it attacks and how it dodges.

## Repo layout

```
src/Rangbaazi.Sim/       deterministic simulation core (fixed-point, no floats, no Godot)
client/                  the web and Android game: standard Godot 4.7.2, GDScript (sim, netcode, scene, tests)
server/Rangbaazi.Relay/  WebSocket relay for online play from browsers and phones
src/Rangbaazi.Net/       online 1v1 for the C# build: rollback netcode (Backdash), handshake, desync check, UDP
src/Rangbaazi.Social/    text chat + voice chat logic, safety policy, provider interfaces
tests/                   xUnit tests for all three (run headless, no Godot needed)
game/                    Godot 4.7.2 .NET project (presentation only)
tools/                   dev scripts (godot.ps1: editor, run, smoke test, record, online play)
docs/design/             GDD addenda
docs/tech/               architecture and engineering notes
```

## Conventions

- Gameplay rules go in `src/Rangbaazi.Sim`, never in Godot scripts. Read the determinism rules in [`docs/tech/architecture.md`](docs/tech/architecture.md) first.
- Binary assets (art, audio, models, fonts, PDFs) go through Git LFS automatically via `.gitattributes`. Git LFS is currently disabled for the GitHub repo, so pushing a commit that adds such files fails until LFS is enabled there.
- All characters, lines, songs and art must be original (GDD B8 cultural guardrails).
- Third-party code and its licence notices are listed in [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt); ship it with every build.
