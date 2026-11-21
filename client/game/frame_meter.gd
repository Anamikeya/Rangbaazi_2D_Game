class_name FrameMeter
extends RefCounted
## Frame-time meter for rooms (rooms v1, polish 6). It is fed the time of every drawn frame and,
## every WINDOW_USEC, has one log line ready: the average frame time and frame rate, the slowest
## frame, and how long the arena's own drawing took on average. main.gd prints it while a room's
## match is on, in every build, so a release APK can be measured with nothing on screen:
##   adb logcat -s godot:I | findstr /L "[perf]"
## (on the web the same line is in the browser's console). Presentation only: it never touches a
## GameState.

const WINDOW_USEC := 5_000_000 # one line every 5 s
const TAG := "[perf]"

var _start := -1
var _last := -1
var _frames := 0
var _slowest := 0
var _draw_usec := 0
var _draws := 0
# The window just completed: [span, frames, slowest, draw usec, draws].
var _done: Array = []


## Starts afresh (a new match, or the meter was paused).
func reset() -> void:
	_start = -1
	_last = -1
	_frames = 0
	_slowest = 0
	_draw_usec = 0
	_draws = 0


## One frame shown at `now_usec` (Time.get_ticks_usec), its arena drawing having taken `draw_usec`
## (0 if none). Returns true when a window is full: line() then describes it.
func frame(now_usec: int, draw_usec: int) -> bool:
	if _start < 0:
		_start = now_usec
		_last = now_usec
		return false
	var step := now_usec - _last
	_last = now_usec
	_frames += 1
	_slowest = maxi(_slowest, step)
	if draw_usec > 0:
		_draw_usec += draw_usec
		_draws += 1
	var span := now_usec - _start
	if span < WINDOW_USEC:
		return false
	_done = [span, _frames, _slowest, _draw_usec, _draws]
	_start = now_usec
	_frames = 0
	_slowest = 0
	_draw_usec = 0
	_draws = 0
	return true


## The log line for the window frame() last completed; `what` says what was measured ("room of 15
## fighters, big level, ...").
func line(what: String) -> String:
	if _done.is_empty():
		return ""
	return report(_done[0], _done[1], _done[2], _done[3], _done[4], what)


## The log line for `frames` frames over `span_usec`, the slowest `slowest_usec`, and `draws` arena
## draws taking `draw_usec` in all. Static so tests can check it.
static func report(span_usec: int, frames: int, slowest_usec: int, draw_usec: int, draws: int, what: String) -> String:
	var avg_ms := span_usec / 1000.0 / maxf(1.0, frames)
	var fps := frames * 1_000_000.0 / maxf(1.0, span_usec)
	var draw_ms := draw_usec / 1000.0 / maxf(1.0, draws)
	return "%s %s: %.1f ms a frame on average (%.1f fps), slowest %.1f ms, arena drawing %.2f ms" % [
		TAG, what, avg_ms, fps, slowest_usec / 1000.0, draw_ms]
