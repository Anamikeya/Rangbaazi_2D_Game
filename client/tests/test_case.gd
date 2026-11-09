class_name TestCase
extends RefCounted
## Base for the headless tests (see run_tests.gd): collect failures instead of stopping.

var errors: Array[String] = []


func check(condition: bool, message: String) -> bool:
	if not condition:
		errors.append(message)
	return condition


func check_eq(actual: Variant, expected: Variant, message: String) -> bool:
	return check(actual == expected, "%s: expected %s, got %s" % [message, str(expected), str(actual)])
