#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
git submodule update --init --recursive
mkdir -p artifacts/dev-plugin

docker compose -f dev/docker-compose.yml up --detach --build --force-recreate
python3 dev/seed.py

cat <<'EOF'

Fireblocks dev stack is ready at http://localhost:14142
Create a test invoice with: python3 dev/create-invoice.py
Follow BTCPay logs with: docker compose -f dev/docker-compose.yml logs -f btcpay
EOF
