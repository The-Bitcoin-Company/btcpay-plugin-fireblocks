# Manual same-network vault sweep

Requires a ready collection vault and its original saved core workspace credentials
(see [core setup](core-api-setup.md)). Sweeps are independent of Dynamic checkout:
they do not credit invoices, change payment records, or change checkout settings.

1. Save one **sweep wallet destination** in the collection vault settings. This is
   separate from the Dynamic settlement address. Verify ownership and network:
   a Solana address alone cannot distinguish mainnet from testnet.
2. Select **Sweep now**. Review the source vault, exact Fireblocks asset/network,
   environment and saved destination. The amount defaults to available balance;
   reduce it if desired (USDC: six decimal places; SOL test asset: nine).
3. Confirm the displayed amount within 15 minutes. The current available balance
   is checked again. A single `TRANSFER` uses the source asset unchanged and a
   one-time wallet address destination; no exchange, swap or bridge is involved.
4. Read the settings status. **Pending** is not completion. Approve/sign only
   through the normal Fireblocks controls. Refresh status manually or wait for
   the existing minute worker (also runs on startup); no webhook is required.
   Only a verified provider `COMPLETED` transaction is shown as completed.

Native SOL gas, balance reservations, policy, signing and approvals remain the
operator's responsibility. A default full-balance SOL sweep can fail because of
fees; choose a smaller amount. No auto-fueling or remediation is attempted.
Provider error bodies and substatus text are never displayed or logged by the
sweep implementation; safe guidance directs the operator to the console.

## Uncertain submission / recovery

The server commits a local pending record **before** the transfer request, with a
permanent `externalTxId` and matching HTTP idempotency key. Repeated confirmations
of that record never POST again, even after completion or HTTP-key expiry.
An unresolved sweep blocks all newer submissions for that collection vault.
Changing the saved destination cannot redirect an already confirmed transfer.

A lost response is looked up using `GET /transactions/external_tx_id/{id}`;
once the transaction ID is recovered, subsequent reads use that ID. Keep the
original workspace credentials available. A missing/unknown provider transaction,
404, unknown status, timeout or partial completion remains unresolved, not failed.

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
