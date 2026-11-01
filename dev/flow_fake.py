#!/usr/bin/env python3
# pyright: basic
"""Deterministic, credential-free Dynamic Flow fake for the disposable dev harness."""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import re
import threading
import time
import urllib.request
import uuid
from datetime import UTC, datetime, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, ClassVar
from urllib.parse import urlsplit

USDC_SOLANA_MINT = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v"
FAKE_CREDENTIAL = "dev-flow-token"
FAKE_WEBHOOK_SECRET = os.environ.get("FLOW_FAKE_WEBHOOK_SECRET", "dev-webhook-secret")


class FakeRequestError(ValueError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def validate_request(body: dict[str, Any]) -> None:
    if body.get("currency") != "USD" or not re.fullmatch(r"[0-9]+\.[0-9]{2}", str(body.get("amount"))):
        raise FakeRequestError("amount_invalid", "A fixed USD amount with two decimals is required")
    if not isinstance(body.get("expiresIn"), int) or body["expiresIn"] <= 0:
        raise FakeRequestError("expiry_invalid", "expiresIn must be positive")

    settlement = body.get("settlementConfig", {})
    expected_settlement = {
        "chainName": "SOL",
        "chainId": "101",
        "symbol": "USDC",
        "tokenAddress": USDC_SOLANA_MINT,
        "tokenDecimals": 6,
    }
    if settlement.get("strategy") != "preferred_order" or settlement.get("settlements") != [expected_settlement]:
        raise FakeRequestError("settlement_invalid", "Exactly one USDC/Solana settlement is required")

    destinations = body.get("destinationConfig", {}).get("destinations")
    if (
        not isinstance(destinations, list)
        or len(destinations) != 1
        or destinations[0].get("chainName") != "SOL"
        or destinations[0].get("type") != "address"
        or not destinations[0].get("identifier")
    ):
        raise FakeRequestError("destination_invalid", "Exactly one Solana address destination is required")

    memo = body.get("memo", {})
    if not memo.get("invoiceId") or not memo.get("paymentAttemptId"):
        raise FakeRequestError("memo_invalid", "Invoice and payment-attempt correlation is required")
    base_url = body.get("baseUrl", "")
    if urlsplit(base_url).scheme not in {"http", "https"} or not urlsplit(base_url).netloc:
        raise FakeRequestError("base_url_invalid", "An absolute hosted baseUrl is required")


def make_payment_url(base_url: str, environment_id: str, flow_id: str) -> str:
    correlation = json.dumps(
        {"environmentId": environment_id, "flowId": flow_id}, separators=(",", ":")
    ).encode()
    segment = base64.urlsafe_b64encode(correlation).decode().rstrip("=")
    separator = "&" if "?" in base_url else "?"
    return f"{base_url}{separator}flow={segment}"


def make_snapshot(environment_id: str, flow_id: str, request: dict[str, Any]) -> dict[str, Any]:
    settlement = request["settlementConfig"]["settlements"][0]
    destination = request["destinationConfig"]["destinations"][0]
    return {
        "id": flow_id,
        "environmentId": environment_id,
        "mode": "payment",
        "status": "pending",
        "expiresAt": (
            datetime.now(UTC) + timedelta(seconds=request["expiresIn"])
        ).isoformat().replace("+00:00", "Z"),
        "memo": request["memo"],
        "settlementConfig": request["settlementConfig"],
        "destinationConfig": request["destinationConfig"],
        "execution": {"status": "created"},
        "settlement": {
            "status": "pending",
            "amount": None,
            "amountAtomic": None,
            "transactionId": None,
            "destination": destination["identifier"],
            "chainName": settlement["chainName"],
            "chainId": settlement["chainId"],
            "symbol": settlement["symbol"],
            "tokenAddress": settlement["tokenAddress"],
            "tokenDecimals": settlement["tokenDecimals"],
            "completedAt": None,
        },
        "risk": {"status": "pending"},
    }


def apply_state(snapshot: dict[str, Any], update: dict[str, Any]) -> None:
    """Apply a local test control without deriving settlement from execution."""
    allowed = {
        "flowStatus", "executionStatus", "settlementStatus", "riskStatus", "amount",
        "amountAtomic", "transactionId", "destination", "chainName", "chainId",
        "symbol", "tokenAddress", "tokenDecimals", "environmentId",
    }
    if unknown := set(update) - allowed:
        raise FakeRequestError("state_invalid", f"Unsupported state fields: {', '.join(sorted(unknown))}")
    if "flowStatus" in update:
        snapshot["status"] = update["flowStatus"]
    if "environmentId" in update:
        snapshot["environmentId"] = update["environmentId"]
    if "executionStatus" in update:
        snapshot["execution"]["status"] = update["executionStatus"]
    if "riskStatus" in update:
        snapshot["risk"]["status"] = update["riskStatus"]
    settlement = snapshot["settlement"]
    for key in (
        "amount", "amountAtomic", "transactionId", "destination", "chainName",
        "chainId", "symbol", "tokenAddress", "tokenDecimals",
    ):
        if key in update:
            settlement[key] = update[key]
    if "settlementStatus" in update:
        settlement["status"] = update["settlementStatus"]
        settlement["completedAt"] = (
            datetime.now(UTC).isoformat().replace("+00:00", "Z")
            if update["settlementStatus"] == "completed" else None
        )


def webhook_body(event_id: str, event_type: str, snapshot: dict[str, Any]) -> bytes:
    return json.dumps(
        {
            "id": event_id,
            "type": event_type,
            "data": {"flowId": snapshot["id"], "environmentId": snapshot["environmentId"]},
        },
        separators=(",", ":"),
    ).encode()


def webhook_signature(body: bytes, secret: str = FAKE_WEBHOOK_SECRET) -> str:
    return "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()


class FlowFakeHandler(BaseHTTPRequestHandler):
    mode = os.environ.get("FLOW_FAKE_MODE", "success")
    timeout_seconds = int(os.environ.get("FLOW_FAKE_TIMEOUT_SECONDS", "30"))
    flows: ClassVar[dict[str, dict[str, Any]]] = {}
    lock: ClassVar[threading.Lock] = threading.Lock()

    def do_GET(self) -> None:
        if self.path == "/health":
            self.send_json(200, {"ok": True})
            return
        match = re.fullmatch(r"/api/v0/server/([^/]+)/flows/([^/]+)", self.path)
        if match is not None:
            if self.headers.get("Authorization") != f"Bearer {FAKE_CREDENTIAL}":
                self.send_json(401, {"code": "unauthorized", "error": "invalid fake credential"})
                return
            with self.lock:
                snapshot = self.flows.get(match.group(2))
                if snapshot is None or snapshot["environmentId"] != match.group(1):
                    self.send_json(404, {"code": "not_found", "error": "flow not found"})
                    return
                self.send_json(200, {"flow": snapshot})
                return
        if self.path.startswith("/pay"):
            content = (
                b"<!doctype html><title>Fake Flow payment</title>"
                b"<h1>Fake hosted Flow payment</h1>"
                b"<p>This page never reports settlement to BTCPay.</p>"
            )
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)
            return
        self.send_json(404, {"error": "not found"})

    def do_POST(self) -> None:
        control = re.fullmatch(r"/control/flows/([^/]+)/(state|notify)", self.path)
        if control is not None:
            self.handle_control(control.group(1), control.group(2))
            return
        match = re.fullmatch(r"/api/v0/server/([^/]+)/payment-links", self.path)
        if match is None:
            self.send_json(404, {"error": "not found"})
            return
        if self.headers.get("Authorization") != f"Bearer {FAKE_CREDENTIAL}":
            self.send_json(401, {"code": "unauthorized", "error": "invalid fake credential"})
            return
        try:
            body = self.read_json()
            validate_request(body)
        except (json.JSONDecodeError, UnicodeDecodeError):
            self.send_json(400, {"code": "invalid_json", "error": "invalid JSON"})
            return
        except FakeRequestError as error:
            self.send_json(422, {"code": error.code, "error": str(error)})
            return

        if self.mode == "error":
            self.send_json(422, {"code": "fake_provider_error", "error": "requested fake failure"})
            return
        if self.mode == "timeout":
            time.sleep(self.timeout_seconds)

        flow_id = str(uuid.uuid4())
        snapshot = make_snapshot(match.group(1), flow_id, body)
        with self.lock:
            self.flows[flow_id] = snapshot
        self.send_json(
            201,
            {
                "flow": {
                    "id": flow_id,
                    "mode": "payment",
                    "expiresAt": snapshot["expiresAt"],
                },
                "paymentUrl": make_payment_url(body["baseUrl"], match.group(1), flow_id),
            },
        )

    def handle_control(self, flow_id: str, action: str) -> None:
        try:
            body = self.read_json()
        except (json.JSONDecodeError, UnicodeDecodeError):
            self.send_json(400, {"code": "invalid_json", "error": "invalid JSON"})
            return
        with self.lock:
            snapshot = self.flows.get(flow_id)
            if snapshot is None:
                self.send_json(404, {"code": "not_found", "error": "flow not found"})
                return
            if action == "state":
                try:
                    apply_state(snapshot, body)
                except FakeRequestError as error:
                    self.send_json(422, {"code": error.code, "error": str(error)})
                    return
                self.send_json(200, {"flow": snapshot})
                return

            event_type = body.get("type", "flow.settlement.updated")
            if event_type not in {"flow.execution.updated", "flow.settlement.updated", "flow.risk.updated"}:
                self.send_json(422, {"code": "event_invalid", "error": "unsupported event type"})
                return
            target = body.get("targetUrl", "")
            if urlsplit(target).scheme not in {"http", "https"} or not urlsplit(target).netloc:
                self.send_json(422, {"code": "target_invalid", "error": "absolute targetUrl is required"})
                return
            event_id = body.get("eventId") or str(uuid.uuid4())
            content = webhook_body(event_id, event_type, snapshot)

        request = urllib.request.Request(
            target,
            data=content,
            method="POST",
            headers={
                "Content-Type": "application/json",
                "x-dynamic-signature-256": webhook_signature(content),
            },
        )
        try:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            with opener.open(request, timeout=15) as response:
                status = response.status
        except OSError:
            self.send_json(502, {"code": "delivery_failed", "error": "webhook delivery failed"})
            return
        self.send_json(200, {"delivered": True, "status": status, "eventId": event_id})

    def read_json(self) -> dict[str, Any]:
        length = int(self.headers.get("Content-Length", "0"))
        if length > 1024 * 1024:
            raise FakeRequestError("request_too_large", "request body is too large")
        return json.loads(self.rfile.read(length))

    def send_json(self, status: int, body: dict[str, Any]) -> None:
        content = json.dumps(body, separators=(",", ":")).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def log_message(self, format_string: str, *args: Any) -> None:
        # Never log Authorization/signature headers, secrets, or request bodies.
        print(f"flow-fake: {format_string % args}")


def main() -> None:
    server = ThreadingHTTPServer(("0.0.0.0", 8080), FlowFakeHandler)
    print(f"flow-fake listening on :8080 in {FlowFakeHandler.mode} mode")
    server.serve_forever()


if __name__ == "__main__":
    main()
