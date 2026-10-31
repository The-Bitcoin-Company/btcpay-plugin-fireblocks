#!/usr/bin/env bash
# The same build/test/package gate runs locally and in GitHub Actions.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
plugin=BTCPayServer.Plugins.Fireblocks
project="src/$plugin/$plugin.csproj"
packer=submodules/btcpayserver/BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj
version=$(python3 -c 'import xml.etree.ElementTree as E; print(E.parse("src/BTCPayServer.Plugins.Fireblocks/BTCPayServer.Plugins.Fireblocks.csproj").findtext(".//Version"))')
# Refuse to publish an archive whose assembly version differs from the release tag.
if [[ -n "${RELEASE_TAG:-}" && "$RELEASE_TAG" != "v$version" ]]; then
  echo "Release tag $RELEASE_TAG must match project version v$version" >&2
  exit 1
fi

# Fail deliberately if a host upgrade leaves the SDK pin behind.
python3 - <<'PY'
import json
from pathlib import Path
sdk = json.loads(Path('global.json').read_text())['sdk']['version']
assert f'dotnet/sdk:{sdk}-' in Path('submodules/btcpayserver/Dockerfile').read_text(), 'SDK pin must match the pinned host Dockerfile'
PY
python3 scripts/test_verify_package.py

dotnet restore BTCPayServer.Plugins.Fireblocks.slnx
dotnet restore "$packer"
dotnet build BTCPayServer.Plugins.Fireblocks.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet test tests/BTCPayServer.Plugins.Fireblocks.Tests -c Release --no-build --no-restore --logger trx --results-directory artifacts/test-results
# Never let stale publish files leak into a new package.
rm -rf artifacts/publish artifacts/package
dotnet publish "$project" -c Release --no-build --no-restore -o artifacts/publish -p:ContinuousIntegrationBuild=true
dotnet run --project "$packer" -c Release --no-restore -p:ContinuousIntegrationBuild=true -- artifacts/publish "$plugin" artifacts/package
directory="artifacts/package/$plugin/$version"
python3 scripts/verify_package.py "$directory"
# Upload this directory itself so download-artifact exposes checksums and assets
# at the artifact root (not inside a version subdirectory).
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "directory=$directory" >> "$GITHUB_OUTPUT"
fi
