#!/usr/bin/env python3
# pyright: basic
"""Exercise signed webhooks, API reconciliation, and real BTCPay transitions locally."""

import argparse
import json
import subprocess
import time
import urllib.error
import urllib.request
from decimal import Decimal
from pathlib import Path

from btcpay import ensure_flow_configuration, ensure_store, request, wait_until_ready

FAKE_URL = "http://localhost:14143"
USDC_MINT = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v"


def fake_request(flow_id: str, action: str, body: dict) -> dict:
    req = urllib.request.Request(
        f"{FAKE_URL}/control/flows/{flow_id}/{action}",
        data=json.dumps(body, separators=(",", ":")).encode(),
        method="POST",
        headers={"Content-Type": "application/json"},
    )
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(req, timeout=20) as response:
        return json.load(response)


def flow_method(store_id: str, invoice_id: str) -> dict:
    methods = request("GET", f"/api/v1/stores/{store_id}/invoices/{invoice_id}/payment-methods")
    return next(method for method in methods if method["paymentMethodId"] == "FIREBLOCKS-FLOW")


def invoice(store_id: str, invoice_id: str) -> dict:
    return request("GET", f"/api/v1/stores/{store_id}/invoices/{invoice_id}")


def wait_for_status(store_id: str, invoice_id: str, expected: str, timeout: int) -> dict:
    deadline = time.monotonic() + timeout
    while True:
        current = invoice(store_id, invoice_id)
        if current["status"] == expected:
            return current
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Invoice {invoice_id} did not become {expected}: {current}")
        time.sleep(2)


def notify(store_id: str, flow_id: str, event_type: str, event_id: str) -> None:
    fake_request(flow_id, "notify", {
        "targetUrl": f"http://btcpay:8080/stores/{store_id}/fireblocks/webhook",
        "type": event_type,
        "eventId": event_id,
    })


def completed_state(amount: str, risk: str, transaction_id: str) -> dict:
    return {
        "settlementStatus": "completed",
        "riskStatus": risk,
        "amount": amount,
        "amountAtomic": str(int(Decimal(amount) * 1_000_000)),
        "transactionId": transaction_id,
    }


def assert_no_payment(store_id: str, invoice_id: str) -> None:
    method = flow_method(store_id, invoice_id)
    if method.get("payments"):
        raise RuntimeError(f"Execution/risk-only update unexpectedly credited {invoice_id}")
    if invoice(store_id, invoice_id)["status"] != "New":
        raise RuntimeError(f"Execution/risk-only update unexpectedly transitioned {invoice_id}")


def reject_invalid_signature(store_id: str, flow_id: str) -> None:
    body = json.dumps({
        "id": "invalid-signature-exercise",
        "type": "flow.settlement.updated",
        "data": {"flowId": flow_id, "environmentId": "dev-environment"},
    }, separators=(",", ":")).encode()
    req = urllib.request.Request(
        f"http://localhost:14142/stores/{store_id}/fireblocks/webhook",
        data=body,
        method="POST",
        headers={"Content-Type": "application/json", "x-dynamic-signature-256": "sha256=" + "0" * 64},
    )
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        opener.open(req, timeout=10)
    except urllib.error.HTTPError as error:
        if error.code == 401:
            return
        raise
    raise RuntimeError("Invalid webhook signature was not rejected")


def create_flow_invoice(store_id: str, order: str) -> tuple[str, str]:
    created = request("POST", f"/api/v1/stores/{store_id}/invoices", {
        "amount": "25.00",
        "currency": "USD",
        "metadata": {"orderId": order},
    })
    method = flow_method(store_id, created["id"])
    flow_id = method.get("additionalData", {}).get("flowId")
    if not flow_id:
        raise RuntimeError(f"No Flow binding was created for {created['id']}: {method}")
    return created["id"], flow_id


def exercise_webhook(store_id: str) -> None:
    invoice_id, flow_id = create_flow_invoice(store_id, f"flow-webhook-{int(time.time())}")
    fake_request(flow_id, "state", {
        "executionStatus": "source_confirmed",
        "settlementStatus": "processing",
        "riskStatus": "approved",
    })
    notify(store_id, flow_id, "flow.execution.updated", f"execution-{flow_id}")
    time.sleep(2)
    assert_no_payment(store_id, invoice_id)

    fake_request(flow_id, "state", completed_state("25.000000", "review", f"tx-{flow_id}"))
    notify(store_id, flow_id, "flow.settlement.updated", f"settlement-{flow_id}")
    time.sleep(2)
    assert_no_payment(store_id, invoice_id)
    reject_invalid_signature(store_id, flow_id)
    assert_no_payment(store_id, invoice_id)

    fake_request(flow_id, "state", {"riskStatus": "approved"})
    notify(store_id, flow_id, "flow.risk.updated", f"risk-{flow_id}")
    wait_for_status(store_id, invoice_id, "Settled", 25)
    # Repeat the exact event and settlement; BTCPay must retain one payment.
    notify(store_id, flow_id, "flow.risk.updated", f"risk-{flow_id}")
    notify(store_id, flow_id, "flow.settlement.updated", f"settlement-{flow_id}")
    time.sleep(2)
    payments = flow_method(store_id, invoice_id).get("payments", [])
    if len(payments) != 1:
        raise RuntimeError(f"Duplicate settlement created {len(payments)} payments")
    payment = payments[0]
    if payment["id"] != f"tx-{flow_id}" or Decimal(str(payment["value"])) != 25:
        raise RuntimeError(f"Unexpected BTCPay payment evidence: {payment}")
    if payment["destination"] != USDC_MINT or payment["status"] != "Settled":
        raise RuntimeError(f"Unexpected BTCPay payment destination/status: {payment}")
    print(f"Webhook invoice {invoice_id}: Settled with one verified USDC payment")


def exercise_amount_policy(store_id: str) -> None:
    under_id, under_flow = create_flow_invoice(store_id, f"flow-under-{int(time.time())}")
    fake_request(under_flow, "state", completed_state("4.999999", "approved", f"tx-{under_flow}"))
    notify(store_id, under_flow, "flow.settlement.updated", f"settlement-{under_flow}")
    deadline = time.monotonic() + 25
    while True:
        under = invoice(store_id, under_id)
        under_payments = flow_method(store_id, under_id).get("payments", [])
        if under_payments and under["status"] == "New" and under.get("additionalStatus") == "PaidPartial":
            break
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Underpayment was not recorded/evaluated for {under_id}: {under}")
        time.sleep(2)
    if len(under_payments) != 1 or Decimal(str(under_payments[0]["value"])) != Decimal("4.999999"):
        raise RuntimeError(f"Underpayment precision was not preserved: {under_payments}")
    print(f"Underpayment invoice {under_id}: exact partial payment without settlement")

    over_id, over_flow = create_flow_invoice(store_id, f"flow-over-{int(time.time())}")
    fake_request(over_flow, "state", completed_state("30.500000", "approved", f"tx-{over_flow}"))
    notify(store_id, over_flow, "flow.settlement.updated", f"settlement-{over_flow}")
    over = wait_for_status(store_id, over_id, "Settled", 25)
    over_payments = flow_method(store_id, over_id)["payments"]
    if over.get("additionalStatus") != "PaidOver" or Decimal(str(over_payments[0]["value"])) != Decimal("30.5"):
        raise RuntimeError(f"Overpayment did not use BTCPay accounting: {over}, {over_payments}")
    print(f"Overpayment invoice {over_id}: Settled with BTCPay PaidOver accounting")


def run_sql(statement: str) -> None:
    subprocess.run([
        "docker", "compose", "-f", str(Path(__file__).parent / "docker-compose.yml"),
        "exec", "-T", "postgres", "psql", "-U", "postgres", "-d", "btcpayserver",
        "-c", statement,
    ], check=True, capture_output=True, text=True)


def exercise_legacy_rate_recovery(store_id: str) -> None:
    invoice_id, flow_id = create_flow_invoice(store_id, f"flow-legacy-rate-{int(time.time())}")
    # Simulate an invoice activated before USDC accounting was introduced. The
    # recorder must persist the fixed rate in the invoice blob before AddPayment.
    run_sql(f"UPDATE \"Invoices\" SET \"Blob2\" = \"Blob2\" #- '{{rates,USDC}}' WHERE \"Id\" = '{invoice_id}'")
    fake_request(flow_id, "state", completed_state("25.000000", "approved", f"tx-{flow_id}"))
    notify(store_id, flow_id, "flow.settlement.updated", f"settlement-{flow_id}")
    wait_for_status(store_id, invoice_id, "Settled", 25)
    print(f"Legacy-rate invoice {invoice_id}: persisted USDC accounting before payment")


def exercise_existing_payment_recovery(store_id: str) -> None:
    invoice_id, flow_id = create_flow_invoice(store_id, f"flow-existing-payment-{int(time.time())}")
    fake_request(flow_id, "state", completed_state("25.000000", "approved", f"tx-{flow_id}"))
    notify(store_id, flow_id, "flow.settlement.updated", f"settlement-{flow_id}")
    wait_for_status(store_id, invoice_id, "Settled", 25)

    # Recreate the durable state of a crash after AddPayment but before the invoice
    # watcher ran: payment exists, invoice is unpaid, and the attempt is outstanding.
    run_sql(f"UPDATE \"Invoices\" SET \"Status\" = 'New', \"ExceptionStatus\" = 'None' WHERE \"Id\" = '{invoice_id}'")
    # Keep the attempt Settled: startup repair, not another webhook, must replay the
    # idempotent invoice evaluation after the in-memory notification was lost.
    subprocess.run([
        "docker", "compose", "-f", str(Path(__file__).parent / "docker-compose.yml"),
        "restart", "btcpay",
    ], check=True, capture_output=True, text=True)
    wait_until_ready()
    wait_for_status(store_id, invoice_id, "Settled", 35)
    if len(flow_method(store_id, invoice_id).get("payments", [])) != 1:
        raise RuntimeError(f"Existing-payment recovery duplicated payment for {invoice_id}")
    print(f"Existing-payment invoice {invoice_id}: startup repair woke BTCPay accounting")


def exercise_reconciliation(store_id: str) -> None:
    invoice_id, flow_id = create_flow_invoice(store_id, f"flow-reconcile-{int(time.time())}")
    fake_request(flow_id, "state", completed_state("25.000000", "approved", f"tx-{flow_id}"))
    # Deliberately send no webhook. The startup/one-minute outstanding pass must recover it.
    wait_for_status(store_id, invoice_id, "Settled", 80)
    payments = flow_method(store_id, invoice_id).get("payments", [])
    if len(payments) != 1 or payments[0]["id"] != f"tx-{flow_id}":
        raise RuntimeError(f"Reconciliation created unexpected payments: {payments}")
    print(f"Missed-notification invoice {invoice_id}: reconciled through the server API")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--skip-reconciliation", action="store_true", help="skip the one-minute missed-notification check")
    args = parser.parse_args()
    wait_until_ready()
    store = ensure_store()
    ensure_flow_configuration(store)
    exercise_webhook(store["id"])
    exercise_amount_policy(store["id"])
    exercise_legacy_rate_recovery(store["id"])
    exercise_existing_payment_recovery(store["id"])
    if not args.skip_reconciliation:
        exercise_reconciliation(store["id"])


if __name__ == "__main__":
    main()
