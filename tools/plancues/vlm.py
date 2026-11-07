"""Optional local vision model (LM Studio, OpenAI-style API): a vote on the page type where the rules are unsure.

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
PROMPT = ("Classify this page of an architectural document set. Answer with one type: floor_plan (a plan of one storey), "
          "site_plan (a plan of the building in its surroundings or an area plan), section (a cut through a building), "
          "elevation (a facade view), seating_plan (seats, sectors or blocks of an arena), access_map (how to get there: roads, stops, "
          "parking), mixed_sheet (several drawings or photos on one sheet), photo, text. Look at the drawing, not at the file name.")


def _key(path, page, png):
    return hashlib.sha1(png).hexdigest() + "_" + MODEL


def ask(png, max_tokens=12000, timeout=900):
    body = {"model": MODEL, "max_tokens": max_tokens, "temperature": 0, "response_format": SCHEMA, "messages": [
        {"role": "user", "content": [{"type": "text", "text": PROMPT},
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
    cache_file = os.path.join(out, "_cache", "vlm.json")
    try:
        cache = json.load(open(cache_file))
    except (OSError, ValueError):
        cache = {}
    for p in pages:
        if not p.get("unsure") or p.get("error"):
            continue
        if labels is not None and only_unsure and (p["path"], p["page"]) not in labels:
            continue
        doc = pdfio.open_doc(os.path.join(root, p["path"]))
        page = doc[p["page"] - 1]
        img = pdfio.render(page, width_px=1400)
        ok, buf = cv2.imencode(".png", img[:, :, ::-1])
        png = buf.tobytes()
        k = _key(p["path"], p["page"], png)
        if k not in cache:
            try:
                cache[k] = ask(png)
            except Exception as e:  # the model is optional: keep the rules' answer
                log("model failed on %s p%d: %s" % (p["path"], p["page"], e))
                continue
            os.makedirs(os.path.dirname(cache_file), exist_ok=True)
            json.dump(cache, open(cache_file, "w"))
        p["vlm_type"] = cache[k]
        if cache[k] in TYPES:
            p["type"] = cache[k]
        log("model: %s p%d rules %s -> %s" % (p["path"], p["page"], p["rule_type"], p["type"]))
