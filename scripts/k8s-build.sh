#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! minikube status --format='{{.Host}}' 2>/dev/null | grep -q "Running"; then
  echo "Minikube is not running. Start it first with: minikube start" >&2
  exit 1
fi

eval "$(minikube docker-env)"
export BUILDX_BUILDER=default

echo "Building trading-worker:local inside Minikube Docker..."
docker build -t trading-worker:local -f "$REPO_ROOT/services/worker/Dockerfile" "$REPO_ROOT"

echo "Building trading-server:local inside Minikube Docker..."
docker build -t trading-server:local -f "$REPO_ROOT/services/server/Dockerfile" "$REPO_ROOT"

echo "Done. Built images:"
docker images --format "table {{.Repository}}\t{{.Tag}}\t{{.Size}}" | grep -E "REPOSITORY|trading-(worker|server)"
