extends SceneTree
## End-to-end check of rooms of up to 15 (the server runs the match) against a real server:
##   dotnet run --project server/Rangbaazi.Relay
##   godot --headless --path client -s res://tests/room_e2e.gd -- --relay ws://127.0.0.1:8080
## A host and a guest RoomSession join one room; the host checks the 10-15 minute limit, adds 3
## CPUs and starts. Then, in real time (60 ticks a second): the guest holds right and must move
## right, both must see the same 5 fighters and frames from the server, a quick-chat line must
## reach the host, and the host ends the match, which must bring both back to the lobby.
## Exit code 0 = pass.

const TIMEOUT_MS := 60_000


func _initialize() -> void:
	var rng := RandomNumberGenerator.new()
	rng.randomize()
	var room := "RE%04d" % rng.randi_range(0, 9999)
	var host := RoomSession.new(room, true, 10, "Host")
	var guest: RoomSession = null
	var started := Time.get_ticks_msec()
	var next_tick := started
	var step := "join"
	var result := ""
	var guest_x0 := 0
	var hold_ticks := 0
	var moved := 0
	print("room e2e: %s" % RoomSession.url_for_room(room, true, 10, "Host"))

	while result == "":
		var now := Time.get_ticks_msec()
		if now < next_tick:
			OS.delay_msec(next_tick - now)
			continue
		next_tick += 16
		var guest_buttons := Buttons.RIGHT if step == "move" else Buttons.NONE
		host.tick(Buttons.NONE)
		if guest != null:
			guest.tick(guest_buttons)
		if host.phase == RoomSession.Phase.ENDED or (guest != null and guest.phase == RoomSession.Phase.ENDED):
			result = "FAIL ended early (host: %s / guest: %s)" % [host.end_message, guest.end_message if guest != null else ""]
			break
		if Time.get_ticks_msec() - started > TIMEOUT_MS:
			result = "FAIL timed out at step %s" % step
			break

		match step:
			"join":
				if host.phase == RoomSession.Phase.LOBBY and host.is_host():
					guest = RoomSession.new(room, false, 10, "Guest")
					step = "lobby"
			"lobby":
				if guest.phase == RoomSession.Phase.LOBBY and host.players.size() == 2:
					host.set_minutes(42) # above the limit: the server keeps it at 15
					host.set_bots(3)
					step = "settings"
			"settings":
				if host.minutes == 15 and host.bot_count() == 3 and guest.players.size() == 5:
					host.start_match()
					step = "start"
			"start":
				if host.phase == RoomSession.Phase.PLAYING and guest.phase == RoomSession.Phase.PLAYING \
						and guest.state != null and guest.state.frame > 30:
					if guest.state.fighters.size() != 5 or guest.my_fighter != 1 or host.my_fighter != 0:
						result = "FAIL fighters %d, guest is %d, host is %d" % [guest.state.fighters.size(), guest.my_fighter, host.my_fighter]
						break
					if guest.rules.time_limit_frames != 15 * 60 * 60:
						result = "FAIL time limit %d" % guest.rules.time_limit_frames
						break
					guest_x0 = guest.state.fighters[1].pos_x
					step = "move"
			"move":
				hold_ticks += 1
				if hold_ticks == 60:
					moved = guest.state.fighters[1].pos_x - guest_x0
					if moved <= 0:
						result = "FAIL the guest held right for a second but moved %d" % moved
						break
					if absi(host.state.frame - guest.state.frame) > 30:
						result = "FAIL host and guest are %d frames apart" % absi(host.state.frame - guest.state.frame)
						break
					guest.say(0)
					step = "chat"
			"chat":
				if not host.chat_log.is_empty():
					var line: Dictionary = host.chat_log[-1]
					if str(line["text"]) != RoomSession.PHRASES[0] or int(line["fighter"]) != 1:
						result = "FAIL chat arrived as %s" % str(line)
						break
					step = "weapons"
			"weapons":
				# Prop Boxes drop within seconds and reach every client in the STATE frames.
				for prop in guest.state.props:
					if prop.kind == Weapons.PROP_BOX and prop.weapon != Weapons.NONE:
						host.end_match()
						step = "end"
						break
			"end":
				if host.phase == RoomSession.Phase.LOBBY and guest.phase == RoomSession.Phase.LOBBY:
					result = "PASS 5 fighters, guest moved %.1f m, chat delivered, a weapon box arrived, back to the lobby" % Fx.to_float(moved)

	host.close()
	if guest != null:
		guest.close()
	for n in 10:
		host.tick(0)
		if guest != null:
			guest.tick(0)
		OS.delay_msec(10)
	print("ROOM E2E " + result)
	quit(0 if result.begins_with("PASS") else 1)
