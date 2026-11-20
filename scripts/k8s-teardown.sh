#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
K8S_DIR="$REPO_ROOT/k8s"

echo "Deleting namespace trading-system..."
kubectl delete namespace trading-system --ignore-not-found

echo "Deleting Traefik ClusterRole, ClusterRoleBinding, and CRDs..."
kubectl delete -f "$K8S_DIR/06-traefik-rbac-crds.yaml" --ignore-not-found

echo "Teardown complete."
