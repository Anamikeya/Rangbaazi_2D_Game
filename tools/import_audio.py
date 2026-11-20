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
music and ambience. Re-run after the packs' audio changes, then run Godot's --import.
"""
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
    # Each weapon's attack (game weapon id: Weapons in client/sim), and the cherry bomb's burst.
    "weapon_1.wav": ("round-brawler-v3", "audio/weapons/w011-primary.wav"),  # belan
    "weapon_2.wav": ("round-brawler-v3", "audio/weapons/w031-primary.wav"),  # baseball bat
    "weapon_3.wav": ("round-brawler-v3", "audio/weapons/w006-primary.wav"),  # bowling ball
    "weapon_4.wav": ("round-brawler-v3", "audio/weapons/w061-primary.wav"),  # shotgun
    "weapon_5.wav": ("round-brawler-v3", "audio/weapons/w005-primary.wav"),  # uzi
    "weapon_6.wav": ("round-brawler-v3", "audio/weapons/w008-primary.wav"),  # cherry bombs
    "weapon_7.wav": ("round-brawler-v3", "audio/weapons/w054-primary.wav"),  # bow
    "weapon_8.wav": ("round-brawler-v3", "audio/weapons/w055-primary.wav"),  # boomerang
    "weapon_9.wav": ("round-brawler-v3", "audio/weapons/w007-primary.wav"),  # trampoline
    "weapon_10.wav": ("round-brawler-v3", "audio/weapons/w070-primary.wav"),  # mines
    "weapon_11.wav": ("round-brawler-v3", "audio/weapons/w068-primary.wav"),  # homing missile
    "weapon_12.wav": ("round-brawler-v3", "audio/weapons/w098-primary.wav"),  # black-hole bomb
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


def put(src, dst, loop=False):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(src, dst)
    with open(dst + ".import", "w", encoding="utf-8", newline="\n") as f:
        f.write(IMPORT.format(loop=1 if loop else 0))


def main():
    art = sys.argv[1]
    v6 = os.path.join(art, "round-brawler-v6")
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = os.path.join(root, "client", "audio")
    if os.path.exists(out):
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
    for name, (pack, rel) in SFX.items():
        put(os.path.join(art, pack, rel), os.path.join(out, "sfx", name))
        count += 1
    size = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(out) for f in fs if f.endswith(".wav"))
    print(f"{count} sounds, {size / 1e6:.1f} MB of WAV in client/audio (compressed on import)")


if __name__ == "__main__":
    main()
