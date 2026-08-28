#!/usr/bin/env python3
"""
Dump the RAW payload of the received boosting requests, untouched and untruncated.

Why: the bot reads the rank range out of the request text, and right now every request
comes back "range di rank non riconosciuto". Before touching the parser we need to see
what the server actually sends — which fields exist, and where (if anywhere) the buyer's
current/desired rank lives.

Usage:
  1. Log in to eldorado.gg in the browser.
  2. DevTools -> Application -> Cookies -> copy the value of __Host-EldoradoIdToken.
  3. Save it into scripts/token.txt (just the eyJ... value, nothing else).
  4. python scripts/dump_request_payload.py

Writes scripts/received-raw.json (the whole response) and prints, for the first few
requests, every field name it carries plus anything that looks rank-shaped.
"""

import json
import os
import re
import sys
import urllib.error
import urllib.request

BASE = "https://www.eldorado.gg"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36")

HERE = os.path.dirname(os.path.abspath(__file__))

RANK_WORDS = re.compile(
    r"iron|bronze|silver|gold|platinum|plat|diamond|ascendant|immortal|radiant|"
    r"rank|tier|division|current|desired|target|from|to|level",
    re.IGNORECASE)


def get(path: str, token: str):
    req = urllib.request.Request(BASE + path, method="GET", headers={
        "Cookie": f"__Host-EldoradoIdToken={token}",
        "Accept": "application/json",
        "User-Agent": UA,
    })
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            body = r.read().decode("utf-8", "replace")
            return r.status, body
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace") if e.fp else ""


def walk(node, path=""):
    """Yield (dotted-path, value) for every leaf in the payload."""
    if isinstance(node, dict):
        for key, value in node.items():
            yield from walk(value, f"{path}.{key}" if path else key)
    elif isinstance(node, list):
        for i, value in enumerate(node):
            yield from walk(value, f"{path}[{i}]")
    else:
        yield path, node


def read_token() -> str:
    token_path = os.path.join(HERE, "token.txt")
    if not os.path.exists(token_path):
        sys.exit(f"Manca {token_path}. Incolla dentro il valore del cookie "
                 f"__Host-EldoradoIdToken preso da eldorado.gg.")
    with open(token_path, encoding="utf-8") as f:
        token = f.read().strip().strip('"')
    if not token.startswith("ey"):
        sys.exit("Il token non sembra un JWT (deve iniziare con 'ey').")
    return token


def main():
    token = read_token()

    status, body = get(
        "/api/boostingOffers/me/boostingRequests/received"
        "?filter=ActiveRequests&pageSize=50", token)
    print(f"received: HTTP {status}, {len(body)} byte")

    if status == 401:
        sys.exit("401: il token è scaduto. Ricopialo dal browser.")
    if status != 200:
        sys.exit(f"Risposta inattesa:\n{body[:2000]}")

    out = os.path.join(HERE, "received-raw.json")
    with open(out, "w", encoding="utf-8") as f:
        f.write(body)
    print(f"payload completo salvato in {out}\n")

    payload = json.loads(body)
    results = payload.get("results", []) if isinstance(payload, dict) else []
    print(f"{len(results)} richieste nella pagina\n")

    if not results:
        return

    # Every field name that appears anywhere, so nothing gets missed.
    fields = {}
    for item in results:
        for path, value in walk(item):
            fields.setdefault(re.sub(r"\[\d+\]", "[]", path), set()).add(
                type(value).__name__)

    print("=== TUTTI I CAMPI PRESENTI NELLA LISTA ===")
    for path in sorted(fields):
        marker = "  <-- possibile rank" if RANK_WORDS.search(path) else ""
        print(f"  {path}  ({'/'.join(sorted(fields[path]))}){marker}")

    print("\n=== VALORI CHE SEMBRANO RANK (primi 5 item) ===")
    for item in results[:5]:
        title = item.get("boostingCategoryTitle")
        print(f"\n--- {item.get('id')} · {title} ---")
        hits = [(p, v) for p, v in walk(item)
                if isinstance(v, str) and RANK_WORDS.search(v)]
        if hits:
            for path, value in hits:
                print(f"    {path} = {value!r}")
        else:
            print("    nessun valore rank-shaped in questo item")

    # Does a per-request detail endpoint carry more than the list does?
    first = results[0].get("id")
    print(f"\n=== PROVA ENDPOINT DI DETTAGLIO per {first} ===")
    for path in (
        f"/api/boostingOffers/me/boostingRequests/{first}",
        f"/api/boostingRequests/{first}",
        f"/api/boostingRequests/{first}/details",
    ):
        code, detail = get(path, token)
        print(f"  GET {path} -> HTTP {code} ({len(detail)} byte)")
        if code == 200 and detail:
            name = os.path.join(HERE, f"detail-{first}.json")
            with open(name, "w", encoding="utf-8") as f:
                f.write(detail)
            print(f"    salvato in {name}")
            print(f"    anteprima: {detail[:1200]}")
            break


if __name__ == "__main__":
    main()
