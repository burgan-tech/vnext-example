# Metabase Dashboards — vnext-example (account-opening)

These are **this domain's** Metabase dashboards. They live here, in the
vnext-example repo, because the schema selection, reporting fields, cards, and
dashboard layout are specific to the `account-opening` flow. The vNext runtime
only provides the Metabase *engine* (see `README-Metabase.md` in the vnext repo);
each domain owns its own dashboards.

## Files

| File | Purpose |
|---|---|
| `provision.sh` | Idempotent: creates a read-only DB user, registers this domain's DB/schema connection in Metabase, and builds 10 cards + the "Account Opening — Business Overview" dashboard. |
| `seed.sql` | Optional synthetic seed (500 instances / 90 days) for a fresh environment with no real runtime data. Real workflow runs populate the same tables. |

## Prerequisites

1. The vNext runtime is running and the (opt-in) Metabase engine is up. Using the
   `vnext-runtime` dev tool:
   ```bash
   make up-infra                     # shared infrastructure (postgres, …)
   make up-metabase                  # opt-in shared Metabase engine
   make up-vnext DOMAIN=vnext-example
   ```
   (In production each domain runs its own Metabase; only the connection target differs.)
2. This domain's instance data exists in `vNext_Vnext_example` /
   schema `account_opening` — either from real workflow runs or from `seed.sql`.

## Usage

```bash
cd vnext-example

# (optional) seed synthetic data if the schema is empty
docker exec -i vnext-postgres psql -U postgres -d vNext_Vnext_example \
  < etc/docker/config/metabase/seed.sql

# build the dashboards
./etc/docker/config/metabase/provision.sh
```

Then open **http://localhost:3030** → dashboard **"Account Opening — Business Overview"**.
Default admin: `admin@vnext.local` / `Admin123!` (override via env).

## Configuration (env-overridable)

| Var | Default | Notes |
|---|---|---|
| `POSTGRES_DB` | `vNext_Vnext_example` | This domain's runtime database |
| `PG_SCHEMA` | `account_opening` | Per-flow schema (hyphen→underscore). Run once per schema for multi-flow reporting. |
| `SUCCESS_STATE` | `account-opening-success` | Terminal success state for completion-rate cards |
| `CONN_NAME` | `vnext-example` | **Metabase DB-connection name.** One per domain — keep stable per domain, distinct across domains so a shared Metabase doesn't collide. |
| `FLOW_KEY` | `account-opening` | **Workflow namespace.** Prefixes every card (`[<flow>] …`) and the dashboard, so multiple workflows never overwrite each other. |
| `DASHBOARD_NAME` | `<FLOW_KEY> — Business Overview` | Auto-namespaced by `FLOW_KEY`; override for a prettier title. |
| `METABASE_URL` | `http://localhost:3030` | Metabase host |
| `PG_CONTAINER` / `PG_DOCKER_HOST` | `vnext-postgres` | Postgres container name / network host |
| `METABASE_ADMIN_EMAIL` / `_PASSWORD` | `admin@vnext.local` / `Admin123!` | Metabase admin |
| `METABASE_RO_USER` / `_PASSWORD` | `metabase_ro` / `metabase_ro_pass` | Read-only Postgres role created for Metabase |

The card SQL uses `__SCHEMA__` / `__SUCCESS__` placeholders that `provision.sh`
substitutes from `PG_SCHEMA` / `SUCCESS_STATE`, so the same script retargets to
any schema or domain DB without editing the queries.

## Multiple workflows in one domain

One DB connection covers all of a domain's schemas. Run the script **once per
workflow**, varying `PG_SCHEMA` / `SUCCESS_STATE` / `FLOW_KEY` — `CONN_NAME` stays
the same (one connection per domain). Each run produces its own namespaced
dashboard; nothing overwrites:

```bash
# one connection (CONN_NAME=vnext-example), one dashboard per workflow
PG_SCHEMA=account_opening  SUCCESS_STATE=account-opening-success FLOW_KEY=account-opening \
  ./etc/docker/config/metabase/provision.sh
PG_SCHEMA=loan_disbursement SUCCESS_STATE=<state> FLOW_KEY=loan-disbursement \
  ./etc/docker/config/metabase/provision.sh
# … repeat per workflow
```

## Another domain (copy-me)

Copy this `metabase/` folder into the new domain repo and run with that domain's
values — only `POSTGRES_DB`, `CONN_NAME`, `PG_SCHEMA`, `SUCCESS_STATE`, `FLOW_KEY`
change, plus the payload-specific card SQL (the structural cards are reusable):

```bash
POSTGRES_DB=vNext_Morph_touch CONN_NAME=morph-touch \
PG_SCHEMA=<flow_schema> SUCCESS_STATE=<state> FLOW_KEY=<flow> \
  ./etc/docker/config/metabase/provision.sh
```

## Dashboard cards

Status distribution · daily applications (90d) · completion rate · avg completion
duration · account-type distribution · currency breakdown · live funnel by state ·
duration P50/P95 by type · branch completion rate · daily failure trend.

## Data model

Queries read `<schema>."Instances"` and `<schema>."InstancesData"` (JSONB `Data`),
joined on `InstanceId`, filtered to `IsLatest = true`. Key JSONB paths:
`accountType`, `currency`, `branchCode`, `initialDeposit`,
`accountCreation.accountNumber`, `userSession.userId`. Success is
`Status = 'C' AND CurrentState = 'account-opening-success'`.

## Production

Each domain runs its own vNext instance + Metabase on its own cluster, so this
provisioning runs per-domain against that domain's own engine and database.
