#!/usr/bin/env bash
# Build and run the Agency Harness with Podman on this machine (macOS / Linux / WSL).
# Usage:  ./scripts/host.sh            build + (re)start on http://localhost:8080
#         ./scripts/host.sh --tunnel   ...and open a public HTTPS address with Cloudflare
set -euo pipefail
cd "$(dirname "$0")/.."

CLI="${CONTAINER_CLI:-podman}"          # set CONTAINER_CLI=docker to use Docker instead
IMAGE="${IMAGE:-agency-harness:latest}"
NAME=agency-harness
PORT="${HOST_PORT:-8080}"
ENV_FILE=harness.env

command -v "$CLI" >/dev/null || { echo "$CLI is not installed or not on PATH."; exit 1; }

# 1. Secrets live in harness.env (git-ignored), created once with a strong random admin key.
if [ ! -f "$ENV_FILE" ]; then
  ADMIN_KEY="adm_$(od -An -tx1 -N20 /dev/urandom | tr -d ' \n')"
  cat > "$ENV_FILE" <<ENV
# Agency Harness settings. Keep this file private.
Harness__AdminKey=$ADMIN_KEY
# Add your Claude API key to have real Claude agents (otherwise a simulated runner is used):
# ANTHROPIC_API_KEY=sk-ant-...
ENV
  chmod 600 "$ENV_FILE"
  echo "Created $ENV_FILE with a new admin key."
fi

# 2. Build the image (skip with SKIP_BUILD=1 if you already have it).
if [ "${SKIP_BUILD:-0}" != "1" ]; then
  echo "Building $IMAGE…"
  "$CLI" build -t "$IMAGE" .
fi

# 3. (Re)start the container. Data stays in the named volume across restarts and upgrades.
"$CLI" volume inspect agency-harness-data >/dev/null 2>&1 || "$CLI" volume create agency-harness-data >/dev/null
"$CLI" rm -f "$NAME" >/dev/null 2>&1 || true
"$CLI" run -d --name "$NAME" --restart=unless-stopped \
  -p "$PORT:8080" -v agency-harness-data:/data --env-file "$ENV_FILE" "$IMAGE" >/dev/null

printf "Starting"
for _ in $(seq 1 40); do
  if curl -fsS "http://localhost:$PORT/health/ready" >/dev/null 2>&1; then echo " ready."; break; fi
  printf "."; sleep 1
done
curl -fsS "http://localhost:$PORT/health/ready" >/dev/null || { echo; echo "Not healthy yet. Check: $CLI logs $NAME"; exit 1; }

ADMIN_KEY=$(grep '^Harness__AdminKey=' "$ENV_FILE" | cut -d= -f2-)
cat <<MSG

Agency Harness is running at http://localhost:$PORT  (MCP: http://localhost:$PORT/mcp)

Create your agency's API key (shown once, save it):
  curl -s -X POST http://localhost:$PORT/admin/tenants -H "X-Admin-Key: $ADMIN_KEY" \\
       -H 'Content-Type: application/json' -d '{"name":"My Agency"}'

Logs:   $CLI logs -f $NAME
Stop:   $CLI stop $NAME
MSG

# 4. Optional: a public HTTPS address via a free Cloudflare quick tunnel (no account needed).
if [ "${1:-}" = "--tunnel" ]; then
  command -v cloudflared >/dev/null || { echo "Install cloudflared first: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/"; exit 1; }
  echo; echo "Opening a public tunnel. Look for the https://….trycloudflare.com address below. Ctrl+C closes it."
  exec cloudflared tunnel --url "http://localhost:$PORT"
fi
