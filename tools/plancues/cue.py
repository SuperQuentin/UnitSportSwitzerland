"""The cue file: one JSON per building, the stages fill it in turn (scale, extract, place); export and review read it."""
import json
import os
import re

TOP_KEYS = ("version", "venue", "status", "reasons", "sources", "scale", "placement", "levels", "facts")


def cue_dir(out, venue):
    return os.path.join(out, venue, "cues")


def cue_name(rel_path, page, region=None):
    stem = re.sub(r"[^A-Za-z0-9]+", "_", os.path.splitext(os.path.basename(rel_path))[0]).strip("_")
    return "%s_p%d%s" % (stem, page, "" if region is None else "_r%d" % region)


def cue_path(out, venue, name):
    return os.path.join(cue_dir(out, venue), name + ".json")


def new_cue(venue, rel_path, page, bbox, kind):
    return dict(version=1, venue=venue, status="rejected", reasons=["not processed"],
                sources=[dict(path=rel_path, page=page, bbox=[round(v, 2) for v in bbox], kind=kind)],
                scale=None, placement=None, levels=[], facts=[])


def add_fact(cue, key, value, unit=None, source=0, confidence=0.5):
    cue["facts"] = [f for f in cue["facts"] if f["key"] != key]
    cue["facts"].append(dict(key=key, value=value, unit=unit, source=source, confidence=round(confidence, 2)))


def fact(cue, key, default=None):
    for f in cue["facts"]:
        if f["key"] == key:
            return f["value"]
    return default


def save(path, cue):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    clean = {k: cue[k] for k in TOP_KEYS}
    with open(path, "w", encoding="utf-8") as f:
        json.dump(clean, f, indent=1, ensure_ascii=False, default=float)


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def list_cues(out, venue):
    d = cue_dir(out, venue)
    if not os.path.isdir(d):
        return []
    return sorted(os.path.join(d, f) for f in os.listdir(d) if f.endswith(".json"))


def set_status(cue, status, reasons):
    order = {"ok": 0, "low_confidence": 1, "rejected": 2}
    if status is None:
        return
    if order[status] >= order.get(cue["status"], 2) or cue["reasons"] == ["not processed"]:
        cue["status"] = status
    cue["reasons"] = [r for r in cue["reasons"] if r != "not processed"] + [r for r in reasons if r not in cue["reasons"]]
