#!/usr/bin/env bash
set -euo pipefail

NAMESPACE="trading-system"
INTERVAL="2"
ONCE="false"

usage() {
  cat <<'EOF'
Usage: ./scripts/k8s-status.sh [options]
Options:
  --once                 Print one snapshot and exit
  -n, --interval <sec>   Refresh interval in seconds (default: 2)
  --namespace <ns>       Kubernetes namespace (default: trading-system)
  -h, --help             Show this help message
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --once)
      ONCE="true"
      shift
      ;;
    -n|--interval)
      INTERVAL="${2:-2}"
      shift 2
      ;;
    --namespace)
      NAMESPACE="${2:-trading-system}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

render_snapshot() {
  local now minikube_ip traefik_port direct_url
  now="$(date '+%Y-%m-%d %H:%M:%S %Z')"
  minikube_ip="$(minikube ip 2>/dev/null || true)"
  traefik_port="$(kubectl get svc traefik -n "$NAMESPACE" -o jsonpath='{.spec.ports[?(@.name=="web")].nodePort}' 2>/dev/null || true)"
  if [[ -n "$minikube_ip" && -n "$traefik_port" ]]; then
    direct_url="http://${minikube_ip}:${traefik_port}"
  else
    direct_url="n/a"
  fi

  echo "=== Trading System K8s Status | ns=${NAMESPACE} | ${now} ==="
  echo "Direct Traefik URL: ${direct_url}"
  echo ""

  echo "--- PODS & RESOURCES (use / req / lim) ---"
  local pods_json top_out
  pods_json="$(kubectl get pods -n "$NAMESPACE" -o json 2>/dev/null || echo '{"items":[]}')"
  top_out="$(kubectl top pods -n "$NAMESPACE" --no-headers 2>/dev/null || true)"

  PODS_JSON="$pods_json" TOP_OUT="$top_out" python3 - <<'PY'
import json, os

pods = json.loads(os.environ.get("PODS_JSON", '{"items":[]}')).get("items", [])
top_map = {}
for line in os.environ.get("TOP_OUT", "").splitlines():
    parts = line.split()
    if len(parts) >= 3:
        top_map[parts[0]] = (parts[1], parts[2])

if not pods:
    print("No pods found.")
    raise SystemExit(0)

fmt = "{:<28} {:<7} {:<14} {:<8} {:<24} {:<24}"
print(fmt.format("POD", "READY", "STATUS", "RESTARTS", "CPU (use/req/lim)", "MEM (use/req/lim)"))
for p in pods:
    name = p["metadata"]["name"]
    phase = p.get("status", {}).get("phase", "Unknown")
    cstatuses = p.get("status", {}).get("containerStatuses", [])
    total = len(p.get("spec", {}).get("containers", []))
    ready = sum(1 for c in cstatuses if c.get("ready"))
    restarts = sum(c.get("restartCount", 0) for c in cstatuses)

    reason = phase
    for c in cstatuses:
        state = c.get("state", {})
        if "waiting" in state and state["waiting"].get("reason"):
            reason = state["waiting"]["reason"]
            break
        if "terminated" in state and state["terminated"].get("reason"):
            reason = state["terminated"]["reason"]
            break

    reqs = p.get("spec", {}).get("containers", [{}])[0].get("resources", {})
    req_cpu = reqs.get("requests", {}).get("cpu", "-")
    lim_cpu = reqs.get("limits", {}).get("cpu", "-")
    req_mem = reqs.get("requests", {}).get("memory", "-")
    lim_mem = reqs.get("limits", {}).get("memory", "-")
    use_cpu, use_mem = top_map.get(name, ("n/a", "n/a"))

    print(fmt.format(
        name[:28],
        f"{ready}/{total}",
        reason[:14],
        str(restarts),
        f"{use_cpu}/{req_cpu}/{lim_cpu}",
        f"{use_mem}/{req_mem}/{lim_mem}",
    ))
PY

  echo ""
  echo "--- AUTOSCALING (HPA) ---"
  kubectl get hpa -n "$NAMESPACE" 2>/dev/null || echo "No HPAs found."

  echo ""
  echo "--- SERVICES & ENDPOINTS ---"
  local svc_json ep_json
  svc_json="$(kubectl get svc -n "$NAMESPACE" -o json 2>/dev/null || echo '{"items":[]}')"
  ep_json="$(kubectl get endpoints -n "$NAMESPACE" -o json 2>/dev/null || echo '{"items":[]}')"

  SVC_JSON="$svc_json" EP_JSON="$ep_json" python3 - <<'PY'
import json, os

svcs = json.loads(os.environ.get("SVC_JSON", '{"items":[]}')).get("items", [])
eps = json.loads(os.environ.get("EP_JSON", '{"items":[]}')).get("items", [])
ep_map = {}
for e in eps:
    name = e["metadata"]["name"]
    ready_cnt = sum(len(s.get("addresses", [])) for s in e.get("subsets", []))
    not_ready_cnt = sum(len(s.get("notReadyAddresses", [])) for s in e.get("subsets", []))
    ep_map[name] = f"{ready_cnt} ready" + (f", {not_ready_cnt} not-ready" if not_ready_cnt else "")

if not svcs:
    print("No services found.")
    raise SystemExit(0)

fmt = "{:<22} {:<12} {:<26} {:<20}"
print(fmt.format("SERVICE", "TYPE", "PORTS", "ENDPOINTS"))
for s in svcs:
    name = s["metadata"]["name"]
    stype = s.get("spec", {}).get("type", "-")
    ports = ",".join(
        f"{p.get('port')}" + (f":{p['nodePort']}" if p.get("nodePort") else "")
        for p in s.get("spec", {}).get("ports", [])
    )
    print(fmt.format(name[:22], stype[:12], ports[:26], ep_map.get(name, "0 ready")))
PY

  echo ""
  echo "--- RECENT WARNINGS (last 5) ---"
  local warnings
  warnings="$(kubectl get events -n "$NAMESPACE" --field-selector type=Warning --sort-by=.lastTimestamp 2>/dev/null | tail -n 5 || true)"
  if [[ -z "$warnings" ]]; then
    echo "None"
  else
    echo "$warnings"
  fi
}

if [[ "$ONCE" == "true" ]]; then
  render_snapshot
  exit 0
fi

trap 'exit 0' INT TERM
while true; do
  clear
  render_snapshot
  sleep "$INTERVAL"
done
