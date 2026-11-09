class_name RollbackCore
extends RefCounted
## Rollback netcode for a 1v1, independent of the network: the session feeds it local buttons and
## the opponent's messages; it keeps the match running ahead on predicted input and, when the
## opponent's real input differs from the guess, loads the saved state of that frame and
## re-simulates up to the present.
##
## The relay is a WebSocket, so inputs arrive complete and in order: no acks, resends or
## redundancy are needed (unlike UDP rollback libraries). What stays is the rollback itself,
## input delay, a prediction limit, time sync and the desync check.
##
## Frames: state.frame is the next frame to simulate. Each player's input for frame f is known to
## that player `input_delay` frames early; the first `input_delay` frames of each side are empty.

## Frames between checksums compared with the opponent.
const CHECK_INTERVAL := 30
## Saved states kept for rollback; must be more than prediction + 1.
const RING := 16

var rules: MatchRules
var state: GameState
var seed: int
var local_slot: int
var input_delay: int
var prediction: int

## Test hook: runs after every simulated frame, re-simulations included.
var after_advance: Callable

# Rollback stats (GDD KPI: median rollback 4 frames).
var rollback_count := 0
var rollback_last := 0
var rollback_max := 0
var _rollback_hist := PackedInt32Array()

var checksums_verified := 0
var last_verified_frame := -1
var desync_frame := -1

var _local := PackedInt32Array()        # local input by frame
var _remote := PackedInt32Array()       # opponent input by frame; frames 0..size-1 are known
var _used_remote := PackedInt32Array()  # the opponent input used the last time each frame was simulated
var _ring: Array[GameState] = []
var _ring_frame := PackedInt32Array()
var _rollback_from := -1

var _my_checks := {}      # frame -> checksum, for final frames
var _their_checks := {}   # frame -> checksum the opponent sent, not compared yet
var _next_check := CHECK_INTERVAL

var _newest_sender_frame := -1
var _sync_samples := PackedFloat32Array()


func _init(rules_: MatchRules, seed_: int, local_slot_: int, input_delay_: int = 2, prediction_: int = 8) -> void:
	assert(local_slot_ == 0 or local_slot_ == 1)
	assert(prediction_ >= 1 and prediction_ + 1 < RING)
	rules = rules_
	seed = seed_
	local_slot = local_slot_
	input_delay = maxi(0, input_delay_)
	prediction = prediction_
	state = Simulation.create_initial_state(rules, 2, seed)
	for i in RING:
		_ring.append(GameState.new(2, 0))
	_ring_frame.resize(RING)
	_ring_frame.fill(-1)
	_rollback_hist.resize(RING + 1)
	# The first input_delay frames have no local input: they are empty on both sides.
	for f in input_delay:
		_local.append(Buttons.NONE)


## The empty inputs this side starts with; send them first, so the opponent's input stream starts at frame 0.
func padding() -> PackedInt32Array:
	return _local.duplicate()


## Frames whose opponent input is known (frames 0 .. remote_frames - 1).
func remote_frames() -> int:
	return _remote.size()


## The newest frame whose state is final here: every input before it is known, and it has been simulated.
func final_frame() -> int:
	return mini(_remote.size(), state.frame)


## False while the match is as far ahead of the opponent's known input as prediction allows.
func can_advance() -> bool:
	return state.frame - _remote.size() < prediction


## Records the local buttons for frame state.frame + input_delay and returns that frame (send it).
## Call exactly once before each advance().
func add_local_input(buttons: int) -> int:
	var frame := _local.size()
	assert(frame == state.frame + input_delay, "add_local_input once per advance")
	_local.append(buttons & Buttons.KNOWN)
	return frame


## Simulates state.frame with the local input and the opponent's (known, or predicted: the last known).
func advance() -> void:
	var f := state.frame
	_save(f)
	var theirs := _remote[f] if f < _remote.size() else (_remote[_remote.size() - 1] if not _remote.is_empty() else Buttons.NONE)
	if _used_remote.size() <= f:
		_used_remote.resize(f + 1)
	_used_remote[f] = theirs
	var inputs := PackedInt32Array([0, 0])
	inputs[local_slot] = _local[f]
	inputs[1 - local_slot] = theirs
	Simulation.advance(state, rules, inputs)
	if after_advance.is_valid():
		after_advance.call(state)


## The opponent's buttons for `frame`. False if frames arrive out of order (a broken stream).
func receive_remote(frame: int, buttons: int, sender_frame: int, their_advantage: int) -> bool:
	if frame < _remote.size():
		return true # already known
	if frame > _remote.size():
		return false
	_remote.append(buttons & Buttons.KNOWN)
	if frame < state.frame and _used_remote[frame] != _remote[frame]:
		if _rollback_from < 0 or frame < _rollback_from:
			_rollback_from = frame
	# Time sync sample: how far ahead this side runs, with the network delay cancelled out
	# (both sides see the same delay, in opposite directions).
	if sender_frame > _newest_sender_frame:
		_newest_sender_frame = sender_frame
		var mine := state.frame - sender_frame
		_sync_samples.append((mine - their_advantage) / 2.0)
		if _sync_samples.size() > 32:
			_sync_samples.remove_at(0)
	return true


## This side's frame minus the newest frame the opponent reported (sent with each input).
func local_advantage() -> int:
	return state.frame - _newest_sender_frame if _newest_sender_frame >= 0 else 0


## How many ticks to sit out so the opponent catches up (0 unless this side is clearly ahead).
func recommended_skip() -> int:
	if _sync_samples.size() < 16:
		return 0
	var total := 0.0
	for s in _sync_samples:
		total += s
	var ahead := total / _sync_samples.size()
	if ahead < 1.5:
		return 0
	_sync_samples.clear()
	return mini(int(ahead), 6)


## Re-simulates from the earliest frame whose opponent input was mispredicted. Call after
## receiving messages and before advancing.
func apply_rollback() -> void:
	if _rollback_from < 0:
		return
	var from := _rollback_from
	_rollback_from = -1
	var to := state.frame
	var slot := from % RING
	assert(_ring_frame[slot] == from, "rollback beyond the saved states")
	state.copy_from(_ring[slot])
	for f in range(from, to):
		advance()
	var depth := to - from
	rollback_count += 1
	rollback_last = depth
	rollback_max = maxi(rollback_max, depth)
	_rollback_hist[mini(depth, _rollback_hist.size() - 1)] += 1


func rollback_median() -> int:
	if rollback_count == 0:
		return 0
	var seen := 0
	for d in _rollback_hist.size():
		seen += _rollback_hist[d]
		if seen * 2 >= rollback_count:
			return d
	return _rollback_hist.size() - 1


## Checksums of frames that just became final (every CHECK_INTERVAL frames) as [frame, value]
## pairs, to send to the opponent. Call after advancing.
func new_checksums() -> Array:
	var out := []
	var final := final_frame()
	while _next_check <= final:
		var at := _state_at(_next_check)
		if at == null:
			push_warning("Checksum frame %d is no longer saved" % _next_check)
		else:
			var value := at.checksum()
			_my_checks[_next_check] = value
			out.append([_next_check, value])
		_next_check += CHECK_INTERVAL
	return out


func receive_checksum(frame: int, value: int) -> void:
	if _their_checks.size() < 256:
		_their_checks[frame] = value


## Compares the opponent's checksums with ours for frames final on both sides. Returns the first
## frame that differs (a desync), or -1.
func verify() -> int:
	for frame in _their_checks.keys():
		if not _my_checks.has(frame):
			if frame <= final_frame() - CHECK_INTERVAL * 8:
				_their_checks.erase(frame) # we never checked that frame; nothing to compare
			continue
		var same: bool = _my_checks[frame] == _their_checks[frame]
		_forget_old_checks()
		_their_checks.erase(frame)
		_my_checks.erase(frame)
		if not same:
			desync_frame = frame
			return frame
		checksums_verified += 1
		last_verified_frame = maxi(last_verified_frame, frame)
	return -1


## Both players' inputs for a frame whose inputs are all known (for replays and tests).
func inputs_for(frame: int) -> PackedInt32Array:
	assert(frame < _local.size() and frame < _remote.size())
	var inputs := PackedInt32Array([0, 0])
	inputs[local_slot] = _local[frame]
	inputs[1 - local_slot] = _remote[frame]
	return inputs


## Replays the first `frames` frames offline from a fresh match with the final inputs.
func replay(frames: int) -> GameState:
	var s := Simulation.create_initial_state(rules, 2, seed)
	for f in frames:
		Simulation.advance(s, rules, inputs_for(f))
	return s


func _save(f: int) -> void:
	var slot := f % RING
	_ring[slot].copy_from(state)
	_ring_frame[slot] = f


func _forget_old_checks() -> void:
	var oldest := final_frame() - CHECK_INTERVAL * 16
	for frame in _my_checks.keys():
		if frame < oldest:
			_my_checks.erase(frame)


func _state_at(frame: int) -> GameState:
	if frame == state.frame:
		return state
	var slot := frame % RING
	return _ring[slot] if _ring_frame[slot] == frame else null
