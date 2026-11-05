# RANGBAAZI — notes for Claude

2D platform brawler (Godot 4.7.2 .NET, C#). The GDD is `RANGBAAZI — Game Design Document.pdf` at the repo root; it is deliberately not committed (gitignored), so it may be missing in a fresh clone. Addenda are in `docs/design/`. Architecture and determinism rules are in `docs/tech/architecture.md`; read it before touching `src/Rangbaazi.Sim`.

## Commands

- Run all tests: `dotnet test Rangbaazi.sln`
- Build everything, including the Godot project, without the editor: `dotnet build Rangbaazi.sln`
- In-engine smoke test (builds first; exit code 0 = pass): `powershell -ExecutionPolicy Bypass -File tools/godot.ps1 smoke`. Run it after changing `game/` or the sim.
- Launch Godot through `tools/godot.ps1` (`editor`, `run`, `record -Out "$env:TEMP\smoke.avi"`), never the winget `godot` alias, which crashes with ".NET: Assemblies not found". Keep recordings out of the repo.
- Keep `TargetFramework` in `game/Rangbaazi.csproj` a literal, equal to `RangbaaziTargetFramework` in `Directory.Build.props`. The Godot editor rewrites property references.

## Hard rules

- **Gameplay logic lives only in `src/Rangbaazi.Sim`.** Godot scripts in `game/` read `GameState` and draw it; they never change gameplay state.
- **The sim is deterministic.** No float/double/decimal (use `Fix`), no System.Math/Random/HashCode/DateTime/Stopwatch/Task. The build and the `DeterminismGuardTests` enforce this; don't suppress them.
- All mutable match state lives in `GameState`. New `Fighter` fields must be added to `Fighter.AddTo`; new `GameState` fields to `Checksum()` and `CopyFrom()`. Keep `Fighter` a plain value type (no reference fields) so copying it copies everything.
- Floats appear only in `game/` (`FixExtensions.ToFloat`) for rendering.
- **Chat and voice** (`src/Rangbaazi.Social`) never touch the sim or the rollback P2P socket. UI goes through `IChatService` / `VoiceController`. Keep the age-band safety defaults in `SocialPolicy`; any change needs a design note in `docs/design/B10-chat-and-voice.md`.
- Content must be original: no real film titles, dialogue, songs, actor likenesses, or deities as playable fighters (GDD B8).
- Every sim change comes with a test, including a determinism or rollback test when it adds state.
