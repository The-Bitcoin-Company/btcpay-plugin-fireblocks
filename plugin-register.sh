#!/usr/bin/env bash
set -euo pipefail

# Configure the pinned BTCPay Server checkout to load the plugin during debugging.
cd "$(dirname "$0")"
# shellcheck source=plugin-env.sh
source plugin-env.sh

TARGET_PATH="$(dotnet build "src/$PROJECT/$PROJECT.csproj" -p:Configuration=Debug -getProperty:TargetPath)"

printf '{ "DEBUG_PLUGINS": "%s" }' "$TARGET_PATH" > "submodules/btcpayserver/BTCPayServer/appsettings.dev.json"

echo "The plugin will now start when debugging BTCPay Server"