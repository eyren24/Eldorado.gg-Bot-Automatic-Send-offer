#!/usr/bin/env python3
"""
Authenticate to the Eldorado seller Cognito pool via USER_SRP_AUTH (email + password)
and print the IdToken -- the exact flow the official Eldorado API docs require
("Email + Password login method is required").

Public pool config (not secret):
  region   us-east-2
  pool     us-east-2_MlnzCFgHk
  client   1956req5ro9drdtbf5i6kis4la   (public SPA client, no client secret)

The SRP calls need NO AWS credentials (sent unsigned). Requires: boto3, pycognito.

Usage:
  python cognito_srp_login.py --email elothrower0@gmail.com --password 'YourPass123!'
"""

import argparse
import base64
import json
import sys

import boto3
from botocore import UNSIGNED
from botocore.config import Config
from botocore.exceptions import ClientError
from pycognito.aws_srp import AWSSRP

REGION = "us-east-2"
POOL_ID = "us-east-2_MlnzCFgHk"
CLIENT_ID = "1956req5ro9drdtbf5i6kis4la"

HINTS = {
    "NotAuthorizedException": "Wrong password, OR the account has no native password "
                              "(Google-federated only). If you just set a password, double-check it.",
    "UserNotFoundException": "No native email/password user with this email in the pool.",
    "PasswordResetRequiredException": "Cognito requires a password reset first -- run "
                                      "cognito_password_reset.py (request + confirm), then retry.",
    "UserNotConfirmedException": "The user exists but is not confirmed.",
    "InvalidParameterException": "Bad parameter -- often the user can't do SRP (e.g. federated).",
}


def decode_jwt(token: str) -> dict:
    """Decode (NOT cryptographically verify) a JWT payload for inspection."""
    try:
        payload = token.split(".")[1]
        payload += "=" * (-len(payload) % 4)
        return json.loads(base64.urlsafe_b64decode(payload))
    except Exception as e:  # noqa: BLE001
        return {"_decode_error": str(e)}


def main() -> int:
    ap = argparse.ArgumentParser(description="Eldorado Cognito SRP login.")
    ap.add_argument("--email", required=True)
    ap.add_argument("--password", required=True)
    ap.add_argument("--show-token", action="store_true",
                    help="print the full IdToken (sensitive)")
    args = ap.parse_args()

    client = boto3.client("cognito-idp", region_name=REGION,
                          config=Config(signature_version=UNSIGNED))
    srp = AWSSRP(username=args.email, password=args.password,
                 pool_id=POOL_ID, client_id=CLIENT_ID, client=client)

    print(f"USER_SRP_AUTH  email={args.email}  pool={POOL_ID}  client={CLIENT_ID}")
    try:
        resp = srp.authenticate_user()
    except ClientError as e:
        err = e.response["Error"]
        code, msg = err["Code"], err["Message"]
        print(f"\nLOGIN FAILED: {code} - {msg}")
        if code in HINTS:
            print(f">> {HINTS[code]}")
        return 1
    except Exception as e:  # noqa: BLE001
        print(f"\nLOGIN ERROR (non-AWS): {type(e).__name__} - {e}")
        return 1

    challenge = resp.get("ChallengeName")
    if challenge:
        print(f"\nADDITIONAL CHALLENGE REQUIRED: {challenge}")
        print(json.dumps(resp.get("ChallengeParameters", {}), indent=2))
        print(">> Login did not complete -- e.g. MFA or NEW_PASSWORD_REQUIRED.")
        return 2

    res = resp["AuthenticationResult"]
    id_token = res["IdToken"]
    print("\nLOGIN OK -- tokens received.")
    print(f"  IdToken      len={len(id_token)}")
    print(f"  AccessToken  len={len(res.get('AccessToken', ''))}")
    print(f"  RefreshToken {'present' if 'RefreshToken' in res else 'absent'}")
    print(f"  ExpiresIn    {res.get('ExpiresIn')}s   TokenType={res.get('TokenType')}")

    claims = decode_jwt(id_token)
    keys = ("email", "email_verified", "sub", "cognito:username",
            "token_use", "iss", "aud", "exp", "identities")
    interesting = {k: claims[k] for k in keys if k in claims}
    print("\nIdToken claims (decoded, signature NOT verified):")
    print(json.dumps(interesting, indent=2, ensure_ascii=False, default=str))
    if "identities" in claims:
        print(">> NOTE: 'identities' present -> account is also linked to a federated IdP (e.g. Google).")

    if args.show_token:
        print("\nFull IdToken (send as cookie: __Host-EldoradoIdToken=<this>):")
        print(id_token)
    else:
        print("\n(Re-run with --show-token to print the full IdToken for API calls.)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
