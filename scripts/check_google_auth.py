#!/usr/bin/env python3
"""
Detect whether Eldorado has enabled the Cognito Hosted UI / Google (OAuth) login
on the seller BOT app client.

*** SUPERSEDED -- USE THE APP INSTEAD ***

    EldoradoApp.exe --check-google

Cloudflare now runs a MANAGED CHALLENGE over the whole login.eldorado.gg zone
(/oauth2/authorize and /oauth2/token alike), so this script -- and curl, and any
HttpClient -- can only ever get a 403 "Just a moment..." page back. It cannot read
the real state any more. The app's check drives the embedded WebView2, which solves
the challenge like a normal browser, and writes its verdict to
%AppData%\\EldoradoApp\\google-auth-check.txt.

STATUS as of 2026-08-11: ENABLED. /oauth2/authorize?identity_provider=Google now
redirects to accounts.google.com (Google client 818133653938-..., coming back to
https://login.eldorado.gg/oauth2/idpresponse). It used to answer "Login pages
unavailable"; that has flipped.

This file is kept because it still works from any network that isn't challenged,
and it documents the endpoint being probed. No credentials, nothing destructive.

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
        print(">> Cloudflare's managed challenge blocked this request -- expected, it covers "
              "the whole login.eldorado.gg zone. Run the in-app check instead, which uses a "
              "real browser engine:")
        print("     EldoradoApp.exe --check-google")
        print("   Verdict is written to %AppData%\\EldoradoApp\\google-auth-check.txt")
        print(">> Last known state (2026-08-11): ENABLED -- authorize redirects to Google.")
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
