"""Compose smoke: register -> verify -> login and webhook -> inbox.

Booted by CI (`compose-empty-db`) and usable from the local gate. Requires the full
Compose stack (api + frontend on $API_URL, mailpit on $MAILPIT_URL) with the Development
verify token. No Meta secrets are required: the webhook handshake and signature rejection
prove the verification path with the Development token, and the authenticated inbox read
proves the registered tenant can query through the app_runtime role.
"""
import json
import os
import time
import uuid
import urllib.error
import urllib.request

BASE = os.environ.get("API_URL", "http://localhost:8080").rstrip("/")
MAILPIT = os.environ.get("MAILPIT_URL", "http://localhost:8025").rstrip("/")
VERIFY_TOKEN = os.environ.get("WHATSAPP_VERIFY_TOKEN", "development-verify-token")
PASSWORD = "Smoke-Test-123!"


def post(path, data, headers=None):
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(data).encode(),
        headers={"Content-Type": "application/json", **(headers or {})},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


def get(path, headers=None):
    req = urllib.request.Request(BASE + path, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


slug = "ci-smoke-" + uuid.uuid4().hex[:8]
email = slug + "@example.com"
print("SMOKE_SLUG:", slug, flush=True)

status, body = post("/api/v1/auth/register", {
    "workspaceName": slug, "workspaceSlug": slug, "displayName": "Smoke",
    "email": email, "password": PASSWORD,
})
assert status == 202, (status, body)
print("register -> 202 OK", flush=True)

token = None
for _ in range(30):
    try:
        with urllib.request.urlopen(MAILPIT + "/api/v1/messages", timeout=10) as r:
            messages = json.load(r).get("messages", [])
        match = next(
            (m for m in messages if email in [t.get("Address", "") for t in m.get("To", [])]),
            None,
        )
        if match is not None:
            with urllib.request.urlopen(MAILPIT + "/api/v1/message/" + match["ID"], timeout=10) as d:
                text = json.load(d).get("Text", "")
            token = text.split("token:")[-1].strip()
            break
    except Exception:
        pass
    time.sleep(2)
assert token, "verification email never arrived"
print("verification email received", flush=True)

status, body = post("/api/v1/auth/verify-email", {"token": token})
assert status == 200, (status, body)
print("verify-email -> 200 OK", flush=True)

status, body = post("/api/v1/auth/login", {"tenantSlug": slug, "email": email, "password": PASSWORD})
assert status == 200, (status, body)
access = json.loads(body)["accessToken"]
assert access
print("login -> 200 OK", flush=True)

status, body = get(
    "/api/v1/webhooks/whatsapp?hub.mode=subscribe"
    "&hub.verify_token=" + VERIFY_TOKEN + "&hub.challenge=ci-challenge"
)
assert status == 200 and body == "ci-challenge", (status, body)
print("webhook handshake OK", flush=True)

status, body = post("/api/v1/webhooks/whatsapp", {"entry": []})
assert status == 401, (status, body)
print("unsigned webhook rejected (401) OK", flush=True)

status, body = get("/api/v1/conversations", {"Authorization": "Bearer " + access})
assert status == 200 and "items" in json.loads(body), (status, body)
print("authenticated inbox read OK", flush=True)

print("SMOKE PASSED", flush=True)
