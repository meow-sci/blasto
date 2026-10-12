#!/usr/bin/env python3
"""Search the KSA modding knowledge base index.

Usage:
  python3 scripts/search.py <terms...> [--type how-to|feature|mod|topic|submod]
                            [--category X] [--mod X] [--lifecycle TOKEN] [--limit 15]

Scoring (case-insensitive, per term): title x5, tags/techniques/hooks/game_types x3,
description x2, path x1. All terms must match somewhere (AND); falls back to OR if none do.
Output: one line per hit: <path>  [<type>|<difficulty>]  <title> — <description>
Paths are relative to the skill root. Needs Python 3.8+ (stdlib only).
"""
import argparse
import json
import os
import sys

TYPES = {
    "how-to": "How-To",
    "feature": "Mod Feature",
    "mod": "Mod",
    "topic": "Game Feature",
    "submod": "Submod",
}


def load_index():
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, "..", "assets", "index.jsonl")
    if not os.path.isfile(path):
        sys.exit("error: search index not found at %s\n"
                 "The skill looks incomplete; rebuild it with `node scripts/skill_build.ts` "
                 "in the ksa-coffer repo, or grep references/ directly." % os.path.normpath(path))
    rows = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def as_list(v):
    if v is None:
        return []
    return v if isinstance(v, list) else [v]


def score_row(row, terms):
    title = (row.get("title") or "").lower()
    desc = (row.get("description") or "").lower()
    path = (row.get("path") or "").lower()
    keys = " ".join(str(x) for k in ("tags", "techniques", "hooks", "game_types")
                    for x in as_list(row.get(k))).lower()
    total = 0
    matched = 0
    for t in terms:
        s = 0
        if t in title:
            s += 5
        if t in keys:
            s += 3
        if t in desc:
            s += 2
        if t in path:
            s += 1
        if s:
            matched += 1
        total += s
    return total, matched


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("terms", nargs="*")
    ap.add_argument("--type", choices=sorted(TYPES))
    ap.add_argument("--category")
    ap.add_argument("--mod")
    ap.add_argument("--lifecycle")
    ap.add_argument("--limit", type=int, default=15)
    a = ap.parse_args()

    rows = load_index()
    if a.type:
        rows = [r for r in rows if r.get("type") == TYPES[a.type]]
    if a.category:
        rows = [r for r in rows if (r.get("category") or "").lower() == a.category.lower()]
    if a.mod:
        rows = [r for r in rows if (r.get("mod") or "").lower() == a.mod.lower()]
    if a.lifecycle:
        lc = a.lifecycle.lower()
        rows = [r for r in rows if any(lc == str(x).lower() or str(x).lower().startswith(lc)
                                       for x in as_list(r.get("lifecycle")))]

    terms = [t.lower() for t in a.terms]
    if terms:
        scored = [(score_row(r, terms), r) for r in rows]
        hits = [(s, r) for (s, m), r in scored if m == len(terms) for s in [s]]
        if not hits:
            hits = [(s, r) for (s, m), r in scored if m > 0 for s in [s]]
            if hits:
                print("note: no result matches all terms; showing results matching any term")
        hits.sort(key=lambda x: (-x[0], x[1].get("path", "")))
    else:
        hits = [(0, r) for r in sorted(rows, key=lambda r: r.get("path", ""))]

    if not hits:
        print("no results")
        return
    for _, r in hits[:max(a.limit, 1)]:
        desc = " ".join((r.get("description") or "").split())
        if len(desc) > 140:
            desc = desc[:139].rstrip() + "..."
        kind = r.get("type", "?")
        if r.get("difficulty"):
            kind += "|" + r["difficulty"]
        print("%s  [%s]  %s — %s" % (r.get("path"), kind, r.get("title"), desc))
    if len(hits) > a.limit:
        print("(%d more; raise --limit)" % (len(hits) - a.limit))


if __name__ == "__main__":
    main()
