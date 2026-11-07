"""Optional local vision model (LM Studio, OpenAI-style API): a vote on the page type where the classifier is unsure.

Never required, never in tests. Answers are cached by the page's content hash. Only the type is used: numbers a model
reads off a drawing are discarded, and a room label a model reads is kept only if the text layer has it too.
"""
import base64
import hashlib
import json
import os
import urllib.request

import cv2

import pdfio
from sortpages import TYPES

URL = "http://127.0.0.1:1234/v1/chat/completions"
MODEL = "qwen/qwen3.8-27b"
SCHEMA = {"type": "json_schema", "json_schema": {"name": "page", "strict": True, "schema": {
    "type": "object", "properties": {"type": {"type": "string", "enum": TYPES}}, "required": ["type"], "additionalProperties": False}}}
PROMPT = ("You sort pages of architectural documents (brochures, competition reports, press kits, scanned drawings) into ONE type.\n"
"floor_plan: a horizontal plan of ONE storey (or of one whole building seen from above) showing walls and rooms: CAD plans, coloured or schematic room plans, hand sketches of a plan, a roof plan. The page is mostly that single plan, whatever its size on the page.\n"
"site_plan: a plan of a site or district: buildings as footprints or black masses among streets, parcels, trees or terrain; a campus or master plan; a historic plan of a whole monastery or town.\n"
"section: a vertical cut through a building or terrain (long section, cross section, construction detail section). If the page is mostly ONE section drawing, even with a small title block or table, it is a section.\n"
"elevation: a facade view of a building from the front or side, drawn without a cut (also an axonometric or perspective line drawing of a facade, or a sheet of several facade views).\n"
"seating_plan: the seats, sectors, blocks or tribunes of a stadium or hall: usually coloured zones around a pitch or rink with sector letters or numbers, or an isometric view of the levels of an arena.\n"
"access_map: a road or transit map showing how to get to a place: streets, parking, bus and train stops, motorway exits; also a campus or city map with a legend of buildings.\n"
"mixed_sheet: one page that holds several separate technical drawings together, or several drawings or photos together with columns of text (a brochure page with plans and text, a competition panel, a page of thumbnails).\n"
"photo: only photographs, renderings or model photos, with at most a caption; no technical drawing and no body text.\n"
"text: a page that is mostly running text, a table, a list, a cover or a nearly empty page; one or two pictures beside long text still make it a text page.\n"
"Judge what is drawn on the page. The hint line says how much text and how many embedded pictures the file has (a scan has none: it is one picture). Answer with the type only.")


def _key(png, hint):
    return hashlib.sha1(png + hint.encode()).hexdigest() + "_" + MODEL + "_" + hashlib.sha1(PROMPT.encode()).hexdigest()[:8]


def hint_of(p):
    """What the file says about the page that a render does not: words in the text layer, embedded pictures."""
    return "\nHint: the page has %d words of text and %d embedded pictures." % (p.get("n_words", 0), p.get("n_images", 0))


def render(path, page, width=1000):
    """PNG bytes of a page, 1000 px on its long side."""
    pg = pdfio.open_doc(path)[page - 1]
    long_side = max(pg.rect.width, pg.rect.height)
    img = pdfio.render(pg, scale=width / long_side)
    return cv2.imencode(".png", img[:, :, ::-1])[1].tobytes()


def ask(png, hint="", max_tokens=40, timeout=300):
    """One page type from the model. Reasoning is off (reasoning_effort none): about 2.5 s a page instead of 45-280 s."""
    body = {"model": MODEL, "max_tokens": max_tokens, "temperature": 0, "reasoning_effort": "none", "response_format": SCHEMA, "messages": [
        {"role": "user", "content": [{"type": "text", "text": PROMPT + hint},
                                     {"type": "image_url", "image_url": {"url": "data:image/png;base64," + base64.b64encode(png).decode()}}]}]}
    req = urllib.request.Request(URL, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        out = json.load(r)
    return json.loads(out["choices"][0]["message"]["content"])["type"]


def keep_label(label, text_layer):
    """A model's room label survives only when the PDF text layer (or OCR) contains it too."""
    norm = lambda s: " ".join(s.lower().split())
    return bool(label) and norm(label) in norm(text_layer)


def vote(pages, root, out, labels=None, only_unsure=True, log=print):
    """Ask the model about every page that needs a vote (p["needs_vote"]); sets p["vlm_type"]. With `labels` and
    `only_unsure`, only labelled pages are asked (to score the model without running the whole corpus)."""
    cache_file = os.path.join(out, "_cache", "vlm.json")
    try:
        cache = json.load(open(cache_file))
    except (OSError, ValueError):
        cache = {}
    for p in pages:
        if not p.get("needs_vote") or p.get("error") or p.get("duplicate_of"):
            continue
        if labels is not None and only_unsure and (p["path"], p["page"]) not in labels:
            continue
        hint = hint_of(p)
        png = render(os.path.join(root, p["path"]), p["page"])
        k = _key(png, hint)
        if k not in cache:
            try:
                cache[k] = ask(png, hint)
            except Exception as e:  # the model is optional: keep the classifier's answer
                log("model failed on %s p%d: %s" % (p["path"], p["page"], e))
                continue
            os.makedirs(os.path.dirname(cache_file), exist_ok=True)
            json.dump(cache, open(cache_file, "w"))
        p["vlm_type"] = cache[k]
        log("model: %s p%d -> %s" % (p["path"], p["page"], cache[k]))
    for p in pages:  # a duplicate takes its original's vote
        if p.get("duplicate_of"):
            src = next((q for q in pages if "%s#%d" % (q["path"], q["page"]) == p["duplicate_of"]), None)
            if src is not None and src.get("vlm_type"):
                p["vlm_type"] = src["vlm_type"]
