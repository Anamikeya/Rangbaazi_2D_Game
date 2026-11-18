"""Copies the finished art pack into the game in a compact form.

    python tools/import_art.py <art pack folder>   (e.g. .../game-art/round-brawler-v6)

Writes client/assets/:
    characters/<id>/clips.json   the rig (parts, rest poses, texture slots, springs) and the 24 own
                                 moves plus any shared moves the fighter replaces, as compact arrays
    characters/<id>/*.wbin       the fighter's images (WebP, renamed so Godot doesn't re-import and
                                 re-encode them: the game decodes them itself, keeping them small)
    shared/clips.json            the 18 shared moves
    arenas/<id>/arena.json       the painted layers, the three platform kinds and which platform uses
                                 which (in the sim's platform order, see tools/gen_arena_stages.py)
    arenas/<id>/*.wbin           the arena's layer and platform images
    index.json                   the fighters (id, name, role) and arenas (stage id, folder, name)

Clip arrays: tracks part -> [[frame, x, y, rotation, scaleX, scaleY, opacity, easing index], ...],
textures part -> [[frame, slot], ...], order -> [[frame, [parts back to front]], ...]. Easing indexes
follow EASINGS below. Re-run after the art pack changes; it replaces client/assets.
"""
import json
import os
import shutil
import sys

EASINGS = ["linear", "step", "in", "out", "in_out", "out_back", "out_elastic"]
ARENA_ID = {"shahn": "rooftops"}


def r2(v):
    return round(float(v), 2)


def compact_clip(d):
    tracks = {}
    for part, keys in d.get("tracks", {}).items():
        tracks[part] = [[k["frame"], r2(k.get("x", 0)), r2(k.get("y", 0)), round(float(k.get("rotation", 0)), 4),
                         r2(k.get("scaleX", 1)), r2(k.get("scaleY", 1)), r2(k.get("opacity", 1)),
                         EASINGS.index(k.get("easing", "linear")) if k.get("easing", "linear") in EASINGS else 0]
                        for k in keys]
    textures = {}
    for t in d.get("textures", []):
        textures[t["part"]] = [[k["frame"], k.get("slot", k.get("texture"))] for k in t.get("keys", [])]
    order = [[o["frame"], o["parts"]] for o in d.get("drawOrder", [])]
    return {"d": d["durationFrames"], "loop": bool(d.get("loop", False)), "mode": d.get("mode", "replace"),
            "pm": 1 if d.get("positionMode") == "rest_offset" else (2 if d.get("positionMode") == "delta" else 0),
            "tracks": tracks, "tex": textures, "order": order}


def copy_image(pack, rel, out_dir, used):
    """Copies an image (prefers the art/ WebP of a PNG part) and returns its .wbin name."""
    src = os.path.join(pack, rel)
    base = os.path.splitext(os.path.basename(rel))[0]
    webp = os.path.join(os.path.dirname(src), "art", base + ".webp")
    if rel.endswith(".png") and os.path.exists(webp):
        src = webp
    if not os.path.exists(src):
        return None
    name = base + ".wbin"
    if name in used and used[name] != src:
        name = base + "-" + str(len(used)) + ".wbin"
    used[name] = src
    shutil.copyfile(src, os.path.join(out_dir, name))
    return name


def import_character(pack, cid, out_root):
    out = os.path.join(out_root, "characters", cid)
    os.makedirs(out, exist_ok=True)
    rig = json.load(open(os.path.join(pack, "characters", cid, "rig.json"), encoding="utf-8"))
    used = {}
    parts = {}
    for pname, p in rig["parts"].items():
        q = {k: p[k] for k in ("width", "height", "pivot", "rest", "z", "parent", "spring", "textureGeometry") if k in p}
        slots = {}
        for slot, rel in (p.get("textures") or {}).items():
            slots[slot] = copy_image(pack, rel, out, used) if rel else None
        tex = p.get("texture")
        if tex and tex not in slots and "/" in str(tex):
            slots["normal"] = copy_image(pack, tex, out, used)
            tex = "normal"
        q["textures"] = slots
        q["texture"] = tex
        parts[pname] = q
    clips = {}
    anim_dir = os.path.join(pack, "characters", cid, "anim")
    for f in sorted(os.listdir(anim_dir)):
        if f.endswith(".json"):
            clips[f[:-5]] = compact_clip(json.load(open(os.path.join(anim_dir, f), encoding="utf-8")))
    data = {"id": cid, "parts": parts, "clips": clips}
    json.dump(data, open(os.path.join(out, "clips.json"), "w", encoding="utf-8"), separators=(",", ":"))


def import_arena(pack, folder, out_root, level):
    sid = ARENA_ID.get(folder, folder)
    out = os.path.join(out_root, "arenas", sid)
    os.makedirs(out, exist_ok=True)
    art = json.load(open(os.path.join(pack, "levels", folder, "arena-art.json"), encoding="utf-8"))
    used = {}
    layers = []
    for layer in art.get("layers", []):
        name = copy_image(pack, layer["file"], out, used)
        if name:
            layers.append({"file": name, "parallax": layer.get("parallax", 1), "z": layer.get("z", 0),
                           "x": layer.get("x", 0), "y": layer.get("y", 0), "w": layer.get("width", 1280), "h": layer.get("height", 720)})
    pieces = {}
    for kind, piece in (art.get("platformPieces") or {}).items():
        pieces[kind] = {"cap": piece.get("capWidth", 20), "tile": piece.get("tileWidth", 72),
                        "left": copy_image(pack, piece["left"], out, used),
                        "middle": copy_image(pack, piece["middle"], out, used),
                        "right": copy_image(pack, piece["right"], out, used)}
    ids = [p["id"] for p in level["layouts"][0]["platforms"]]
    assign = art.get("platformAssignments") or {}
    data = {"id": sid, "title": level["title"], "sky": art.get("sky", "#1a1626"), "layers": layers, "pieces": pieces,
            "platforms": [assign.get(i, "steps") for i in ids], "platformIds": ids}
    json.dump(data, open(os.path.join(out, "arena.json"), "w", encoding="utf-8"), separators=(",", ":"))
    return sid


# Names the user set, by id (the ids and art folders keep the packs' spelling):
#   the Portal Ghost (id "akmal") is Simran, the cat Disc Duelist (id "simran") is Aastha,
#   the Slime Trickster (id "chandan") is Akhmal, the Precision Ace (id "artha") is Chandan.
NAME_FIX = {"shahn": "Shan", "saklan": "Saqlain", "avipray": "Abhipray",
            "akmal": "Simran", "simran": "Aastha",
            "chandan": "Akhmal", "artha": "Chandan"}

# Each arena belongs to the fighter whose folder it shares, except where the user moved one:
# Akhmal (the Slime Trickster) owns Steel Courtyard, and Aastha (the cat) owns Slimeworks.
ARENA_OWNER = {"simran": "chandan", "chandan": "simran"}  # arena folder -> owner's character id


def main():
    pack = sys.argv[1]
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_root = os.path.join(root, "client", "assets")
    if os.path.exists(out_root):
        shutil.rmtree(out_root)
    os.makedirs(out_root)

    shared = {}
    for f in sorted(os.listdir(os.path.join(pack, "shared", "anim"))):
        if f.endswith(".json"):
            shared[f[:-5]] = compact_clip(json.load(open(os.path.join(pack, "shared", "anim", f), encoding="utf-8")))
    os.makedirs(os.path.join(out_root, "shared"))
    json.dump({"clips": shared}, open(os.path.join(out_root, "shared", "clips.json"), "w", encoding="utf-8"), separators=(",", ":"))

    # The cast list (names, roles) is characters.json, in this pack or the v3 pack beside it.
    cast_file = os.path.join(pack, "characters.json")
    if not os.path.exists(cast_file):
        cast_file = os.path.join(os.path.dirname(os.path.abspath(pack)), "round-brawler-v3", "characters.json")
    cast = json.load(open(cast_file, encoding="utf-8"))["characters"]
    fighters = []
    for c in cast:
        c["name"] = NAME_FIX.get(c["id"], c["name"])
        if os.path.exists(os.path.join(pack, "characters", c["id"], "rig.json")):
            import_character(pack, c["id"], out_root)
            fighters.append({"id": c["id"], "name": c["name"], "role": c.get("role", ""), "type": c.get("type", "")})

    arenas = []
    for folder in sorted(os.listdir(os.path.join(pack, "levels"))):
        lp = os.path.join(pack, "levels", folder, "level.json")
        if os.path.exists(lp) and os.path.exists(os.path.join(pack, "levels", folder, "arena-art.json")):
            level = json.load(open(lp, encoding="utf-8"))
            sid = import_arena(pack, folder, out_root, level)
            arenas.append({"id": sid, "folder": folder, "title": level["title"], "owner": ARENA_OWNER.get(folder, folder)})

    json.dump({"fighters": fighters, "arenas": arenas, "easings": EASINGS},
              open(os.path.join(out_root, "index.json"), "w", encoding="utf-8"), indent=1)
    total = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(out_root) for f in fs)
    print(f"{len(fighters)} fighters, {len(arenas)} arenas, {total / 1e6:.1f} MB in client/assets")


if __name__ == "__main__":
    main()
