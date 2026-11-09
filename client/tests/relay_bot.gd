extends SceneTree
## A headless sparring partner for trying the online game: joins a room on a relay and plays it
## with a bot, in real time, until the time is up.
##   godot --headless --path client -s res://tests/relay_bot.gd -- --relay ws://127.0.0.1:8080 --room ABCD [--seconds 120] [--level normal]
## Then open the game (or ?room=ABCD on the web) and join the same room.

func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	var room := _arg(args, "--room", "BOT")
	var seconds := _arg(args, "--seconds", "120").to_int()
	var levels := { "easy": SparringBot.Level.EASY, "normal": SparringBot.Level.NORMAL, "hard": SparringBot.Level.HARD }
	var level: int = levels.get(_arg(args, "--level", "normal"), SparringBot.Level.NORMAL)
	var session := OnlineSession.new(OnlineSession.relay_url_for_room(room), room)
	print("relay bot: room %s via %s" % [room, OnlineSession.relay_url_for_room(room)])
	var end_at := Time.get_ticks_msec() + seconds * 1000
	var next_tick := Time.get_ticks_msec()
	var last_phase := -1
	while Time.get_ticks_msec() < end_at:
		var now := Time.get_ticks_msec()
		if now < next_tick:
			OS.delay_msec(next_tick - now)
			continue
		next_tick += 16
		var buttons := Buttons.NONE
		if session.phase == OnlineSession.Phase.RUNNING:
			buttons = SparringBot.think(session.state, session.rules, session.local_slot, level)
		session.tick(buttons)
		if session.phase != last_phase:
			last_phase = session.phase
			print("relay bot: phase %d %s" % [session.phase, session.end_message])
		if session.phase == OnlineSession.Phase.ENDED:
			break
	if session.core != null:
		print("relay bot: frame %d, verified %d checksums, rollbacks %d (max %d)" % [
			session.core.state.frame, session.checksums_verified, session.core.rollback_count, session.rollback_max])
	session.close()
	quit(0)


func _arg(args: PackedStringArray, flag: String, fallback: String) -> String:
	var i := args.find(flag)
	return args[i + 1] if i >= 0 and i + 1 < args.size() else fallback
