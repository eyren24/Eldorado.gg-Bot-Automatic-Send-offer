#!/usr/bin/env python3
"""
Detect whether Eldorado has enabled the Cognito Hosted UI / Google (OAuth) login
on the seller BOT app client.

Today the bot client `1956req5ro9drdtbf5i6kis4la` has NO Hosted UI configured, so
hitting /oauth2/authorize returns "Login pages unavailable. Please contact an
administrator." When Eldorado turns on Google-supported auth for THIS client
(their "summer 2026" plan), that endpoint will instead serve a real login page or
redirect to Google -- that flip is the signal.

CAVEAT: this only detects case (A) -- same client gets Hosted UI enabled. If they
instead publish a NEW OAuth client id, this probe can't see it; you'd learn that
from api@eldorado.gg. So: DISABLED here = "not yet (on this client)", ENABLED =
"go", and either way confirm with Eldorado before rewiring the app.

Just GETs a public OAuth endpoint with our own client id (what a browser does when
you click "login"). No credentials, nothing destructive.

Usage:
  python check_google_auth.py            # one-shot check
  python check_google_auth.py --verbose  # also print status + a body snippet
"""

import argparse
import sys
import urllib.error
import urllib.parse
import urllib.request

CLIENT_ID = "1956req5ro9drdtbf5i6kis4la"
HOSTED_UI = "https://login.eldorado.gg"
REDIRECT = "https://eldorado.gg/account/auth-callback"
DISABLED_MARKER = "Login pages unavailable"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Capture 3xx instead of following -- a redirect to Google means Hosted UI is live."""
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def authorize_url() -> str:
    q = urllib.parse.urlencode({
        "client_id": CLIENT_ID,
        "response_type": "code",
        "scope": "openid email profile",
        "redirect_uri": REDIRECT,
    })
    return f"{HOSTED_UI}/oauth2/authorize?{q}"


def probe() -> tuple[str, int, str, str]:
    """Return (verdict, status, location, body_snippet)."""
    opener = urllib.request.build_opener(NoRedirect)
    req = urllib.request.Request(authorize_url(), method="GET",
                                 headers={"User-Agent": "Mozilla/5.0 eldorado-auth-probe"})
    status, location, body = 0, "", ""
    try:
        with opener.open(req, timeout=30) as resp:
            status = resp.status
            location = resp.headers.get("Location", "")
            body = resp.read(4000).decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        status = e.code
        location = e.headers.get("Location", "") if e.headers else ""
        body = e.read(4000).decode("utf-8", "replace") if e.fp else ""
    except urllib.error.URLError as e:
        return "ERROR", 0, "", f"network: {e.reason}"

    loc = location.lower()
    low = body.lower()
    if status == 403 and ("just a moment" in low or "cf-" in low or "challenge" in low
                          or "cloudflare" in low):
        verdict = "BLOCKED_CLOUDFLARE"
    elif DISABLED_MARKER.lower() in low:
        verdict = "DISABLED"
    elif status in (301, 302, 303, 307, 308) and ("google" in loc or "accounts.google" in loc):
        verdict = "ENABLED (redirects to Google)"
    elif status in (301, 302, 303, 307, 308):
        verdict = f"CHANGED (redirects to {location or '?'})"
    elif status == 200 and ("password" in body.lower() or "login" in body.lower()
                            or "sign in" in body.lower()):
        verdict = "ENABLED (login page served)"
    else:
        verdict = "CHANGED (unexpected response -- inspect manually)"
    return verdict, status, location, body


def main() -> int:
    ap = argparse.ArgumentParser(description="Probe Eldorado bot-client Hosted UI status.")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    verdict, status, location, body = probe()
    print(f"Hosted UI ({HOSTED_UI}, client {CLIENT_ID})")
    print(f"Verdict: {verdict}")
    if args.verbose:
        print(f"  HTTP {status}")
        if location:
            print(f"  Location: {location}")
        snippet = " ".join(body.split())[:300]
        print(f"  Body: {snippet}")

    if verdict == "BLOCKED_CLOUDFLARE":
        print(">> Cloudflare anti-bot blocked the automated request -- this probe can't read "
              "the real state. Check manually in a REAL browser instead:")
        print(f"   {authorize_url()}")
        print(">>   'Login pages unavailable' = still OFF | a Cognito/Google login = ON.")
        return 2
    if verdict == "DISABLED":
        print(">> Google/OAuth still OFF on the bot client. Keep using (or waiting for) "
              "email+password, and re-check later.")
        return 0
    if verdict.startswith("ENABLED"):
        print(">> SIGNAL: Hosted UI looks live on the bot client! Re-test the OAuth/PKCE "
              "flow, and confirm with api@eldorado.gg before rewiring the app.")
        return 10  # distinct code so a scheduler/loop can react
    if verdict == "ERROR":
        print(">> Could not reach the endpoint; check connectivity and retry.")
        return 1
    print(">> Behavior changed from the known 'unavailable' state -- inspect with --verbose.")
    return 10


if __name__ == "__main__":
    sys.exit(main())
