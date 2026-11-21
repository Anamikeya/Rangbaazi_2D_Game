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

## Timed free-for-all (online rooms) when above 0: nobody is ever eliminated, a fighter who leaves
## the blast zone always respawns, KOs score, and the match ends after this many frames.
var time_limit_frames := 0
## A ring-out within this many frames of a fighter's last hit credits that attacker with the KO.
var ko_credit_frames := 300
## Comeback "rage": hits do up to rage_percent more damage and knockback, scaling with the
## attacker's own Josh up to rage_josh. 0 is off.
var rage_percent := 0
var rage_josh := 150
## Hidden second wind: after a respawn, this much incoming damage is absorbed. 0 is off.
var respawn_shield := 0
## The fist flurry: empty-handed, Light and Heavy pressed on the same frame start one of four
## combos at random, with equal odds (a jab; a neutral heavy; a jab then a neutral heavy; two
## neutral heavies). Off, both together are the heavy.
var flurry := false
## The stage's hazards run (StageDefinition.hazard_*). Off, they are scenery.
var hazards := false
## Prop Boxes drop weapons (Weapons). Off, the match is fists only.
var weapons := false
## Each fighter's own weapon, by fighter index (Weapons.signature_for): with it their hits do
## Weapons.SIGNATURE_PERCENT. Empty or shorter: no signature.
var signatures: PackedInt32Array = []


## Each fighter's team, by fighter index: 0 is no team. Fighters on the same team never hit each
## other (Simulation.allies). Empty or shorter: no teams. Part of the rules, not of the state: a
## fighter's team never changes during a match.
var teams: PackedInt32Array = []


func signature_of(fighter: int) -> int:
	return signatures[fighter] if fighter >= 0 and fighter < signatures.size() else Weapons.NONE


func team_of(fighter: int) -> int:
	return teams[fighter] if fighter >= 0 and fighter < teams.size() else 0


## A team match: some fighter has a team. Then a timed match is won by a team
## (Simulation.timed_team_winner).
func has_teams() -> bool:
	for team in teams:
		if team != 0:
			return true
	return false


## A room match (MatchRules.Room): the timed free-for-all with weapons, rage and the hidden
## second wind.
static func room(stage_def: StageDefinition, minutes: int, signature_list := PackedInt32Array(), team_list := PackedInt32Array()) -> MatchRules:
	var r := MatchRules.new()
	r.stage = stage_def
	r.time_limit_frames = minutes * 60 * Simulation.TICKS_PER_SECOND
	r.rage_percent = 50
	r.respawn_shield = 20
	r.weapons = true
	r.hazards = true
	r.flurry = true
	r.signatures = signature_list
	r.teams = team_list
	return r


## A summary of every setting that affects the match, for the online hello: peers with different
## rules (or a different build of the sim) must not play each other.
func fingerprint() -> int:
	var h := StateHasher.new()
	h.add_int(Simulation.SIM_VERSION)
	for v in [stocks, respawn_frames, max_josh, guts, guts_josh, guts_delay_frames, guts_heal,
			guts_armor_frames, min_hit_stop, max_hit_stop, time_limit_frames, ko_credit_frames,
			rage_percent, rage_josh, respawn_shield, weapons, flurry, hazards]:
		h.add_u64(int(v))
	h.add_int(signatures.size())
	for w in signatures:
		h.add_int(w)
	h.add_int(teams.size())
	for team in teams:
		h.add_int(team)
	tuning.add_to(h)
	stage.add_to(h)
	return h.value()
