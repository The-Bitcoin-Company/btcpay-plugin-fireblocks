# pyright: basic
"""Small Greenfield API client for the disposable local development stack."""

from __future__ import annotations

import base64
import json
import os
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit


def validate_base_url(value: str) -> str:
    """Allow API mutations only against a loopback BTCPay development instance."""
    url = value.rstrip("/")
    parsed = urlsplit(url)
    if (
        parsed.scheme not in {"http", "https"}
        or parsed.hostname not in {"localhost", "127.0.0.1", "::1"}
        or parsed.username is not None
        or parsed.password is not None
        or parsed.query
        or parsed.fragment
    ):
        raise ValueError("BTCPAY_DEV_URL must be an HTTP(S) loopback URL without credentials")
    return url


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Prevent a loopback endpoint from redirecting API credentials elsewhere."""

    def redirect_request(self, *args: Any, **kwargs: Any) -> None:
        return None


OPENER = urllib.request.build_opener(NoRedirect)
BASE_URL = validate_base_url(os.environ.get("BTCPAY_DEV_URL", "http://localhost:14142"))
EMAIL = os.environ.get("BTCPAY_DEV_EMAIL", "dev@fireblocks.local")
PASSWORD = os.environ.get("BTCPAY_DEV_PASSWORD", "DevPassword123!")
STORE_NAME = os.environ.get("BTCPAY_DEV_STORE_NAME", "Fireblocks Dev")
API_KEY_PATH = Path(__file__).resolve().parent.parent / "artifacts" / "dev" / "api-key"


def get_api_key() -> str | None:
    try:
        return API_KEY_PATH.read_text().strip() or None
    except FileNotFoundError:
        return None


def save_api_key(api_key: str) -> None:
    API_KEY_PATH.parent.mkdir(parents=True, exist_ok=True)
    descriptor = os.open(API_KEY_PATH, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w") as key_file:
        key_file.write(api_key + "\n")
    API_KEY_PATH.chmod(0o600)


def request(
    method: str,
    path: str,
    body: dict[str, Any] | None = None,
    *,
    authenticate: bool = True,
) -> Any:
    data = None if body is None else json.dumps(body).encode()
    headers = {"Accept": "application/json"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    if authenticate:
        api_key = get_api_key()
        if api_key:
            headers["Authorization"] = f"token {api_key}"
        else:
            credentials = base64.b64encode(f"{EMAIL}:{PASSWORD}".encode()).decode()
            headers["Authorization"] = f"Basic {credentials}"
    req = urllib.request.Request(BASE_URL + path, data=data, headers=headers, method=method)
    try:
        with OPENER.open(req, timeout=30) as response:
            content = response.read()
            return json.loads(content) if content else None
    except urllib.error.HTTPError as error:
        detail = error.read().decode(errors="replace")
        raise RuntimeError(f"{method} {path} failed ({error.code}): {detail}") from error


def wait_until_ready() -> None:
    timeout = int(os.environ.get("BTCPAY_DEV_STARTUP_TIMEOUT", "300"))
    deadline = time.monotonic() + timeout
    while True:
        try:
            request("GET", "/api/v1/server/info", authenticate=False)
            return
        except (OSError, RuntimeError) as error:
            # Server info requires authentication; its 401 still proves HTTP is ready.
            if isinstance(error, RuntimeError) and "(401)" in str(error):
                return
            if time.monotonic() >= deadline:
                raise RuntimeError(
                    f"BTCPay did not become ready within {timeout} seconds; "
                    "inspect 'docker compose -f dev/docker-compose.yml logs btcpay'."
                ) from error
            time.sleep(2)


def ensure_store() -> dict[str, Any]:
    """Create the first admin and named store, or reuse them on later runs."""
    try:
        stores = request("GET", "/api/v1/stores")
    except RuntimeError as error:
        if "(401)" not in str(error):
            raise
        # A reset BTCPay volume can leave a stale local key. Retry once with Basic.
        API_KEY_PATH.unlink(missing_ok=True)
        try:
            stores = request("GET", "/api/v1/stores")
        except RuntimeError as basic_error:
            if "(401)" not in str(basic_error):
                raise
            request(
                "POST",
                "/api/v1/users",
                {"email": EMAIL, "password": PASSWORD, "isAdministrator": True},
                authenticate=False,
            )
            stores = request("GET", "/api/v1/stores")

    if get_api_key() is None:
        # Keep Basic available as a recovery path after BTCPay's five-minute grace period,
        # then avoid its login throttling by using an API key for all routine requests.
        request("PUT", "/api/v1/users/me", {"allowGreenfieldBasicAuth": True})
        key_data = request(
            "POST",
            "/api/v1/api-keys",
            {"label": "Fireblocks local development", "permissions": ["unrestricted"]},
        )
        save_api_key(key_data["apiKey"])

    store = next((item for item in stores if item["name"] == STORE_NAME), None)
    if store is None:
        store = request("POST", "/api/v1/stores", {"name": STORE_NAME})

    wallet_path = f"/api/v1/stores/{store['id']}/payment-methods/BTC-CHAIN"
    try:
        request("GET", wallet_path)
    except RuntimeError as error:
        if "(404)" not in str(error):
            raise
        # A fixed, watch-only regtest wallet makes invoice checkout immediately usable.
        # No private key is saved by BTCPay, and this mnemonic must never hold real funds.
        wallet_request = {
            "existingMnemonic": "abandon " * 11 + "about",
            "savePrivateKeys": False,
            "label": "Disposable regtest wallet",
        }
        deadline = time.monotonic() + 120
        while True:
            try:
                request("POST", wallet_path + "/wallet/generate", wallet_request)
                break
            except RuntimeError as generation_error:
                if "(503)" not in str(generation_error) or time.monotonic() >= deadline:
                    raise
                time.sleep(2)
    return store
