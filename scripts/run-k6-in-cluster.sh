#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

NAMESPACE="${K8S_NAMESPACE:-trading-system}"
POD_NAME="${K6_POD_NAME:-k6-loadtest}"
VUS="${1:-50}"
DURATION="${2:-60s}"
BASE_URL="${3:-http://traefik.${NAMESPACE}.svc.cluster.local}"
SSE_TIMEOUT="${SSE_TIMEOUT:-5s}"

cleanup() {
  kubectl delete pod "$POD_NAME" -n "$NAMESPACE" --ignore-not-found --wait=false >/dev/null 2>&1 || true
}
trap cleanup INT TERM EXIT

kubectl create configmap k6-scripts \
  -n "$NAMESPACE" \
  --from-file="$REPO_ROOT/loadtests/" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null

kubectl delete pod "$POD_NAME" -n "$NAMESPACE" --ignore-not-found --wait=true >/dev/null 2>&1 || true

OVERRIDES=$(cat <<EOF
{
  "spec": {
    "containers": [{
      "name": "${POD_NAME}",
      "image": "grafana/k6:latest",
      "args": ["run", "/scripts/user-simulation.js"],
      "env": [
        {"name": "VUS", "value": "${VUS}"},
        {"name": "DURATION", "value": "${DURATION}"},
        {"name": "BASE_URL", "value": "${BASE_URL}"},
        {"name": "SSE_TIMEOUT", "value": "${SSE_TIMEOUT}"}
      ],
      "volumeMounts": [{"name": "scripts", "mountPath": "/scripts"}]
    }],
    "volumes": [{"name": "scripts", "configMap": {"name": "k6-scripts"}}]
  }
}
EOF
)

rc=0
kubectl run "$POD_NAME" -n "$NAMESPACE" --rm -i --restart=Never \
  --image=grafana/k6:latest \
  --overrides="$OVERRIDES" \
  2> >(grep -vE 'msg="Request Failed" error="request timeout"' >&2 || true) || rc=$?
wait
exit "$rc"
