# BTCPay Server Fireblocks plugin

Creates hosted Fireblocks Flow payment links for BTCPay USD invoices, with store-scoped Dynamic configuration and USDC settlement on Solana. This release covers payment creation and checkout only: **it does not confirm settlement or mark invoices paid**.

## Prerequisites

- .NET SDK **10.0.403** (exact version pinned in `global.json`), Git with submodule support.
- Python 3 for package verification and archive normalization.
- NuGet access for restore. Docker with Compose is needed for the local BTCPay/regtest harness
  and for running the CI gate in containers; it is not needed for host-installed .NET unit tests.

## Checkout, build, test

```bash
git clone --recurse-submodules https://github.com/The-Bitcoin-Company/btcpay-plugin-fireblocks.git
cd btcpay-plugin-fireblocks
# Also run after pulling changes to the pinned submodule:
git submodule update --init --recursive
dotnet build
dotnet test
```

`src/BTCPayServer.Plugins.Fireblocks` contains the plugin; `tests/BTCPayServer.Plugins.Fireblocks.Tests`
covers provider contracts, validation, credential redaction, persistence, timeout/error behavior, and checkout. The solution builds the referenced host;
it does not run the host's database/browser integration suites. CI runs the Release build, these same
plugin tests, and the package verifier via `bash scripts/ci.sh`.

## Nix development and package

On Linux with Nix flakes enabled, enter the reproducible development shell:

```bash
nix develop
dotnet --version # 10.0.403
```

The shell provides the pinned .NET SDK, Git, and Python 3. Initialize the BTCPay submodule as above
for normal `dotnet` commands; the plain workflow remains fully supported and does not require Nix.
The development shell does not replace Docker for the optional regtest harness.

Build the plugin package and run the same package build through the flake checks:

```bash
nix build
nix flake check
```

`nix build` does not require an initialized submodule: the flake independently pins the same BTCPay
v2.4.7 source revision. The `result` symlink contains the framework-dependent publish output at
`result/share/btcpayserver/Plugins/BTCPayServer.Plugins.Fireblocks/`, ready to mount or copy into a
BTCPay plugins directory. This is the plugin-directory package intended for future NixOS modules,
not the `.btcpay` release archive produced by `scripts/ci.sh`.

NuGet dependencies are locked in `nix/deps.json`. After changing project or BTCPay dependencies,
regenerate the lock with the package's fetch-deps helper and commit the result:

```bash
nix build .#fetch-deps
./result nix/deps.json
```

The `nixpkgs` lock also pins SDK 10.0.403. When upgrading BTCPay, update its source input together
with the Git submodule and the other compatibility pins described below.

## Upstream and compatibility

Adapted from the [official plugin template](https://github.com/btcpayserver/btcpayserver-plugin-template)
at commit `21fd2d529b94c9ff076f9bf61893d85854d86ba3` (current upstream when bootstrapped),
following the [plugin development docs](https://docs.btcpayserver.org/Developers/plugins/).
Kept its Razor SDK project conventions, `src`/`tests` layout, .NET ignore rules, and debug registration
scripts; removed the sample controller/menu/views and replaced the integration test with focused unit tests.
The solution lists only the plugin and tests to avoid building host projects concurrently with conflicting
`StaticWebAssetsEnabled` properties. Open the host's own solution separately when debugging it.

The current template uses a **ProjectReference to a source submodule, not a BTCPay NuGet package**.
The BTCPay dependency is therefore pinned by the gitlink at `submodules/btcpayserver`:
**v2.4.7**, commit `2868deedd9d911b715fec7306ea9cada36d86bff`.
Do not replace this with an unversioned checkout or an invented package reference.
The runtime dependency is `>=2.4.7 && <2.5.0`; earlier 2.4 patches and 2.5+ are not declared supported.
The deliberate build pairing is **BTCPay v2.4.7 ↔ .NET SDK 10.0.403**, matching the SDK image in
that release's `Dockerfile` and its `net10.0` target. `global.json` disables SDK roll-forward.
When upgrading, update the submodule pin, `global.json` (from the new host Dockerfile), dependency
condition, and tests together; validate the packaged plugin on the new host.

## Local BTCPay + regtest harness

From a clean checkout, one command initializes the pinned submodule, builds the Release plugin,
starts BTCPay Server, PostgreSQL, NBXplorer, and Bitcoin Core in regtest, and seeds a store:

```bash
./dev/up.sh
```

Open **http://localhost:14142** and sign in with the development-only credentials
`dev@fireblocks.local` / `DevPassword123!`. The seeded store is **Fireblocks Dev**. These
credentials and the database are local/disposable; never reuse them or this stack for real funds.
Only loopback port `14142` is published. Named volumes retain BTCPay, PostgreSQL, NBXplorer, and
regtest chain data between restarts.

The build container uses the same **.NET SDK 10.0.403** and **BTCPay v2.4.7** source pin as CI.
It publishes to `artifacts/dev-plugin`, which is mounted read-only at
`/datadir/Plugins/BTCPayServer.Plugins.Fireblocks` in the BTCPay container. Confirm startup by
looking for `Running plugin BTCPayServer.Plugins.Fireblocks - 0.1.0` in the logs or by opening
**Server Settings → Plugins**. Seeding configures Fireblocks Flow against the disposable fake provider,
so no real Dynamic environment or credential is needed locally.

### Develop, restart, and inspect

After changing plugin source, rerun `./dev/up.sh`. It rebuilds changed layers, republishes the
plugin, recreates BTCPay so the new assembly loads, and safely reuses the existing admin/store.
Then inspect logs and exercise BTC or fake Flow checkout:

```bash
# Create an invoice through the local Greenfield API and print its checkout URL.
python3 dev/create-invoice.py
# Optional amount/currency (fiat invoices require an available rate source):
python3 dev/create-invoice.py --amount 25 --currency USD

# Inspect plugin loading and application errors.
docker compose -f dev/docker-compose.yml logs -f btcpay
# Inspect the regtest node or database when diagnosing dependencies.
docker compose -f dev/docker-compose.yml logs -f bitcoind postgres nbxplorer
```

Open the printed checkout URL, or sign in and use **Stores → Fireblocks Dev → Invoices → Create
Invoice**. For Flow, create a **USD** invoice and select **FIREBLOCKS-FLOW** at checkout. Its hosted
link points to the fake page at **http://localhost:14143**; visiting it never changes invoice state.
Seeding also configures a fixed **watch-only regtest BTC wallet** (no private keys are
saved), so checkout has an on-chain payment method. The well-known development mnemonic is public
and must never hold real funds. The helper uses only Python's standard library. Override
`BTCPAY_DEV_URL`, `BTCPAY_DEV_EMAIL`, `BTCPAY_DEV_PASSWORD`, or `BTCPAY_DEV_STORE_NAME` when needed.
For production isolation, `BTCPAY_DEV_URL` must be loopback; API redirects and environment proxies
are disabled.
`BTCPAY_DEV_STARTUP_TIMEOUT` controls the readiness wait in seconds (default 300). Seeding stores
an unrestricted key for the disposable admin in `artifacts/dev/api-key` with owner-only permissions;
routine calls use that key to avoid Basic-auth expiry and login throttling. The key is ignored by Git
and automatically replaced if stale. Basic auth is also enabled as a recovery path. If both local
authentication methods are lost, enable **Greenfield Basic Auth** in the local user's settings or
recreate the stack with `down --volumes`.

Stop the stack while retaining data, or delete all local harness data and start fresh:

```bash
docker compose -f dev/docker-compose.yml down
docker compose -f dev/docker-compose.yml down --volumes
```

### Run the CI gate in the harness

Use the following command to run the exact Release build, plugin and helper tests, package checks, and
`scripts/ci.sh` gate in the pinned SDK container rather than relying on a host .NET installation:

```bash
./dev/ci.sh
```

The working tree and a persistent NuGet cache are mounted into that container. CI currently has
no separate lint step; the command above is the complete CI test subset. Python helpers can be
syntax-checked with `python3 -m compileall dev`, and Compose configuration with
`docker compose -f dev/docker-compose.yml config --quiet`.

## Run against the local BTCPay checkout

From the repository root:

```bash
./plugin-register.sh
(cd submodules/btcpayserver/BTCPayServer.Tests && docker compose up -d dev)
dotnet run --project submodules/btcpayserver/BTCPayServer --launch-profile 'Bitcoin-HTTPS'
```

The registration script builds the Debug plugin and writes its absolute assembly path as `DEBUG_PLUGINS`
in `submodules/btcpayserver/BTCPayServer/appsettings.dev.json`. It **replaces** that local file;
back up any existing settings first. The file is ignored by the host checkout. For a separate 2.4.7
source checkout, set `DEBUG_PLUGINS` to the same absolute DLL path in that checkout's development settings.
Rebuild after changes and restart BTCPay (or use a breakpoint in `Plugin.Execute` in your debugger).

Look for `Running plugin BTCPayServer.Plugins.Fireblocks - 0.1.0` in the startup logs and the entry
in **Server Settings → Plugins**. Configure the store through **Plugins → Fireblocks Flow**.
Delete the generated `appsettings.dev.json` (or remove its `DEBUG_PLUGINS` entry) to unregister.
Stop development dependencies with `docker compose down` from `BTCPayServer.Tests` when finished.

### Load via a plugins directory

Alternatively, stop a local compatible BTCPay instance and publish:

```bash
dotnet publish src/BTCPayServer.Plugins.Fireblocks -c Release -o artifacts/fireblocks
```

Copy the **contents** of `artifacts/fireblocks` into
`<BTCPay data directory>/Plugins/BTCPayServer.Plugins.Fireblocks/`, so that the DLL is at
`Plugins/BTCPayServer.Plugins.Fireblocks/BTCPayServer.Plugins.Fireblocks.dll`, then restart BTCPay.
Use the configured data directory (it varies by OS/network/container); the plugin directory name,
assembly filename, and metadata identifier must match. Do not install this copy and a `DEBUG_PLUGINS`
copy simultaneously. Host assemblies are intentionally excluded from the published plugin.
Plugins run with the host's privileges: use a disposable development instance, not production funds.

## CI and release packages

`.github/workflows/ci.yml` runs on every pull request, pushes to `master` (the default branch),
`v*` tag pushes, and manual dispatch. The `nix-flake` job runs `nix flake check`, including the
Nix package build. Actions are SHA-pinned. Only the tag publication job gets
`contents: write`; all builds use `contents: read` and checkout does not persist credentials.
NuGet downloads are cached; build outputs are deliberately rebuilt rather than restored from cache.
Build and publish use `ContinuousIntegrationBuild` and the same pinned source and SDK.

Run the exact CI gate locally:

```bash
bash scripts/ci.sh
```

The pinned host's **PluginPacker** produces an installable `.btcpay` archive and `.btcpay.json`
metadata under `artifacts/package/BTCPayServer.Plugins.Fireblocks/<version>/`, alongside `SHA256SUMS`.
The verifier preserves **all** published files (including future content assets), rejects bundled
host assemblies, normalizes ZIP entry order/timestamps/permissions, and regenerates checksums.
This makes archive construction repeatable for identical publish output; it is not a claim of
bit-for-bit reproducibility across different operating systems or dependency feeds.

Download `fireblocks-plugin` from a workflow run's **Artifacts** section (including PR builds);
GitHub wraps the package files in an outer ZIP. Test results are uploaded separately.
For a release, update the project `<Version>`, `version` in `flake.nix`, and the version assertion
in `PluginTests.cs` (the plugin reads its version from assembly metadata), merge the reviewed change, then push a
matching tag such as `v0.1.0`. Mismatched tags fail before building.
After the full gate passes, the tag workflow creates a GitHub release and attaches the `.btcpay`,
`.btcpay.json`, and checksums. Rerunning a tag build replaces that release's assets.

Verify downloads with `sha256sum --check SHA256SUMS`. Install the `.btcpay` file via a compatible
BTCPay instance's **Server Settings → Plugins → Upload Plugin** and restart when prompted.
The metadata and checksums are companion files, not separate plugins. Release packages contain the same payment-link integration; settlement processing remains a separate
future change.

## Fireblocks Flow configuration

Configure a store under **Plugins → Fireblocks Flow**, or through the BTCPay payment-method Greenfield
endpoint for `FIREBLOCKS-FLOW`:

- **Dynamic environment ID** and **Flow API endpoint** (production default:
  `https://app.dynamic.xyz/api/v0`).
- **Server API token** with `flow.write` scope. It is used only by the server-side client. Blank UI
  updates retain the saved token; configuration responses omit the credential.
- **Hosted payment base URL** (production default: `https://pay.dynamicauth.com`). Register its origin
  as an allowed CORS origin in Dynamic, as required by the payment-link API.
- **Solana USDC destination address**, a valid base58 address. The request has exactly one settlement
  (Solana mainnet chain `SOL`/`101`, USDC mint
  `EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v`, six decimals) and one matching destination.
- **Maximum payment lifetime in minutes**. At prompt creation the plugin sets the BTCPay invoice
  expiration to the earlier of its existing expiration and this policy deadline. This keeps checkout
  and the Flow link on the same local lifetime, but can also shorten other payment methods on that
  invoice; it never extends the invoice's lifetime.

Only fixed-amount **USD** invoices are supported. Requests use payment mode with `currency: "USD"`;
the requested USD value is persisted separately from any eventual token settlement amount. Every
attempt stores its immutable creation JSON, monotonic attempt number, invoice/attempt correlation,
provider endpoint/environment (never the credential), Flow ID, hosted URL, requested USD amount, and
local/provider expiries in the plugin's PostgreSQL schema. A unique invoice/attempt-number constraint
prevents concurrent processes from issuing duplicate creation calls. Definite failures and attempts
whose local and provider expiries have both passed can be renewed without deleting history.
A timeout, transport failure, 5xx response, or ambiguous success is recorded as uncertain and is never
retried merely because time passed. Such an attempt requires explicit provider/operator reconciliation
(outside this release) before another Flow should be created for that invoice.

The checkout displays only the hosted URL and public attempt metadata. Expired links, and links whose
fixed amount no longer matches the invoice balance after another payment method is used, are suppressed
instead of presenting a stale charge. There is no browser-return settlement endpoint, and opening a
link does not add a BTCPay payment or change invoice state. Future
settlement work must reconcile verified provider notifications or server-side Flow reads; it must not
trust a browser return.

### Local fake provider and failure modes

`./dev/up.sh` starts `flow-fake` on loopback port `14143` and seeds a development-only token. The fake
validates the same USD/USDC-Solana/expiry/correlation request shape and returns deterministic hosted
links. Restarting BTCPay preserves the invoice/attempt/Flow binding. To inspect the persisted attempts,
query the disposable database:

```bash
docker compose -f dev/docker-compose.yml exec -T postgres psql -U postgres -d btcpayserver \
  -c 'SELECT invoice_id, attempt_number, state, flow_id, requested_usd, expires_at, provider_expires_at, api_endpoint, environment_id FROM "Fireblocks".fireblocks_payment_attempts;'
```

Exercise provider rejection or a delayed response by setting the fake mode before startup:

```bash
FLOW_FAKE_MODE=error ./dev/up.sh
FLOW_FAKE_MODE=timeout FLOW_FAKE_TIMEOUT_SECONDS=30 ./dev/up.sh
```

The production API client has a 15-second timeout. After testing a failure, return to
`FLOW_FAKE_MODE=success ./dev/up.sh`; use a new invoice for a clean successful attempt. The fake and
its token are strictly development fixtures. Real API endpoints require HTTPS and use BTCPay's SSRF
protection. Plain HTTP/private endpoints work only when the harness explicitly enables
`FIREBLOCKS_ENABLE_LOCAL_FLOW_FAKE=true`; do not enable that escape hatch in production. Real
credentials must never be committed or pasted into documentation.

## License

MIT — see [LICENSE](LICENSE).
