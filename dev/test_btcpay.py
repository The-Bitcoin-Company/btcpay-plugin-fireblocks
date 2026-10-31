# pyright: basic
"""Regression tests for local-only Greenfield helper behavior."""

import tempfile
import unittest
from pathlib import Path
from unittest.mock import ANY, call, patch

import btcpay


class BaseUrlTests(unittest.TestCase):
    def test_accepts_loopback_urls(self) -> None:
        for url in (
            "http://localhost:14142",
            "http://127.0.0.1:14142/",
            "https://[::1]:14142/btcpay",
        ):
            with self.subTest(url=url):
                self.assertEqual(btcpay.validate_base_url(url), url.rstrip("/"))

    def test_rejects_non_loopback_and_ambiguous_urls(self) -> None:
        for url in (
            "https://example.com",
            "http://localhost.evil.example",
            "http://user@localhost:14142",
            "file:///tmp/btcpay",
            "http://localhost:14142/?next=https://example.com",
        ):
            with self.subTest(url=url), self.assertRaises(ValueError):
                btcpay.validate_base_url(url)


class SeedTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.key_path = Path(self.temp_dir.name) / "api-key"
        self.path_patch = patch.object(btcpay, "API_KEY_PATH", self.key_path)
        self.path_patch.start()

    def tearDown(self) -> None:
        self.path_patch.stop()
        self.temp_dir.cleanup()

    @patch("btcpay.request")
    def test_existing_account_provisions_persistent_api_key(self, request) -> None:
        store = {"id": "store-id", "name": btcpay.STORE_NAME}
        request.side_effect = [[store], {}, {"apiKey": "local-secret"}, {}]

        self.assertEqual(btcpay.ensure_store(), store)
        self.assertEqual(self.key_path.read_text(), "local-secret\n")
        self.assertEqual(self.key_path.stat().st_mode & 0o777, 0o600)
        self.assertIn(
            call("PUT", "/api/v1/users/me", {"allowGreenfieldBasicAuth": True}),
            request.call_args_list,
        )
        self.assertIn(
            call(
                "POST",
                "/api/v1/api-keys",
                {"label": "Fireblocks local development", "permissions": ["unrestricted"]},
            ),
            request.call_args_list,
        )

    @patch("btcpay.request")
    def test_persisted_api_key_avoids_basic_reauthentication(self, request) -> None:
        self.key_path.write_text("local-secret\n")
        store = {"id": "store-id", "name": btcpay.STORE_NAME}
        request.side_effect = [[store], {}]

        self.assertEqual(btcpay.ensure_store(), store)
        self.assertNotIn(call("PUT", "/api/v1/users/me", ANY), request.call_args_list)
        self.assertEqual(request.call_count, 2)

    @patch("btcpay.request")
    def test_stale_key_is_replaced_when_new_admin_is_seeded(self, request) -> None:
        self.key_path.write_text("stale-secret\n")
        store = {"id": "store-id", "name": btcpay.STORE_NAME}
        request.side_effect = [
            RuntimeError("GET stores failed (401)"),
            RuntimeError("GET stores failed (401)"),
            {},
            [store],
            {},
            {"apiKey": "replacement-secret"},
            {},
        ]

        self.assertEqual(btcpay.ensure_store(), store)
        self.assertEqual(self.key_path.read_text(), "replacement-secret\n")
        self.assertIn(
            call(
                "POST",
                "/api/v1/users",
                {"email": btcpay.EMAIL, "password": btcpay.PASSWORD, "isAdministrator": True},
                authenticate=False,
            ),
            request.call_args_list,
        )


if __name__ == "__main__":
    unittest.main()
