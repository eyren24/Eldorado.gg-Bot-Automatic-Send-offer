#!/usr/bin/env python3
"""
Dump what a boosting request really contains.

The received-requests feed carries only 8 fields and NO rank — the job description lives
on the request itself:

    GET /api/boostingOffers/boostingRequests/{id}/details   -> the buyer's form answers
    GET /api/boosting/formConfig/{gameId}/{categoryId}      -> what those answer ids mean

For Valorant rank boosts input 26 is "Current Rank", 53 "Desired Rank", 60 "Server".
This script fetches all three and prints the answers with their field names, so the bot's
mapping can be checked against live data.

Usage:
  1. Log in to eldorado.gg in the browser.
  2. DevTools -> Application -> Cookies -> copy the value of __Host-EldoradoIdToken.
  3. Put ONLY that value (eyJ...) into scripts/token.txt — nothing else, no console output.
  4. python scripts/dump_request_payload.py
"""

import json
import os
import sys
import urllib.error
import urllib.request

BASE = "https://www.eldorado.gg"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36")

HERE = os.path.dirname(os.path.abspath(__file__))


def get(path: str, token: str | None = None):
    headers = {"Accept": "application/json", "User-Agent": UA}
    if token:
        headers["Cookie"] = f"__Host-EldoradoIdToken={token}"
    req = urllib.request.Request(BASE + path, method="GET", headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace") if e.fp else ""
    except Exception as e:                       # noqa: BLE001 - diagnostic script
        return -1, f"{type(e).__name__}: {e}"


def read_token() -> str:
    path = os.path.join(HERE, "token.txt")
    if not os.path.exists(path):
        sys.exit(f"Manca {path}: incolla il cookie __Host-EldoradoIdToken.")
    with open(path, encoding="utf-8") as f:
        token = f.read().strip().strip('"')
    if not token.startswith("ey") or "\n" in token or " " in token:
        sys.exit("token.txt non contiene un JWT pulito. Deve avere SOLO il valore "
                 "del cookie (inizia con 'ey', una riga, nessuno spazio).")
    return token


def field_names(game: str, category: str):
    """input id -> (title, {option id: option name}) from the public form schema."""
    status, body = get(f"/api/boosting/formConfig/{game}/{category}")
    if status != 200:
        print(f"  formConfig {game}/{category}: HTTP {status}")
        return {}

    with open(os.path.join(HERE, f"formconfig-{game}-{category}.json"),
              "w", encoding="utf-8") as f:
        f.write(body)

    schema, names = json.loads(body), {}

    def collect(node):
        if isinstance(node, dict):
            for inp in node.get("inputs") or []:
                if isinstance(inp, dict) and "id" in inp:
                    options = {v["id"]: v["name"] for v in (inp.get("values") or [])
                               if isinstance(v, dict) and "id" in v and "name" in v}
                    names[inp["id"]] = (inp.get("title") or "", options)
            for value in node.values():
                collect(value)
        elif isinstance(node, list):
            for value in node:
                collect(value)

    collect(schema)
    return names


def find_description_values(node):
    """The descriptionValues array, wherever the envelope puts it."""
    if isinstance(node, dict):
        if isinstance(node.get("descriptionValues"), list):
            return node["descriptionValues"]
        for value in node.values():
            found = find_description_values(value)
            if found is not None:
                return found
    elif isinstance(node, list):
        for value in node:
            found = find_description_values(value)
            if found is not None:
                return found
    return None


def main():
    # Buyers write emoji in the free-text fields; the Windows console is cp1252.
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

    token = read_token()

    status, body = get("/api/boostingOffers/me/boostingRequests/received"
                       "?filter=ActiveRequests&pageSize=50", token)
    print(f"received: HTTP {status}, {len(body)} byte")
    if status == 401:
        sys.exit("401: token scaduto, ricopialo dal browser.")
    if status != 200:
        sys.exit(f"Risposta inattesa:\n{body[:1000]}")

    with open(os.path.join(HERE, "received-raw.json"), "w", encoding="utf-8") as f:
        f.write(body)

    results = json.loads(body).get("results", [])
    print(f"{len(results)} richieste\n")
    if not results:
        return

    schemas = {}
    ok = 0

    for item in results[:8]:
        rid, game = item["id"], item.get("gameId", "")
        category = item.get("boostingCategoryId", "")
        print(f"--- {rid}  ·  {item.get('boostingCategoryTitle')}  "
              f"(game {game}, cat {category}) ---")

        key = (game, category)
        if key not in schemas:
            schemas[key] = field_names(game, category)
        names = schemas[key]

        code, detail = get(f"/api/boostingOffers/boostingRequests/{rid}/details", token)
        if code != 200:
            code2, detail2 = get(f"/api/boostingOffers/boostingRequests/{rid}", token)
            print(f"    /details -> HTTP {code}; /{'{id}'} -> HTTP {code2}")
            if code2 != 200:
                continue
            detail = detail2

        with open(os.path.join(HERE, f"detail-{rid}.json"), "w", encoding="utf-8") as f:
            f.write(detail)

        values = find_description_values(json.loads(detail))
        if values is None:
            print(f"    nessun descriptionValues; envelope: {detail[:300]}")
            continue

        ok += 1
        for entry in values:
            fid = entry.get("id")
            raw = entry.get("value")
            title, options = names.get(fid, (f"(campo {fid})", {}))
            label = options.get(raw, raw) if not isinstance(raw, str) else raw
            if isinstance(raw, str) and raw.isdigit() and int(raw) in options:
                label = options[int(raw)]
            print(f"    [{fid:>3}] {title:<36} = {label!r}")
        print()

    print(f"{ok} richieste con dettagli leggibili. "
          f"I JSON sono in {HERE} (detail-*.json, formconfig-*.json).")


if __name__ == "__main__":
    main()
