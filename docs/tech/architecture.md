# Architecture

This expands GDD Part B9 into the actual repo structure. It is the reference for where code goes and which rules it has to follow.

## Projects

```
                 ┌──────────────────────────┐
                 │ game/Rangbaazi.csproj    │  Godot 4.7.2 .NET: scenes, input, camera, VFX, audio, UI
                 │ (presentation, layer 3)  │
                 └──────┬───────────┬───────┘
                        │           │
          ┌─────────────▼──┐   ┌────▼──────────────────┐
          │ Rangbaazi.Sim  │   │ Rangbaazi.Social       │
          │ layer 1        │   │ layer 4 (client side)  │
          │ deterministic  │   │ chat + voice logic     │
          │ pure C#        │   │ provider interfaces    │
          └────────────────┘   └────────────────────────┘
          tests/Rangbaazi.Sim.Tests   tests/Rangbaazi.Social.Tests
```

| Layer (GDD B9) | Where | Status |
|---|---|---|
| 1. Simulation core | `src/Rangbaazi.Sim` | Graybox: movement, jumps, platforms, Josh %, ring-out, and combat spec v0.1 (`docs/design/combat-spec-v0.1.md`): jab, four heavies and the ground slam as data (`MoveData`), dodge, parry, hit-stop, Guts. `Diagnostics/`: `SparringBot` (simple CPU player with `CpuLevel` Easy/Normal/Hard, used by tests and the graybox's CPU opponents), `SmokeScript`, and `KoChart` (KO percents per move, for the spec) |
| 2. Netcode | `src/Rangbaazi.Net` (to add) | Planned: Backdash P2P session wrapping `Simulation.Advance` |
| 3. Presentation | `game/` | Graybox scene draws sim state with shapes, including hitboxes, dodges, stuns, hit-stop shake and Guts; players 2+ can be CPU-controlled (F2), Guts can be switched off (F4) |
| 4. Services | `src/Rangbaazi.Social` (+ backend adapters later) | Chat/voice interfaces, safety policy, offline hub |
| 5. Tools | `tools/`, `game/addons/` (to add) | `tools/godot.ps1` (editor, run, smoke test, record). Planned: move-set editor plugin, Blender export scripts |

Dependency rule: **game → Sim, Social**. Sim and Social never reference Godot or each other.

## Determinism rules (Rangbaazi.Sim)

Rollback netcode re-simulates frames after late inputs arrive. Every peer must compute bit-identical state from the same inputs. The build enforces these rules:

| Rule | Enforced by |
|---|---|
| No `float`, `double` or `decimal`; use `Fix` / `FixVec2` / `FixRect` | `DeterminismGuardTests` (source scan + reflection) |
| No `System.Math`, `MathF`, `Random`, `HashCode`, `DateTime.Now`, `Stopwatch`, `Guid.NewGuid`, `Task` | `BannedSymbols.txt` + BannedApiAnalyzers (build error RS0030) |
| Warnings are errors | `TreatWarningsAsErrors` in `Rangbaazi.Sim.csproj` |
| All mutable state lives in `GameState` (value-type `Fighter`s + `SimRandom`) | Reflection tests in `DeterminismTests`. `ChecksumAndCloneCoverEveryFighterField`: `Fighter` holds no references at any depth, and every field changes the checksum and survives `Clone`. `ChecksumAndCloneCoverEveryGameStateField`: the same for `GameState`'s other fields. `CloneSharesNoMutableReferences`: a clone shares no reference-type field (the `Fighters` array). Plus the rollback tests `RollbackResimulationMatchesOriginalTimeline` and `RollbackThroughParriesHitStopAndGutsMatchesTheOriginalTimeline` |
| Static data (stages, tuning, move frame data) is immutable and outside the state | `StageDefinition`, `FighterTuning`, `MoveData`, `MatchRules` are init-only; a fighter stores only a `MoveId` and a frame count |
| Gameplay collision is custom AABB, never Godot physics | Sim has no Godot reference |
| Iterate in fixed order (player index); collect hits first, apply after | `ResolveHits` |
| Ragdolls, particles, camera and audio are client-only and never feed back into the sim | Presentation only reads `GameState` |

Fixed-point format: Q47.16 in a `long` (resolution 1/65536). Multiplication and division go through `Int128` so intermediate results can't overflow. Sim units are metres, y-up, 60 ticks/s.

## Frame loop

```
Godot _PhysicsProcess (60 Hz)
  └─ read local input → Buttons (16-bit per player)
  └─ [netcode: exchange inputs, predict remote, roll back on mismatch]   ← Backdash, next
  └─ Simulation.Advance(state, rules, inputs)
  └─ QueueRedraw / update visuals from state
```

Rollback API already supported by the sim:
- save: `GameState.Clone()` or `CopyFrom(...)` into a preallocated ring buffer
- load: `state.CopyFrom(saved)`
- desync check: `GameState.Checksum()` (FNV-1a, stable across machines)

`GrayboxMatch` sets `Engine.PhysicsTicksPerSecond = Simulation.TicksPerSecond` at startup, so one physics tick is always one sim frame. It is set in code because the editor removes `project.godot` settings that equal Godot's defaults.

## Testing

| Layer | How | Command |
|---|---|---|
| Sim, Social | xUnit, no Godot needed: rules, determinism, rollback re-simulation, banned-API guard, and `SpecTests`, which keep the combat spec's frame data and KO table equal to the code | `dotnet test Rangbaazi.sln` |
| Game (Godot) | In-engine smoke test: `SparringBot`s play `SmokeScript` through synthetic key events. Each frame the buttons read back must equal the buttons the bots pressed. At the end the checksum must equal a pure-sim replay in the same process, and the quick-chat key must have reached the chat hub. An exception, or a match that stops advancing, also fails it, so it never hangs. | `./tools/godot.ps1 smoke` (exit code 0 = pass) |

`SparringBot.Think(state, rules, player, level)` is a pure function: it reads `GameState` and never writes it or draws from the match RNG (`SparringBotTests.ThinkNeverChangesState`). That makes it safe for rollback and soak tests, and a starting point for training-mode CPUs. The level defaults to `Hard`, which is what `SmokeScript` plays.

CPU players are an input source, not sim state. `GrayboxMatch` fills a CPU player's `Buttons` from `SparringBot.Think` on the same pre-advance state as everyone else's, so the sim can't tell CPU input from a person's. Under rollback, each peer recomputes the CPU's buttons from the restored state (`CpuOpponentTests.CpuInputsSurviveRollback`). `CpuOpponentTests` also soak-tests the bot in long free-for-alls, failing if a CPU ever walks or jumps off the stage on its own and loses the stock. It checks that one-on-one CPU matches at every pairing of levels finish, so there are no stand-offs, that the CPU can reach an opponent wherever they stand (at the stage edge, or under a platform it is on), and that CPU matches use the whole move set: dodges, parries, heavies and the up-heavy recovery.

## Godot editor notes

- `game/Rangbaazi.csproj` needs a literal `TargetFramework`. The editor rewrites a property reference such as `$(RangbaaziTargetFramework)` and leaves a `.csproj.old` backup. Keep it in sync with `Directory.Build.props` by hand.
- Commit the `*.cs.uid` files the editor creates next to scripts. Godot uses them to keep references working when files move.
- The winget `godot` alias is a symlink and can't find the .NET assemblies; `tools/godot.ps1` finds the real executable.
- On Godot 4.7.2 the headless **editor** misbehaves here: `--headless --import` crashed on exit (0xC0000005), and `--headless --build-solutions --quit` hung after a successful build. For CI, build with `dotnet build` and run the smoke test headless (`tools/godot.ps1 smoke`, i.e. `--headless --path game --fixed-fps 60 -- --smoke`). That path works and exits 0 or 1. Without `-- --smoke` the graybox scene never quits on its own.

## Chat and voice (Rangbaazi.Social)

See `docs/design/B10-chat-and-voice.md`. Key points:
- UI talks to `IChatService` and `VoiceController` only. Backends (Nakama chat, EOS voice) are adapters added later.
- `LocalChatHub` is an in-process "server" for offline development and tests.
- `SocialPolicy` sets defaults and maximums by age band; `BlockList` is shared by chat and voice.
- Chat and voice traffic never goes through the rollback P2P session.

## Next technical steps

1. **Backdash 1v1 prototype** (GDD C3): add `src/Rangbaazi.Net`, wrap `Simulation.Advance` in a Backdash session, test between two PCs.
2. **Combat, next pass** (v0.1 is in: `docs/design/combat-spec-v0.1.md`): light strings and directional lights, grabs and throws, then Taali and a Signature Scene. Run the C3 blind playtest on v0.1 first, with Guts on and off (F4).
3. **Camera director**: multi-target framing, plus screen shake and zoom on big hits, all on the presentation side. Hit-stop itself is sim state (`Fighter.HitStop`), so the camera reads it rather than adding its own.
4. **Headless replay tool**: record inputs and replay them in tests to catch desyncs across builds. The smoke test already compares the in-engine run with a pure-sim replay on one machine; recorded replays would extend that across builds and platforms.
