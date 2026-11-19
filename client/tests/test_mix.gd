extends TestCase
## Mixed fighters from the character creator (ArtLibrary's "mix" ids).


func test_mix_id_round_trips() -> void:
	var id := ArtLibrary.make_mix("rajat", "shahn", "akshat", "suhani", "  Rocky!.7 ")
	check_eq(id, "mix.rajat.shahn.akshat.suhani.Rocky7", "the name is cleaned")
	var m := ArtLibrary.mix_parts(id)
	check_eq(m.size(), 5, "five parts")
	check_eq(m[0], "rajat", "head")
	check_eq(m[3], "suhani", "moves")
	check_eq(ArtLibrary.look(id), "rajat", "a mix looks like its head")
	check_eq(ArtLibrary.look("shahn"), "shahn", "a fighter looks like itself")
	check_eq(ArtLibrary.fighter_name(id), "Rocky7", "its name")
	check_eq(ArtLibrary.fighter_name(ArtLibrary.make_mix("rajat", "rajat", "rajat", "rajat", "")), "Custom", "no name")


func test_bad_mix_ids_are_rejected() -> void:
	for bad in ["mix.rajat.shahn.akshat", "mix.rajat.shahn.akshat.nobody.X", "mix.rajat.shahn.akshat.suhani.X.Y", "rajat.shahn.akshat.suhani.X.Y"]:
		check(ArtLibrary.mix_parts(bad).is_empty(), "%s is not a mix" % bad)
		check(ArtLibrary.character(bad).is_empty(), "%s has no rig" % bad)


func test_mix_rig_takes_each_piece_from_its_fighter() -> void:
	var rig := ArtLibrary.character("mix.rajat.shahn.akshat.suhani.X")
	var parts: Dictionary = rig.get("parts", {})
	for part in ["head", "backHand", "frontHand", "backFoot", "frontFoot"]:
		check(parts.has(part), "has %s" % part)
	check(str(parts["head"]["textures"]["neutral"]).begins_with("rajat/"), "Rajat's head")
	check(str(parts["frontHand"]["textures"]["normal"]).begins_with("shahn/"), "Shan's hands")
	check(str(parts["frontFoot"]["textures"]["normal"]).begins_with("akshat/"), "Akshat's feet")
	check(parts.has("secondary") and str(parts["secondary"].get("parent", "")) == "head", "the head brings its hat")
	check(rig["clips"] == ArtLibrary.character("suhani")["clips"], "Suhani's moves")
	check(ArtLibrary.character_texture("mix.rajat.shahn.akshat.suhani.X", str(parts["head"]["textures"]["neutral"])) != null, "the head's picture loads")
	# The fighters' own rigs are untouched by the copy.
	check(not str(ArtLibrary.character("rajat")["parts"]["head"]["textures"]["neutral"]).contains("/"), "Rajat's rig is unchanged")
