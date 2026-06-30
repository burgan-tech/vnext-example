#!/usr/bin/env bash
# ============================================================
# provision.sh — Metabase dashboards for the vnext-example domain
#
# DOMAIN-OWNED CONFIG. This script lives in the vnext-example repo because the
# dashboards, schema selection, and reporting fields are specific to THIS
# domain's "account-opening" flow. The vNext runtime only provides the Metabase
# container (opt-in); each domain provisions its own dashboards against it.
#
# What this script does:
#   1. Creates a read-only PostgreSQL user for Metabase
#   2. Waits for Metabase to become healthy
#   3. Runs first-time Metabase setup (skipped if already done)
#   4. Adds/updates the domain PostgreSQL database connection
#   5. Waits for schema sync to complete
#   6. Creates 10 questions (SQL cards) for account-opening
#   7. Creates the "Account Opening — Business Overview" dashboard
#   8. Lays the cards out on the dashboard
#
# Prerequisites:
#   - The vNext runtime stack is up with the Metabase profile enabled:
#       (in the vnext repo)  cd etc/docker && docker compose --profile metabase up -d
#   - This domain's instance data exists in $POSTGRES_DB / $PG_SCHEMA.
#
# Usage:
#   cd vnext-example
#   ./etc/docker/config/metabase/provision.sh
#
# All settings are env-overridable (see the block below) so the same script
# works against any domain DB/schema or a different Metabase host.
# ============================================================

set -euo pipefail

METABASE_URL="${METABASE_URL:-http://localhost:3030}"
# Name of the running Postgres container to exec into for the read-only-user step.
PG_CONTAINER="${PG_CONTAINER:-vnext-postgres}"
# Docker-network hostname Metabase uses to reach Postgres (the compose service name).
PG_DOCKER_HOST="${PG_DOCKER_HOST:-vnext-postgres}"
POSTGRES_HOST="${POSTGRES_HOST:-localhost}"
POSTGRES_PORT="${POSTGRES_PORT:-5432}"
# This domain's runtime database.
POSTGRES_DB="${POSTGRES_DB:-vNext_Vnext_example}"
POSTGRES_USER="${POSTGRES_USER:-postgres}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-postgres}"
# Per-flow schema in the vNext runtime DB. The account-opening flow lives in
# schema "account_opening" (workflow name, hyphens→underscores). A domain that
# reports across several flows would run this script once per schema (or extend it).
PG_SCHEMA="${PG_SCHEMA:-account_opening}"
# Terminal state key for a successful account opening (used by completion-rate cards).
SUCCESS_STATE="${SUCCESS_STATE:-account-opening-success}"
# --- Namespacing (lets one shared Metabase host many domains/workflows safely) ---
# CONN_NAME: display name of THIS domain's Metabase database connection. Keep it
#   stable per domain (one connection per domain DB), distinct across domains.
CONN_NAME="${CONN_NAME:-vnext-example}"
# FLOW_KEY: workflow this dashboard covers. Prefixes every card name and the
#   dashboard so multiple workflows never overwrite each other. Run once per flow.
FLOW_KEY="${FLOW_KEY:-account-opening}"
# DASHBOARD_NAME: auto-namespaced by FLOW_KEY; override for a prettier title.
DASHBOARD_NAME="${DASHBOARD_NAME:-${FLOW_KEY} — Business Overview}"
METABASE_ADMIN_EMAIL="${METABASE_ADMIN_EMAIL:-admin@vnext.local}"
METABASE_ADMIN_PASSWORD="${METABASE_ADMIN_PASSWORD:-Admin123!}"
METABASE_RO_USER="${METABASE_RO_USER:-metabase_ro}"
METABASE_RO_PASSWORD="${METABASE_RO_PASSWORD:-metabase_ro_pass}"

# ----------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------
# Logs go to stderr so command substitution (e.g. C1=$(create_card ...)) captures
# only the function's real return value, never the progress lines.
log()  { echo "[$(date '+%H:%M:%S')] $*" >&2; }
fail() { echo "[ERROR] $*" >&2; exit 1; }

require_tool() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required but not installed."; }
require_tool curl
require_tool jq

# ----------------------------------------------------------------
# Step 1: Create read-only PostgreSQL user
# ----------------------------------------------------------------
log "Creating read-only Metabase PostgreSQL user..."
docker exec "$PG_CONTAINER" psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '${METABASE_RO_USER}') THEN
    CREATE USER ${METABASE_RO_USER} WITH PASSWORD '${METABASE_RO_PASSWORD}';
    GRANT CONNECT ON DATABASE \"${POSTGRES_DB}\" TO ${METABASE_RO_USER};
  END IF;
END
\$\$;
-- Grants are re-applied every run (outside the user-exists guard) so an existing
-- RO user also gains access when the target schema changes.
GRANT USAGE ON SCHEMA \"${PG_SCHEMA}\" TO ${METABASE_RO_USER};
GRANT SELECT ON ALL TABLES IN SCHEMA \"${PG_SCHEMA}\" TO ${METABASE_RO_USER};
ALTER DEFAULT PRIVILEGES IN SCHEMA \"${PG_SCHEMA}\" GRANT SELECT ON TABLES TO ${METABASE_RO_USER};
" 2>/dev/null && log "Read-only user ready (schema: ${PG_SCHEMA})." || log "Skipped (user/grants may already exist)."

# ----------------------------------------------------------------
# Step 2: Wait for Metabase to be healthy (up to 5 minutes)
# ----------------------------------------------------------------
log "Waiting for Metabase at $METABASE_URL ..."
ATTEMPTS=0
MAX_ATTEMPTS=30
until curl -sf "$METABASE_URL/api/health" 2>/dev/null | grep -q '"status":"ok"'; do
  ATTEMPTS=$((ATTEMPTS + 1))
  [ "$ATTEMPTS" -ge "$MAX_ATTEMPTS" ] && fail "Metabase did not become healthy within 5 minutes."
  log "  ... not ready yet (attempt $ATTEMPTS/$MAX_ATTEMPTS), retrying in 10s"
  sleep 10
done
log "Metabase is healthy."

# ----------------------------------------------------------------
# Step 3: First-time setup (skipped if already configured)
# ----------------------------------------------------------------
# Gate on has-user-setup, NOT setup-token: some Metabase versions keep the
# setup-token populated even after the admin account exists, so keying off the
# token alone would re-attempt setup and fail (409) on every re-run.
PROPS=$(curl -sf "$METABASE_URL/api/session/properties")
SETUP_DONE=$(echo "$PROPS" | jq -r '.["has-user-setup"] // false')
SETUP_TOKEN=$(echo "$PROPS" | jq -r '.["setup-token"] // empty')

if [ "$SETUP_DONE" != "true" ] && [ -n "$SETUP_TOKEN" ]; then
  log "Running first-time Metabase setup..."
  curl -sf -X POST "$METABASE_URL/api/setup" \
    -H "Content-Type: application/json" \
    -d "$(jq -n \
      --arg token "$SETUP_TOKEN" \
      --arg email "$METABASE_ADMIN_EMAIL" \
      --arg password "$METABASE_ADMIN_PASSWORD" \
      '{
        token: $token,
        prefs: {site_name: "vNext Analytics", site_locale: "en"},
        database: null,
        user: {
          first_name: "Admin",
          last_name: "vNext",
          email: $email,
          password: $password,
          site_name: "vNext Analytics"
        }
      }')" > /dev/null
  log "First-time setup complete."
else
  log "Metabase already configured, skipping setup."
fi

# ----------------------------------------------------------------
# Step 4: Authenticate
# ----------------------------------------------------------------
log "Authenticating..."
SESSION_TOKEN=$(curl -sf -X POST "$METABASE_URL/api/session" \
  -H "Content-Type: application/json" \
  -d "$(jq -n --arg email "$METABASE_ADMIN_EMAIL" --arg password "$METABASE_ADMIN_PASSWORD" \
    '{username: $email, password: $password}')" \
  | jq -r '.id')
[ -z "$SESSION_TOKEN" ] && fail "Authentication failed."
log "Authenticated (session: ${SESSION_TOKEN:0:8}...)."

MB_HEADER="X-Metabase-Session: $SESSION_TOKEN"

# ----------------------------------------------------------------
# Step 5: Add PostgreSQL database connection (idempotent)
# ----------------------------------------------------------------
EXISTING_DB_ID=$(curl -sf -H "$MB_HEADER" "$METABASE_URL/api/database" \
  | jq -r --arg n "$CONN_NAME" '.data[] | select(.name == $n) | .id // empty' 2>/dev/null | head -1)

# Connection body — Metabase reaches postgres by its Docker service name.
DB_BODY=$(jq -n \
  --arg name "$CONN_NAME" \
  --arg pg_host "$PG_DOCKER_HOST" \
  --argjson pg_port 5432 \
  --arg pg_db "$POSTGRES_DB" \
  --arg pg_user "$METABASE_RO_USER" \
  --arg pg_pass "$METABASE_RO_PASSWORD" \
  '{
    name: $name,
    engine: "postgres",
    details: {
      host: $pg_host,
      port: $pg_port,
      dbname: $pg_db,
      user: $pg_user,
      password: $pg_pass,
      ssl: false,
      "tunnel-enabled": false,
      "schema-filters-type": "all"
    }
  }')

if [ -z "$EXISTING_DB_ID" ]; then
  log "Adding vNext PostgreSQL database connection (dbname: $POSTGRES_DB)..."
  DB_ID=$(curl -sf -X POST "$METABASE_URL/api/database" \
    -H "Content-Type: application/json" -H "$MB_HEADER" -d "$DB_BODY" | jq -r '.id')
  log "Database connection created (id: $DB_ID)."
else
  DB_ID="$EXISTING_DB_ID"
  # Update in place so a re-run always repoints to the current dbname/credentials.
  curl -sf -X PUT "$METABASE_URL/api/database/$DB_ID" \
    -H "Content-Type: application/json" -H "$MB_HEADER" -d "$DB_BODY" > /dev/null
  log "Database connection updated (id: $DB_ID, dbname: $POSTGRES_DB)."
fi

# ----------------------------------------------------------------
# Step 6: Trigger schema sync and wait for completion
# ----------------------------------------------------------------
log "Triggering database schema sync..."
curl -sf -X POST -H "$MB_HEADER" "$METABASE_URL/api/database/$DB_ID/sync" > /dev/null || true
log "Waiting 30s for sync to complete..."
sleep 30

# ----------------------------------------------------------------
# Step 7: Helper — create or reuse a card (idempotent by name)
# ----------------------------------------------------------------
create_card() {
  local name="$1" display="$2" query="$3" viz_settings="$4"

  # Namespace the card by workflow so multiple flows never collide by name.
  name="[$FLOW_KEY] $name"

  # Resolve SQL tokens to the runtime schema / success-state for this environment.
  query="${query//__SCHEMA__/$PG_SCHEMA}"
  query="${query//__SUCCESS__/$SUCCESS_STATE}"

  local body
  body=$(jq -n \
    --arg name "$name" \
    --arg display "$display" \
    --arg query "$query" \
    --argjson db_id "$DB_ID" \
    --argjson viz "$viz_settings" \
    '{
      name: $name,
      display: $display,
      visualization_settings: $viz,
      dataset_query: {
        type: "native",
        native: {query: $query, "template-tags": {}},
        database: $db_id
      }
    }')

  # Idempotent by name: update the existing card (refreshes SQL) or create a new one.
  local existing_id card_id
  existing_id=$(curl -sf -H "$MB_HEADER" "$METABASE_URL/api/card" \
    | jq -r --arg n "$name" '.[] | select(.name == $n) | .id // empty' 2>/dev/null | head -1)

  if [ -n "$existing_id" ]; then
    card_id=$(curl -sf -X PUT "$METABASE_URL/api/card/$existing_id" \
      -H "Content-Type: application/json" -H "$MB_HEADER" -d "$body" | jq -r '.id')
    log "  Updated card '$name' (id: $card_id)."
  else
    card_id=$(curl -sf -X POST "$METABASE_URL/api/card" \
      -H "Content-Type: application/json" -H "$MB_HEADER" -d "$body" | jq -r '.id')
    log "  Created card '$name' (id: $card_id)."
  fi
  echo "$card_id"
}

# ----------------------------------------------------------------
# Step 8: Create the 10 questions
# ----------------------------------------------------------------
log "Creating questions (cards)..."

# Q1 — Total Applications by Status (pie)
C1=$(create_card \
  "Total Applications by Status" \
  "pie" \
  'SELECT
  CASE "Status"
    WHEN '"'"'A'"'"' THEN '"'"'Active'"'"'
    WHEN '"'"'B'"'"' THEN '"'"'Busy'"'"'
    WHEN '"'"'C'"'"' THEN '"'"'Completed'"'"'
    WHEN '"'"'F'"'"' THEN '"'"'Faulted'"'"'
    WHEN '"'"'P'"'"' THEN '"'"'Passive'"'"'
  END AS status_label,
  COUNT(*) AS count
FROM __SCHEMA__."Instances"
WHERE "Flow" = '"'"'account-opening'"'"'
GROUP BY 1
ORDER BY 2 DESC' \
  '{}')

# Q2 — Daily New Applications — last 90 days (line)
C2=$(create_card \
  "Daily New Applications (90d)" \
  "line" \
  'SELECT
  DATE_TRUNC('"'"'day'"'"', "CreatedAt") AS date,
  COUNT(*) AS new_applications
FROM __SCHEMA__."Instances"
WHERE "Flow" = '"'"'account-opening'"'"'
  AND "CreatedAt" >= NOW() - INTERVAL '"'"'90 days'"'"'
GROUP BY 1
ORDER BY 1' \
  '{"graph.x_axis.title_text":"Date","graph.y_axis.title_text":"New Applications"}')

# Q3 — Completion Rate % (scalar)
C3=$(create_card \
  "Completion Rate %" \
  "scalar" \
  'SELECT
  ROUND(
    100.0 * SUM(CASE WHEN "Status" = '"'"'C'"'"' AND "CurrentState" = '"'"'__SUCCESS__'"'"' THEN 1 ELSE 0 END)
    / NULLIF(COUNT(*), 0),
    1
  ) AS completion_rate_pct
FROM __SCHEMA__."Instances"
WHERE "Flow" = '"'"'account-opening'"'"'' \
  '{}')

# Q4 — Average Completion Duration (scalar, minutes)
C4=$(create_card \
  "Avg Completion Duration (min)" \
  "scalar" \
  'SELECT
  ROUND(
    AVG(EXTRACT(EPOCH FROM "Duration") / 60.0),
    1
  ) AS avg_duration_minutes
FROM __SCHEMA__."Instances"
WHERE "Flow" = '"'"'account-opening'"'"'
  AND "Status" = '"'"'C'"'"'
  AND "Duration" IS NOT NULL' \
  '{}')

# Q5 — Account Type Distribution for Completed Instances (pie)
C5=$(create_card \
  "Account Type Distribution (Completed)" \
  "pie" \
  'SELECT
  d."Data" ->> '"'"'accountType'"'"' AS account_type,
  COUNT(*) AS completions
FROM __SCHEMA__."InstancesData" d
JOIN __SCHEMA__."Instances" i ON i."Id" = d."InstanceId"
WHERE i."Flow" = '"'"'account-opening'"'"'
  AND i."Status" = '"'"'C'"'"'
  AND i."CurrentState" = '"'"'__SUCCESS__'"'"'
  AND d."IsLatest" = true
  AND (d."Data" ->> '"'"'accountType'"'"') IS NOT NULL
GROUP BY 1
ORDER BY 2 DESC' \
  '{}')

# Q6 — Currency Breakdown (pie)
C6=$(create_card \
  "Currency Breakdown" \
  "pie" \
  'SELECT
  d."Data" ->> '"'"'currency'"'"' AS currency,
  COUNT(*) AS count
FROM __SCHEMA__."InstancesData" d
JOIN __SCHEMA__."Instances" i ON i."Id" = d."InstanceId"
WHERE i."Flow" = '"'"'account-opening'"'"'
  AND d."IsLatest" = true
  AND (d."Data" ->> '"'"'currency'"'"') IS NOT NULL
GROUP BY 1
ORDER BY 2 DESC' \
  '{}')

# Q7 — Live Funnel: Active Instances by Current State (bar)
C7=$(create_card \
  "Live Funnel: Active Instances by State" \
  "bar" \
  'SELECT
  "CurrentState" AS state,
  COUNT(*) AS instance_count
FROM __SCHEMA__."Instances"
WHERE "Flow" = '"'"'account-opening'"'"'
  AND "Status" = '"'"'A'"'"'
GROUP BY 1
ORDER BY 2 DESC' \
  '{"graph.x_axis.title_text":"State","graph.y_axis.title_text":"Active Instances"}')

# Q8 — Completion Duration P50/P95 by Account Type (bar)
C8=$(create_card \
  "Duration P50 / P95 by Account Type (min)" \
  "bar" \
  'SELECT
  d."Data" ->> '"'"'accountType'"'"' AS account_type,
  ROUND(PERCENTILE_CONT(0.5)  WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM i."Duration") / 60.0)::numeric, 1) AS p50_min,
  ROUND(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM i."Duration") / 60.0)::numeric, 1) AS p95_min
FROM __SCHEMA__."Instances" i
JOIN __SCHEMA__."InstancesData" d ON d."InstanceId" = i."Id" AND d."IsLatest" = true
WHERE i."Flow" = '"'"'account-opening'"'"'
  AND i."Status" = '"'"'C'"'"'
  AND i."Duration" IS NOT NULL
  AND (d."Data" ->> '"'"'accountType'"'"') IS NOT NULL
GROUP BY 1
ORDER BY 2 DESC' \
  '{}')

# Q9 — Branch Completion Rate (table)
C9=$(create_card \
  "Branch Completion Rate" \
  "table" \
  'SELECT
  d."Data" ->> '"'"'branchCode'"'"' AS branch,
  COUNT(*) AS total_applications,
  SUM(CASE WHEN i."Status" = '"'"'C'"'"' AND i."CurrentState" = '"'"'__SUCCESS__'"'"' THEN 1 ELSE 0 END) AS successful,
  ROUND(
    100.0 * SUM(CASE WHEN i."Status" = '"'"'C'"'"' AND i."CurrentState" = '"'"'__SUCCESS__'"'"' THEN 1 ELSE 0 END)
    / NULLIF(COUNT(*), 0),
    1
  ) AS success_rate_pct
FROM __SCHEMA__."Instances" i
JOIN __SCHEMA__."InstancesData" d ON d."InstanceId" = i."Id" AND d."IsLatest" = true
WHERE i."Flow" = '"'"'account-opening'"'"'
  AND (d."Data" ->> '"'"'branchCode'"'"') IS NOT NULL
GROUP BY 1
ORDER BY 2 DESC' \
  '{}')

# Q10 — Daily Failures: Faulted / Timed-out / Cancelled (line)
C10=$(create_card \
  "Daily Failure Trend (90d)" \
  "line" \
  'SELECT
  DATE_TRUNC('"'"'day'"'"', "CreatedAt") AS date,
  COUNT(*) FILTER (WHERE "Status" = '"'"'F'"'"')                                      AS faulted,
  COUNT(*) FILTER (WHERE "CurrentState" = '"'"'timeouted'"'"')                        AS timed_out,
  COUNT(*) FILTER (WHERE "Status" = '"'"'C'"'"' AND "CurrentState" = '"'"'cancelled'"'"') AS cancelled
FROM __SCHEMA__."Instances"
WHERE "Flow" = '"'"'account-opening'"'"'
  AND "CreatedAt" >= NOW() - INTERVAL '"'"'90 days'"'"'
GROUP BY 1
ORDER BY 1' \
  '{"graph.x_axis.title_text":"Date","graph.y_axis.title_text":"Count"}')

log "All 10 cards ready: C1=$C1 C2=$C2 C3=$C3 C4=$C4 C5=$C5 C6=$C6 C7=$C7 C8=$C8 C9=$C9 C10=$C10"

# ----------------------------------------------------------------
# Step 9: Create dashboard (idempotent by name)
# ----------------------------------------------------------------
log "Creating dashboard..."
EXISTING_DASH=$(curl -sf -H "$MB_HEADER" "$METABASE_URL/api/dashboard" \
  | jq -r --arg n "$DASHBOARD_NAME" '.[] | select(.name == $n) | .id // empty' 2>/dev/null | head -1)

if [ -n "$EXISTING_DASH" ]; then
  DASHBOARD_ID="$EXISTING_DASH"
  log "Dashboard '$DASHBOARD_NAME' already exists (id: $DASHBOARD_ID). Re-applying card layout (idempotent PUT)."
else
  DASHBOARD_ID=$(curl -sf -X POST "$METABASE_URL/api/dashboard" \
    -H "Content-Type: application/json" \
    -H "$MB_HEADER" \
    -d "$(jq -n --arg name "$DASHBOARD_NAME" --arg flow "$FLOW_KEY" \
      '{name: $name, description: ("Business visibility dashboard for the " + $flow + " workflow. Data source: InstancesData JSONB.")}')" \
    | jq -r '.id')
  log "Dashboard created (id: $DASHBOARD_ID)."
fi

# ----------------------------------------------------------------
# Step 10: Lay the cards out on the dashboard.
#
# Modern Metabase (v0.48+) attaches cards via a single
#   PUT /api/dashboard/:id   { "dashcards": [ ... ] }
# call. The legacy per-card  POST /api/dashboard/:id/cards  endpoint
# no longer persists placements on current versions, so we build the
# full dashcards array in one PUT. New placements use negative ids.
#
# Grid: 24 columns wide. Row heights in ~150px units.
#
# Layout (each of the 10 cards placed exactly once):
#   Row 0  h=4  | C1 Status Pie (8) | C3 Completion % (8) | C4 Avg Duration (8)
#   Row 4  h=6  | C2 Daily Applications line (24)
#   Row 10 h=6  | C5 Account Type pie (12) | C6 Currency pie (12)
#   Row 16 h=6  | C7 Live Funnel bar (24)
#   Row 22 h=6  | C8 Duration P50/P95 bar (12) | C9 Branch table (12)
#   Row 28 h=6  | C10 Daily Failure line (24)
# ----------------------------------------------------------------
log "Laying out cards on dashboard..."

DASHCARDS=$(jq -n \
  --argjson c1 "$C1" --argjson c2 "$C2" --argjson c3 "$C3" --argjson c4 "$C4" --argjson c5 "$C5" \
  --argjson c6 "$C6" --argjson c7 "$C7" --argjson c8 "$C8" --argjson c9 "$C9" --argjson c10 "$C10" '
  def dc(id; cid; row; col; sx; sy):
    {id: id, card_id: cid, dashboard_tab_id: null, row: row, col: col,
     size_x: sx, size_y: sy, series: [], parameter_mappings: [], visualization_settings: {}};
  { dashcards: [
      dc(-1;  $c1;  0;  0; 8; 4),
      dc(-2;  $c3;  0;  8; 8; 4),
      dc(-3;  $c4;  0; 16; 8; 4),
      dc(-4;  $c2;  4;  0; 24; 6),
      dc(-5;  $c5; 10;  0; 12; 6),
      dc(-6;  $c6; 10; 12; 12; 6),
      dc(-7;  $c7; 16;  0; 24; 6),
      dc(-8;  $c8; 22;  0; 12; 6),
      dc(-9;  $c9; 22; 12; 12; 6),
      dc(-10; $c10; 28; 0; 24; 6)
  ]}')

ATTACHED=$(curl -sf -X PUT "$METABASE_URL/api/dashboard/$DASHBOARD_ID" \
  -H "Content-Type: application/json" \
  -H "$MB_HEADER" \
  -d "$DASHCARDS" | jq -r '.dashcards | length')
log "Attached $ATTACHED cards to dashboard."

log ""
log "=========================================================="
log " Metabase provisioning complete!"
log " Dashboard: $METABASE_URL/dashboard/$DASHBOARD_ID"
log " Admin:     $METABASE_ADMIN_EMAIL / $METABASE_ADMIN_PASSWORD"
log "=========================================================="
