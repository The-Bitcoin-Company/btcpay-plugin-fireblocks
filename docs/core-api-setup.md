# Core Fireblocks API connection

The **Core Fireblocks API** section under the store's Fireblocks integration is independent of Dynamic Flow. It provides a signed, read-only connection test and collection-vault provisioning. Sweeps, scheduling, exchange transfers and automatic SOL fueling are not supported.

## Workspace setup

1. Create or select a Fireblocks Sandbox workspace. In its console, create an API user with permission to read vault accounts (a read-only/Viewer role is sufficient; do not grant spending privileges for this test).
2. Generate an RSA key pair and CSR following [Fireblocks API user setup](https://developers.fireblocks.com/docs/quickstart). Register the CSR/public key with that workspace's API user and retain the matching **unencrypted RSA private key PEM** securely (2048 bits or larger). The plugin accepts the PEM contents, not a filename. Never paste a private key into an issue, chat, support diagnostic or source file.
3. Copy the workspace API key into **Core workspace API key**, paste the matching private key, and choose the correct fixed endpoint:

   | Selection | Fixed base URL |
   | --- | --- |
   | Sandbox | `https://sandbox-api.fireblocks.io/v1` |
   | US Mainnet/Testnet | `https://api.fireblocks.io/v1` |

   EU endpoints and arbitrary endpoint overrides are not supported. Mainnet and Testnet share the US endpoint but remain separate workspaces.
4. Use **Test connection**. This uses the entered credentials, falling back to saved secrets when fields are blank. It does **not** save changes. **Save core settings** persists the settings separately from the Flow form; blank secrets preserve their saved values. Secrets are not returned to the browser or Greenfield configuration responses. Replace both credentials when changing workspaces; retaining old secrets does not migrate credentials.
5. A successful test means authenticated vault-account read access was accepted. An empty workspace is valid. The test sends only `GET /v1/vault/accounts_paged?limit=1`, does not create a vault or transaction, and discards the provider response body. It does not prove spending permissions, asset availability or future sweep readiness.

Each Sandbox, production and Testnet workspace needs its **own API user/key, matching RSA key and workspace assets**. Do not reuse production credentials in Sandbox. Sandbox/testnet assets are not mainnet assets and cannot settle mainnet invoices; funding/setup of test assets is a separate operator action, not part of this connection test.

For authentication/permission errors check the environment, API key and matching private key, API user activation/read permissions, IP allowlist and server clock synchronization. Network errors require outbound HTTPS access. Rate-limit errors should be retried later. Provider response bodies and underlying exception text are deliberately not displayed or logged by the core client. Request logging is disabled for this HTTP client; do not enable request/body capture in reverse proxies, APM or external diagnostics for credential-bearing settings requests. JWTs contain the API key and are also credentials.

## Storage and backup protection

Core credentials are stored in the existing **server-side per-store settings table in the BTCPay database**, under the plugin-named `Fireblocks.CoreApi` setting. They are independent of the Dynamic payment-method configuration, server API token and webhook signing secret. Disabling, replacing or deleting Dynamic Flow configuration does not remove core workspace credentials. This introduces no secret manager and no additional at-rest encryption layer. Write-only UI/API behavior is **not** database encryption: database administrators, host administrators and anyone with readable database backups can access the private key.

Protect database access and host volumes with least privilege; encrypt disks and backups, restrict backup access and retention, and secure restore/export workflows. Use HTTPS for operator access. Do not log settings POST bodies or enable model-binding value diagnostics. Keep a secure offline copy of the private key if required by your recovery policy; use Fireblocks API-user revocation/rotation when a key may be exposed. To replace secrets, supply new values and save; blank does not delete them. Test responses never include private keys, API keys or raw provider diagnostics.

## Dynamic Flow remains mainnet-only

Core API settings do not change the existing Dynamic environment, API token, webhook secret, hosted checkout, settlement address or fixed USDC/Solana mainnet asset/network policy. Configuring a Sandbox or Testnet core workspace **does not make Dynamic Flow checkout testnet-compatible**. A store can save/test core credentials with Dynamic Flow disabled and without configuring Dynamic credentials. Adding core settings never enables Flow checkout.

## Create a collection vault

Save core settings first, then select a supported asset under **Collection vault**. The list intersects the workspace's `/supported_assets` response with a narrow allowlist: mainnet `USDC_SOL`, or test `SOL_TEST` / `USDC_SOL_TEST` where available. Sandbox never offers mainnet USDC. Unsupported/custom tokens are intentionally excluded. Vault creation requires vault-account and asset-wallet creation permissions beyond the read-only connection test.

**Create vault and use address** creates one account (automatic fueling disabled), creates its asset wallet, and retrieves its deposit address. Pending wallet activation can require approvals or SOL supplied by the operator in the Fireblocks console. Use **Refresh status / retry**; there is no background provisioning job. Activation transactions, when returned by Fireblocks, must complete before the address is ready. Failures retain a safe diagnostic and existing progress; raw provider bodies are never shown.

A ready production-compatible vault exposes **Use address (replace saved destination)**. This explicit confirmation saves the destination without replacing credentials or enabling checkout. Sandbox/test results are visibly non-production and cannot be applied to the mainnet destination; existing checkout remains unchanged. Provisioning metadata (vault ID, asset ID, address, workspace environment and a hashed workspace identity) is stored separately as `Fireblocks.CollectionVault`, independent of Dynamic settings.

Each store gets one provisioning operation. Refreshes/resubmissions reuse it, serialized across server processes. The account POST uses a persisted idempotency key and identical body; wallet retries address the same vault/asset pair. If an account response remains ambiguous after 23 hours, creation stops rather than risking duplication beyond Fireblocks' 24-hour idempotency window. Reconcile the named **BTCPay collection** account in the console before any manual recovery; do not delete the operation setting and retry blindly. There is no UI reset or arbitrary existing-vault linking. Restore the original workspace credentials to refresh an operation after switching workspaces. Back up the provisioning record with the database.

Authentication follows the [Fireblocks JWT contract](https://developers.fireblocks.com/reference/signing-a-request-jwt-structure): RS256, per-request nonce, exact path/query, short expiration and SHA-256 request body hash.
