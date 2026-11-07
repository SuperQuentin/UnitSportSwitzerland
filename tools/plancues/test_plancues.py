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


def _face(tris, y, down, *p):
    """A flat polygon as a fan, wound like the tiles' solids: a face looking down has a negative plan winding."""
    for k in range(1, len(p) - 1):
        a, b, c = p[0], p[k], p[k + 1]
        if (((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])) < 0) != down:
            b, c = c, b
        tris.append([(a[0], y, a[1]), (b[0], y, b[1]), (c[0], y, c[1])])


class FrameTests(unittest.TestCase):
    """bldgframe, the twin of BuildingOutline.cs (HouseLayoutTests has the same two cases)."""

    def test_a_building_stepped_down_a_slope_gets_its_whole_base(self):
        import bldgframe
        tris = []
        _face(tris, 0, True, (490, 496), (500, 496), (500, 504), (490, 504))
        _face(tris, 3, True, (500, 500), (500, 496), (510, 496), (510, 504), (500, 504))
        _face(tris, 9, False, (490, 496), (510, 496), (510, 504), (490, 504))
        o = bldgframe.outline(dict(tris=np.array(tris, dtype="<f4"), min_y=0.0, tile=(2594, 1120)))
        self.assertEqual(len(o["rings"]), 1)
        self.assertAlmostEqual(abs(sum(bldgframe._area(r) for r in bldgframe.local_rings(o))), 160, places=0)
        self.assertAlmostEqual(o["length"], 20, places=1)
        self.assertAlmostEqual(o["width"], 8, places=1)

    def test_eaves_are_no_floor(self):
        import bldgframe
        tris = []
        _face(tris, 0, True, (495, 496), (505, 496), (505, 504), (495, 504))
        _face(tris, 6, False, (494.3, 495.3), (505.7, 495.3), (505.7, 504.7), (494.3, 504.7))
        _face(tris, 5.8, True, (494.3, 495.3), (505.7, 495.3), (505, 496), (495, 496))
        _face(tris, 5.8, True, (505.7, 495.3), (505.7, 504.7), (505, 504), (505, 496))
        _face(tris, 5.8, True, (505.7, 504.7), (494.3, 504.7), (495, 504), (505, 504))
        _face(tris, 5.8, True, (494.3, 504.7), (494.3, 495.3), (495, 496), (495, 504))
        o = bldgframe.outline(dict(tris=np.array(tris, dtype="<f4"), min_y=0.0, tile=(2594, 1120)))
        self.assertAlmostEqual(abs(sum(bldgframe._area(r) for r in bldgframe.local_rings(o))), 80, places=0)
        self.assertAlmostEqual(o["length"], 10, places=1)
        self.assertAlmostEqual(o["width"], 8, places=1)


class OrientationPlanTests(unittest.TestCase):
    """Orientation plans: flat colour rooms with names in the text layer, no scale bar, no walls."""
    M = 0.12  # true metres per point
    # (rect in page points, fill, label): an L, 40 x 30 m at M; the vertical arm is at the top of the page (north up on the map is page up)
    ROOMS = [((0, 0, 133, 70), (0.78, 0.78, 0.78), "Lager"), ((0, 76, 133, 150), (0.98, 0.94, 0.5), "Mensa"),
             ((0, 156, 78, 250), (1.0, 0.6, 0.78), "Aula"), ((84, 156, 162, 250), (0.5, 0.75, 0.95), "Buro 1"),
             ((168, 156, 246, 250), (0.6, 0.87, 0.6), "Bibliothek"), ((252, 156, 333, 250), (0.98, 0.75, 0.4), "WC Damen")]

    def draw(self, page, legend=True, entrance=True):
        ox, oy = 80, 40
        for (x0, y0, x1, y1), col, label in self.ROOMS:
            page.draw_rect(pymupdf.Rect(ox + x0, oy + y0, ox + x1, oy + y1), fill=col, color=None)
            page.insert_text((ox + x0 + 6, oy + (y0 + y1) / 2), label, fontsize=9)
        if legend:  # a swatch and its word, far from the building
            page.draw_rect(pymupdf.Rect(500, 330, 530, 350), fill=(1.0, 0.6, 0.78), color=None)
            page.insert_text((536, 344), "Treppe", fontsize=9)
        if entrance:
            page.insert_text((ox + 40, oy + 262), "Eingang", fontsize=9)

    def pdf(self, d, **kw):
        path = os.path.join(d, "orient.pdf")
        make_pdf(path, lambda pg: self.draw(pg, **kw), size=(600, 400))
        return path

    def test_colour_regions_are_rooms_with_names_and_marks(self):
        import orient
        with tempfile.TemporaryDirectory() as d:
            path = self.pdf(d)
            img, ppp, org, words = orient.render(path, 1)
            res = orient.extract(img, ppp, org, words)
        self.assertTrue(res["ok"], res["why"])
        self.assertEqual(len(res["rooms"]), 6)  # the legend swatch is another drawing, not a room
        labels = sorted(r["label"] for r in res["rooms"])
        self.assertEqual(labels, sorted(["Lager", "Mensa", "Aula", "Buro 1", "Bibliothek", "WC Damen"]))
        wc = next(r for r in res["rooms"] if r["label"] == "WC Damen")
        self.assertIn("wc", wc["features"])
        self.assertEqual(wc["type"], "WC")
        ent = [m for m in res["marks"] if m["kind"] == "entrance"]
        self.assertEqual(len(ent), 1)  # "Eingang" is under the building, on its outline
        self.assertIsNotNone(ent[0]["at_px"])
        self.assertFalse(any(m["text"] == "Treppe" for m in res["marks"]))  # the legend's word is not part of the building
        want = sum((x1 - x0) * (y1 - y0) for (x0, y0, x1, y1), _, _ in self.ROOMS) * ppp * ppp
        self.assertAlmostEqual(sum(r["area_px"] for r in res["rooms"]) / want, 1.0, delta=0.06)

    def test_a_label_beside_the_plan_reaches_its_room_by_a_leader_line(self):
        import orient
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "l.pdf")

            def draw(pg):
                for (x0, y0, x1, y1), col, _ in self.ROOMS:
                    pg.draw_rect(pymupdf.Rect(80 + x0, 40 + y0, 80 + x1, 40 + y1), fill=col, color=None)
                pg.insert_text((260, 30), "Sonderausstellung", fontsize=9)  # above the plan, tied to Bibliothek (x 248-326, y 196-290)
                sh = pg.new_shape()
                sh.draw_line((300, 34), (300, 205))  # the leader: from the label down into the room
                sh.finish(width=0.5, color=(0, 0, 0))
                sh.commit()
            make_pdf(path, draw, size=(600, 400))
            img, ppp, org, words = orient.render(path, 1)
            plain = orient.extract(img, ppp, org, words)
            led = orient.extract(img, ppp, org, words, lines=orient.leader_lines(path, 1))
        self.assertEqual([r["label"] for r in plain["rooms"]], [None] * 6)  # no text lies in a room
        named = [r["label"] for r in led["rooms"] if r["label"]]
        self.assertEqual(named, ["Sonderausstellung"])

    def test_text_and_line_work_are_not_rooms(self):
        import orient
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "t.pdf")

            def draw(pg):
                self.draw(pg, legend=False)
                sh = pg.new_shape()
                sh.draw_line((20, 20), (560, 20))  # a rule and a caption in dark ink
                sh.finish(width=1.0, color=(0, 0, 0))
                sh.commit()
                pg.insert_text((20, 14), "UNI GEBAEUDE UEBERSICHT 4. OG", fontsize=12)
            make_pdf(path, draw, size=(600, 400))
            img, ppp, org, words = orient.render(path, 1)
            res = orient.extract(img, ppp, org, words)
        self.assertEqual(len(res["rooms"]), 6)

    def cands(self):
        """The building the plan is of (an L, 40 x 30 m)."""
        L = [(0, 0), (40, 0), (40, 12), (16, 12), (16, 30), (0, 30)]
        fp = Polygon([(2600000.0 + x, 1200000.0 + y) for x, y in L])
        return [dict(egid=1, fp=fp)]

    def test_a_gated_plan_is_geometry_in_metres_low_confidence_no_doors(self):
        import pipeline
        with tempfile.TemporaryDirectory() as d:
            self.pdf(d)
            c, res, j = pipeline.orientation_stage(d, "V", "orient.pdf", 1, None, {"at": [2600010, 1200010]}, cands=self.cands(), do_place=False)
        self.assertEqual(c["status"], "low_confidence", c["reasons"])
        self.assertEqual(c["scale"]["method"], "footprint_fit")
        self.assertAlmostEqual(c["scale"]["m_per_pt"] / self.M, 1.0, delta=0.05)
        lvl = c["levels"][0]
        self.assertEqual(lvl["extra"]["method"], "orientation_plan")
        self.assertEqual(len(lvl["rooms"]), 6)
        self.assertEqual(lvl["openings"], [])  # no door is drawn, none is invented
        self.assertEqual(len(lvl["extra"]["entrance_labels"]), 1)
        self.assertAlmostEqual(sum(r["area_m2"] for r in lvl["rooms"]), 702, delta=30)  # the rooms drawn, without the gaps between them

    def test_a_plan_that_fails_the_gate_is_topology_only(self):
        import json
        import pipeline
        far = [(0, 0), (90, 0), (90, 20), (0, 20)]  # not this building
        cands = [dict(egid=2, fp=Polygon([(2600000.0 + x, 1200000.0 + y) for x, y in far]))]
        with tempfile.TemporaryDirectory() as d:
            self.pdf(d)
            c, res, j = pipeline.orientation_stage(d, "V", "orient.pdf", 1, None, {"at": [2600010, 1200010]}, cands=cands, do_place=False)
        self.assertEqual(c["status"], "rejected")
        self.assertIsNone(c["scale"])
        self.assertEqual(c["levels"], [])  # nothing in metres
        self.assertTrue(any("topology only" in r for r in c["reasons"]))
        t = c["topology"]
        self.assertEqual(len(t["rooms"]), 6)
        self.assertAlmostEqual(sum(r["area_share"] for r in t["rooms"]), 1.0, places=2)
        names = {r["id"]: r["label"] for r in t["rooms"]}
        pair = {frozenset((names[a["a"]], names[a["b"]])) for a in t["adjacency"]}
        self.assertIn(frozenset(("Aula", "Buro 1")), pair)
        self.assertIn(frozenset(("Aula", "Mensa")), pair)
        self.assertNotIn(frozenset(("Aula", "WC Damen")), pair)
        self.assertEqual(len(t["entrances"]), 1)
        text = json.dumps(t)
        for dim in ("m_per_pt", "area_m2", "width_m"):
            self.assertNotIn(dim, text)

    def test_a_raster_page_has_rooms_but_no_names(self):
        import cv2
        import orient
        img = np.full((400, 600, 3), 255, np.uint8)
        for (x0, y0, x1, y1), col, _ in self.ROOMS:
            img[40 + y0:40 + y1, 80 + x0:80 + x1] = [int(v * 255) for v in col]
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "scan.png")
            cv2.imwrite(p, img[:, :, ::-1])
            im, ppp, org, words = orient.render(p, 1)
            res = orient.extract(im, ppp, org, words)
        self.assertTrue(res["ok"])
        self.assertEqual(len(res["rooms"]), 6)
        self.assertEqual([r["label"] for r in res["rooms"]], [None] * 6)  # there is no text layer and no OCR


class ExportGuardTests(unittest.TestCase):
    """A layout that keeps only a small part of the extracted rooms is refused (UniS: 98 rooms became one 3 m2 sliver)."""

    def test_guard_numbers(self):
        import export
        self.assertIsNotNone(export.keep_guard(1, 98, 3.0, 2800.0))
        self.assertIn("1 of 98", export.keep_guard(1, 98, 3.0, 2800.0))
        self.assertIn("m2 of", export.keep_guard(40, 60, 100.0, 2800.0))  # enough rooms, a small part of the area
        self.assertIsNone(export.keep_guard(50, 98, 1500.0, 2800.0))
        self.assertIsNotNone(export.keep_guard(0, 0, 0.0, 0.0))

    def cue(self, n_out):
        rooms = [dict(label=None, type=None, poly=[[1, -1], [6, -1], [6, -6], [1, -6]], area_m2=25.0)]
        for k in range(n_out):  # rooms drawn far outside the building: left out
            x = 100 + 10 * k
            rooms.append(dict(label=None, type=None, poly=[[x, -1], [x + 6, -1], [x + 6, -6], [x, -6]], area_m2=25.0))
        return dict(placement=dict(e=2600000.0, n=1200000.0, yaw_deg=0.0, egid=5, tile="2600_1200"),
                    levels=[dict(rooms=rooms, openings=[], extra={})])

    def run_layout(self, cue):
        from unittest import mock
        import export
        o = dict(e=2600000.0, n=1200000.0, yaw_deg=0.0, length=20.0, width=10.0)
        ring = [(0.0, 0.0), (20.0, 0.0), (20.0, 10.0), (0.0, 10.0)]
        with mock.patch.object(export.tiles, "buildings_near", return_value=[dict(egid=5)]), \
                mock.patch.object(export.bldgframe, "outline", return_value=o), \
                mock.patch.object(export.bldgframe, "local_rings", return_value=[ring]), \
                mock.patch.object(export.bldgframe, "to_local", side_effect=lambda o_, e, n: (e - o_["e"], n - o_["n"])):
            return export.house_layout(cue, "chunks")

    def test_one_room_of_ten_is_refused(self):
        lay, rep = self.run_layout(self.cue(9))
        self.assertIsNone(lay)
        self.assertIn("1 of 10", rep["refused"])

    def test_every_room_kept_is_not_refused(self):
        lay, rep = self.run_layout(self.cue(0))
        self.assertNotIn("refused", rep)
        self.assertEqual(rep["rooms_kept"], 1)


class DoorTests(unittest.TestCase):
    """doors.infer on synthetic plans: a 20 x 2 m corridor (id 0) along z 0..2 with four 5 m rooms above it (ids 1-4)."""
    FOOT = Polygon([(0, 0), (20, 0), (20, 7), (0, 7)])

    def plan(self, extra=None):
        polys = {0: Polygon([(0, 0), (20, 0), (20, 2), (0, 2)])}
        for k in range(4):
            polys[1 + k] = Polygon([(5 * k, 2), (5 * k + 5, 2), (5 * k + 5, 7), (5 * k, 7)])
        polys.update(extra or {})
        return polys

    def infer(self, polys, real=(), target=None, names=None):
        import doors
        return doors.infer(polys, names or {0: "Korridor"}, list(real), self.FOOT, target)

    def test_a_corridor_with_rooms_gets_one_door_per_room(self):
        out, info = self.infer(self.plan(), real=[dict(_rooms=(0, None))])
        self.assertEqual(sorted(tuple(sorted(o["_rooms"])) for o in out), [(0, 1), (0, 2), (0, 3), (0, 4)])
        self.assertEqual(info["unreachable_after"], [])
        for o in out:
            self.assertAlmostEqual(math.hypot(o["x1"] - o["x0"], o["z1"] - o["z0"]), 0.9, 2)
            self.assertAlmostEqual(o["z0"], 2.0)  # on the shared wall

    def test_a_room_behind_another_gets_a_chained_door(self):
        polys = self.plan({5: Polygon([(0, 7), (5, 7), (5, 10), (0, 10)])})
        out, info = self.infer(polys, real=[dict(_rooms=(0, None))])
        self.assertIn((1, 5), [tuple(sorted(o["_rooms"])) for o in out])
        self.assertNotIn((0, 5), [tuple(sorted(o["_rooms"])) for o in out])
        self.assertEqual(len(out), 5)
        self.assertEqual(info["unreachable_after"], [])

    def test_a_real_door_prevents_an_inferred_one(self):
        real = [dict(_rooms=(0, None)), dict(_rooms=(0, 1))]
        out, _ = self.infer(self.plan(), real=real)
        self.assertEqual(sorted(tuple(sorted(o["_rooms"])) for o in out), [(0, 2), (0, 3), (0, 4)])

    def test_a_front_door_is_inferred_nearest_the_entrance_point(self):
        out, _ = self.infer(self.plan(), target=(15.0, -3.0, "test entrance"))
        front = [o for o in out if o["_rooms"][1] is None]
        self.assertEqual(len(front), 1)
        self.assertEqual(front[0]["_rooms"][0], 0)  # the corridor's outer wall, z = 0, near x = 15
        self.assertAlmostEqual(front[0]["z0"], 0.0)
        self.assertAlmostEqual((front[0]["x0"] + front[0]["x1"]) / 2, 15.0, 1)
        self.assertIn("test entrance", front[0]["_src"])

    def test_every_inferred_door_carries_its_source(self):
        out, _ = self.infer(self.plan(), target=(15.0, -3.0, "x"))
        self.assertEqual(len(out), 5)
        for o in out:
            self.assertTrue(o["_src"].startswith("inferred: "))
            self.assertGreater(o["_conf"], 0)

    def test_the_doors_pass_the_validator(self):
        import export
        polys = self.plan()
        out, _ = self.infer(polys, target=(15.0, -3.0, "x"))
        rooms = [list(polys[i].exterior.coords)[:-1] for i in sorted(polys)]
        self.assertEqual(export.validate(rooms, out, [list(self.FOOT.exterior.coords)[:-1]]), [])

    def test_corners_on_a_neighbours_wall_keep_the_wall_shared(self):
        import export
        a = Polygon([(0, 0), (10, 0), (10, 5), (0, 5)])
        b = Polygon([(0, 5), (3.03, 5), (3.03, 8), (0, 8)])  # a T corner on a's wall, 3 cm off the 12.5 cm grid the old rounding used
        c = Polygon([(3.03, 5), (10, 5), (10, 8), (3.03, 8)])
        ra, rb, rc = export.node_and_snap([a, b, c])
        self.assertTrue(any(abs(p[0] - 3.03) < 1e-6 for p in ra.exterior.coords) is False)  # the straight wall lost the extra corner
        import doors
        self.assertEqual(len(doors.shared_walls({0: ra, 1: rb, 2: rc})), 3)

    def test_a_corridor_round_a_block_is_cut_in_parts_without_holes(self):
        import export
        ring = Polygon([(0, 0), (20, 0), (20, 10), (0, 10)], [[(5, 3), (15, 3), (15, 7), (5, 7)]])
        parts = export.split_holes(ring)
        self.assertGreater(len(parts), 1)
        self.assertTrue(all(not p.interiors for p in parts))
        self.assertAlmostEqual(sum(p.area for p in parts), ring.area, 3)

    def test_a_bow_tie_outline_of_a_scan_keeps_its_larger_part(self):
        import raster
        g = raster.as_polygon([(0, 0), (10, 10), (10, 0), (0, 10)])  # crosses itself: two triangles
        self.assertEqual(g.geom_type, "Polygon")
        self.assertAlmostEqual(g.area, 25.0, 3)
        sq = raster.as_polygon([(0, 0), (4, 0), (4, 4), (0, 4)])
        self.assertAlmostEqual(sq.area, 16.0, 3)


if __name__ == "__main__":
    unittest.main()
