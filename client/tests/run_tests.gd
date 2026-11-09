extends SceneTree
## Headless test runner:
##   godot --headless --path client -s res://tests/run_tests.gd
## Exit code 0 = every test passed. Tests are the test_* methods of the scripts in TEST_SCRIPTS;
## each gets a fresh instance and reports failures through check()/fail().

const TEST_SCRIPTS := [
	"res://tests/test_golden_traces.gd",
	"res://tests/test_rollback.gd",
	"res://tests/test_protocol.gd",
]


func _initialize() -> void:
	var failed := 0
	var passed := 0
	var only := OS.get_environment("RANGBAAZI_TEST")
	for path in TEST_SCRIPTS:
		var script: GDScript = load(path)
		if script == null:
			printerr("FAIL could not load %s" % path)
			failed += 1
			continue
		for method in script.get_script_method_list():
			var name: String = method["name"]
			if not name.begins_with("test_") or (only != "" and not name.contains(only)):
				continue
			var case: RefCounted = script.new()
			var started := Time.get_ticks_msec()
			case.call(name)
			var errors: Array = case.get("errors")
			var ms := Time.get_ticks_msec() - started
			if errors.is_empty():
				passed += 1
				print("PASS %s (%d ms)" % [name, ms])
			else:
				failed += 1
				printerr("FAIL %s: %s" % [name, "\n    ".join(errors)])
	print("%d passed, %d failed" % [passed, failed])
	quit(1 if failed > 0 else 0)
