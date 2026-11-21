"""Tests of tools/gen_weapons.py: its rounding, and one bad weapons.json per validation rule.

    python tools/test_gen_weapons.py        exit 0 = every test passed

Run by tests/Rangbaazi.Sim.Tests/WeaponTableTests.cs when the machine has Python
(docs/design/combat-spec-v0.4-all-weapons.md, section 6, "The generator").
"""
import copy
import os
import sys
import unittest
from fractions import Fraction

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_weapons as gen  # noqa: E402


def rows():
    return copy.deepcopy(gen.load(gen.SOURCE))


def row(rs, name):
    return next(r for r in rs if r["name"] == name)


def read(rel):
    with open(os.path.join(gen.ROOT, rel), encoding="utf-8", newline="") as f:
        return f.read()


class Rounding(unittest.TestCase):
    def test_the_start_up_self_test_passes(self):
        gen.self_test()

    def test_decimals_are_truncated_towards_zero(self):
        self.assertEqual(gen.fx(-0.15), -9830)      # floor would give -9831
        self.assertEqual(gen.fx(-0.0366), -2398)    # floor would give -2399
        self.assertEqual(gen.fx(0.15), 9830)
        self.assertEqual(gen.fx(0.0366), 2398)
        self.assertEqual(gen.fx(1), 65536)
        self.assertEqual(gen.fx(-1), -65536)

    def test_a_decimal_is_the_ratio_fix_from_ratio_gives(self):
        # C#'s Fix.FromRatio and GDScript's Fx.ratio: (n << 16) / d, truncated towards zero.
        for n, d in [(12, 100), (25, 10000), (-15, 100), (366, 10000), (-732, 10000), (4, 1000), (125, 10000), (9, 2), (21, 10)]:
            value = Fraction(n, d)
            want = abs(n * 65536) // d * (1 if n >= 0 else -1)
            self.assertEqual(gen.fx(float(value)), want, (n, d))

    def test_sizes_are_stored_as_half(self):
        self.assertEqual(gen.half(0.6), gen.fx(0.3))
        self.assertEqual(gen.half(2.4), 78643)      # Fix.FromRatio(6, 5)
        self.assertEqual(gen.half(4.2), 137625)     # Fix.FromRatio(21, 10)

    def test_angles_are_the_hand_written_unit_vectors(self):
        vectors = {50: (6428, 7660), 35: (8192, 5736), 25: (9063, 4226), 30: (8660, 5000), 10: (9848, 1736),
                   60: (5000, 8660), 20: (9397, 3420), 70: (3420, 9397), 45: (7071, 7071), 80: (1736, 9848)}
        for degrees, (x, y) in vectors.items():
            self.assertEqual(gen.angle(degrees), (x * 65536 // 10000, y * 65536 // 10000), degrees)


class Table(unittest.TestCase):
    def test_the_real_file_builds(self):
        table, spreads = gen.build(rows())
        self.assertEqual(len(table), 108)
        self.assertTrue(all(len(r) == len(gen.COLUMNS) for r in table))
        self.assertTrue(all(isinstance(v, int) and not isinstance(v, bool) for r in table for v in r))
        self.assertEqual(table[0][0], 0)
        self.assertEqual(spreads[:5], [0, 2398, -2398, 4797, -4797])   # the Uzi's, first in the shared list

    def test_only_the_twelve_are_built_until_a_batch_marks_more(self):
        built = gen.COLUMNS.index(next(c for c in gen.COLUMNS if c[0] == "built"))
        table, _ = gen.build(rows())
        for i, r in enumerate(table):
            source = None if i == 0 else rows()[i - 1]
            want = 0 if source is None else int(source.get("built", source.get("batch", 1) == 0))
            self.assertEqual(r[built], want, i)
        self.assertTrue(all(table[i][built] == 1 for i in range(1, 13)))

    def test_generating_twice_gives_the_same_files(self):
        self.assertEqual(gen.outputs(rows()), gen.outputs(rows()))

    def test_names_become_identifiers_in_both_languages(self):
        self.assertEqual(gen.cs_name("Black-Hole Bomb"), "BlackHoleBomb")
        self.assertEqual(gen.gd_name("Black-Hole Bomb"), "BLACK_HOLE_BOMB")
        self.assertEqual(gen.cs_name(".357 Magnum"), "Magnum357")
        self.assertEqual(gen.gd_name("AK-47"), "AK_47")
        self.assertEqual(gen.gd_const("spreadBy"), "SPREAD_BY")


class Refusals(unittest.TestCase):
    """One bad file per rule of section 1.1. Each must be refused before anything is written."""

    def refused(self, change, text):
        rs = rows()
        change(rs)
        with self.assertRaises(gen.Refused) as caught:
            gen.outputs(rs)
        self.assertIn(text, str(caught.exception))

    def test_an_unknown_column(self):
        self.refused(lambda rs: row(rs, "Belan").update(dammage=8), "unknown column")

    def test_a_name_that_is_not_in_its_list(self):
        self.refused(lambda rs: row(rs, "Belan").update(use="smack"), "is not a use")
        self.refused(lambda rs: row(rs, "Uzi").update(flight="laser"), "is not a flight")
        self.refused(lambda rs: row(rs, "Belan").update(effect="sleep"), "is not a effect")

    def test_an_id_above_a_byte(self):
        # Ids run 1, 2, 3, ... in file order, so an id past 255 is a gap, or a 256th row (which
        # also runs out of move ids: there are only 248 a row can take).
        self.refused(lambda rs: rs[-1].update(id=300), "ids must run")

        def big(rs):
            used = {r["move"] for r in rs}
            free = [m for m in range(7, 256) if m not in used and m != 14]
            for i in range(len(rs), 256):
                rs.append({"id": i + 1, "name": f"Filler {i}", "move": free.pop() if free else 255, "use": "swing", "crate": False})
        self.refused(big, "")

    def test_a_move_above_a_byte_or_one_that_is_taken(self):
        self.refused(lambda rs: row(rs, "Belan").update(move=256), "move 256")
        self.refused(lambda rs: row(rs, "Belan").update(move=14), "move 14")      # the throw's
        self.refused(lambda rs: row(rs, "Belan").update(move=3), "move 3")        # a fist move

    def test_an_id_or_a_move_used_twice(self):
        self.refused(lambda rs: row(rs, "Baseball Bat").update(id=1), "id 1 is used twice")
        self.refused(lambda rs: row(rs, "Baseball Bat").update(move=7), "move 7 is used twice")

    def test_a_supply_above_127(self):
        self.refused(lambda rs: row(rs, "Uzi").update(supply=128), "supply")

    def test_a_repeating_row_that_never_reaches_its_loop_frame(self):
        self.refused(lambda rs: row(rs, "Uzi").update(interval=8), "interval 8")  # a + r is 8

    def test_links_to_rows_that_are_not_there(self):
        self.refused(lambda rs: row(rs, "Thunder Hammer").update(fires=200), "fires 200 is not a row")
        self.refused(lambda rs: row(rs, "Spear").update(thrownAs=0), "thrownAs 0 is not a row")
        self.refused(lambda rs: row(rs, "Jugaad Gauntlet").update(cycleFirst=106), "cycle row 108 is not a row")
        self.refused(lambda rs: row(rs, "Jugaad Gauntlet").pop("cycleCount"), "cycleFirst and cycleCount go together")
        self.refused(lambda rs: row(rs, "Gauntlet pull").update(credit=150), "credit 150 is not a row")

    def test_a_spread_without_spread_by(self):
        self.refused(lambda rs: row(rs, "Uzi").pop("spreadBy"), "spread and spreadBy go together")
        self.refused(lambda rs: row(rs, "Bow").update(spreadBy="burst"), "spread and spreadBy go together")

    def test_leaves_without_a_size_and_a_life(self):
        self.refused(lambda rs: row(rs, "Lota").pop("zoneW"), "leaves zone needs")
        self.refused(lambda rs: row(rs, "Matka").pop("zoneLife"), "leaves wall needs")
        self.refused(lambda rs: row(rs, "Thrown charpai").pop("size"), "leaves pad needs")

    def test_first_together_with_both(self):
        self.refused(lambda rs: row(rs, "Sniper Rifle").update(both=True), "first and both")

    def test_a_value_of_the_wrong_kind(self):
        self.refused(lambda rs: row(rs, "Belan").update(dmg=8.5), "whole number")
        self.refused(lambda rs: row(rs, "Uzi").update(chip="yes"), "true or false")
        self.refused(lambda rs: row(rs, "Belan").update(box=[0.2, 1.3]), "box is near, far, bottom, top")
        self.refused(lambda rs: row(rs, "Belan").update(ang=120), "ang is 0 to 90")
        self.refused(lambda rs: row(rs, "Uzi").update(shot="sparkle"), "is not a shot look")

    def test_two_names_that_would_be_one_identifier(self):
        self.refused(lambda rs: row(rs, "Baseball Bat").update(name="Belan"), "would both be WeaponId.Belan")

    def test_nothing_is_written_for_a_refused_file(self):
        before = {rel: read(rel) for rel in (gen.CS_PATH, gen.GD_PATH, gen.INFO_PATH)}
        rs = rows()
        row(rs, "Belan")["use"] = "smack"
        with self.assertRaises(gen.Refused):
            gen.outputs(rs)
        after = {rel: read(rel) for rel in before}
        self.assertEqual(before, after)


if __name__ == "__main__":
    unittest.main(verbosity=1)
