#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
K8S_DIR="$REPO_ROOT/k8s"

echo "Enabling Minikube metrics-server addon..."
minikube addons enable metrics-server

kubectl create namespace trading-system --dry-run=client -o yaml | kubectl apply -f -

echo "Applying Traefik CRDs and RBAC..."
kubectl apply -f "$K8S_DIR/06-traefik-rbac-crds.yaml"
kubectl wait --for=condition=Established crd/ingressroutes.traefik.io --timeout=60s

echo "Applying namespace, ConfigMap, and default Secret..."
kubectl apply -f "$K8S_DIR/00-namespace-config.yaml"

if [[ -f "$REPO_ROOT/.env" ]]; then
  echo "Syncing trading-secrets from .env..."
  eval "$(
    set -a
    # shellcheck disable=SC1091
    source "$REPO_ROOT/.env"
    printf 'PG_DB=%q\nPG_USER=%q\nPG_PASSWORD=%q\n' \
      "${POSTGRES_DB:-trading_db}" \
      "${POSTGRES_USER:-postgres}" \
      "${POSTGRES_PASSWORD:-postgres_password}"
  )"
  CONN_STR="Host=timescaledb;Port=5432;Database=${PG_DB};Username=${PG_USER};Password=${PG_PASSWORD};Timeout=5;Command Timeout=10"
  kubectl create secret generic trading-secrets \
    --namespace trading-system \
    --from-literal=POSTGRES_USER="$PG_USER" \
    --from-literal=POSTGRES_PASSWORD="$PG_PASSWORD" \
    --from-literal=POSTGRES_DB="$PG_DB" \
    --from-literal="ConnectionStrings__TimescaleDb=$CONN_STR" \
    --dry-run=client -o yaml | kubectl apply -f -
fi

echo "Applying TimescaleDB, Redis, Worker, Server, HPA, Traefik, and Routes..."
kubectl apply -f "$K8S_DIR/01-timescaledb.yaml"
kubectl apply -f "$K8S_DIR/02-redis.yaml"
kubectl apply -f "$K8S_DIR/03-worker.yaml"
kubectl apply -f "$K8S_DIR/04-server.yaml"
kubectl apply -f "$K8S_DIR/05-server-hpa.yaml"
kubectl apply -f "$K8S_DIR/07-traefik-deployment.yaml"
kubectl apply -f "$K8S_DIR/08-traefik-routes.yaml"

echo "Waiting for deployments to become ready..."
for deploy in timescaledb redis worker server traefik; do
  kubectl rollout status "deployment/$deploy" -n trading-system --timeout=180s
done

echo ""
echo "Deployment complete. Access the application on localhost using one of:"
echo "  kubectl port-forward -n trading-system svc/traefik 8080:80"
echo "  minikube service traefik -n trading-system --url"
echo ""
echo "Traefik dashboard (optional):"
echo "  kubectl port-forward -n trading-system svc/traefik 9000:8080"
