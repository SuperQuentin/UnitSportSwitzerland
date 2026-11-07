"""Unit tests on synthetic PDFs made with PyMuPDF; no raw documents, no X: drive. Run: python -m unittest (in tools/plancues)."""
import math
import os
import tempfile
import unittest

import numpy as np
import pymupdf
from shapely.geometry import Polygon

import extract as ex
import footscale
import features
import pipeline
import place
import scale as scalem
import evalsort
import sortmodel
import sortpages
import vlm


def make_pdf(path, draw, size=(600, 400)):
    doc = pymupdf.open()
    page = doc.new_page(width=size[0], height=size[1])
    draw(page)
    doc.save(path)
    doc.close()


def bar(page, x0, y, per_5m, labels=(0, 5, 10, 20)):
    """A scale bar: ticks every 5 m (and 10 m steps), numbers left-aligned at their ticks."""
    sh = page.new_shape()
    for v in labels:
        x = x0 + v / 5.0 * per_5m
        sh.draw_line((x, y), (x, y + 6))
        page.insert_text((x, y + 16), str(v), fontsize=6)
    sh.draw_line((x0, y + 6), (x0 + labels[-1] / 5.0 * per_5m, y + 6))
    sh.finish(width=0.3)
    sh.commit()


def pagedata(path, page=0):
    doc = pymupdf.open(path)
    p = doc[page]
    stats, dr = features.vector_stats(p)
    return p, dr, features.words_of(p)


class ScaleTests(unittest.TestCase):
    def test_scale_bar_numbers_and_ticks(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as d:
            f = os.path.join(d, "bar.pdf")
            make_pdf(f, lambda p: bar(p, 50, 350, 20.0))  # 20 pt per 5 m: 0.25 m per pt
            p, dr, words = pagedata(f)
            res = scalem.find_scale_bar(words, dr)
            self.assertIsNotNone(res)
            self.assertAlmostEqual(res["m_per_pt"], 0.25, places=3)
            self.assertEqual(res["n_labels"], 4)

    def test_written_ratio_is_not_trusted(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as d:
            f = os.path.join(d, "ratio.pdf")

            def draw(p):
                p.insert_text((50, 50), "1:100", fontsize=10)
                p.draw_rect(pymupdf.Rect(100, 100, 300, 200))
            make_pdf(f, draw)
            p, dr, words = pagedata(f)
            self.assertIsNone(scalem.decide(words, dr, p.rect)["m_per_pt"])

    def test_known_object_pitch(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as d:
            f = os.path.join(d, "pitch.pdf")

            def draw(p):
                s = 4.0  # pt per m
                x0, y0 = 100, 100
                sh = p.new_shape()
                sh.draw_rect(pymupdf.Rect(x0, y0, x0 + 105 * s, y0 + 68 * s))
                sh.finish(width=0.5)
                sh.draw_line((x0 + 52.5 * s, y0), (x0 + 52.5 * s, y0 + 68 * s))
                sh.finish(width=0.5)
                sh.draw_circle((x0 + 52.5 * s, y0 + 34 * s), 9.15 * s)
                sh.finish(width=0.5)
                sh.commit()
            make_pdf(f, draw, size=(600, 400))
            p, dr, words = pagedata(f)
            res = scalem.decide(words, dr, p.rect)
            self.assertEqual(res["method"], "known_object")
            self.assertAlmostEqual(res["m_per_pt"], 0.25, delta=0.005)

    def test_bar_and_object_cross_check(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as d:
            f = os.path.join(d, "both.pdf")

            def draw(p):
                bar(p, 20, 380, 20.0)
                s = 4.0
                sh = p.new_shape()
                sh.draw_rect(pymupdf.Rect(100, 100, 100 + 105 * s, 100 + 68 * s))
                sh.finish(width=0.5)
                sh.draw_line((100 + 52.5 * s, 100), (100 + 52.5 * s, 100 + 68 * s))
                sh.finish(width=0.5)
                sh.draw_circle((100 + 52.5 * s, 100 + 34 * s), 9.15 * s)
                sh.finish(width=0.5)
                sh.commit()
            make_pdf(f, draw, size=(600, 420))
            p, dr, words = pagedata(f)
            res = scalem.decide(words, dr, p.rect)
            self.assertEqual(res["method"], "scale_bar")
            self.assertAlmostEqual(res["cross_check"]["ratio"], 1.0, delta=0.02)


def columns(page, n=25):
    """Small heavy marks (columns): enough heavy paths for the weight class to count as a real share of the page."""
    for i in range(n):
        sh = page.new_shape()
        sh.draw_rect(pymupdf.Rect(10 + i * 6, 10, 13 + i * 6, 13))
        sh.finish(width=1.6)
        sh.commit()


def plan_drawing(page):
    """Two rooms side by side with a door gap between them, heavy walls among thin hatching, a scale bar."""
    columns(page)
    sh = page.new_shape()
    for i in range(60):  # thin detail
        sh.draw_line((60, 60 + i * 4), (340, 60 + i * 4))
    sh.finish(width=0.1, color=(0.5, 0.5, 0.5))
    wall = 1.6
    sh = page.new_shape()
    # outer box 200 x 100 pt at 0.25 m/pt = 50 x 25 m, middle wall with a 4 pt (1 m) gap... drawn as a 6 pt (1.5 m) door gap
    sh.draw_rect(pymupdf.Rect(100, 150, 300, 250))
    sh.finish(width=wall)
    sh.draw_line((200, 150), (200, 190))
    sh.draw_line((200, 196), (200, 250))
    sh.finish(width=wall)
    sh.commit()
    bar(page, 20, 330, 20.0)


class ExtractTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.pdf = os.path.join(self.tmp.name, "plan.pdf")
        make_pdf(self.pdf, plan_drawing, size=(400, 400))
        self.p, self.dr, self.words = pagedata(self.pdf)

    def tearDown(self):
        self.tmp.cleanup()

    def test_wall_weights_are_the_heavy_class(self):
        cl = ex.stroke_classes(self.dr)
        self.assertIn(1.6, cl["wall_widths"])
        self.assertNotIn(0.1, cl["wall_widths"])
        self.assertEqual(cl["heaviest"], 1.6)

    def test_rooms_and_door_from_synthetic_plan(self):
        m = 0.25
        cl = ex.stroke_classes(self.dr)
        strokes, solids = ex.select_wall_paths(self.dr, m, cl)
        fr = ex.Frame((0, 0, 400, 400), m)
        mask = ex.clean_mask(ex.rasterize(strokes, solids, fr))
        ol = ex.outline_mask(mask, 2.0)
        markers, _ = ex.segment_rooms(mask, ol, fr, door_m=2.6)  # rooms here are 25 m wide: a 1.5 m gap is a door
        markers = ex.grow_into_walls(markers, mask, 5)
        ids = [int(i) for i in np.unique(markers) if i > 1]
        polys = ex.simplify_coverage(ex.resolve_overlaps(ex.room_polygons(markers, fr, ids)))
        self.assertEqual(len(polys), 2)
        areas = sorted(p.area for p in polys.values())
        self.assertAlmostEqual(areas[0], 25 * 25, delta=60)  # 50 x 25 m split in half
        free = ((mask == 0) & (ol > 0)).astype(np.uint8)
        doors = ex.doors_between(polys, markers, free, fr)
        self.assertEqual(len(doors), 1)
        self.assertTrue(0.5 <= doors[0][4] <= 2.5)  # a door-sized neck

    def test_door_swing_arc_is_not_a_wall(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as d:
            f = os.path.join(d, "arc.pdf")

            def draw(p):
                columns(p)
                sh = p.new_shape()
                sh.draw_line((100, 100), (300, 100))
                sh.finish(width=1.6)
                sh.draw_bezier((150, 100), (150, 104), (152, 106), (156, 106))
                sh.finish(width=1.6)
                for i in range(40):
                    sh.draw_line((10, 10 + i), (50, 10 + i))
                sh.finish(width=0.1)
                sh.commit()
            make_pdf(f, draw)
            p, dr, _ = pagedata(f)
            cl = ex.stroke_classes(dr)
            strokes, _ = ex.select_wall_paths(dr, 0.25, cl)
            self.assertEqual(len(strokes), 25 + 1)  # the columns and the straight wall, not the swing arc

    def test_label_from_text_layer_and_type(self):
        self.assertEqual(ex.room_type_of("Seminarraum 20 Plaetze"), "Study")
        self.assertEqual(ex.room_type_of("WC / DU"), "WC")
        self.assertIsNone(ex.room_type_of("A 027"))


class SchematicTests(unittest.TestCase):
    def test_schematic_site_map_is_rejected(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as d:
            f = os.path.join(d, "Venue", "situationsplan_anreise.pdf")
            os.makedirs(os.path.dirname(f))

            def draw(p):
                p.insert_text((40, 40), "Situationsplan  Anreise mit dem Bus", fontsize=14)
                sh = p.new_shape()
                for i in range(30):
                    sh.draw_rect(pymupdf.Rect(40 + i * 15, 100 + (i % 5) * 30, 50 + i * 15, 120 + (i % 5) * 30))
                sh.finish(width=0.5, fill=(0.9, 0.7, 0.2), color=(0.2, 0.2, 0.2))
                bar(p, 20, 360, 20.0, labels=(0, 5, 10))
                sh.commit()
            make_pdf(f, draw, size=(600, 420))
            c, _ = pipeline.scale_stage(d, "Venue", "Venue/situationsplan_anreise.pdf", 1)
            self.assertEqual(c["status"], "rejected")
            self.assertTrue(any("schematic" in r for r in c["reasons"]))
            self.assertIsNone(c["placement"])


class PlaceTests(unittest.TestCase):
    def test_footprint_fit_recovers_turn_and_position(self):
        fp = Polygon([(0, 0), (40, 0), (40, 15), (20, 15), (20, 30), (0, 30)])  # an L, LV95 metres (offset below)
        from shapely import affinity
        fp = affinity.translate(fp, 2600000, 1200000)
        yaw = 33.0
        a = math.radians(yaw)
        # the plan: the same L in the plan's own frame, with the turn undone about the centroid
        c = fp.centroid
        plan = affinity.translate(fp, -c.x, -c.y)
        plan = Polygon([place.to_plan(x + c.x, y + c.y, c.x, c.y, yaw) for x, y in plan.exterior.coords])
        f = place.fit_footprint(plan, fp, yaw_hint=yaw + 4)
        self.assertGreater(f["iou"], 0.93)
        self.assertAlmostEqual(f["yaw_deg"] % 360, yaw, delta=1.5)
        e0, n0 = place.to_lv95(-f["origin"][0], -f["origin"][1], f["e"], f["n"], f["yaw_deg"])
        self.assertAlmostEqual(math.hypot(e0 - c.x, n0 - c.y), 0, delta=1.0)

    def test_a_misfit_is_rejected(self):
        fp = Polygon([(0, 0), (60, 0), (60, 10), (0, 10)])
        plan = Polygon([(0, 0), (20, 0), (20, 20), (0, 20)])
        f = place.fit_footprint(plan, fp)
        st, why = place.status_of_fit(f)
        self.assertNotEqual(st, "ok")
        self.assertTrue(why)

    def test_north_arrow_yaw(self):
        self.assertAlmostEqual(place.yaw_of_north_arrow(0, -1), 90.0 - 90.0, places=6)  # north up: page right is east: yaw 0
        self.assertAlmostEqual(place.yaw_of_north_arrow(-math.cos(math.radians(27.2)), -math.sin(math.radians(27.2))) % 360, 297.2, delta=0.01)


class FootScaleTests(unittest.TestCase):
    """A plan in page points at a known scale against footprints in LV95: the scale must be found, and wrong matches refused."""
    M = 0.12  # true metres per point
    E0, N0 = 2600000.0, 1200000.0
    L = [(0, 0), (40, 0), (40, 12), (16, 12), (16, 30), (0, 30)]  # an L, 40 x 30 m

    def fp(self, pts, dx=0.0, dy=0.0):
        return Polygon([(self.E0 + x + dx, self.N0 + y + dy) for x, y in pts])

    def plan(self, pts, yaw=27.0, m=None):
        """The shape as a plan drawn at scale m, turned by yaw (the plan's frame is not the map's)."""
        m = m or self.M
        f = self.fp(pts)
        c = f.centroid
        pm = Polygon([place.to_plan(x, y, c.x, c.y, yaw) for x, y in f.exterior.coords])
        return place.scaled(pm, 1.0 / m)

    def bld(self, egid, pts, dx=0.0, dy=0.0):
        return dict(egid=egid, fp=self.fp(pts, dx, dy))

    def test_scale_recovered_and_confirmed_by_the_written_ratio(self):
        m = 200 * footscale.PT_M  # 1:200 on the paper
        j = footscale.judge(self.plan(self.L, m=m), [self.bld(1, self.L)], words=[(0, 0, 1, 1, "Massstab"), (0, 0, 1, 1, "1:200")])
        self.assertEqual(j["status"], "ok", j["reasons"])
        self.assertAlmostEqual(j["m_per_pt"] / m, 1.0, delta=0.015)
        self.assertEqual(j["egid"], 1)
        self.assertTrue(j["cross_check"]["agrees"])

    def test_without_a_written_ratio_it_is_low_confidence(self):
        j = footscale.judge(self.plan(self.L), [self.bld(1, self.L)])
        self.assertEqual(j["status"], "low_confidence", j["reasons"])
        self.assertAlmostEqual(j["m_per_pt"] / self.M, 1.0, delta=0.015)

    def test_a_wrong_written_ratio_does_not_confirm(self):
        j = footscale.judge(self.plan(self.L), [self.bld(1, self.L)], words=[(0, 0, 1, 1, "1:100")])
        self.assertEqual(j["status"], "low_confidence")
        self.assertFalse(j["cross_check"]["agrees"])

    def test_a_plain_rectangle_is_not_distinctive(self):
        sq = [(0, 0), (30, 0), (30, 28), (0, 28)]  # near-square: no aspect cue either
        j = footscale.judge(self.plan(sq), [self.bld(1, sq)], words=[(0, 0, 1, 1, "1:340")])
        self.assertEqual(j["status"], "rejected")
        self.assertLess(j["distinct"], footscale.THR["distinct"])

    def test_an_elongated_rectangle_only_gets_low_confidence(self):
        bar_ = [(0, 0), (60, 0), (60, 14), (0, 14)]
        j = footscale.judge(self.plan(bar_), [self.bld(1, bar_)])
        self.assertEqual(j["status"], "low_confidence", j["reasons"])
        self.assertTrue(any("aspect" in r for r in j["reasons"]))

    def test_a_partial_plan_is_rejected(self):
        wing = [(0, 0), (40, 0), (40, 12), (16, 12), (16, 14), (0, 14)]  # one wing of the L only
        # the plan is drawn from the wing's own footprint, the building is the whole L
        j = footscale.judge(self.plan(wing), [self.bld(1, self.L)])
        self.assertEqual(j["status"], "rejected")
        self.assertIsNone(j["m_per_pt"])

    def test_the_wrong_neighbour_loses(self):
        other = [(0, 0), (30, 0), (30, 10), (22, 10), (22, 25), (0, 25)]  # a different L next door
        cands = [self.bld(7, other, dx=60), self.bld(1, self.L), self.bld(9, [(0, 0), (35, 0), (35, 35), (0, 35)], dx=-70)]
        j = footscale.judge(self.plan(self.L), cands)
        self.assertEqual(j["egid"], 1)
        self.assertGreater(j["margin"], footscale.THR["margin"])
        self.assertAlmostEqual(j["m_per_pt"] / self.M, 1.0, delta=0.015)

    def test_two_lookalikes_with_other_sizes_are_refused(self):
        big = [(x * 1.02, y * 1.02) for x, y in self.L]  # nearly the same shape, nearly the same size
        j = footscale.judge(self.plan(self.L), [self.bld(1, self.L), self.bld(2, big, dx=80)], thr=dict(twin_scale=0.0))
        self.assertEqual(j["status"], "rejected")
        self.assertIn("another building", " ".join(j["reasons"]))

    def test_an_implausible_scale_is_refused(self):
        j = footscale.judge(self.plan(self.L, m=3.0), [self.bld(1, self.L)])  # 3 m per point: the plan would be a stamp
        self.assertEqual(j["status"], "rejected")
        self.assertIn("plausible", " ".join(j["reasons"]))

    def test_written_ratios(self):
        w = lambda *t: [(0, 0, 1, 1, x) for x in t]
        self.assertEqual(footscale.written_ratios(w("M", "1:200", "und", "1", ":", "500", "10:30", "1:7")), [200, 500])


class ModelTests(unittest.TestCase):
    def test_model_label_needs_the_text_layer(self):
        self.assertTrue(vlm.keep_label("Foyer", "A 027 Foyer Lift"))
        self.assertFalse(vlm.keep_label("salle de sport", "rez-de-chaussee 0 5 10 20 30"))


def plan_page(p):
    """Walls on a grid: orthogonal strokes, closed rooms, a few words."""
    sh = p.new_shape()
    for x in range(100, 501, 100):
        sh.draw_line((x, 60), (x, 340))
    for y in (60, 200, 340):
        sh.draw_line((100, y), (500, y))
    sh.finish(width=1.6)
    sh.commit()
    p.insert_text((120, 130), "Grundriss Erdgeschoss", fontsize=8)


def photo_page(p):
    """A smooth, noisy colour image over the page: no vector paths, soft gradients, many colours."""
    import cv2
    rng = np.random.default_rng(1)
    yy, xx = np.mgrid[0:400, 0:600]
    img = np.stack([128 + 100 * np.sin(xx / 37.0), 128 + 100 * np.sin(yy / 23.0 + xx / 91.0), 128 + 100 * np.cos(xx / 53.0)], axis=2)
    img = np.clip(img + rng.normal(0, 12, img.shape), 0, 255).astype(np.uint8)
    p.insert_image(p.rect, stream=cv2.imencode(".png", img)[1].tobytes())


class SortFeatureTests(unittest.TestCase):
    def feats(self, draw):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as d:
            f = os.path.join(d, "p.pdf")
            make_pdf(f, draw)
            doc = pymupdf.open(f)
            out = features.page_features(doc, 0)
            doc.close()
        return out

    def test_plan_is_orthogonal_with_closed_rooms(self):
        f = self.feats(plan_page)
        self.assertEqual(f["v_orth"], 1.0)
        self.assertGreaterEqual(f["rooms"], 6)  # a 4 x 2 grid of rooms
        self.assertGreater(f["hl_orth"], 0.9)
        self.assertEqual(f["n_images"], 0)
        self.assertEqual(f["n_scale_ratio"], 0)

    def test_photo_has_soft_gradients_and_no_straight_lines(self):
        ph, pl = self.feats(photo_page), self.feats(plan_page)
        self.assertGreater(ph["img_max"], 0.9)
        self.assertGreater(ph["soft"], pl["soft"])
        self.assertLess(ph["flat6"], 0.8)
        self.assertGreater(ph["ncol"], pl["ncol"])
        self.assertEqual(ph["v_orth"], 0.0)

    def test_scale_ratio_and_big_words_are_counted(self):
        def draw(p):
            p.insert_text((50, 100), "Plan 1:200  Schnitt 1 : 500", fontsize=10)
            p.insert_text((50, 200), "TITLE", fontsize=40)
        f = self.feats(draw)
        self.assertEqual(f["n_scale_ratio"], 2)
        self.assertEqual(f["n_big_words"], 1)

    def test_vector_has_one_value_per_name(self):
        v = sortmodel.vector(self.feats(plan_page), "Grundriss_EG.pdf")
        self.assertEqual(len(v), len(sortmodel.names()))
        self.assertTrue(np.isfinite(v).all())
        self.assertIsNone(sortmodel.vector({"error": "cannot open"}, "x.pdf"))

    def test_model_trains_and_tells_a_plan_from_a_photo(self):
        pl, ph = self.feats(plan_page), self.feats(photo_page)
        pages, labels = [], {}
        for i in range(6):
            for kind, f in (("floor_plan", pl), ("photo", ph)):
                p = dict(f, path="%s_%d.pdf" % (kind, i), page=1)
                pages.append(p)
                labels[(p["path"], 1)] = kind
        m = sortmodel.train(pages, labels)
        self.assertEqual(max(sortmodel.probabilities(m, pl, "x.pdf").items(), key=lambda kv: kv[1])[0], "floor_plan")
        self.assertEqual(max(sortmodel.probabilities(m, ph, "x.pdf").items(), key=lambda kv: kv[1])[0], "photo")


class EvalTests(unittest.TestCase):
    def test_confusion_precision_recall(self):
        pairs = [("floor_plan", "floor_plan")] * 3 + [("floor_plan", "site_plan"), ("site_plan", "site_plan"), ("section", "floor_plan")]
        m, acc = evalsort.confusion(pairs)
        self.assertAlmostEqual(acc, 4 / 6)
        pc = evalsort.per_class(m)
        self.assertAlmostEqual(pc["floor_plan"][0], 3 / 4)  # one section was called a floor plan
        self.assertAlmostEqual(pc["floor_plan"][1], 3 / 4)  # one floor plan was called a site plan
        self.assertIn("floor_plan", evalsort.text_matrix(m))


if __name__ == "__main__":
    unittest.main()
