# pyright: basic
"""Small Greenfield API client for the disposable local development stack."""

from __future__ import annotations

import base64
import json
import os
import time
import urllib.error
import urllib.request
from typing import Any

BASE_URL = os.environ.get("BTCPAY_DEV_URL", "http://localhost:14142").rstrip("/")
EMAIL = os.environ.get("BTCPAY_DEV_EMAIL", "dev@fireblocks.local")
PASSWORD = os.environ.get("BTCPAY_DEV_PASSWORD", "DevPassword123!")
STORE_NAME = os.environ.get("BTCPAY_DEV_STORE_NAME", "Fireblocks Dev")


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
        credentials = base64.b64encode(f"{EMAIL}:{PASSWORD}".encode()).decode()
        headers["Authorization"] = f"Basic {credentials}"
    req = urllib.request.Request(BASE_URL + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
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
        request(
            "POST",
            "/api/v1/users",
            {"email": EMAIL, "password": PASSWORD, "isAdministrator": True},
            authenticate=False,
        )
        stores = request("GET", "/api/v1/stores")

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
