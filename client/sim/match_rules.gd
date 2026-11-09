class_name MatchRules
extends RefCounted
## Per-match settings, not part of the rollback state (Simulation.cs MatchRules, FighterTuning.cs,
## StageDefinition.cs). Treat as immutable once a match starts: both online players must use
## equal rules, which the online hello checks with fingerprint().

var stage: StageDefinition = StageDefinition.graybox()
var tuning: FighterTuning = FighterTuning.default_tuning()
var stocks := 3              # GDD B2: stocks default to 3
var respawn_frames := 60
var max_josh := 300

## Guts (GDD B2): once per stock, a fighter whom a hit leaves at guts_josh or more, and who then
## keeps the stock for guts_delay_frames, gets guts_heal Josh back and guts_armor_frames of armor.
var guts := true
var guts_josh := 150
var guts_delay_frames := 180 # 3 s
var guts_heal := 30
var guts_armor_frames := 60

## Hit-stop range (4-10 frames, scaled by damage).
var min_hit_stop := 4
var max_hit_stop := 10


## A summary of every setting that affects the match, for the online hello: peers with different
## rules (or a different build of the sim) must not play each other.
func fingerprint() -> int:
	var h := StateHasher.new()
	h.add_int(Simulation.SIM_VERSION)
	for v in [stocks, respawn_frames, max_josh, guts, guts_josh, guts_delay_frames, guts_heal,
			guts_armor_frames, min_hit_stop, max_hit_stop]:
		h.add_u64(int(v))
	tuning.add_to(h)
	stage.add_to(h)
	return h.value()
