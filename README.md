# BTCPay Server Fireblocks plugin

Development scaffold only: **no payment method is enabled and no payments are processed**.
There are no provider credentials, API clients, or provider configuration in this bootstrap.

## Prerequisites

- .NET SDK **10.0.403** (exact version pinned in `global.json`), Git with submodule support.
- Python 3 for package verification and archive normalization.
- NuGet access for restore. Docker Compose is needed only to run the local BTCPay dependencies,
  not for the scaffold's unit tests.

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
