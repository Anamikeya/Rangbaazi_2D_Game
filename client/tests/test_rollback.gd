extends TestCase
## Rollback netcode: two players' sessions through an in-memory relay, each played by a
## SparringBot. They must stay in step through latency, end the same way, match an offline replay
## of their inputs, and handle every way a match can end.

const FRAMES := 600


func _bot(s: OnlineSession, level: int) -> int:
	if s.phase != OnlineSession.Phase.RUNNING:
		return Buttons.NONE
	return SparringBot.think(s.state, s.rules, s.local_slot, level)


func _pair(relay: FakeRelay, room: String = "TEST") -> Array:
	var a := OnlineSession.new("", room, 2, func(): return relay.socket(room))
	var b := OnlineSession.new("", room, 2, func(): return relay.socket(room))
	return [a, b]


## Steps the relay and both sessions until done() or max_steps. tick_a / tick_b false freezes a side.
func _run(relay: FakeRelay, a: OnlineSession, b: OnlineSession, done: Callable, max_steps: int,
		tick_a: bool = true, tick_b: bool = true) -> bool:
	for i in max_steps:
		if done.call():
			return true
		relay.step()
		if tick_a:
			a.tick(_bot(a, SparringBot.Level.NORMAL))
		if tick_b:
			b.tick(_bot(b, SparringBot.Level.HARD))
	return done.call()


func _both_running(a: OnlineSession, b: OnlineSession) -> bool:
	return a.phase == OnlineSession.Phase.RUNNING and b.phase == OnlineSession.Phase.RUNNING


func _check_in_step(a: OnlineSession, b: OnlineSession, label: String) -> void:
	check_eq(a.core.seed, b.core.seed, label + " seed")
	check(a.local_slot != b.local_slot, label + " different sides")
	check(a.checksums_verified > 0 and b.checksums_verified > 0, label + " checksums compared")
	var common := mini(a.core.final_frame(), b.core.final_frame())
	var ra := a.core.replay(common)
	var rb := b.core.replay(common)
	check_eq(ra.checksum(), rb.checksum(), label + " replays agree at frame %d" % common)
	check(a.core.rollback_max <= OnlineSession.PREDICTION and b.core.rollback_max <= OnlineSession.PREDICTION,
		label + " rollbacks within the prediction window (%d, %d)" % [a.core.rollback_max, b.core.rollback_max])
	var hits := 0
	for f in ra.fighters:
		hits += f.josh + (ra.fighters.size() - f.stocks)
	check(hits > 0, label + " the bots actually fought")


func _play(latency: int, jitter: int) -> void:
	var relay := FakeRelay.new(latency * 31 + jitter)
	relay.latency = latency
	relay.jitter = jitter
	var p := _pair(relay)
	var a: OnlineSession = p[0]
	var b: OnlineSession = p[1]
	var label := "latency %d jitter %d" % [latency, jitter]
	var ok := _run(relay, a, b, func(): return (a.core != null and b.core != null and a.core.final_frame() >= FRAMES
		and b.core.final_frame() >= FRAMES) or a.phase == OnlineSession.Phase.ENDED or b.phase == OnlineSession.Phase.ENDED, 6000)
	if not check(ok and _both_running(a, b), "%s: still running at frame %d (A: %s, B: %s)" % [label, FRAMES, a.end_message, b.end_message]):
		return
	_check_in_step(a, b, label)
	if latency > 0:
		check(a.core.rollback_count + b.core.rollback_count > 0, label + " latency but no rollbacks")
	# Also check the live state of whichever is behind against the other's saved inputs.
	var behind := a if a.core.state.frame <= b.core.state.frame else b
	var ahead := b if behind == a else a
	var f := behind.core.final_frame()
	if f == behind.core.state.frame:
		check_eq(behind.core.state.checksum(), ahead.core.replay(f).checksum(), label + " live state equals the other side's replay")


func test_no_latency() -> void:
	_play(0, 0)


func test_small_latency() -> void:
	_play(3, 1)


func test_high_latency_with_jitter() -> void:
	_play(8, 4)


func test_desync_is_caught_on_both_sides() -> void:
	var relay := FakeRelay.new()
	relay.latency = 2
	var p := _pair(relay)
	var a: OnlineSession = p[0]
	var b: OnlineSession = p[1]
	# A's game changes P1's Josh on frame 100 every time it simulates it: a real, lasting desync.
	a.after_advance = func(s: GameState):
		if s.frame == 100:
			s.fighters[0].josh += 3
	var ended := _run(relay, a, b, func(): return a.phase == OnlineSession.Phase.ENDED and b.phase == OnlineSession.Phase.ENDED, 3000)
	check(ended, "both ended (A: %s, B: %s)" % [a.end_message, b.end_message])
	check(a.end_message.to_lower().contains("desync") and b.end_message.to_lower().contains("desync"),
		"both say desync (A: %s, B: %s)" % [a.end_message, b.end_message])


func test_different_rules_are_refused() -> void:
	var relay := FakeRelay.new()
	var p := _pair(relay)
	var a: OnlineSession = p[0]
	var b: OnlineSession = p[1]
	b.rules.guts = false
	var ended := _run(relay, a, b, func(): return a.phase == OnlineSession.Phase.ENDED and b.phase == OnlineSession.Phase.ENDED, 600)
	check(ended, "both ended")
	check(a.end_message.contains("different") and b.end_message.contains("different"), "A: %s / B: %s" % [a.end_message, b.end_message])


func test_leaving_ends_the_match_for_the_opponent() -> void:
	var relay := FakeRelay.new()
	relay.latency = 2
	var p := _pair(relay)
	var a: OnlineSession = p[0]
	var b: OnlineSession = p[1]
	check(_run(relay, a, b, func(): return _both_running(a, b) and a.core.state.frame > 60, 2000), "running")
	a.close()
	check(_run(relay, a, b, func(): return b.phase == OnlineSession.Phase.ENDED, 30), "B noticed")
	check_eq(b.end_message, "Your opponent left.", "B's message")


func test_rematch_starts_a_fresh_match() -> void:
	var relay := FakeRelay.new()
	relay.latency = 2
	var p := _pair(relay)
	var a: OnlineSession = p[0]
	var b: OnlineSession = p[1]
	check(_run(relay, a, b, func(): return _both_running(a, b) and a.core.state.frame > 60, 2000), "first match running")
	var first_seed := a.core.seed
	a.rematch()
	check(_run(relay, a, b, func(): return b.phase == OnlineSession.Phase.ENDED, 30), "B told about the rematch")
	check(b.end_message.contains("rematch"), "B's message: " + b.end_message)
	b.rematch()
	check(_run(relay, a, b, func(): return _both_running(a, b) and a.core.final_frame() > 120 and a.checksums_verified > 0, 3000),
		"second match running (A: %d %s, B: %d %s)" % [a.phase, a.end_message, b.phase, b.end_message])
	if a.core != null and b.core != null:
		check(a.core.seed != first_seed, "a new seed")
		check_eq(a.core.seed, b.core.seed, "same seed")


func test_a_dead_link_ends_the_match() -> void:
	var relay := FakeRelay.new()
	relay.latency = 1
	var p := _pair(relay)
	var a: OnlineSession = p[0]
	var b: OnlineSession = p[1]
	check(_run(relay, a, b, func(): return _both_running(a, b) and a.core.state.frame > 60, 2000), "running")
	relay.blocked = true
	var saw_interrupted := false
	for i in OnlineSession.LOST_AFTER + 60:
		relay.step()
		a.tick(Buttons.NONE)
		b.tick(Buttons.NONE)
		saw_interrupted = saw_interrupted or a.interrupted
	check(saw_interrupted, "shown as interrupted first")
	check(a.phase == OnlineSession.Phase.ENDED and b.phase == OnlineSession.Phase.ENDED, "both ended")
	check_eq(a.end_message, "Lost the connection to your opponent.", "A's message")


func test_the_first_player_waits_for_the_second() -> void:
	var relay := FakeRelay.new()
	var a := OnlineSession.new("", "WAIT", 2, func(): return relay.socket("WAIT"))
	for i in 300:
		relay.step()
		a.tick(Buttons.NONE)
	check_eq(a.phase, OnlineSession.Phase.WAITING_FOR_OPPONENT, "waiting")
	for i in OnlineSession.KEEPALIVE_INTERVAL:
		relay.step()
		a.tick(Buttons.NONE)
	check(relay._sockets[0].texts_sent > 0, "a waiting player keeps its relay connection alive")
	check_eq(a.local_slot, 0, "first in is P1")
	var b := OnlineSession.new("", "WAIT", 2, func(): return relay.socket("WAIT"))
	check(_run(relay, a, b, func(): return _both_running(a, b) and b.core.state.frame > 30, 1000), "both running")
	check_eq(b.local_slot, 1, "second in is P2")


func test_a_third_player_is_turned_away() -> void:
	var relay := FakeRelay.new()
	var p := _pair(relay, "FULL")
	var c := OnlineSession.new("", "FULL", 2, func(): return relay.socket("FULL"))
	for i in 20:
		relay.step()
		p[0].tick(Buttons.NONE)
		p[1].tick(Buttons.NONE)
		c.tick(Buttons.NONE)
	check_eq(c.phase, OnlineSession.Phase.ENDED, "turned away")
	check(c.end_message.contains("already has two players"), c.end_message)


func test_different_input_delays_still_agree() -> void:
	var relay := FakeRelay.new()
	relay.latency = 3
	var a := OnlineSession.new("", "DLY", 1, func(): return relay.socket("DLY"))
	var b := OnlineSession.new("", "DLY", 3, func(): return relay.socket("DLY"))
	var ok := _run(relay, a, b, func(): return (a.core != null and b.core != null and a.core.final_frame() >= 300
		and b.core.final_frame() >= 300) or a.phase == OnlineSession.Phase.ENDED or b.phase == OnlineSession.Phase.ENDED, 3000)
	check(ok and _both_running(a, b), "running (A: %s, B: %s)" % [a.end_message, b.end_message])
	if ok and _both_running(a, b):
		_check_in_step(a, b, "delays 1 and 3")
