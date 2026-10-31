# BTCPay Server Fireblocks plugin

Development scaffold only: **no payment method is enabled and no payments are processed**.
There are no provider credentials, API clients, or provider configuration in this bootstrap.

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
contains metadata/discovery and no-op registration tests. The solution builds the referenced host;
it does not run the host's database/browser integration suites. CI runs the Release build, these same
plugin tests, and the package verifier via `bash scripts/ci.sh`.

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
**Server Settings → Plugins**. The plugin remains an inert scaffold: it has no Fireblocks checkout
payment method or provider configuration yet.

### Develop, restart, and inspect

After changing plugin source, rerun `./dev/up.sh`. It rebuilds changed layers, republishes the
plugin, recreates BTCPay so the new assembly loads, and safely reuses the existing admin/store.
Then inspect logs and exercise the baseline checkout UI:

```bash
# Create an invoice through the local Greenfield API and print its checkout URL.
python3 dev/create-invoice.py
# Optional amount/currency:
python3 dev/create-invoice.py --amount 25 --currency USD

# Inspect plugin loading and application errors.
docker compose -f dev/docker-compose.yml logs -f btcpay
# Inspect the regtest node or database when diagnosing dependencies.
docker compose -f dev/docker-compose.yml logs -f bitcoind postgres nbxplorer
```

Open the printed checkout URL, or sign in and use **Stores → Fireblocks Dev → Invoices → Create
Invoice**. Seeding also configures a fixed **watch-only regtest BTC wallet** (no private keys are
saved), so checkout has an on-chain payment method. The well-known development mnemonic is public
and must never hold real funds. The helper uses only Python's standard library. Override
`BTCPAY_DEV_URL`, `BTCPAY_DEV_EMAIL`, `BTCPAY_DEV_PASSWORD`, or `BTCPAY_DEV_STORE_NAME` when needed;
`BTCPAY_DEV_STARTUP_TIMEOUT` controls the readiness wait in seconds (default 300).

Stop the stack while retaining data, or delete all local harness data and start fresh:

```bash
docker compose -f dev/docker-compose.yml down
docker compose -f dev/docker-compose.yml down --volumes
```

### Run the CI gate in the harness

Use the following command to run the exact Release build, plugin tests, package checks, and
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
in **Server Settings → Plugins**. There is deliberately no payment-method UI yet.
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
`v*` tag pushes, and manual dispatch. Actions are SHA-pinned. Only the tag publication job gets
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
For a release, update the project `<Version>` and the version assertion in `PluginTests.cs`
(the plugin reads its version from assembly metadata), merge the reviewed change, then push a
matching tag such as `v0.1.0`. Mismatched tags fail before building.
After the full gate passes, the tag workflow creates a GitHub release and attaches the `.btcpay`,
`.btcpay.json`, and checksums. Rerunning a tag build replaces that release's assets.

Verify downloads with `sha256sum --check SHA256SUMS`. Install the `.btcpay` file via a compatible
BTCPay instance's **Server Settings → Plugins → Upload Plugin** and restart when prompted.
The metadata and checksums are companion files, not separate plugins. Packages remain a scaffold;
release automation does not make payment processing available.

## Future payment-method seam

`Plugin.Execute(IServiceCollection)` calls `AddFireblocksPaymentMethod()`. This currently returns the
same collection without changing registrations. Add the real handler registration there in a later
issue; do not advertise a fake payment method or add provider configuration to this scaffold.

## License

MIT — see [LICENSE](LICENSE).
