#!/usr/bin/env python3
# pyright: basic
"""Wait for local BTCPay and idempotently seed its development store."""

from btcpay import BASE_URL, EMAIL, PASSWORD, ensure_store, wait_until_ready


def main() -> None:
    wait_until_ready()
    store = ensure_store()
    print(f"Seeded store: {store['name']} ({store['id']})")
    print(f"BTCPay: {BASE_URL}")
    print(f"Development login: {EMAIL} / {PASSWORD}")


if __name__ == "__main__":
    main()
