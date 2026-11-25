#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 binary not found in PATH. Install Grafana k6 first." >&2
  exit 1
fi

VUS="${1:-50}"
DURATION="${2:-60s}"
BASE_URL="${3:-http://localhost:8080}"

if [[ "$BASE_URL" == "minikube" ]]; then
  MINIKUBE_IP="$(minikube ip 2>/dev/null || true)"
  NODE_PORT="$(kubectl get svc traefik -n trading-system -o jsonpath='{.spec.ports[?(@.name=="web")].nodePort}' 2>/dev/null || true)"
  if [[ -z "$MINIKUBE_IP" || -z "$NODE_PORT" ]]; then
    echo "Failed to resolve Minikube IP or Traefik NodePort." >&2
    exit 1
  fi
  BASE_URL="http://${MINIKUBE_IP}:${NODE_PORT}"
  echo "Resolved direct Traefik URL: $BASE_URL" >&2
elif [[ "$BASE_URL" =~ ^https?://(127\.0\.0\.1|localhost):[0-9]+ ]] && [[ "$VUS" =~ ^[0-9]+$ ]] && (( VUS >= 200 )); then
  echo "WARNING: $BASE_URL looks like a local tunnel/port-forward. At ${VUS} VUs, local tunnels often reset connections; pass 'minikube' as 3rd arg for direct NodePort." >&2
fi

rc=0
k6 run \
  -e VUS="$VUS" \
  -e DURATION="$DURATION" \
  -e BASE_URL="$BASE_URL" \
  "$REPO_ROOT/loadtests/user-simulation.js" \
  "${@:4}" 2> >(grep -vE 'msg="Request Failed" error="request timeout"' >&2 || true) || rc=$?
wait
exit "$rc"
