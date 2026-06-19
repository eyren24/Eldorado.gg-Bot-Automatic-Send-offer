#!/usr/bin/env python3
"""
Test a real Eldorado IdToken (grabbed from a logged-in browser session) against the
Seller API. Answers two questions at once:
  1. Does the API accept the token?           -> authentication works end-to-end
  2. Are private endpoints authorized?         -> the bot/account is whitelisted

The token is sent exactly as the docs require: cookie `__Host-EldoradoIdToken=<jwt>`.
It also decodes (NOT verifies) the JWT so we can read which Cognito client issued it
(`aud`), when it expires, and the account it belongs to.

Put the raw JWT (the long `eyJ...` value of the cookie / local-storage token) in a file
and pass it; nothing is sent anywhere except api.eldorado.gg.

Usage:
  python test_api_token.py --token-file scripts/token.txt
"""

import argparse
import base64
import json
import sys
import urllib.error
import urllib.request

BASE = "https://www.eldorado.gg"
COOKIE_NAME = "__Host-EldoradoIdToken"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36")

# Private endpoints to probe: eligibility gate + the real bot data pipeline.
PROBES = [
    ("GET", "/api/v1/orders/me/seller-api-eligibility"),
    ("GET", "/api/v1/orders/me/seller/orders?displayFilter=DisplaySellingOrders"),
    ("GET", "/api/boostingOffers/me/boostingSubscriptions"),
    ("GET", "/api/boostingOffers/me/boostingRequests/received?filter=ActiveRequests"),
    ("GET", "/api/notifications/me/unreadCount"),
]


def decode_jwt(token: str) -> dict:
    try:
        payload = token.split(".")[1]
        payload += "=" * (-len(payload) % 4)
        return json.loads(base64.urlsafe_b64decode(payload))
    except Exception as e:  # noqa: BLE001
        return {"_decode_error": str(e)}


def call(method: str, path: str, token: str) -> tuple[int, str]:
    req = urllib.request.Request(
        BASE + path,
        method=method,
        headers={
            "Cookie": f"{COOKIE_NAME}={token}",
            "Accept": "application/json",
            "User-Agent": UA,
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return resp.status, resp.read(6000).decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, (e.read(6000).decode("utf-8", "replace") if e.fp else "")
    except urllib.error.URLError as e:
        return 0, f"network: {e.reason}"


def classify(status: int, body: str) -> str:
    low = body.lower()
    if status == 200:
        return "OK — authenticated AND authorized"
    if status in (401, 403) and ("just a moment" in low or "cloudflare" in low or "cf-" in low):
        return "BLOCKED by Cloudflare (not an auth verdict) — test from the app/browser instead"
    if status == 401:
        return "401 — token rejected (expired/invalid) or not signed in"
    if status == 403:
        return "403 — token accepted but NOT authorized (bot needs api@eldorado.gg approval)"
    return f"HTTP {status}"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--token-file", required=True)
    args = ap.parse_args()

    with open(args.token_file, encoding="utf-8") as f:
        token = f.read().strip().strip('"')

    if token.count(".") != 2:
        print("That doesn't look like a JWT (expected three dot-separated parts). "
              "Copy the raw eyJ... value.")
        return 1

    claims = decode_jwt(token)
    print("Token claims (decoded, signature NOT verified):")
    for k in ("aud", "iss", "token_use", "sub", "email", "cognito:username", "exp"):
        if k in claims:
            print(f"  {k}: {claims[k]}")
    if claims.get("aud"):
        same = "SAME as bot client" if claims["aud"] == "1956req5ro9drdtbf5i6kis4la" else "DIFFERENT client"
        print(f"  -> issued for {same}")
    print()

    for method, path in PROBES:
        status, body = call(method, path, token)
        print(f"{method} {path}")
        print(f"  -> {classify(status, body)}")
        snippet = " ".join(body.split())[:600]
        if snippet:
            print(f"  body: {snippet}")
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
