#!/usr/bin/env bash
# python-task-lab — the domain Python runtime (vnext PythonTask, type 23) beside the local runtime.
#
#   ./lab.sh up [<vnext-checkout>]     builds the vNext base image from the checkout, then this lab's domain
#                                      image, and starts it with its Dapr sidecar (needs the vnext infra up)
#   ./lab.sh env                       prints the env the Orchestration host needs (start it with this set)
#   ./lab.sh down                      stops the runtime and its sidecar
set -euo pipefail
LAB="$(cd "$(dirname "$0")" && pwd)"
export VNEXT_DIR="$(cd "${2:-$LAB/../../../vnext}" && pwd)"

case "${1:-}" in
  up)
    docker build -q -f "$VNEXT_DIR/runtimes/python/Dockerfile" -t vnext-python-runtime:local "$VNEXT_DIR" >/dev/null
    docker compose -f "$LAB/docker-compose.yml" up -d --build
    for _ in $(seq 1 30); do curl -sf localhost:8089/v1/health >/dev/null && break; sleep 1; done
    curl -sf localhost:8089/v1/manifest; echo
    for _ in $(seq 1 30); do curl -sf localhost:3589/v1.0/healthz >/dev/null && break; sleep 1; done
    curl -sf localhost:3589/v1.0/invoke/vnext-python-runtime-app/method/v1/health && echo " (via its sidecar)"
    ;;
  env)
    echo "Workflow__PythonRuntime__AppId=vnext-python-runtime-app"
    ;;
  down)
    docker compose -f "$LAB/docker-compose.yml" down
    ;;
  *) sed -n 2,8p "$0"; exit 1 ;;
esac
