# RANGBAAZI — notes for Claude

2D platform brawler for the **web and Android**. The shipping game is `client/`: standard Godot 4.7.2 with GDScript (Godot can't export C# to the web); see `docs/tech/web-client.md`. The older Godot .NET / C# projects (`src/`, `game/`, `tests/`) are kept as the reference implementation the GDScript sim is checked against, until the port is complete. Don't build PC exports. The GDD is `RANGBAAZI — Game Design Document.pdf` at the repo root; it is deliberately not committed (gitignored), so it may be missing in a fresh clone. Addenda are in `docs/design/`. Architecture and determinism rules are in `docs/tech/architecture.md`; read it before touching `src/Rangbaazi.Sim`.

## Commands

- GDScript tests (standard Godot, not .NET): `"$env:LOCALAPPDATA\Programs\Godot-4.7.2-standard\Godot_v4.7.2-stable_win64_console.exe" --headless --path client -s res://tests/run_tests.gd` (exit 0 = pass). Run `--headless --path client --import` first after adding `class_name` scripts.
- Android APK (test build): see `docs/tech/web-client.md`, Android: `--export-release Android ../build/android/rangbaazi.apk` with the debug keystore in `GODOT_ANDROID_KEYSTORE_RELEASE_*`. The phone (Xiaomi) needs the install approved on screen.
- Relay: `dotnet run --project server/Rangbaazi.Relay`; end-to-end netcode check against it: `... --headless --path client -s res://tests/relay_e2e.gd -- --relay ws://127.0.0.1:8080`, and for rooms of up to 15 (the server runs the match with the C# sim): `... -s res://tests/room_e2e.gd -- --relay ws://127.0.0.1:8080`.
- Hosting: `tools/deploy-aws.ps1 -Direct` (AWS CLI signed in with `aws login --profile Admin`, then `$env:AWS_PROFILE='Admin'`; region ap-south-1). It creates or changes paid resources: ask before running it. Live link: https://13-203-5-192.sslip.io/ (see docs/tech/web-client.md, Hosting).
- Art pack into the game: `python tools/gen_arena_stages.py <pack>` (the 31 arena stages, C# and GDScript; then rewrite the goldens) and `python tools/import_art.py <pack> [<delta pack> ...]` (fighters, arenas and effects into `client/assets`; a delta pack's files win). The packs are `C:\Users\user\Documents\ChatGPT\Rangbaazi\game-art\round-brawler-v6` plus the delta `round-brawler-v7`.
- Look at a match without playing it: `... --path client -s ../tools/shot_weapons.gd -- <output folder> [<stage id>] [<fighters>]` (not headless) saves full-size frames of a CPU brawl: the start banner, a KO, the result.
- Weapon sprites into the game: `... --headless --path client -s ../tools/cut_weapons.gd -- <game-art folder>` (writes `client/assets/weapons`); weapon sounds come with `python tools/import_audio.py <game-art folder>`. Weapons are specced in `docs/design/combat-spec-v0.3-weapons.md`.
- Run all tests: `dotnet test Rangbaazi.sln`
- Build everything, including the Godot project, without the editor: `dotnet build Rangbaazi.sln`
- In-engine smoke test (builds first; exit code 0 = pass): `powershell -ExecutionPolicy Bypass -File tools/godot.ps1 smoke`. Run it after changing `game/` or the sim.
- Online smoke test (two headless games over 127.0.0.1, ~20 s): `powershell -ExecutionPolicy Bypass -File tools/godot.ps1 online-smoke`. Run it after changing `src/Rangbaazi.Net` or the online parts of `game/`.
- Launch Godot through `tools/godot.ps1` (`editor`, `run`, `record -Out "$env:TEMP\smoke.avi"`), never the winget `godot` alias, which crashes with ".NET: Assemblies not found". Keep recordings out of the repo.
- Keep `TargetFramework` in `game/Rangbaazi.csproj` a literal, equal to `RangbaaziTargetFramework` in `Directory.Build.props`. The Godot editor rewrites property references.

## Hard rules

- **client/ (GDScript):** gameplay lives only in `client/sim`, ints only (fixed point via `Fx`, never floats), a line-for-line port of `src/Rangbaazi.Sim`. Any sim change goes into both and the golden traces are rewritten (`RANGBAAZI_WRITE_GOLDENS=1 dotnet test tests/Rangbaazi.Sim.Tests --filter GoldenTraceTests`); `client/tests/test_golden_traces.gd` must still pass. New `Fighter`/`GameState` fields go in `copy_from()` and the checksum in the reference's order. Scenes in `client/game` only read state. Bump `Simulation.SIM_VERSION` / `NetProtocol.VERSION` when behaviour or messages change.

- **Gameplay logic lives only in `src/Rangbaazi.Sim`.** Godot scripts in `game/` read `GameState` and draw it; they never change gameplay state.
- **The sim is deterministic.** No float/double/decimal (use `Fix`), no System.Math/Random/HashCode/DateTime/Stopwatch/Task. The build and the `DeterminismGuardTests` enforce this; don't suppress them.
- All mutable match state lives in `GameState`. New `Fighter` fields must be added to `Fighter.AddTo`; new `GameState` fields to `Checksum()` and `CopyFrom()`. Keep `Fighter` a plain value type (no reference fields) so copying it copies everything.
- Floats appear only in `game/` (`FixExtensions.ToFloat`) for rendering.
- **Netcode** lives in `src/Rangbaazi.Net` and only feeds the sim inputs Backdash has agreed with the other peer. Backdash is pinned to 0.7.8-preview; read `docs/tech/netcode.md` before upgrading it or changing the wire format (bump `NetProtocol.Version`).
- **Chat and voice** (`src/Rangbaazi.Social`) never touch the sim or the rollback P2P socket. UI goes through `IChatService` / `VoiceController`. Keep the age-band safety defaults in `SocialPolicy`; any change needs a design note in `docs/design/B10-chat-and-voice.md`.
- Content must be original: no real film titles, dialogue, songs, actor likenesses, or deities as playable fighters (GDD B8).
- Every sim change comes with a test, including a determinism or rollback test when it adds state.
