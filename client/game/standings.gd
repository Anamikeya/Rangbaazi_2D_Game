class_name Standings
extends RefCounted
## Team and fighter totals for the HUD and the result (docs/design/rooms-teams-view-ui-v1.md, 1.7).
## Presentation only: the sim decides the winner itself (Simulation.timed_team_winner); these rank
## by the same rule so what the screen shows agrees with it.


## The KOs of everyone on this team (team > 0).
static func team_kos(state: GameState, rules: MatchRules, team: int) -> int:
	var n := 0
	for i in state.fighters.size():
		if rules.team_of(i) == team:
			n += state.fighters[i].kos
	return n


static func team_falls(state: GameState, rules: MatchRules, team: int) -> int:
	var n := 0
	for i in state.fighters.size():
		if rules.team_of(i) == team:
			n += state.fighters[i].falls
	return n


## The teams that have fighters, best first: the most KOs, then the fewest falls, then the lower
## number. Each entry is {team, kos, falls, members (fighter indexes, best first)}.
static func teams_ranked(state: GameState, rules: MatchRules) -> Array:
	var by_team := {}
	for i in state.fighters.size():
		var team := rules.team_of(i)
		if team <= 0:
			continue
		if not by_team.has(team):
			by_team[team] = {"team": team, "kos": 0, "falls": 0, "members": []}
		var entry: Dictionary = by_team[team]
		entry["kos"] += state.fighters[i].kos
		entry["falls"] += state.fighters[i].falls
		(entry["members"] as Array).append(i)
	var out: Array = by_team.values()
	out.sort_custom(func(a: Dictionary, b: Dictionary) -> bool:
		if a["kos"] != b["kos"]:
			return a["kos"] > b["kos"]
		if a["falls"] != b["falls"]:
			return a["falls"] < b["falls"]
		return a["team"] < b["team"])
	var order := fighters_ranked(state)
	for entry: Dictionary in out:
		var members: Array = entry["members"]
		members.sort_custom(func(a: int, b: int) -> bool: return order.find(a) < order.find(b))
	return out


## Every fighter index, best first: the most KOs, then the fewest falls, then the lower index.
static func fighters_ranked(state: GameState) -> Array:
	var order: Array = []
	for i in state.fighters.size():
		order.append(i)
	order.sort_custom(func(a: int, b: int) -> bool:
		var fa := state.fighters[a]
		var fb := state.fighters[b]
		if fa.kos != fb.kos:
			return fa.kos > fb.kos
		if fa.falls != fb.falls:
			return fa.falls < fb.falls
		return a < b)
	return order
