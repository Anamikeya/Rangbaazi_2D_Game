class_name Nicknames
extends RefCounted
## The winner's nickname (docs/design/combat-spec-v0.2-weapons.md, "The name generator"): a word for
## their main weapon plus a title, master titles for a standout match. Every screen in a room must
## show the same name, so the pick comes from the match itself (its last frame and the winner),
## never from a random number generator.
##
## Draft rule until weapons are in the game: everyone fights with fists, so the weapon words are the
## fists'; a winner with 5 or more KOs gets a master title (the spec's sweet-spot rule needs weapons).

const FIST_WORDS := ["Mukka", "Knuckle", "Thappad", "Fist"]
const REGULAR_TITLES := ["Wala", "Champ", "Ace", "Bhai", "Kid", "Specialist"]
const MASTER_TITLES := ["Baadshah", "Ustaad", "Legend", "Boss", "Maestro", "Machine"]
const MASTER_KOS := 5


static func for_winner(state: GameState) -> String:
	if state == null or not state.match_over or state.winner_index < 0:
		return ""
	var w := state.fighters[state.winner_index]
	var titles := MASTER_TITLES if w.kos >= MASTER_KOS else REGULAR_TITLES
	var pick := absi(state.frame * 31 + state.winner_index * 7 + w.kos * 13 + w.falls)
	var word: String = FIST_WORDS[pick % FIST_WORDS.size()]
	var title: String = titles[(pick / FIST_WORDS.size()) % titles.size()]
	return "\"%s %s\"" % [word, title]
