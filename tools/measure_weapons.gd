extends SceneTree
## Balance by CPU brawl: four Hard CPUs for a minute, only fighter 0 kept armed with one weapon
## (fists are the baseline). Prints, per weapon a crate can hold: its damage, KOs and sweet spots,
## the hits fighter 0 took from its own weapon, fighter 0's KOs and falls, how many of the weapon
## it went through, and how each one left the hand. A crate's worth of a damage weapon should do
## roughly 20 to 75 (docs/design/combat-spec-v0.4-all-weapons.md, section 4.2): "a crate" is
## dmg/min divided by armed.
##
##   godot --headless --path client -s ../tools/measure_weapons.gd -- [<seeds>] [<stage id>] [<first id> <last id>]
##   godot --headless --path client -s ../tools/measure_weapons.gd -- <seeds> <stage id> <id>,<id>,...
##
## Numbers come from the sim's own record (Simulation.weapon_stat), so a weapon's damage is what
## the nickname code counts: hits on others, credited to that weapon.
##
## Effect events (section 4.2's test for the utility rows) are read from the state, not from the
## sim: with hazards off, no boxes and the other three on fists, every status and every shove in
## the match comes from fighter 0's weapon. On a fighter, one event is a frame where it takes a hit
## from fighter 0, a status timer goes up (slow, root, mark, a hold, poison from fighter 0), or it
## is moved by something other than its own speed (a drag, a gate, a swap, a wall) by more than
## 0.02 m. One fighter counts at most one event every 30 frames. "others" is the other three,
## "self" fighter 0 itself (its own hits and statuses only: its own blink or gate trip is the point).
## For a row that only works on armed fighters (armedOnly: the Magnet Gun) the other three are kept
## holding a Belan; their own hits don't count.
##
## Columns at the end: used (it left the hand empty: an empty weapon is thrown, or a last use
## spends it), thrown (it left the hand with supply left: a throw, a disarm) and lost (it was in
## the hand at a fall).

const FRAMES := 3600
const EVENT_GAP := 30
static var MOVE_EPS := Fx.ratio(2, 100)


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	var seeds := int(args[0]) if args.size() > 0 else 4
	var stage := args[1] if args.size() > 1 else "rooftops"
	var only := PackedInt32Array() # a list of ids instead of a range
	if args.size() > 2 and args[2].contains(","):
		for id in args[2].split(","):
			only.append(int(id))
	var first := int(args[2]) if args.size() > 2 and only.is_empty() else 0
	var last := int(args[3]) if args.size() > 3 else WeaponTable.COUNT
	var weapons := PackedInt32Array([Weapons.NONE])
	weapons.append_array(Weapons.crate_list())
	print("id weapon                   dmg/min  KOs  sweet  selfhits selfdmg  myKOs myFalls  armed  a crate  fx/crate selffx/crate  used thrown lost")
	for w in weapons:
		if w != Weapons.NONE and (not only.has(w) if not only.is_empty() else (w < first or w > last)):
			continue
		var dmg := 0; var kos := 0; var sweet := 0; var self_hits := 0; var self_dmg := 0
		var my_kos := 0; var my_falls := 0; var armed := 0
		var fx_others := 0; var fx_self := 0; var used := 0; var thrown := 0; var lost := 0
		for s in seeds:
			var rules := MatchRules.new()
			rules.weapons = true
			rules.hazards = false
			rules.flurry = true
			rules.stocks = 99
			rules.stage = StageDefinition.by_id(stage)
			var state := Simulation.create_initial_state(rules, 4, 100 + s * 7)
			state.prop_timer = 100000000
			var inputs := PackedInt32Array([0, 0, 0, 0])
			var before: Array[Fighter] = []
			var last_event := PackedInt32Array([-1000, -1000, -1000, -1000])
			for i in 4:
				before.append(Fighter.new())
			for f in FRAMES:
				var me := state.fighters[0]
				if w != 0 and f % 60 == 0 and me.is_active() and me.weapon == Weapons.NONE:
					# A weapon that left the hand and is on its way back (a boomerang) is still theirs.
					var flying := false
					for p in state.props:
						if p.kind == Weapons.PROP_SHOT and p.shot == Weapons.SHOT_BOOMERANG and p.owner == 0:
							flying = true
					if not flying:
						me.weapon = w
						me.weapon_supply = Weapons.supply_of(w)
						armed += 1
				if w != 0 and WeaponTable.ROWS[w][WeaponTable.C_ARMED_ONLY] != 0 and f % 60 == 0:
					# A stream that only takes armed fighters (the Magnet Gun): the other three hold a
					# Belan, so it has someone to work on. Their hits are theirs, not counted.
					for i in range(1, 4):
						var o := state.fighters[i]
						if o.is_active() and o.weapon == Weapons.NONE:
							o.weapon = Weapons.BELAN
							o.weapon_supply = Weapons.supply_of(Weapons.BELAN)
				for i in 4:
					inputs[i] = SparringBot.think(state, rules, i, SparringBot.Level.HARD)
					before[i].copy_from(state.fighters[i])
				Simulation.advance(state, rules, inputs)
				me = state.fighters[0]
				var was := before[0]
				if me.hit_count > was.hit_count and me.last_hit_by == 1:
					self_hits += 1
					self_dmg += maxi(0, me.josh - was.josh)
				if w != 0 and was.weapon == w and me.weapon != w:
					if me.falls > was.falls:
						lost += 1
					elif _thrown(was, me):
						thrown += 1
					else:
						used += 1
				for i in 4:
					if f - last_event[i] < EVENT_GAP or not _affected(before[i], state.fighters[i], i == 0, w):
						continue
					last_event[i] = f
					if i == 0:
						fx_self += 1
					else:
						fx_others += 1
			dmg += Simulation.weapon_stat(state, 0, w, 0)
			kos += Simulation.weapon_stat(state, 0, w, 1)
			sweet += Simulation.weapon_stat(state, 0, w, 2)
			my_kos += state.fighters[0].kos
			my_falls += state.fighters[0].falls
		var n := float(seeds)
		var crates := float(maxi(1, armed))
		print("%3d %-23s %7.1f %5.1f %5.1f %8.1f %7.1f %7.1f %6.1f %7.1f %7.1f %9.2f %12.2f %5.1f %6.1f %4.1f" % [
			w, WeaponInfo.name(w) if w > 0 else "Fists", dmg / n, kos / n, sweet / n, self_hits / n,
			self_dmg / n, my_kos / n, my_falls / n, armed / n, dmg / crates, fx_others / crates,
			fx_self / crates, used / n, thrown / n, lost / n])
	quit(0)


## Whether the weapon left the hand with supply still in it (a throw of a full one, a disarm). An
## empty weapon stays in the hand until it is thrown too: that one counts as used up.
static func _thrown(was: Fighter, _now: Fighter) -> bool:
	return was.weapon_supply > 0


## One frame's effect on a fighter, from its state before and after: a hit from fighter 0 (for
## fighter 0, its own), a status that went up, or (for the others) a move not made by its own speed.
static func _affected(a: Fighter, b: Fighter, is_self: bool, w: int) -> bool:
	if not a.is_active() or not b.is_active():
		return false
	if b.hit_count > a.hit_count and b.last_hit_by == 1 and b.last_hit_weapon == w:
		return true
	if b.slow_frames > a.slow_frames or b.root_frames > a.root_frames or b.mark_frames > a.mark_frames:
		return true
	if b.stun_weapon != 0 and b.stun_weapon != a.stun_weapon:
		return true
	if b.dot_frames > a.dot_frames and b.dot_by == 1:
		return true
	if is_self or a.hit_stop > 0 or b.hit_stop > 0:
		return false
	return absi(b.pos_x - a.pos_x - b.vel_x) > MOVE_EPS
