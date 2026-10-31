# pyright: basic
"""Contract tests for the local Dynamic Flow fake."""

import base64
import json
import unittest

from flow_fake import FakeRequestError, make_payment_url, validate_request


def valid_request() -> dict:
    return {
        "amount": "25.00",
        "currency": "USD",
        "settlementConfig": {
            "strategy": "preferred_order",
            "settlements": [
                {
                    "chainName": "SOL",
                    "chainId": "101",
                    "symbol": "USDC",
                    "tokenAddress": "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v",
                    "tokenDecimals": 6,
                }
            ],
        },
        "destinationConfig": {
            "destinations": [
                {"chainName": "SOL", "type": "address", "identifier": "destination"}
            ]
        },
        "memo": {"invoiceId": "invoice", "paymentAttemptId": "attempt"},
        "expiresIn": 900,
        "baseUrl": "http://localhost:14143/pay",
    }


class FlowFakeTests(unittest.TestCase):
    def test_accepts_payment_link_contract(self) -> None:
        validate_request(valid_request())

    def test_rejects_additional_settlement_or_non_usd_amount(self) -> None:
        body = valid_request()
        body["currency"] = "SETTLEMENT_TOKEN"
        with self.assertRaisesRegex(FakeRequestError, "USD"):
            validate_request(body)

        body = valid_request()
        body["settlementConfig"]["settlements"].append(
            body["settlementConfig"]["settlements"][0]
        )
        with self.assertRaisesRegex(FakeRequestError, "Exactly one"):
            validate_request(body)

    def test_rejects_missing_attempt_correlation(self) -> None:
        body = valid_request()
        del body["memo"]["paymentAttemptId"]
        with self.assertRaisesRegex(FakeRequestError, "correlation"):
            validate_request(body)

    def test_payment_url_contains_only_public_flow_correlation(self) -> None:
        url = make_payment_url("http://localhost:14143/pay", "environment", "flow")
        segment = url.split("flow=", 1)[1]
        segment += "=" * (-len(segment) % 4)
        correlation = json.loads(base64.urlsafe_b64decode(segment))
        self.assertEqual(correlation, {"environmentId": "environment", "flowId": "flow"})
        self.assertNotIn("token", url)


if __name__ == "__main__":
    unittest.main()
