class_name Nicknames
extends RefCounted
## The winner's nickname (docs/design/combat-spec-v0.2-weapons.md, "The name generator"): a word for
## their main weapon plus a title, with master titles for a winner who also landed 3 sweet spots
## and a KO with it. Every screen in a room must show the same name, so the pick comes from the
## match itself (its last frame and the winner), never from a random number generator.

## Weapon words by the sim's weapon id (0 is fists); the last row is the all-rounder's.
const WEAPON_WORDS := [
	["Mukka", "Knuckle", "Thappad", "Fist"],
	["Belan", "Rolling-Pin", "Bonk", "Chapati"],
	["Bat", "Home-Run", "Sixer", "Slugger"],
	["Bowling", "Strike", "Gutter", "Ten-Pin"],
	["Shotgun", "Boomstick", "Pump", "Buckshot"],
	["Uzi", "Trigger", "Bullet", "Rat-a-tat"],
	["Pataka", "Cherry", "Fuse", "Firecracker"],
	["Arrow", "Bullseye", "Longbow", "Quiver"],
	["Boomerang", "Comeback", "Return", "Curve"],
	["Trampoline", "Bounce", "Spring", "Jumper"],
	["Mine", "Booby-Trap", "Tripwire", "Surprise"],
	["Missile", "Rocket", "Lock-On", "Seeker"],
	["Black-Hole", "Gravity", "Vortex", "Void"],
]
const ALL_ROUNDER_WORDS := ["Gully", "Mohalla", "Jugaad", "Chaos"]
const REGULAR_TITLES := ["Wala", "Champ", "Ace", "Bhai", "Kid", "Specialist"]
const MASTER_TITLES := ["Baadshah", "Ustaad", "Legend", "Boss", "Maestro", "Machine"]
const ALL_ROUNDER := 15
const MASTER := 128


## A fighter's main weapon as one number, the way rooms send it: 0-12 (0 is fists), or
## ALL_ROUNDER, plus MASTER for a master (Simulation.main_weapon).
static func code_for(state: GameState, fighter: int) -> int:
	var main := Simulation.main_weapon(state, fighter)
	return (ALL_ROUNDER if int(main[0]) < 0 else int(main[0])) | (MASTER if bool(main[1]) else 0)


## The winner's nickname in quotes, or "". `code` is the winner's main weapon (code_for, or the
## number a room sent); below 0, it is worked out from the state.
static func for_winner(state: GameState, code: int = -1) -> String:
	if state == null or not state.match_over or state.winner_index < 0:
		return ""
	if code < 0:
		code = code_for(state, state.winner_index)
	var weapon := code & 127
	var words: Array = WEAPON_WORDS[weapon] if weapon < WEAPON_WORDS.size() else ALL_ROUNDER_WORDS
	var titles: Array = MASTER_TITLES if (code & MASTER) != 0 else REGULAR_TITLES
	var w := state.fighters[state.winner_index]
	var pick := absi(state.frame * 31 + state.winner_index * 7 + w.kos * 13 + w.falls)
	return "\"%s %s\"" % [words[pick % words.size()], titles[(pick / words.size()) % titles.size()]]
