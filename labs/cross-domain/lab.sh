#!/usr/bin/env bash
# Cross-domain lab: three vNext domains (core, partner, discovery) on one Docker network, driven by
# the burgan-tech/vnext-runtime compose template. core and partner run LOCALLY BUILT images (default
# tag "dapr-nr") and register themselves in the discovery domain at startup; cross-domain calls go
# through Dapr Name Resolution (mDNS in compose) + Service Invocation.
#
# Usage: labs/cross-domain/lab.sh <command> [args]
#   images            build orchestrator/execution/inbox/outbox/db-migrator images from $VNEXT_SRC_DIR
#   up                clone runtime template (if missing), start infra + discovery + core + partner, verify
#   down [--all]      stop the three domains (--all also stops shared infra)
#   status            container + health summary
#   verify            health, discovery registrations, sidecar-level cross-domain invoke
#   logs <domain> [service]   follow logs (service default: vnext-app)
#
# Environment (all optional):
#   VNEXT_RUNTIME_DIR      where the vnext-runtime template lives   (default: labs/cross-domain/.vnext-runtime)
#   VNEXT_RUNTIME_REPO     template git url                          (default: https://github.com/burgan-tech/vnext-runtime.git)
#   VNEXT_SRC_DIR          vnext runtime source checkout             (default: ../vnext next to vnext-example)
#   VNEXT_LAB_IMAGE_TAG    image tag for core/partner                (default: dapr-nr)
#   VNEXT_DISCOVERY_IMAGE_TAG   image tag for the discovery domain   (default: latest)
#   VNEXT_DISCOVERY_PACKAGE / _VERSION   npm package published into discovery (default: @burgan-tech/vnext-discovery-runtime 0.0.6)
set -euo pipefail

LAB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXAMPLE_ROOT="$(cd "$LAB_DIR/../.." && pwd)"
RUNTIME_DIR="${VNEXT_RUNTIME_DIR:-$LAB_DIR/.vnext-runtime}"
RUNTIME_REPO="${VNEXT_RUNTIME_REPO:-https://github.com/burgan-tech/vnext-runtime.git}"
RUNTIME_REF="${VNEXT_RUNTIME_REF:-main}"          # branch/tag of the template to clone (pin for reproducibility)
VNEXT_SRC="${VNEXT_SRC_DIR:-$(cd "$EXAMPLE_ROOT/.." && pwd)/vnext}"
IMAGE_TAG="${VNEXT_LAB_IMAGE_TAG:-dapr-nr}"
# discovery runs the SAME locally built images by default — the released `latest` predates the Dapr
# work, so mixing it in means one of the three domains is not running the code under test.
DISCOVERY_TAG="${VNEXT_DISCOVERY_IMAGE_TAG:-$IMAGE_TAG}"
# Dapr control plane + sidecars, pinned. `latest` on a developer machine can be months old (a stale
# 1.16.8 was found on 2026-09-03); placement/scheduler/daprd must move together.
DAPR_VERSION="${VNEXT_LAB_DAPR_VERSION:-1.18.0}"
# Runtime discovery provider under test: `dapr` (name resolution + service invocation, the new path)
# or `http` (registry baseUrl + plain HttpClient, the rollback path). Re-pinned on every `up`.
PROVIDER="${VNEXT_LAB_DISCOVERY_PROVIDER:-dapr}"
case "$PROVIDER" in dapr|http) ;; *) echo "VNEXT_LAB_DISCOVERY_PROVIDER must be dapr or http" >&2; exit 1;; esac
DISCOVERY_PKG="${VNEXT_DISCOVERY_PACKAGE:-@burgan-tech/vnext-discovery-runtime}"
DISCOVERY_PKG_VERSION="${VNEXT_DISCOVERY_PACKAGE_VERSION:-0.0.6}"
NETWORK="vnext-development"
OVERLAY_MARKER="# --- cross-domain lab overlay"

# domain -> port offset (create-domain.sh: app 4201+off, init 3005+off, dapr http 42110+off*100)
# (a case, not an associative array: macOS ships bash 3.2)
offset_of() { case "$1" in core) echo 0;; partner) echo 10;; discovery) echo 30;; *) die "unknown domain $1";; esac; }
DOMAINS_APP=(core partner)          # register themselves + run local images
ALL_DOMAINS=(discovery core partner)

# compose services per domain, WITHOUT vnext-component-publisher (it publishes core-runtime, not ours)
SERVICES="vnext-db-migrator vnext-db-migrator-dapr vnext-app vnext-orchestration-dapr vnext-execution-app vnext-execution-dapr vnext-worker-inbox vnext-worker-inbox-dapr vnext-worker-outbox vnext-worker-outbox-dapr vnext-init"

log()  { printf '\033[1;34m[lab]\033[0m %s\n' "$*"; }
ok()   { printf '\033[0;32m[ ok ]\033[0m %s\n' "$*"; }
fail() { printf '\033[0;31m[fail]\033[0m %s\n' "$*" >&2; }
die()  { fail "$*"; exit 1; }

app_port()   { echo $((4201 + $(offset_of "$1"))); }
init_port()  { echo $((3005 + $(offset_of "$1"))); }
dapr_http()  { echo $((42110 + $(offset_of "$1") * 100)); }
docker_dir() { echo "$RUNTIME_DIR/vnext/docker"; }
# Explicit Dapr Configuration (appconfig: mdns name resolution pinned + OTel tracing) layered over the
# template via a compose override — the template itself ships no --config (implicit mDNS, no tracing).
export LAB_DAPR_CONFIG="$LAB_DIR/dapr/appconfig.yaml"
compose()    { local d=$1; shift; (cd "$(docker_dir)" && docker compose -f docker-compose.yml -f "$LAB_DIR/compose.dapr-appconfig.yml" --env-file "domains/$d/.env" -p "vnext-$d" --profile vnext "$@"); }

require() { command -v "$1" >/dev/null 2>&1 || die "missing tool: $1"; }

ensure_runtime() {
  if [ ! -f "$RUNTIME_DIR/Makefile" ]; then
    log "cloning vnext-runtime template into $RUNTIME_DIR"
    git clone --depth 1 --branch "$RUNTIME_REF" "$RUNTIME_REPO" "$RUNTIME_DIR"
  fi
  [ -f "$(docker_dir)/create-domain.sh" ] || die "unexpected template layout under $RUNTIME_DIR"
}

# Pins KEY=VALUE in an env file (replace or append).
pin_env() { local f=$1 k=$2 v=$3
  if grep -q "^$k=" "$f"; then sed -i.bak "s|^$k=.*|$k=$v|" "$f" && rm -f "$f.bak"; else echo "$k=$v" >> "$f"; fi; }

pin_dapr_versions() { local f=$1
  pin_env "$f" DAPR_RUNTIME_VERSION "$DAPR_VERSION"
  pin_env "$f" DAPR_PLACEMENT_VERSION "$DAPR_VERSION"
  pin_env "$f" DAPR_SCHEDULER_VERSION "$DAPR_VERSION"; }

ensure_infra() {
  pin_dapr_versions "$(docker_dir)/.env"
  if docker ps --format '{{.Names}}' | grep -qx 'vnext-postgres'; then
    local running; running=$(docker inspect dapr-placement --format '{{.Config.Image}}' 2>/dev/null || echo none)
    if [[ "$running" == *":$DAPR_VERSION" ]]; then ok "infra already running (dapr $DAPR_VERSION)"; return; fi
    log "infra running with $running — recreating placement/scheduler at $DAPR_VERSION"
  fi
  log "starting shared infra (make up-infra, dapr $DAPR_VERSION)"
  (cd "$RUNTIME_DIR" && make up-infra)
}

# Render domains/<d>/* from the template, pin image tag, append our orchestration overlay.
ensure_domain() {
  local d=$1 tag=$2
  local ddir; ddir="$(docker_dir)/domains/$d"
  if [ ! -f "$ddir/.env" ]; then
    log "creating domain $d (offset $(offset_of "$d"))"
    (cd "$(docker_dir)" && ./create-domain.sh "$d" "$(offset_of "$d")" >/dev/null)
  fi
  # image tags: vnext runtime (locally built) + pinned Dapr
  pin_env "$ddir/.env" VNEXT_VERSION "$tag"
  pin_dapr_versions "$ddir/.env"
  # discovery registration + dapr provider overlay (core/partner only; discovery must not register itself)
  if [[ " ${DOMAINS_APP[*]} " == *" $d "* ]] && ! grep -qF "$OVERLAY_MARKER" "$ddir/.env.orchestration"; then
    log "applying orchestration overlay to $d"
    { echo; echo "$OVERLAY_MARKER (labs/cross-domain/orchestration.overlay.env) ---";
      sed -e "s|{{DOMAIN}}|$d|g" -e "s|{{PROVIDER}}|$PROVIDER|g" "$LAB_DIR/orchestration.overlay.env"; } >> "$ddir/.env.orchestration"
  fi
  # the provider under test may change between runs — always re-pin it
  if [[ " ${DOMAINS_APP[*]} " == *" $d "* ]]; then
    pin_env "$ddir/.env.orchestration" ServiceDiscovery__Provider "$PROVIDER"
  fi
  (cd "$RUNTIME_DIR" && make db-create DOMAIN="$d" >/dev/null) || die "db-create $d failed"
}

start_domain() {
  local d=$1
  log "starting domain $d"
  # shellcheck disable=SC2086
  compose "$d" up -d $SERVICES >/dev/null 2>&1 || { compose "$d" ps; die "compose up failed for $d (check: docker logs vnext-db-migrator-$d)"; }
}

wait_health() {
  local d=$1 url; url="http://localhost:$(app_port "$d")/health"
  for _ in $(seq 1 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "$url")" = 200 ] && { ok "$d healthy ($url)"; return; }
    sleep 3
  done
  die "$d not healthy after 180s ($url); migrator exit: $(docker inspect "vnext-db-migrator-$d" --format '{{.State.ExitCode}}' 2>/dev/null)"
}

publish_discovery_package() {
  local port; port=$(init_port discovery)
  local probe; probe=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$(app_port discovery)/api/v1/discovery/functions/domain-lookup?key=__probe__")
  if [ "$probe" = 404 ] && curl -s "http://localhost:$(app_port discovery)/api/v1/discovery/functions/domain-lookup?key=__probe__" | grep -q domainName; then
    ok "discovery components already published"; return
  fi
  log "publishing $DISCOVERY_PKG@$DISCOVERY_PKG_VERSION into discovery via init :$port"
  local job; job=$(curl -s -X POST "http://localhost:$port/api/package/publish" -H 'Content-Type: application/json' \
      -d "{\"packageName\":\"$DISCOVERY_PKG\",\"version\":\"$DISCOVERY_PKG_VERSION\",\"reInitialize\":true}" \
      | python3 -c 'import sys,json; print(json.load(sys.stdin).get("jobId",""))')
  [ -n "$job" ] || die "publish job was not accepted"
  for _ in $(seq 1 60); do
    local st; st=$(curl -s "http://localhost:$port/api/package/publish/status/$job" | python3 -c 'import sys,json; print(json.load(sys.stdin).get("status",""))')
    case "$st" in completed) ok "discovery package published"; return;; failed|error) die "discovery publish failed (job $job)";; esac
    sleep 3
  done
  die "discovery publish did not finish (job $job)"
}

cmd_images() {
  require docker
  [ -f "$VNEXT_SRC/BBT.Workflow.sln" ] || [ -d "$VNEXT_SRC/src" ] || die "vnext source not found at $VNEXT_SRC (set VNEXT_SRC_DIR)"
  local pairs=(
    "init:init/VNext.Init.Host/Dockerfile"
    "orchestrator:orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Dockerfile"
    "execution:execution/BBT.Workflow.Execution.HttpApi.Host/Dockerfile"
    "inbox:workers/BBT.Workflow.Workers.Inbox/Dockerfile"
    "outbox:workers/BBT.Workflow.Workers.Outbox/Dockerfile"
    "db-migrator:workers/BBT.Workflow.DbMigrator/Dockerfile")
  for p in "${pairs[@]}"; do
    local name="${p%%:*}" df="${p#*:}"
    log "building ghcr.io/burgan-tech/vnext/$name:$IMAGE_TAG"
    (cd "$VNEXT_SRC" && docker build -q -f "$df" -t "ghcr.io/burgan-tech/vnext/$name:$IMAGE_TAG" .)
  done
  # Dapr images: refresh the pinned version explicitly (a cached `latest` is not trustworthy)
  for img in "daprio/daprd:$DAPR_VERSION" "daprio/dapr:$DAPR_VERSION" "daprio/scheduler:$DAPR_VERSION"; do
    log "pulling $img"; docker pull -q "$img" >/dev/null || die "cannot pull $img"
  done
  ok "images ready (vnext tag $IMAGE_TAG, dapr $DAPR_VERSION)"
}

cmd_up() {
  require docker; require make; require curl; require python3; require git
  ensure_runtime
  docker network inspect "$NETWORK" >/dev/null 2>&1 || docker network create "$NETWORK" >/dev/null
  ensure_infra
  for d in "${ALL_DOMAINS[@]}"; do
    [ "$d" = discovery ] && ensure_domain "$d" "$DISCOVERY_TAG" || ensure_domain "$d" "$IMAGE_TAG"
  done
  for name in orchestrator execution inbox outbox db-migrator init; do
    docker image inspect "ghcr.io/burgan-tech/vnext/$name:$IMAGE_TAG" >/dev/null 2>&1 || die "image $name:$IMAGE_TAG missing — run: $0 images"
  done
  start_domain discovery; wait_health discovery; publish_discovery_package
  for d in "${DOMAINS_APP[@]}"; do start_domain "$d"; done
  for d in "${DOMAINS_APP[@]}"; do wait_health "$d"; done
  cmd_verify
}

cmd_verify() {
  local rc=0
  for d in "${ALL_DOMAINS[@]}"; do
    local code; code=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$(app_port "$d")/health")
    [ "$code" = 200 ] && ok "$d /health 200" || { fail "$d /health $code"; rc=1; }
  done
  for d in "${DOMAINS_APP[@]}"; do
    local body; body=$(curl -s "http://localhost:$(app_port discovery)/api/v1/discovery/functions/domain-lookup?key=$d")
    if echo "$body" | grep -q "\"appId\":\"vnext-app-$d\""; then ok "$d registered in discovery (appId vnext-app-$d)"
    else fail "$d NOT registered: $body"; rc=1; fi
  done
  # sidecar-level proof: core's daprd resolves partner's app-id and reaches partner's orchestrator
  local inv; inv=$(docker exec vnext-app-core sh -c "wget -qO- -S http://localhost:$(dapr_http core)/v1.0/invoke/vnext-app-partner/method/health 2>&1 | head -1" 2>/dev/null || true)
  if echo "$inv" | grep -q '200'; then ok "core sidecar -> vnext-app-partner /health: $inv"
  else fail "core sidecar could not invoke vnext-app-partner: ${inv:-no response}"; rc=1; fi
  local prov; prov=$(docker exec vnext-app-core sh -c 'echo $ServiceDiscovery__Provider' 2>/dev/null || echo "?")
  [ $rc -eq 0 ] && ok "lab ready: core :$(app_port core)  partner :$(app_port partner)  discovery :$(app_port discovery)  (ServiceDiscovery provider: ${prov:-?})"
  return $rc
}

cmd_status() {
  docker ps -a --format '{{.Names}}\t{{.Status}}' | grep -E 'vnext-|dapr-' | sort
  echo; cmd_verify || true
}

cmd_down() {
  ensure_runtime
  for d in "${ALL_DOMAINS[@]}"; do
    [ -f "$(docker_dir)/domains/$d/.env" ] && { log "stopping $d"; compose "$d" down >/dev/null 2>&1 || true; }
  done
  if [ "${1:-}" = "--all" ]; then log "stopping shared infra"; (cd "$RUNTIME_DIR" && make down) || true; fi
  ok "down"
}

cmd_logs() {
  local d=${1:?domain}; local svc=${2:-vnext-app}
  compose "$d" logs -f "$svc"
}

case "${1:-}" in
  images) cmd_images;;
  up) cmd_up;;
  down) shift; cmd_down "$@";;
  status) cmd_status;;
  verify) cmd_verify;;
  logs) shift; cmd_logs "$@";;
  *) sed -n '2,25p' "$0"; exit 1;;
esac
