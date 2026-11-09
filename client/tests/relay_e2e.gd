extends SceneTree
## End-to-end check against a real relay (server/Rangbaazi.Relay), over real WebSockets:
##   dotnet run --project server/Rangbaazi.Relay            (or a deployed one)
##   godot --headless --path client -s res://tests/relay_e2e.gd -- --relay ws://127.0.0.1:8080 [--frames 900]
## Two OnlineSessions in this process join the same room, bots play both sides in real time
## (60 ticks per second), and it passes once both have FRAMES final frames, compared at least one
## checksum with each other, and an offline replay of their inputs agrees. Exit code 0 = pass.

const TIMEOUT_MS := 120_000


func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	var frames := 900
	var i := args.find("--frames")
	if i >= 0 and i + 1 < args.size():
		frames = args[i + 1].to_int()
	var rng := RandomNumberGenerator.new()
	rng.randomize()
	var room := "E2E%04d" % rng.randi_range(0, 9999)
	var url := OnlineSession.relay_url_for_room(room)
	print("relay e2e: %s, %d frames" % [url, frames])

	var a := OnlineSession.new(url, room)
	var b := OnlineSession.new(url, room)
	var started := Time.get_ticks_msec()
	var next_tick := started
	var result := ""
	while result == "":
		var now := Time.get_ticks_msec()
		if now < next_tick:
			OS.delay_msec(next_tick - now)
			continue
		next_tick += 16 # ~60 ticks per second, like the game
		a.tick(_bot(a, SparringBot.Level.NORMAL))
		b.tick(_bot(b, SparringBot.Level.HARD))
		if a.phase == OnlineSession.Phase.ENDED or b.phase == OnlineSession.Phase.ENDED:
			result = "FAIL ended early (A: %s / B: %s)" % [a.end_message, b.end_message]
		elif Time.get_ticks_msec() - started > TIMEOUT_MS:
			result = "FAIL timed out (phases %d / %d)" % [a.phase, b.phase]
		elif a.core != null and b.core != null and a.core.final_frame() >= frames and b.core.final_frame() >= frames \
				and a.checksums_verified > 0 and b.checksums_verified > 0:
			var ra := a.core.replay(frames).checksum()
			var rb := b.core.replay(frames).checksum()
			if ra != rb:
				result = "FAIL replays differ at frame %d" % frames
			else:
				result = "PASS frame=%d checksum=%s verified=%d/%d rollbacks=%d/%d max=%d/%d ping=%d ms" % [
					frames, StateHasher.hex(ra), a.checksums_verified, b.checksums_verified,
					a.core.rollback_count, b.core.rollback_count, a.rollback_max, b.rollback_max, a.ping_ms]
	a.close()
	b.close()
	for n in 10: # let the goodbyes go out
		a.tick(0)
		b.tick(0)
		OS.delay_msec(10)
	print("RELAY E2E " + result)
	quit(0 if result.begins_with("PASS") else 1)


func _bot(s: OnlineSession, level: int) -> int:
	if s.phase != OnlineSession.Phase.RUNNING:
		return Buttons.NONE
	return SparringBot.think(s.state, s.rules, s.local_slot, level)
