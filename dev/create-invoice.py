#!/usr/bin/env python3
# pyright: basic
"""Create a test invoice in the seeded local development store."""

import argparse
import time

from btcpay import BASE_URL, ensure_store, request, wait_until_ready


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--amount", default="10.00", help="invoice amount (default: 10.00)")
    parser.add_argument("--currency", default="USD", help="invoice currency (default: USD)")
    args = parser.parse_args()

    wait_until_ready()
    store = ensure_store()
    invoice = request(
        "POST",
        f"/api/v1/stores/{store['id']}/invoices",
        {
            "amount": args.amount,
            "currency": args.currency,
            "metadata": {"orderId": f"fireblocks-dev-{int(time.time())}"},
        },
    )
    print(f"Invoice: {invoice['id']} ({invoice['status']})")
    print(f"Checkout: {BASE_URL}/i/{invoice['id']}")


if __name__ == "__main__":
    main()
