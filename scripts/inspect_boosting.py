#!/usr/bin/env python3
"""
Inspect the live boosting data to decide the pricing model:
  - distinct games (gameId -> gameName) so we can find Valorant's id
  - per game, its boosting categories (to see how granular they are: one "Rank Boost"
    vs one per rank/region)
  - the received requests grouped by game, with sample category titles

Needs a FRESH IdToken in scripts/token.txt (the cookie value, eyJ...).
  python inspect_boosting.py
"""

import collections
import json
import urllib.error
import urllib.request

BASE = "https://www.eldorado.gg"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36")


def get(path: str, token: str):
    req = urllib.request.Request(BASE + path, method="GET", headers={
        "Cookie": f"__Host-EldoradoIdToken={token}",
        "Accept": "application/json",
        "User-Agent": UA,
    })
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, json.loads(r.read().decode("utf-8", "replace") or "null")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace") if e.fp else ""


def main():
    with open("scripts/token.txt", encoding="utf-8") as f:
        token = f.read().strip().strip('"')

    # 1) Subscriptions -> games + categories
    status, subs = get("/api/boostingOffers/me/boostingSubscriptions", token)
    print(f"subscriptions: HTTP {status}, {len(subs) if isinstance(subs, list) else '?'} rows\n")
    if isinstance(subs, list):
        games = {}
        cats = collections.defaultdict(list)
        for s in subs:
            gid, gname = s.get("gameId"), s.get("gameName")
            games[gid] = gname
            cats[gid].append((s.get("boostingCategoryId"), s.get("boostingCategoryName"),
                              s.get("isSubscribed")))
        print("=== GAMES (gameId -> name, #categories) ===")
        for gid, gname in sorted(games.items(), key=lambda x: (x[1] or "")):
            sub_count = sum(1 for c in cats[gid] if c[2])
            print(f"  {gid:>5}  {gname}   ({len(cats[gid])} categorie, {sub_count} iscritte)")
        print("\n=== categories for games whose name contains 'valorant' ===")
        for gid, gname in games.items():
            if gname and "valorant" in gname.lower():
                print(f"  gameId {gid} = {gname}:")
                for cid, cname, issub in cats[gid]:
                    print(f"      [{cid}] {cname}  (iscritto: {issub})")

    # 2) Received requests grouped by game
    status, recv = get("/api/boostingOffers/me/boostingRequests/received?filter=ActiveRequests&pageSize=50", token)
    results = recv.get("results", []) if isinstance(recv, dict) else []
    print(f"\nreceived ActiveRequests: HTTP {status}, {len(results)} on this page")
    by_game = collections.Counter(r.get("gameId") for r in results)
    titles = collections.defaultdict(set)
    for r in results:
        titles[r.get("gameId")].add(r.get("boostingCategoryTitle"))
    print("=== received grouped by gameId ===")
    for gid, n in by_game.most_common():
        print(f"  gameId {gid}: {n} richieste · titoli: {sorted(titles[gid])}")


if __name__ == "__main__":
    main()
