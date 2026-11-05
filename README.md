# RANGBAAZI

*Working title.* A 2D platform brawler set in a loud, masala-cinema world: up to 8 fighters, grab-anything prop chaos, and Taali-powered "Signature Scene" supers. Local play, online rollback, team modes and (later) a 2D battle royale.

- Game design: `RANGBAAZI — Game Design Document.pdf` (not in git; get it from the team and put it at the repo root, where `.gitignore` keeps it out of commits) and the chat/voice addendum [`docs/design/B10-chat-and-voice.md`](docs/design/B10-chat-and-voice.md)
- Tech: [`docs/tech/architecture.md`](docs/tech/architecture.md)

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

It passes when every frame's buttons read back exactly as the bots pressed them, the final checksum equals a pure-sim replay of the same script, and the quick-chat key reached the chat hub. It prints one line: `SMOKE PASS frames=900 checksum=<16 hex digits> stocksLost=<n> chatMessages=1`, or `SMOKE FAIL` and the reason. To watch it, record a video (Movie Maker, 60 fps): `./tools/godot.ps1 record -Out "$env:TEMP\smoke.avi"`. Recording opens a real window, so leave the keyboard and gamepads alone until it finishes; they would count as input mismatches.

## Graybox controls

| | P1 (keyboard) | P2 (keyboard) | Gamepad N → player N+1 |
|---|---|---|---|
| Move | A / D | ← / → | Left stick / D-pad |
| Jump (x2) | Space | Numpad 0 | A |
| Light attack | J | Numpad 1 | X |
| Heavy *(not wired yet)* | K | Numpad 2 | Y |
| Dodge *(not wired yet)* | L | Numpad 3 | RB |

F5 restarts the match. T sends a quick-chat line through the local chat hub.

## Repo layout

```
src/Rangbaazi.Sim/       deterministic simulation core (fixed-point, no floats, no Godot)
src/Rangbaazi.Social/    text chat + voice chat logic, safety policy, provider interfaces
tests/                   xUnit tests for both (run headless, no Godot needed)
game/                    Godot 4.7.2 .NET project (presentation only)
tools/                   dev scripts (godot.ps1: editor, run, smoke test, record)
docs/design/             GDD addenda
docs/tech/               architecture and engineering notes
```

## Conventions

- Gameplay rules go in `src/Rangbaazi.Sim`, never in Godot scripts. Read the determinism rules in [`docs/tech/architecture.md`](docs/tech/architecture.md) first.
- Binary assets (art, audio, models, fonts, PDFs) go through Git LFS automatically via `.gitattributes`. Git LFS is currently disabled for the GitHub repo, so pushing a commit that adds such files fails until LFS is enabled there.
- All characters, lines, songs and art must be original (GDD B8 cultural guardrails).
