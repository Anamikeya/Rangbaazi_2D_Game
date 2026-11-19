# Character creator

Built 2026-11-19. A player builds their own fighter from the 31 fighters' pieces. It is looks only: every fighter, mixed ones included, has the same moves and numbers.

## What a player picks

| Strip | What it brings |
|---|---|
| Head | The head with its six faces, and the hat or extras that ride on it |
| Hands | Both hands, with their swing smear and anything that rides on them |
| Feet | Both feet |
| Moves | Whose animations the fighter uses (their 24 own moves; the 18 shared ones are the same for all) |

Plus a name of up to 12 characters, cleaned like a player's name. "Surprise me" picks all four at random. The preview can stand, run, punch, do a heavy and celebrate.

The creator opens from "Create your own" in the fighter picker. A player has one creation; it is saved in their settings and stands in the picker's ring with the 31.

## The id

A creation is a fighter id of the form `mix.<head>.<hands>.<feet>.<moves>.<name>`, each of the four one of the 31 ids. It works anywhere a fighter's id does: `ArtLibrary.character()` builds its rig from the four fighters' files (`client/game/art/art_library.gd`).

- **Sounds, hit effects and the own-weapon bonus** follow the head (`ArtLibrary.look`, and `ArenaLimits.Look` on the server).
- **Rooms:** the id is sent as the player's character, so everyone sees the creation. The server accepts only a well-formed mix and cleans its name (`ArenaLimits.CleanCharacter`). The message format is unchanged, so the arena protocol stays at version 3. A client from before this draws a creation as Shan.
- **1v1 duels** don't send characters yet, so the other player doesn't see it there.

## Not built

- More than one saved creation.
- Colours, or pieces that aren't from the 31.
- Fitting pieces to each other: a small head on big feet is drawn as it comes.
