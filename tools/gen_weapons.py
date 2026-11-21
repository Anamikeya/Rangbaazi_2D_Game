"""Generates the weapon table for both sims from tools/weapons.json.

    python tools/gen_weapons.py            write the generated files
    python tools/gen_weapons.py --check    exit 1 if a generated file is stale (writes nothing)

One object per weapon in tools/weapons.json, holding only the columns that differ from the
defaults (docs/design/combat-spec-v0.4-all-weapons.md, sections 1.1, 1.2 and 8). Written from it:

    src/Rangbaazi.Sim/WeaponTable.cs     WeaponId, ShotKind, the name constants, WeaponRow, WeaponTable.Rows
    client/sim/weapon_table.gd           the same rows as arrays of ints, in the same column order
    client/game/weapon_info.gd           names, verbs, shouts, clips, lengths, shot looks, nickname words (not read by the sims)

A row is ints only, in both languages:
  - a decimal becomes its raw 16.16 fixed-point value, truncated towards zero: int(Fraction(str(v)) * 65536),
    which is what Fix.FromRatio and Fx.ratio give for the same ratio;
  - a launch angle in whole degrees becomes round(cos x 10000) and round(sin x 10000) over 10000;
  - `size` and `blast` are stored as half their width;
  - a `spread` list goes into the shared SPREADS array, and the row holds where it starts and its length;
  - a name (use, flight, effect, ...) is its index in the lists below.

The file is refused, and nothing is written, when it breaks a rule in validate(). After changing
weapons.json: run this, then rewrite the goldens (the weapon_table.json golden holds one hash a row).
"""
import json
import math
import os
import re
import sys
from fractions import Fraction

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "tools", "weapons.json")
CS_PATH = os.path.join("src", "Rangbaazi.Sim", "WeaponTable.cs")
GD_PATH = os.path.join("client", "sim", "weapon_table.gd")
INFO_PATH = os.path.join("client", "game", "weapon_info.gd")

ONE = 65536

# The name columns. A name's number is its place in its list; both sims get the same constants.
NAMES = {
    "use": ["none", "swing", "blast", "shoot", "spray", "blink", "detonate", "cycle"],
    "flight": ["none", "roll", "bullet", "bomb", "arrow", "thrown", "boomerang", "pad", "mine", "missile", "hole",
               "lob", "zone", "wall", "speaker", "gate", "drop", "decoy"],
    "effect": ["none", "slow", "chill", "root", "stun", "slip", "poison", "burn", "mark", "disarm", "reel", "leech", "swap"],
    "sweet": ["none", "midattack", "tip", "pointblank", "strike", "streak", "direct", "longshot", "return",
              "airborne", "bounced", "back"],
    "defence": ["none", "bat", "break", "guard", "hold", "sweep"],
    "spend": ["use", "hit", "never"],
    "spreadBy": ["none", "burst", "hold", "widen", "pellet", "supply"],
    "scoop": ["none", "fling", "pull"],
    "leaves": ["none", "zone", "wall", "pad"],
}

# The two per-flight flags (spec 1.3).
FLIES = {"roll", "bullet", "bomb", "arrow", "thrown", "boomerang", "missile", "hole", "lob"}
PLACED = {"pad", "mine", "zone", "wall", "speaker", "gate", "decoy"}

FLIGHT_DOCS = {
    "roll": "Along the ground, off ledges, through the fighters it hits (the bowling ball).",
    "bullet": "Straight and fast, through platforms, gone on its first hit.",
    "bomb": "Lobbed, lands on platforms, has a fuse, bursts.",
    "arrow": "Straight with its own gravity, through platforms; pierces if its row says so.",
    "thrown": "A thrown weapon: it hits once, or lands, and is gone.",
    "boomerang": "Flies out, slows, and comes back to its thrower's hand, hitting on both legs.",
    "pad": "Lies on the stage: anyone touching it is bounced up. It never hits.",
    "mine": "Armed after a moment, it goes off on whoever touches it.",
    "missile": "Flies and steers at the nearest fighter who isn't its owner, and bursts.",
    "hole": "Lands, pulls everyone near it in, then bursts.",
    "lob": "Arcs, lands on platforms, hits on touch, bounces if its row says so.",
    "zone": "An area on the ground that applies its row's effect to everyone in it.",
    "wall": "A solid block: pushes fighters out, stops shots, breaks.",
    "speaker": "Stands and pulses a hit in front of it.",
    "gate": "One of a linked pair that fighters pass through.",
    "drop": "Hangs high, then falls through fighters.",
    "decoy": "Looks like a Prop Box.",
}

# The sweet-spot rules whose `sweetArg` is a distance (fixed point); for the rest it is a whole number.
SWEET_DISTANCE = {"tip", "pointblank"}

# Presentation columns (client/game/weapon_info.gd), and columns only this file reads.
INFO_COLUMNS = {"key", "name", "verb", "shout", "clip", "words", "length", "shot"}
META_COLUMNS = {"batch"}
CLIPS = ["jab", "neutral_heavy", "side_heavy", "down_heavy"]
# How a row's shot is drawn when its flight's usual drawing would be wrong (a bullet is a streak, an
# arrow an arrow, anything else the weapon's own sprite): "spin" the weapon's sprite, spinning (a
# thrown star); "pebble" a small stone; "bolt" a glowing energy bolt; "rocket" a rocket with smoke
# (the launcher stays in the hand); "orb" a dark glowing ball (Dark Energy); "wave" a shockwave
# along the ground (the Thunder Hammer's).
SHOT_LOOKS = ["", "spin", "pebble", "bolt", "rocket", "orb", "wave"]
FISTS = {"name": "", "verb": "Kick", "words": ["Mukka", "Knuckle", "Thappad", "Fist"]}

# Move ids a row can't take: the fist moves (1 to 6) and the shared WeaponThrow (14).
RESERVED_MOVES = {0, 1, 2, 3, 4, 5, 6, 14}

# The table's columns, in order: (json key, kind, default, C# property, C# type).
#   int / fix / half / bool: a number as is, as 16.16 fixed point, as half of it in fixed point, a flag.
#   name: an index into NAMES[key].   Other kinds are worked out in column_values().
COLUMNS = [
    ("id", "int", 0, "Id", "WeaponId"),
    ("move", "int", 0, "Move", "MoveId"),
    ("credit", "credit", 0, "Credit", "WeaponId"),
    ("crate", "bool", True, "Crate", "bool"),
    ("weight", "int", 2, "Weight", "int"),
    ("built", "built", False, "Built", "bool"),
    ("use", "name", "none", "Use", "int"),
    ("s", "int", 0, "Startup", "int"),
    ("a", "int", 0, "Active", "int"),
    ("r", "int", 0, "Recovery", "int"),
    ("dmg", "int", 0, "Damage", "int"),
    ("kb", "fix", 0, "BaseKnockback", "Fix"),
    ("g", "fix", 0, "KnockbackGrowth", "Fix"),
    ("launchX", "angx", 35, "LaunchX", "Fix"),
    ("launchY", "angy", 35, "LaunchY", "Fix"),
    ("hs", "int", 0, "HitstunBonus", "int"),
    ("chip", "bool", False, "Chip", "bool"),
    ("soft", "bool", False, "Soft", "bool"),
    ("first", "bool", False, "First", "bool"),
    ("gbreak", "bool", False, "GuardBreak", "bool"),
    ("armorbreak", "bool", False, "ArmorBreak", "bool"),
    ("noSelf", "bool", False, "NoSelf", "bool"),
    ("boxNear", "box0", 0, "BoxNear", "Fix"),
    ("boxFar", "box1", 0, "BoxFar", "Fix"),
    ("boxBottom", "box2", 0, "BoxBottom", "Fix"),
    ("boxTop", "box3", 0, "BoxTop", "Fix"),
    ("both", "bool", False, "Both", "bool"),
    ("lungeAt", "int", 0, "LungeAt", "int"),
    ("lunge", "fix", 0, "Lunge", "Fix"),
    ("carry", "int", 100, "Carry", "int"),
    ("walk", "bool", False, "Walk", "bool"),
    ("supply", "int", 0, "Supply", "int"),
    ("spend", "name", "use", "Spend", "int"),
    ("throw", "int", 9, "Throw", "int"),
    ("interval", "int", 0, "Interval", "int"),
    ("burst", "int", 0, "Burst", "int"),
    ("burstMax", "int", 0, "BurstMax", "int"),
    ("pellets", "int", 1, "Pellets", "int"),
    ("spreadAt", "spreadAt", 0, "SpreadAt", "int"),
    ("spreadLen", "spreadLen", 0, "SpreadLen", "int"),
    ("spreadBy", "name", "none", "SpreadBy", "int"),
    ("flight", "name", "none", "Flight", "ShotKind"),
    ("vx", "fix", 0, "Vx", "Fix"),
    ("vy", "fix", 0, "Vy", "Fix"),
    ("grav", "fix", 0, "Gravity", "Fix"),
    ("life", "int", 0, "Life", "int"),
    ("fuse", "int", 0, "Fuse", "int"),
    ("size", "half", 0, "ShotHalf", "Fix"),
    ("blast", "half", 0, "BlastHalf", "Fix"),
    ("pierce", "bool", False, "Pierce", "bool"),
    ("maxHits", "int", 0, "MaxHits", "int"),
    ("bounces", "int", 0, "Bounces", "int"),
    ("keep", "int", 0, "Keep", "int"),
    ("edge", "int", 0, "Edge", "int"),
    ("touch", "bool", False, "Touch", "bool"),
    ("land", "bool", False, "Land", "bool"),
    ("feet", "bool", False, "Feet", "bool"),
    ("leave", "bool", False, "Leave", "bool"),
    ("rest", "bool", False, "Rest", "bool"),
    ("rehit", "int", 0, "Rehit", "int"),
    ("brk", "int", 0, "Break", "int"),
    ("turn", "fix", 0, "Turn", "Fix"),
    ("drag", "fix", 0, "Drag", "Fix"),
    ("padLaunch", "fix", 0, "PadLaunch", "Fix"),
    ("pullFrames", "int", 0, "PullFrames", "int"),
    ("reach", "fix", 0, "Reach", "Fix"),
    ("pull", "fix", 0, "Pull", "Fix"),
    ("leaves", "name", "none", "Leaves", "int"),
    ("zoneW", "fix", 0, "ZoneW", "Fix"),
    ("zoneH", "fix", 0, "ZoneH", "Fix"),
    ("zoneLife", "int", 0, "ZoneLife", "int"),
    ("hides", "bool", False, "Hides", "bool"),
    ("drift", "fix", 0, "Drift", "Fix"),
    ("lift", "fix", 0, "Lift", "Fix"),
    ("propDrift", "fix", 0, "PropDrift", "Fix"),
    ("armedOnly", "bool", False, "ArmedOnly", "bool"),
    ("blow", "bool", False, "Blow", "bool"),
    ("tickNear", "fix", 0, "TickNear", "Fix"),
    ("defence", "name", "none", "Defence", "int"),
    ("batMax", "int", 0, "BatMax", "int"),
    ("batCost", "int", 0, "BatCost", "int"),
    ("effect", "name", "none", "Effect", "int"),
    ("effectN", "int", 0, "EffectN", "int"),
    ("dotEvery", "dotEvery", 30, "DotEvery", "int"),
    ("sweetOnly", "bool", False, "SweetOnly", "bool"),
    ("sweet", "name", "none", "Sweet", "int"),
    ("sweetArg", "sweetArg", 0, "SweetArg", "long"),
    ("sweetX", "sweetx", 0, "SweetLaunchX", "Fix"),
    ("sweetY", "sweety", 0, "SweetLaunchY", "Fix"),
    ("sweetStun", "int", 0, "SweetStun", "int"),
    ("farDist", "fix", 0, "FarDist", "Fix"),
    ("farDmg", "int", 0, "FarDamage", "int"),
    ("scoop", "name", "none", "Scoop", "int"),
    ("fires", "int", 0, "Fires", "int"),
    ("thrownAs", "int", 0, "ThrownAs", "int"),
    ("cycleFirst", "int", 0, "CycleFirst", "int"),
    ("cycleCount", "int", 0, "CycleCount", "int"),
    ("cpuMin", "fix", 0, "CpuMin", "Fix"),
    ("cpuMax", "fix", 0, "CpuMax", "Fix"),
    ("cpuBurst", "int", 0, "CpuBurst", "int"),
]

# What a row in weapons.json may hold: the plain columns, plus the ones written in another form.
PLAIN = {c[0]: c for c in COLUMNS if c[1] in ("int", "fix", "half", "bool", "name")}
OTHER_JSON = {"ang": "int", "box": "list", "spread": "list", "sweetAng": "int", "sweetArg": "num", "credit": "int",
              "built": "bool", "dotEvery": "int"}
JSON_COLUMNS = set(PLAIN) | set(OTHER_JSON) | INFO_COLUMNS | META_COLUMNS


def fx(value):
    """A decimal as raw 16.16 fixed point, truncated towards zero (never floor: negatives would differ)."""
    return int(Fraction(str(value)) * ONE)


def half(value):
    return int(Fraction(str(value)) / 2 * ONE)


def angle(degrees):
    """A launch angle as the raw fixed-point (x, y) of round(cos x 10000) / 10000 and round(sin x 10000) / 10000."""
    rad = math.radians(degrees)
    return (int(Fraction(round(math.cos(rad) * 10000), 10000) * ONE),
            int(Fraction(round(math.sin(rad) * 10000), 10000) * ONE))


def self_test():
    """The rounding the twelve built weapons depend on. Run at every start."""
    assert fx(-0.15) == -9830, fx(-0.15)        # the shotgun's kick: Fix.FromRatio(-15, 100)
    assert fx(-0.0366) == -2398, fx(-0.0366)    # the Uzi's spread: Fix.FromRatio(-366, 10000)
    assert fx(0.0366) == 2398 and fx(0.3) == 19660 and half(0.6) == 19660 and half(2.4) == 78643
    assert angle(35) == (int(Fraction(8192, 10000) * ONE), int(Fraction(5736, 10000) * ONE))
    assert angle(30) == (int(Fraction(8660, 10000) * ONE), 32768) and angle(60)[0] == 32768


def identifier(name):
    """The words of a weapon's name; a leading number moves to the end (".357 Magnum" is Magnum 357)."""
    tokens = re.findall(r"[A-Za-z]+|\d+", name)
    if tokens and tokens[0].isdigit():
        tokens = tokens[1:] + tokens[:1]
    return tokens


def cs_name(name):
    return "".join(t[0].upper() + t[1:] for t in identifier(name))


def gd_name(name):
    return "_".join(t.upper() for t in identifier(name))


def gd_const(key):
    """`spreadBy` -> `SPREAD_BY`."""
    return re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", key).upper()


class Refused(ValueError):
    """weapons.json breaks a rule; nothing is written."""


def is_number(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def validate(rows):
    """Raises Refused on the first broken rule (spec 1.1)."""
    if not isinstance(rows, list) or not rows:
        raise Refused("weapons.json must be a list of rows")
    ids, moves = set(), set()
    for n, r in enumerate(rows):
        where = f"row {r.get('id', '?')} ({r.get('name', '?')})"
        for k, v in r.items():
            if k not in JSON_COLUMNS:
                raise Refused(f"{where}: unknown column '{k}'")
            if k in NAMES:
                if v not in NAMES[k]:
                    raise Refused(f"{where}: '{v}' is not a {k}")
            elif k in PLAIN:
                kind = PLAIN[k][1]
                if kind == "bool" and not isinstance(v, bool):
                    raise Refused(f"{where}: '{k}' must be true or false")
                if kind == "int" and (not isinstance(v, int) or isinstance(v, bool)):
                    raise Refused(f"{where}: '{k}' must be a whole number")
                if kind in ("fix", "half") and not is_number(v):
                    raise Refused(f"{where}: '{k}' must be a number")
            elif k in OTHER_JSON:
                kind = OTHER_JSON[k]
                if kind == "int" and (not isinstance(v, int) or isinstance(v, bool)):
                    raise Refused(f"{where}: '{k}' must be a whole number")
                if kind == "num" and not is_number(v):
                    raise Refused(f"{where}: '{k}' must be a number")
                if kind == "bool" and not isinstance(v, bool):
                    raise Refused(f"{where}: '{k}' must be true or false")
                if kind == "list" and (not isinstance(v, list) or not all(is_number(x) for x in v)):
                    raise Refused(f"{where}: '{k}' must be a list of numbers")
        for k in ("id", "move", "name", "use"):
            if k not in r:
                raise Refused(f"{where}: no '{k}'")
        if not isinstance(r["name"], str) or not identifier(r["name"]):
            raise Refused(f"{where}: a name needs a letter or a digit")
        if r["id"] != n + 1:
            raise Refused(f"{where}: ids must run 1, 2, 3, ... in file order (the table is indexed by id)" if r["id"] not in ids
                          else f"{where}: id {r['id']} is used twice")
        if r["id"] > 255:
            raise Refused(f"{where}: an id must fit a byte")
        ids.add(r["id"])
        if r["move"] > 255 or r["move"] in RESERVED_MOVES or r["move"] < 0:
            raise Refused(f"{where}: move {r['move']} must be 7 to 255 and not 14 (a fist move or the throw)")
        if r["move"] in moves:
            raise Refused(f"{where}: move {r['move']} is used twice")
        moves.add(r["move"])
        if r.get("supply", 0) > 127 or r.get("supply", 0) < 0:
            raise Refused(f"{where}: supply must be 0 to 127 (a signed byte in STATE)")
        if r.get("interval", 0) > 0 and r["interval"] >= r.get("a", 0) + r.get("r", 0):
            raise Refused(f"{where}: interval {r['interval']} must be smaller than a + r, or the move ends before it repeats")
        if "box" in r and len(r["box"]) != 4:
            raise Refused(f"{where}: box is near, far, bottom, top")
        if ("spread" in r) != ("spreadBy" in r) or ("spread" in r and not r["spread"]):
            raise Refused(f"{where}: spread and spreadBy go together")
        if r.get("first") and r.get("both"):
            raise Refused(f"{where}: first and both can't go together")
        leaves = r.get("leaves", "none")
        if leaves in ("zone", "wall") and not (r.get("zoneW", 0) > 0 and r.get("zoneH", 0) > 0 and r.get("zoneLife", 0) > 0):
            raise Refused(f"{where}: leaves {leaves} needs zoneW, zoneH and zoneLife")
        if leaves == "pad" and not (r.get("size", 0) > 0 and r.get("zoneLife", 0) > 0):
            raise Refused(f"{where}: leaves pad needs size and zoneLife")
        if "ang" in r and not 0 <= r["ang"] <= 90:
            raise Refused(f"{where}: ang is 0 to 90 whole degrees")
        if "sweetAng" in r and not 0 <= r["sweetAng"] <= 90:
            raise Refused(f"{where}: sweetAng is 0 to 90 whole degrees")
        if "clip" in r and r["clip"] not in CLIPS:
            raise Refused(f"{where}: '{r['clip']}' is not a clip")
        if "shot" in r and r["shot"] not in SHOT_LOOKS:
            raise Refused(f"{where}: '{r['shot']}' is not a shot look")
        if "words" in r and (not isinstance(r["words"], list) or len(r["words"]) != 4):
            raise Refused(f"{where}: words is four nickname words")
    for r in rows:
        where = f"row {r['id']} ({r['name']})"
        for k in ("fires", "thrownAs", "credit"):
            if k in r and r[k] not in ids:
                raise Refused(f"{where}: {k} {r[k]} is not a row")
        if ("cycleFirst" in r) != ("cycleCount" in r):
            raise Refused(f"{where}: cycleFirst and cycleCount go together")
        for k in range(r.get("cycleCount", 0)):
            if r["cycleFirst"] + k not in ids:
                raise Refused(f"{where}: cycle row {r['cycleFirst'] + k} is not a row")
    names = [cs_name(r["name"]) for r in rows]
    for n in names:
        if names.count(n) > 1 or n == "None":
            raise Refused(f"two rows would both be WeaponId.{n}")


def column_values(r, spreads):
    """One row as ints, in COLUMNS order. Appends its spread list to `spreads`."""
    out = []
    for key, kind, default, _, _ in COLUMNS:
        if kind == "int":
            v = r.get(key, default)
        elif kind == "fix":
            v = fx(r.get(key, default))
        elif kind == "half":
            v = half(r.get(key, default))
        elif kind == "bool":
            v = 1 if r.get(key, default) else 0
        elif kind == "name":
            v = NAMES[key].index(r.get(key, default))
        elif kind == "credit":
            v = r.get("credit", r.get("id", 0))
        elif kind == "built":
            v = 1 if r.get("built", r.get("batch", 1) == 0) else 0
        elif kind in ("angx", "angy"):
            v = angle(r.get("ang", default))[0 if kind == "angx" else 1]
        elif kind.startswith("box"):
            v = fx(r.get("box", [0, 0, 0, 0])[int(kind[3])])
        elif kind == "spreadAt":
            v = len(spreads) if "spread" in r else 0
            spreads.extend(fx(x) for x in r.get("spread", []))
        elif kind == "spreadLen":
            v = len(r.get("spread", []))
        elif kind == "dotEvery":
            v = r.get("dotEvery", 15 if r.get("effect") == "burn" else default)
        elif kind == "sweetArg":
            v = r.get("sweetArg", 0)
            v = fx(v) if r.get("sweet") in SWEET_DISTANCE else int(v)
        elif kind in ("sweetx", "sweety"):
            v = angle(r["sweetAng"])[0 if kind == "sweetx" else 1] if "sweetAng" in r else 0
        else:
            raise AssertionError(kind)
        out.append(v)
    return out


def build(rows):
    """(table rows as int lists, with the empty row 0 first; the shared spread list)."""
    validate(rows)
    spreads = []
    none = {"id": 0, "move": 0, "name": "None", "use": "none", "crate": False, "batch": 1}
    table = [column_values(none, spreads)]
    for r in rows:
        table.append(column_values(r, spreads))
    return table, spreads


HEADER = "Generated by tools/gen_weapons.py from tools/weapons.json. Do not edit: change the json and rerun it."


def write_cs(rows, table, spreads):
    o = []
    o.append("// <auto-generated>")
    o.append(f"// {HEADER}")
    o.append("// </auto-generated>")
    o.append("using Rangbaazi.Sim.FixedMath;")
    o.append("")
    o.append("namespace Rangbaazi.Sim;")
    o.append("")
    o.append("/// <summary>The weapons (docs/design/combat-spec-v0.4-all-weapons.md). 0 is empty-handed; a row's id is its number here.</summary>")
    o.append("public enum WeaponId : byte")
    o.append("{")
    o.append("    None,")
    for r in rows:
        o.append(f"    {cs_name(r['name'])},")
    o.append("}")
    o.append("")
    o.append("/// <summary>How a projectile prop behaves: a row's <c>flight</c>.</summary>")
    o.append("public enum ShotKind : byte")
    o.append("{")
    o.append("    None,")
    for f in NAMES["flight"][1:]:
        o.append(f"    /// <summary>{FLIGHT_DOCS[f]}</summary>")
        o.append(f"    {f[0].upper() + f[1:]},")
    o.append("}")
    for key, names in NAMES.items():
        if key == "flight":
            continue
        o.append("")
        o.append(f"/// <summary>A row's <c>{key}</c> column.</summary>")
        o.append(f"public static class Weapon{key[0].upper() + key[1:]}")
        o.append("{")
        for i, n in enumerate(names):
            o.append(f"    public const int {n[0].upper() + n[1:]} = {i};")
        o.append("}")
    o.append("")
    o.append("/// <summary>One weapon's fixed numbers: a row of the table. Immutable, outside the rollback state.</summary>")
    o.append("public sealed class WeaponRow")
    o.append("{")
    o.append("    private readonly long[] _columns;")
    o.append("")
    for key, kind, _, prop, typ in COLUMNS:
        o.append(f"    public {typ} {prop} {{ get; }}")
    o.append("")
    o.append("    /// <summary>The attack's frame data, built from the row.</summary>")
    o.append("    public MoveData MoveData { get; }")
    o.append("")
    o.append("    /// <summary>Every column as an int, in the table's order: what the table golden hashes.</summary>")
    o.append("    public System.ReadOnlySpan<long> Columns => _columns;")
    o.append("")
    o.append("    internal WeaponRow(long[] c)")
    o.append("    {")
    o.append("        _columns = c;")
    for i, (key, kind, _, prop, typ) in enumerate(COLUMNS):
        if typ == "Fix":
            o.append(f"        {prop} = Fix.FromRaw(c[{i}]);")
        elif typ == "bool":
            o.append(f"        {prop} = c[{i}] != 0;")
        elif typ == "long":
            o.append(f"        {prop} = c[{i}];")
        else:
            o.append(f"        {prop} = ({typ})c[{i}];")
    o.append("        MoveData = new MoveData")
    o.append("        {")
    o.append("            Startup = Startup, Active = Active, Recovery = Recovery,")
    o.append("            Damage = Damage, BaseKnockback = BaseKnockback, KnockbackGrowth = KnockbackGrowth,")
    o.append("            LaunchX = LaunchX, LaunchY = LaunchY, HitstunBonus = HitstunBonus,")
    o.append("            HitboxNear = BoxNear, HitboxFar = BoxFar, HitboxBottom = BoxBottom, HitboxTop = BoxTop,")
    o.append("            HitsBothSides = Both, ImpulseFrame = LungeAt, LungeSpeed = Lunge,")
    o.append("            Effect = Effect, EffectN = EffectN,")
    o.append("        };")
    o.append("    }")
    o.append("}")
    o.append("")
    o.append("/// <summary>The weapon table: one <see cref=\"WeaponRow\"/> per <see cref=\"WeaponId\"/>, row 0 empty.</summary>")
    o.append("public static class WeaponTable")
    o.append("{")
    o.append(f"    public const int Count = {len(table)};")
    o.append(f"    public const int ColumnCount = {len(COLUMNS)};")
    o.append("")
    o.append("    /// <summary>Every row's <c>spread</c> list, end to end; a row holds where its own starts and its length.</summary>")
    o.append("    public static readonly Fix[] Spreads =")
    o.append("    {")
    o.append("        " + " ".join(f"Fix.FromRaw({v})," for v in spreads))
    o.append("    };")
    o.append("")
    flights = NAMES["flight"]
    o.append("    private static readonly bool[] FliesFlags = { " + ", ".join("true" if f in FLIES else "false" for f in flights) + " };")
    o.append("    private static readonly bool[] PlacedFlags = { " + ", ".join("true" if f in PLACED else "false" for f in flights) + " };")
    o.append("")
    o.append("    /// <summary>A bat can return it, a guard or a parry destroys it, a wall stops it.</summary>")
    o.append("    public static bool Flies(ShotKind flight) => FliesFlags[(int)flight];")
    o.append("")
    o.append("    /// <summary>It counts towards its owner's placed-prop limit.</summary>")
    o.append("    public static bool Placed(ShotKind flight) => PlacedFlags[(int)flight];")
    o.append("")
    o.append("    public static readonly WeaponRow[] Rows =")
    o.append("    {")
    names = ["None"] + [r["name"] for r in rows]
    for name, values in zip(names, table):
        o.append(f"        new(new long[] {{ {', '.join(str(v) for v in values)} }}), // {name}")
    o.append("    };")
    o.append("}")
    return "\n".join(o) + "\n"


def write_gd(rows, table, spreads):
    o = []
    o.append("class_name WeaponTable")
    o.append(f"## {HEADER}")
    o.append("## The weapon table (WeaponTable.cs): one row of ints per weapon id, row 0 empty, columns in the")
    o.append("## reference's order. Read a value as ROWS[id][C_...]. Fix values are raw Fx ints.")
    o.append("")
    o.append(f"const COUNT := {len(table)}")
    o.append(f"const COLUMN_COUNT := {len(COLUMNS)}")
    o.append("")
    o.append("# Weapon ids.")
    o.append("const NONE := 0")
    for r in rows:
        o.append(f"const {gd_name(r['name'])} := {r['id']}")
    o.append("")
    o.append("# Columns.")
    for i, (key, _, _, _, _) in enumerate(COLUMNS):
        o.append(f"const C_{gd_const(key)} := {i}")
    for key, names in NAMES.items():
        o.append("")
        o.append(f"# A row's `{key}`.")
        for i, n in enumerate(names):
            o.append(f"const {gd_const(key)}_{n.upper()} := {i}")
    o.append("")
    flights = NAMES["flight"]
    o.append("## By flight. FLIES: a bat can return it, a guard or a parry destroys it, a wall stops it.")
    o.append("## PLACED: it counts towards its owner's placed-prop limit.")
    o.append("const FLIES := [" + ", ".join("true" if f in FLIES else "false" for f in flights) + "]")
    o.append("const PLACED := [" + ", ".join("true" if f in PLACED else "false" for f in flights) + "]")
    o.append("")
    o.append("## Every row's `spread` list, end to end; a row holds where its own starts and its length.")
    o.append("static var SPREADS := PackedInt64Array([" + ", ".join(str(v) for v in spreads) + "])")
    o.append("")
    o.append("static var ROWS: Array[PackedInt64Array] = [")
    names = ["None"] + [r["name"] for r in rows]
    for name, values in zip(names, table):
        o.append(f"\tPackedInt64Array([{', '.join(str(v) for v in values)}]), # {name}")
    o.append("]")
    return "\n".join(o) + "\n"


def gd_string(s):
    return json.dumps(s, ensure_ascii=False)


def write_info(rows):
    by_id = {r["id"]: r for r in rows}

    def shown(r, key, default):
        """A hidden row shows as the weapon it is a part of."""
        if key in r:
            return r[key]
        credit = by_id.get(r.get("credit", r["id"]), r)
        return credit.get(key, default) if credit is not r else default

    cols = [
        ("KEYS", [""] + [shown(r, "key", "") or shown(by_id.get(r.get("credit", r["id"]), r), "key", "") for r in rows]),
        ("NAMES", [FISTS["name"]] + [r["name"] for r in rows]),
        ("VERBS", [FISTS["verb"]] + [shown(r, "verb", "Use") for r in rows]),
        ("SHOUTS", [""] + [shown(r, "shout", "") for r in rows]),
        ("CLIPS", [""] + [shown(r, "clip", "side_heavy") for r in rows]),
        ("SHOTS", [""] + [r.get("shot", "") for r in rows]),
    ]
    o = []
    o.append("class_name WeaponInfo")
    o.append("extends RefCounted")
    o.append(f"## {HEADER}")
    o.append("## What the game shows for each weapon id (0 is fists): the pack key of its sprite and sound, its")
    o.append("## HUD name, the touch button's verb, its sweet-spot shout, the fighter clip its attack borrows,")
    o.append("## how its shot is drawn when that isn't its flight's usual drawing (spin, pebble, bolt, rocket, orb,")
    o.append("## wave),")
    o.append("## how long it is drawn in the hand, and four nickname words. The sims never read this.")
    o.append("")
    for name, values in cols:
        o.append(f"const {name} := [")
        for i in range(0, len(values), 8):
            o.append("\t" + ", ".join(gd_string(v) for v in values[i:i + 8]) + ",")
        o.append("]")
        o.append("")
    lengths = [0.0] + [float(shown(r, "length", 0)) for r in rows]
    o.append("const LENGTHS := [")
    for i in range(0, len(lengths), 12):
        o.append("\t" + ", ".join(repr(v) for v in lengths[i:i + 12]) + ",")
    o.append("]")
    o.append("")
    o.append("const WORDS := [")
    o.append("\t[" + ", ".join(gd_string(w) for w in FISTS["words"]) + "],")
    for r in rows:
        o.append("\t[" + ", ".join(gd_string(w) for w in shown(r, "words", [])) + "],")
    o.append("]")
    o.append("")
    for fn, arr, ret, default in [("key", "KEYS", "String", '""'), ("name", "NAMES", "String", '""'), ("verb", "VERBS", "String", '""'),
                                  ("shout", "SHOUTS", "String", '""'), ("clip", "CLIPS", "String", '""'),
                                  ("shot", "SHOTS", "String", '""'),
                                  ("length", "LENGTHS", "float", "0.0")]:
        o.append("")
        o.append(f"static func {fn}(id: int) -> {ret}:")
        o.append(f"\treturn {arr}[id] if id >= 0 and id < {arr}.size() else {default}")
        o.append("")
    o.append("")
    o.append("## Four nickname words, or none for an id that isn't a weapon.")
    o.append("static func words(id: int) -> Array:")
    o.append("\treturn WORDS[id] if id >= 0 and id < WORDS.size() else []")
    return "\n".join(o) + "\n"


def outputs(rows):
    table, spreads = build(rows)
    return {CS_PATH: write_cs(rows, table, spreads), GD_PATH: write_gd(rows, table, spreads), INFO_PATH: write_info(rows)}


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main(argv):
    self_test()
    check = "--check" in argv
    try:
        files = outputs(load(SOURCE))
    except Refused as e:
        print(f"weapons.json refused: {e}", file=sys.stderr)
        return 2
    stale = []
    for rel, text in files.items():
        path = os.path.join(ROOT, rel)
        current = None
        if os.path.exists(path):
            with open(path, encoding="utf-8", newline="") as f:
                current = f.read().replace("\r\n", "\n")
        if current == text:
            continue
        stale.append(rel)
        if not check:
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
    if check and stale:
        print("stale, rerun tools/gen_weapons.py: " + ", ".join(stale), file=sys.stderr)
        return 1
    print("up to date" if not stale else "wrote " + ", ".join(stale))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
