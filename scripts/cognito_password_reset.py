#!/usr/bin/env python3
"""
Probe / drive the AWS Cognito ForgotPassword flow for an Eldorado seller account.

These are PUBLIC, UNAUTHENTICATED Cognito User Pool operations (the same ones the
"Forgot password?" link on login.eldorado.gg uses). No AWS credentials, no request
signing required -- just the public app-client id. We use this to observe how the
system responds for a Google-federated account (does a native password user exist?).

Pool config is the public Eldorado seller config (not a secret):
  region   us-east-2
  pool     us-east-2_MlnzCFgHk
  clientId 1956req5ro9drdtbf5i6kis4la   (public SPA client, no client secret)

Usage:
  # Step 1 - ask Cognito to start a reset (sends a code to the email IF a native user exists)
  python cognito_password_reset.py request --email you@example.com

  # Step 2 - set the new password using the code that arrived by email
  python cognito_password_reset.py confirm --email you@example.com --code 123456 --password 'NewPass123!'

Interpreting the result of `request`:
  * CodeDeliveryDetails returned        -> a native email/password user EXISTS; reset is possible.
  * InvalidParameterException
      "...no registered/verified email..."-> user exists but has no usable password path (often federated).
  * UserNotFoundException                -> no native user with that email (may be hidden for privacy).
  * NotAuthorizedException               -> account disabled / federated-only / client misconfig.
  * LimitExceededException               -> too many attempts, wait and retry.
"""

import argparse
import json
import sys
import urllib.error
import urllib.request

REGION = "us-east-2"
CLIENT_ID = "1956req5ro9drdtbf5i6kis4la"
ENDPOINT = f"https://cognito-idp.{REGION}.amazonaws.com/"

# Default to the account email on file; override with --email.
DEFAULT_EMAIL = "elothrower0@gmail.com"


def call(target: str, payload: dict) -> tuple[int, dict]:
    """POST a Cognito IDP action and return (http_status, parsed_json_body)."""
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        ENDPOINT,
        data=body,
        method="POST",
        headers={
            "Content-Type": "application/x-amz-json-1.1",
            "X-Amz-Target": f"AWSCognitoIdentityProviderService.{target}",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            raw = resp.read().decode("utf-8") or "{}"
            return resp.status, json.loads(raw)
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8") or "{}"
        try:
            parsed = json.loads(raw)
        except json.JSONDecodeError:
            parsed = {"raw": raw}
        return e.code, parsed
    except urllib.error.URLError as e:
        return 0, {"__type": "NetworkError", "message": str(e.reason)}


def show(status: int, data: dict) -> None:
    print(f"\nHTTP {status}")
    print(json.dumps(data, indent=2, ensure_ascii=False))
    err = data.get("__type")
    if err:
        short = err.split("#")[-1]
        print(f"\n>> Cognito error type: {short}")
        msg = data.get("message", "")
        if short == "InvalidParameterException" and "verified" in msg.lower():
            print(">> Reading: a user exists but has no password-resettable email/phone "
                  "(typical of a Google-federated account).")
        elif short == "UserNotFoundException":
            print(">> Reading: no native email/password user with this email in the pool.")
        elif short == "NotAuthorizedException":
            print(">> Reading: blocked -- likely federated-only / disabled / client secret required.")
        elif short == "LimitExceededException":
            print(">> Reading: rate-limited. Wait a while before retrying.")
    elif "CodeDeliveryDetails" in data:
        d = data["CodeDeliveryDetails"]
        print(f"\n>> SUCCESS: a reset code was sent via {d.get('DeliveryMedium')} "
              f"to {d.get('Destination')}.")
        print(">> A native password account EXISTS. Run the 'confirm' step with the code.")


def cmd_request(args):
    print(f"ForgotPassword  email={args.email}  clientId={CLIENT_ID}")
    status, data = call("ForgotPassword", {"ClientId": CLIENT_ID, "Username": args.email})
    show(status, data)


def cmd_confirm(args):
    print(f"ConfirmForgotPassword  email={args.email}")
    status, data = call(
        "ConfirmForgotPassword",
        {
            "ClientId": CLIENT_ID,
            "Username": args.email,
            "ConfirmationCode": args.code,
            "Password": args.password,
        },
    )
    show(status, data)
    if status == 200 and not data.get("__type"):
        print("\n>> Password set. You can now authenticate via SRP (email + password).")


def main():
    p = argparse.ArgumentParser(description="Cognito ForgotPassword probe for Eldorado.")
    sub = p.add_subparsers(dest="cmd", required=True)

    pr = sub.add_parser("request", help="start a password reset (sends a code)")
    pr.add_argument("--email", default=DEFAULT_EMAIL)
    pr.set_defaults(func=cmd_request)

    pc = sub.add_parser("confirm", help="finish reset with the emailed code")
    pc.add_argument("--email", default=DEFAULT_EMAIL)
    pc.add_argument("--code", required=True)
    pc.add_argument("--password", required=True)
    pc.set_defaults(func=cmd_confirm)

    args = p.parse_args()
    args.func(args)


if __name__ == "__main__":
    sys.exit(main())
