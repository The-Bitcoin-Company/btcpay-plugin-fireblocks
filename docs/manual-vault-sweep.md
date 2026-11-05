# Manual same-network vault sweep

Requires a ready collection vault and its original saved core workspace credentials
(see [core setup](core-api-setup.md)). Sweeps are independent of Dynamic checkout:
they do not credit invoices, change payment records, or change checkout settings.

1. Save one **wallet address or connected exchange account** in the collection vault settings. This is
   separate from the Dynamic settlement address. Verify ownership and network:
   a Solana address alone cannot distinguish mainnet from testnet.
2. Select **Sweep now**. Review the source vault, exact Fireblocks asset/network,
   environment and saved destination. The amount defaults to available balance;
   reduce it if desired (USDC: six decimal places; SOL test asset: nine).
3. Confirm the displayed amount within 15 minutes. The current available balance
   is checked again. A single `TRANSFER` uses the source asset unchanged and a
   one-time wallet address or connected `EXCHANGE_ACCOUNT` destination; no swap,
   sale, conversion, exchange withdrawal, trading or bridge is involved.
4. Read the settings status. **Pending** is not completion. Approve/sign only
   through the normal Fireblocks controls. Refresh status manually or wait for
   the existing minute worker (also runs on startup); no webhook is required.
   Only a verified provider `COMPLETED` transaction is shown as completed.

Native SOL gas, balance reservations, policy, signing and approvals remain the
operator's responsibility. A default full-balance SOL sweep can fail because of
fees; choose a smaller amount. No auto-fueling or remediation is attempted.
Provider error bodies and substatus text are never displayed or logged by the
sweep implementation; safe guidance directs the operator to the console.

## Minimal-safe design: optional interval sweeps

This is the cumulative vault/sweep interface for [#17](https://github.com/The-Bitcoin-Company/btcpay-plugin-fireblocks/issues/17), extending [#16](https://github.com/The-Bitcoin-Company/btcpay-plugin-fireblocks/issues/16) / [PR #22](https://github.com/The-Bitcoin-Company/btcpay-plugin-fireblocks/pull/22) and collection-vault #15 / PR #21. Inspected source: `075e7d9` (including `032afd4`, `3ec9d40`, `f87f76b`). Core workspace credentials, explicit vault creation/activation, one saved same-network destination and manual confirmation remain the interface; interval automation is a separate spending opt-in, not checkout activation.

Smallest safe default: automation is off, even with a funded vault and saved destination. Standard opt-in: follow the [configuration reference](core-api-setup.md#sweep-automation-configuration). No new environment variables or scheduler dependencies. Reuse the minute worker, store settings, database advisory lock, sweep records, submission helper and external-ID reconciliation. Complexity budget: three operator fields and check metadata only; no rule engine, new persistence framework, cron, webhooks or automatic approval/fueling. No exception is needed. Full available balance is requested; fees may cause rejection, especially for native SOL. Threshold is not a spending cap.

The lock covers eligibility, balance read and durable pending persistence before POST. Checks persist their timestamp even without submission; a crash cannot reset the interval. Pending records block both manual and automatic submissions indefinitely until verified terminal. Missed intervals run once on the next worker pass, never catch up. Changing destination disables automation, requiring explicit opt-in again. Existing manual confirmations are expired by an automatic submission. Workspace/vault identity and destination validation remain mandatory. Production access, wallet activation, checkout payments and Fireblocks policy/approval/signing remain independent boundaries; automation does not enable or bypass them.
## Minimal-safe design: connected exchange destinations

Extends [#16](https://github.com/The-Bitcoin-Company/btcpay-plugin-fireblocks/issues/16)
with [#18](https://github.com/The-Bitcoin-Company/btcpay-plugin-fireblocks/issues/18),
while retaining the interval automation delivered by
[#17](https://github.com/The-Bitcoin-Company/btcpay-plugin-fireblocks/issues/17).
The cumulative baseline now includes that automation and the manual-sweep changes.

The canonical configuration reference is [core setup](core-api-setup.md), plus
the saved sweep settings described here. No new environment variables, credentials,
activation flags or scheduler are introduced. Existing wallet settings migrate
unchanged; unset destination remains inactive. The cumulative interface is saved
core credentials/environment, a ready collection vault, one explicitly saved
wallet or exchange destination, then prepare and confirm. Saving is not spending.
Fireblocks policy/approval/signing and the explicit confirmation remain independent
irreversible-operation boundaries; connection testing never grants spending access.

Complexity budget: extend the existing destination snapshot and service, add only
read-only connected-account discovery and exact asset-ID validation. Do not add
exchange connectivity management, a parallel transfer path, trading or accounting.
Exact network-qualified legacy asset IDs must match; symbols such as USDC alone
are insufficient. Missing/unknown account status or asset identity fails closed.
Revalidate on save, preparation and confirmation. Changing destination expires
existing confirmations and disables automation; unresolved records continue to block either destination.

Standard opt-in: follow core setup, load connected accounts, select **Exchange
account**, save, then Sweep now and confirm. Only already-connected accounts are
offered. Production exchange connections and credentials are managed in Fireblocks,
not BTCPay. Sandbox/Testnet exchange support is limited; an empty list or no exact
asset match is expected and must not be worked around with a mainnet asset.
This is a transfer only: no sale, conversion, exchange withdrawal or trading.

## Uncertain submission / recovery

The server commits a local pending record **before** the transfer request, with a
permanent `externalTxId` and matching HTTP idempotency key. Repeated confirmations
of that record never POST again, even after completion or HTTP-key expiry.
An unresolved sweep blocks all newer submissions for that collection vault.
Changing the saved destination cannot redirect an already confirmed transfer.

A lost response is looked up using `GET /transactions/external_tx_id/{id}`;
once the transaction ID is recovered, subsequent reads use that ID. Keep the
original workspace credentials available. A missing/unknown provider transaction,
404, unknown status, HTTP timeout or partial completion remains unresolved, not
failed. An explicit provider transaction status of `TIMEOUT` is a terminal failure,
not an HTTP timeout; `CANCELLING` remains pending until cancellation is confirmed.

If the process stopped after persisting pending but before actually submitting,
the record deliberately remains blocked. Use its external ID to reconcile with
Fireblocks support/console. **Do not blindly transfer again, delete the local
record, or manually clear a pending state.** There is intentionally no force-clear
or automatic POST retry. Confirming non-submission and safely releasing a record
requires an operator-supported recovery procedure outside this feature.

Records and destination are independent server-side store settings, surviving
restart and Dynamic configuration deletion. Protect the database and backups;
restoring a backup from before a sweep can lose its identity and is not a supported
way to retry. Keep transaction history for reconciliation.

## Fresh-environment evidence

At source revision `cf27616`, the full `nix develop --command bash scripts/ci.sh` gate passed: 232 tests, build and package/checksum verification. The following isolated mock-provider exercise also passed all 63 `VaultSweepTests` cases:

```sh
nix develop --command bash -c '
  fresh=$(mktemp -d)
  trap "rm -rf \"$fresh\"" EXIT
  env -i HOME="$fresh" XDG_CONFIG_HOME="$fresh/config" XDG_STATE_HOME="$fresh/state" \
    PATH="$PATH" DOTNET_ROOT="${DOTNET_ROOT:-}" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
    dotnet test tests/BTCPayServer.Plugins.Fireblocks.Tests -c Release \
      --no-build --no-restore --filter FullyQualifiedName~VaultSweepTests
'
```

Prerequisites: restored/built checkout and pinned SDK from the normal CI gate. Feature environment is cleared; HOME/config/state are temporary. Each test starts with empty serialized store settings, generated ephemeral RSA credentials and an in-memory ready Sandbox `SOL_TEST` vault; no provider credentials, real network calls or funds. Default operation (`AutomationIsOffByDefaultEvenWithSavedDestination`) performed zero balance reads/transfers. Standard opt-in (`AutomaticThresholdUsesAvailableBalanceAndSharedSubmission`) saved destination, enabled a 60-minute interval and minimum 10: available 9 submitted zero transfers, available 10 or 10.123456789 submitted exactly one full-available-balance transfer to the saved destination. Invalid/unset minimum, invalid interval/precision, wrong workspace, changed destination, unavailable/locked balance and unresolved records failed closed. Interval boundary, missed checks, concurrent service instances and restart/uncertain response were exercised. No lifetime budget/reset or conflicting environment variables exist.

Limitation: serialization is tested with the fake store's shared lock; production cross-process serialization reuses the existing PostgreSQL advisory transaction lock, inspected in `VaultProvisioningStore.Lock`, not a new live multi-server database exercise. UI binding/rendering is build-checked, not browser-tested. Real approval/signing, fees and provider balance freshness need the operator-owned Sandbox exercise below; automation never bypasses these. No live service was activated.

## Small Sandbox acceptance exercise

No production-value transfer is required. Use a separate Sandbox workspace and a
small faucet-funded `SOL_TEST` collection vault; use a second wallet you control on
the **same test network**, with one-time destinations allowed by workspace policy.

- Save that wallet as the sweep destination. Open Sweep now; check Sandbox,
  `SOL_TEST`, vault ID, address and available balance. Cancel: no transfer appears.
- Reopen; try zero, more than balance and excessive decimals: no transfer appears.
- Reopen, choose a small amount leaving native SOL for fees, and confirm. Repost
  the same confirmation/refresh the browser: exactly one external ID/transfer.
- With approval pending, verify Pending and that Sweep now is disabled. Complete
  the normal Sandbox approval/signing process; refresh until provider COMPLETED.
- Restart BTCPay without a webhook configured. Status should recover automatically;
  completed records stay completed and the old confirmation cannot submit again.
- In a controlled test, interrupt connectivity during submission. Restore it and
  restart: the external-ID lookup recovers the transfer, or remains safely blocked
  if non-submission cannot be established. Never issue a replacement transfer.
- Test policy rejection and insufficient native gas with tiny test amounts. Verify
  redacted guidance, no invoice/accounting changes and no automatic fueling or
  resubmission. A failed broadcast may still consume provider/network fees; the
  plugin cannot undo such fees or promise unchanged on-chain balances.

The automated tests use a fake provider for request shape, persistence ordering,
confirmation, amount validation, repeated requests, restart recovery and status
mapping. The live exercise above still requires a Sandbox operator; it is not a
claim of a live transfer test.
