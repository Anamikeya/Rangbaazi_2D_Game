"""Copies the art packs' original sound into the game.

    python tools/import_audio.py <game-art folder>   (the folder holding round-brawler-v3 and -v6)

All of it is the packs' own procedural synthesis (no third-party recordings; see the packs'
audio-manifest.json "provenance"). Writes client/audio/:
    music/<arena folder>.wav      one loop per arena (v6)
    ambience/<arena folder>.wav   one loop per arena (v6)
    foley/<fighter>.step.wav, foley/<fighter>.swish.wav   each fighter's footstep and whoosh (v6)
    cheer/<fighter>.wav           each fighter's victory jingle (v6)
    sfx/*.wav                     hit, land, jump, KO, parry, warning, click... (v3 shared cues, v6 parry),
                                  weapon_<id>.wav for each weapon's attack and boom.wav
with a .import beside each so Godot compresses it (QOA, about a fifth of the WAV size) and loops the
music and ambience. Which weapons, and which pack key each one is, comes from tools/weapons.json
(the weapon table's source): every row with a `key` gets weapon_<id>.wav from the pack's
audio/weapons/<key>-primary.wav. The hidden rows have no key and no sound of their own.

A sound that was imported before keeps its .import file (Godot's uid and settings in it); only a
new sound gets a fresh stub. Re-run after the packs' audio changes or after new weapons, then run
Godot's --import (godot --headless --path client --import) so the new ones are filled in.
"""
import json
import os
import shutil
import sys

SWISH_WORDS = ("swish", "whoosh", "swing", "air", "whisper", "whirr", "hiss", "hum")
SFX = {
    # game name: (pack, path)
    "hit.wav": ("round-brawler-v3", "audio/weapons/w001-primary.wav"),
    "hurt.wav": ("round-brawler-v3", "audio/ui/hurt.wav"),
    "big_hit.wav": ("round-brawler-v3", "audio/ui/break.wav"),
    "jump.wav": ("round-brawler-v3", "audio/ui/jump.wav"),
    "land.wav": ("round-brawler-v3", "audio/ui/land.wav"),
    "ko.wav": ("round-brawler-v3", "audio/ui/defeat.wav"),
    "warning.wav": ("round-brawler-v3", "audio/ui/warning.wav"),
    "click.wav": ("round-brawler-v3", "audio/ui/switch.wav"),
    "pickup.wav": ("round-brawler-v3", "audio/ui/pickup.wav"),
    "guts.wav": ("round-brawler-v3", "audio/ui/ignite.wav"),
    "parry.wav": ("round-brawler-v6", "audio/parry.wav"),
    # The cherry bomb's burst: every burst plays it. Each weapon's own sound is added in weapon_sounds().
    "boom.wav": ("round-brawler-v3", "audio/weapons/w008-secondary.wav"),
}

IMPORT = """[remap]

importer="wav"
type="AudioStreamWAV"

[params]

force/8_bit=false
force/mono=false
force/max_rate=false
force/max_rate_hz=44100
edit/trim=false
edit/normalize=false
edit/loop_mode={loop}
edit/loop_begin=0
edit/loop_end=-1
compress/mode=2
"""


# The .import files that were there before this run, by path: put() writes them back.
KEPT = {}


def put(src, dst, loop=False):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(src, dst)
    with open(dst + ".import", "w", encoding="utf-8", newline="\n") as f:
        f.write(KEPT.get(dst + ".import") or IMPORT.format(loop=1 if loop else 0))


def weapon_sounds(root):
    """weapon_<id>.wav for every row of tools/weapons.json that has a pack key (a hidden row's is empty)."""
    with open(os.path.join(root, "tools", "weapons.json"), encoding="utf-8") as f:
        rows = json.load(f)
    return {f"weapon_{r['id']}.wav": ("round-brawler-v3", f"audio/weapons/{r['key']}-primary.wav") for r in rows if r.get("key")}


def main():
    art = sys.argv[1]
    v6 = os.path.join(art, "round-brawler-v6")
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = os.path.join(root, "client", "audio")
    if os.path.exists(out):
        for d, _, fs in os.walk(out):
            for name in fs:
                if name.endswith(".import"):
                    with open(os.path.join(d, name), encoding="utf-8") as f:
                        KEPT[os.path.join(d, name)] = f.read()
        shutil.rmtree(out)
    count = 0
    audio = os.path.join(v6, "audio")
    for f in sorted(os.listdir(audio)):
        if f.endswith(".music.wav"):
            put(os.path.join(audio, f), os.path.join(out, "music", f.replace(".music", "")), loop=True)
            count += 1
        elif f.endswith(".ambience.wav"):
            put(os.path.join(audio, f), os.path.join(out, "ambience", f.replace(".ambience", "")), loop=True)
            count += 1
    for f in sorted(os.listdir(os.path.join(audio, "celebrations"))):
        put(os.path.join(audio, "celebrations", f), os.path.join(out, "cheer", f))
        count += 1
    foley = os.path.join(audio, "foley")
    by_fighter = {}
    for f in sorted(os.listdir(foley)):
        fighter, rest = f.split(".", 1)
        by_fighter.setdefault(fighter, []).append(rest)
    for fighter, files in by_fighter.items():
        swish = next((r for r in files if any(w in r for w in SWISH_WORDS)), files[-1])
        step = next((r for r in files if r != swish), swish)
        put(os.path.join(foley, f"{fighter}.{swish}"), os.path.join(out, "foley", f"{fighter}.swish.wav"))
        put(os.path.join(foley, f"{fighter}.{step}"), os.path.join(out, "foley", f"{fighter}.step.wav"))
        count += 2
    for name, (pack, rel) in {**SFX, **weapon_sounds(root)}.items():
        put(os.path.join(art, pack, rel), os.path.join(out, "sfx", name))
        count += 1
    size = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(out) for f in fs if f.endswith(".wav"))
    print(f"{count} sounds, {size / 1e6:.1f} MB of WAV in client/audio (compressed on import)")


if __name__ == "__main__":
    main()
