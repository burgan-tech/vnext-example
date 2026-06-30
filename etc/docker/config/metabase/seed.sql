-- ============================================================
-- Seed: account-opening workflow instances (500 rows / 90 days)
-- Purpose: Metabase dashboard demo and testing
--
-- TARGET (verified 2026-06-29 against the running vnext-runtime stack):
--   Database : vNext_Vnext_example   (per-domain DB, domain = vnext-example)
--   Schema   : account_opening       (per-flow schema; hyphen -> underscore)
--   Container: vnext-postgres         (owned by the vnext-runtime compose)
--
-- Usage:
--   docker exec -i vnext-postgres \
--     psql -U postgres -d vNext_Vnext_example < seed.sql
--
-- Re-seed (wipe first):
--   docker exec -i vnext-postgres psql -U postgres -d vNext_Vnext_example -c \
--     'DELETE FROM account_opening."Instances" WHERE "Flow" = '\''account-opening'\'';'
--
-- NOTES
--  * VersionNo / IsLatest are managed by the trg_instancesdata_set_version_and_latest
--    BEFORE-INSERT trigger (present in this DB). One data row per instance => the
--    trigger sets VersionNo=1, IsLatest=true. Values below are advisory only.
--  * LastTouchedAt and HasActiveIncident are GENERATED ALWAYS — never inserted.
--  * Faulted rows carry an open incident (isResolved=false) so HasActiveIncident=true.
--  * States/data fields match the real account-opening workflow definition.
-- ============================================================

\echo 'Seeding account-opening workflow data into vNext_Vnext_example.account_opening (500 instances / 90 days)...'

BEGIN;

SET search_path TO account_opening;

-- Safety guard: abort if already seeded to prevent duplicate data
DO $$
BEGIN
  IF (SELECT COUNT(*) FROM account_opening."Instances" WHERE "Flow" = 'account-opening') >= 100 THEN
    RAISE EXCEPTION 'account-opening seed data already present (>=100 rows). '
      'Wipe first: DELETE FROM account_opening."Instances" WHERE "Flow" = ''account-opening''; '
      'Then re-run this script.';
  END IF;
END $$;

-- ----------------------------------------------------------------
-- Step 1: Generate parametric rows (500 instances, 90-day spread)
-- MATERIALIZED ensures gen_random_uuid() is evaluated once per row.
-- ----------------------------------------------------------------
WITH params AS MATERIALIZED (
  SELECT
    gen_random_uuid()                                            AS inst_id,
    s.i                                                          AS seq,
    -- Spread evenly: 500 instances across 90 days (one every ~4.32 h)
    NOW() - (s.i::float * 4.32 * INTERVAL '1 hour')             AS created_at,
    -- Outcome bucket (s.i % 20):
    --   0..11 (60%) success | 12..14 (15%) active | 15..16 (10%) faulted
    --   17 (5%) cancelled   | 18 (5%) timed-out   | 19 (5%) passive
    (s.i % 20)                                                  AS bucket,
    -- Account type: 50% demand-deposit, 20% time-deposit, 20% savings, 10% investment
    CASE
      WHEN s.i % 10 <= 4 THEN 'demand-deposit'
      WHEN s.i % 10 <= 6 THEN 'time-deposit'
      WHEN s.i % 10 <= 8 THEN 'savings-account'
      ELSE 'investment-account'
    END                                                         AS account_type,
    -- Currency: 80% TRY, 10% USD, 10% EUR
    CASE
      WHEN s.i % 10 <= 7 THEN 'TRY'
      WHEN s.i % 10 = 8  THEN 'USD'
      ELSE 'EUR'
    END                                                         AS currency,
    -- Branch: 10 distinct branches
    'BRN' || LPAD(((s.i % 10) + 1)::text, 3, '0')              AS branch_code,
    -- Initial deposit amounts (realistic ranges per account type)
    CASE
      WHEN s.i % 10 <= 4 THEN 500   + (s.i * 17  % 49500 )  -- demand:     500–50 000
      WHEN s.i % 10 <= 6 THEN 5000  + (s.i * 31  % 195000)  -- time:     5 000–200 000
      WHEN s.i % 10 <= 8 THEN 1000  + (s.i * 23  % 29000 )  -- savings:  1 000–30 000
      ELSE                     10000 + (s.i * 41  % 490000)  -- invest:  10 000–500 000
    END                                                         AS initial_deposit,
    -- Account purpose
    CASE s.i % 4
      WHEN 0 THEN 'personal'
      WHEN 1 THEN 'business'
      WHEN 2 THEN 'savings'
      ELSE        'investment'
    END                                                         AS account_purpose,
    -- 50 distinct users (each user has ~10 applications on average)
    'user-' || LPAD(((s.i % 50) + 1)::text, 3, '0')            AS user_id,
    -- Device channel
    CASE s.i % 3
      WHEN 0 THEN 'mobile-ios'
      WHEN 1 THEN 'mobile-android'
      ELSE        'web'
    END                                                         AS device_type,
    -- Processing duration in minutes
    (2 + s.i % 18)                                              AS duration_min
  FROM generate_series(1, 500) AS s(i)
),

-- ----------------------------------------------------------------
-- Step 2: Derive status, state, type, timestamps from the bucket
-- ----------------------------------------------------------------
enriched AS MATERIALIZED (
  SELECT
    p.*,
    -- Instance Status: A=Active, C=Completed, F=Faulted, P=Passive
    CASE
      WHEN p.bucket <= 11 THEN 'C'   -- success
      WHEN p.bucket <= 14 THEN 'A'   -- active / in-progress
      WHEN p.bucket <= 16 THEN 'F'   -- faulted
      WHEN p.bucket  = 17 THEN 'C'   -- cancelled  (Completed via cancel finish state)
      WHEN p.bucket  = 18 THEN 'C'   -- timed out  (Completed via timeout finish state)
      ELSE                     'P'   -- passive (deactivated/parked)
    END                                                         AS status,
    -- CurrentState (matches real workflow state keys)
    CASE
      WHEN p.bucket <= 11 THEN 'account-opening-success'
      WHEN p.bucket <= 14 THEN                                      -- active: spread across the funnel
        CASE p.seq % 4
          WHEN 0 THEN 'account-type-selection'
          WHEN 1 THEN
            CASE p.account_type
              WHEN 'demand-deposit'     THEN 'demand-deposit-info'
              WHEN 'time-deposit'       THEN 'time-deposit-info'
              WHEN 'savings-account'    THEN 'savings-account-info'
              ELSE                           'investment-account-info'
            END
          WHEN 2 THEN 'account-summary'
          ELSE        'policy-validation'
        END
      WHEN p.bucket <= 16 THEN                                      -- faulted: where creation fails
        CASE p.seq % 2 WHEN 0 THEN 'account-creation' ELSE 'policy-validation' END
      WHEN p.bucket  = 17 THEN 'cancelled'
      WHEN p.bucket  = 18 THEN 'timeouted'
      ELSE                     'account-summary'                    -- passive: parked mid-flow
    END                                                         AS current_state,
    -- StateType: 1=Initial, 2=Intermediate, 3=Finish, 5=Wizard
    CASE
      WHEN p.bucket <= 11 THEN 3
      WHEN p.bucket <= 14 THEN
        CASE p.seq % 4 WHEN 0 THEN 1 WHEN 3 THEN 2 ELSE 5 END
      WHEN p.bucket <= 16 THEN 2
      WHEN p.bucket  = 17 THEN 3
      WHEN p.bucket  = 18 THEN 3
      ELSE                     5
    END                                                         AS state_type,
    -- StateSubType: 0=None,1=Success,2=Error,6=Human,7=Cancelled,8=Timeout
    CASE
      WHEN p.bucket <= 11 THEN 1
      WHEN p.bucket <= 14 THEN 6
      WHEN p.bucket <= 16 THEN 2
      WHEN p.bucket  = 17 THEN 7
      WHEN p.bucket  = 18 THEN 8
      ELSE                     0
    END                                                         AS state_subtype,
    -- CompletedAt + Duration only for terminal/parked statuses (not Active)
    CASE
      WHEN p.bucket BETWEEN 12 AND 14 THEN NULL
      ELSE NOW() - (p.seq::float * 4.32 * INTERVAL '1 hour')
                 + (p.duration_min * INTERVAL '1 minute')
    END                                                         AS completed_at,
    CASE
      WHEN p.bucket BETWEEN 12 AND 14 THEN NULL
      ELSE p.duration_min * INTERVAL '1 minute'
    END                                                         AS duration_iv,
    -- Generated account artifacts (success only)
    'ACC' || LPAD(p.seq::text, 10, '0')                         AS account_number,
    'TR' || LPAD((p.seq * 7)::text, 24, '0')                    AS iban
  FROM params p
),

-- ----------------------------------------------------------------
-- Step 3: Insert Instances; return Id + Key for the data join
-- ----------------------------------------------------------------
ins_instances AS (
  INSERT INTO account_opening."Instances" (
    "Id", "Flow", "FlowVersion",
    "CurrentState", "CurrentStateType", "CurrentStateSubType",
    "EffectiveState", "EffectiveStateType", "EffectiveStateSubType",
    "Status", "CreatedAt", "CompletedAt", "Duration",
    "CreatedBy", "ModifiedAt", "Key", "Tags", "ExtraProperties", "Incidents"
  )
  SELECT
    e.inst_id, 'account-opening', '1.0.0',
    e.current_state, e.state_type, e.state_subtype,
    e.current_state, e.state_type, e.state_subtype,
    e.status, e.created_at, e.completed_at, e.duration_iv,
    e.user_id, e.created_at,
    'AO-' || LPAD(e.seq::text, 6, '0'),
    ARRAY[]::text[],
    '{"Sync":"false","Callback":"","FlowType":"F"}',
    -- Faulted instances carry one open incident (drives HasActiveIncident=true)
    CASE WHEN e.status = 'F' THEN
      jsonb_build_array(jsonb_build_object(
        'code',       'CORE_BANKING_TIMEOUT',
        'message',    'Core banking account creation failed',
        'state',      e.current_state,
        'isResolved', false,
        'occurredAt', e.completed_at::text
      ))
    ELSE '[]'::jsonb END
  FROM enriched e
  RETURNING "Id" AS inst_id, "Key"
),

-- ----------------------------------------------------------------
-- Step 4: Insert InstancesData (one row per instance).
-- VersionNo / IsLatest set by the BEFORE-INSERT trigger.
-- JSONB payload mirrors the real account-opening master schema.
-- ----------------------------------------------------------------
ins_data AS (
  INSERT INTO account_opening."InstancesData" (
    "Id", "InstanceId", "Version", "VersionNo", "IsLatest",
    "ETag", "EnteredAt", "Data", "DataHash"
  )
  SELECT
    gen_random_uuid(), ii.inst_id, '1.0.0', 1, true,
    UPPER(SUBSTRING(MD5(ii.inst_id::text || e.seq::text), 1, 26)),
    e.created_at,
    CASE
      -- Success: full payload incl. accountCreation result
      WHEN e.status = 'C' AND e.bucket <= 11 THEN jsonb_build_object(
        'accountType',           e.account_type,
        'accountName',           initcap(replace(e.account_type, '-', ' ')) || ' — ' || e.user_id,
        'accountPurpose',        e.account_purpose,
        'currency',              e.currency,
        'branchCode',            e.branch_code,
        'initialDeposit',        e.initial_deposit,
        'termsAccepted',         true,
        'privacyPolicyAccepted', true,
        'confirmed',             true,
        'userSession',  jsonb_build_object(
          'userId', e.user_id, 'deviceId', 'dev-' || e.device_type || '-' || e.seq::text,
          'userAgent', e.device_type, 'ipAddress', '10.0.' || (e.seq % 255) || '.' || ((e.seq * 3) % 255)),
        'initial',      jsonb_build_object('requestId', 'REQ-' || e.seq::text, 'session', jsonb_build_object()),
        'accountCreation', jsonb_build_object(
          'accountNumber', e.account_number, 'accountId', 'AID-' || e.seq::text,
          'iban', e.iban, 'accountType', e.account_type, 'currency', e.currency,
          'branchCode', e.branch_code, 'status', 'CREATED', 'success', true,
          'isActive', true, 'createdAt', e.completed_at::text)
      )
      -- Cancelled: partial payload up to cancellation
      WHEN e.status = 'C' AND e.bucket = 17 THEN jsonb_build_object(
        'accountType', e.account_type, 'currency', e.currency, 'branchCode', e.branch_code,
        'initialDeposit', e.initial_deposit, 'cancelledAt', e.completed_at::text,
        'userSession', jsonb_build_object(
          'userId', e.user_id, 'deviceId', 'dev-' || e.device_type || '-' || e.seq::text,
          'userAgent', e.device_type, 'ipAddress', '10.0.' || (e.seq % 255) || '.' || ((e.seq * 3) % 255)),
        'initial', jsonb_build_object('requestId', 'REQ-' || e.seq::text, 'session', jsonb_build_object())
      )
      -- Timed out: minimal payload (abandoned before completion)
      WHEN e.status = 'C' AND e.bucket = 18 THEN jsonb_build_object(
        'accountType', e.account_type, 'currency', e.currency, 'branchCode', e.branch_code,
        'timedOutAt', e.completed_at::text,
        'userSession', jsonb_build_object(
          'userId', e.user_id, 'deviceId', 'dev-' || e.device_type || '-' || e.seq::text,
          'userAgent', e.device_type, 'ipAddress', '10.0.' || (e.seq % 255) || '.' || ((e.seq * 3) % 255)),
        'initial', jsonb_build_object('requestId', 'REQ-' || e.seq::text, 'session', jsonb_build_object())
      )
      -- Faulted: partial payload with error context
      WHEN e.status = 'F' THEN jsonb_build_object(
        'accountType', e.account_type, 'currency', e.currency, 'branchCode', e.branch_code,
        'initialDeposit', e.initial_deposit, 'failedAt', e.completed_at::text, 'failedState', e.current_state,
        'userSession', jsonb_build_object(
          'userId', e.user_id, 'deviceId', 'dev-' || e.device_type || '-' || e.seq::text,
          'userAgent', e.device_type, 'ipAddress', '10.0.' || (e.seq % 255) || '.' || ((e.seq * 3) % 255)),
        'initial', jsonb_build_object('requestId', 'REQ-' || e.seq::text, 'session', jsonb_build_object())
      )
      -- Active / Passive: payload reflects progress so far
      ELSE jsonb_build_object(
        'accountType', CASE WHEN e.current_state = 'account-type-selection' THEN NULL ELSE e.account_type END,
        'currency', e.currency, 'branchCode', e.branch_code, 'initialDeposit', e.initial_deposit,
        'userSession', jsonb_build_object(
          'userId', e.user_id, 'deviceId', 'dev-' || e.device_type || '-' || e.seq::text,
          'userAgent', e.device_type, 'ipAddress', '10.0.' || (e.seq % 255) || '.' || ((e.seq * 3) % 255)),
        'initial', jsonb_build_object('requestId', 'REQ-' || e.seq::text, 'session', jsonb_build_object())
      )
    END,
    MD5(ii.inst_id::text || e.seq::text)
  FROM ins_instances ii
  JOIN enriched e ON e.seq = SUBSTRING(ii."Key" FROM 4)::int
  RETURNING "InstanceId"
)
SELECT 'Seeded ' || COUNT(*) || ' InstancesData rows.' AS result FROM ins_data;

COMMIT;

\echo 'Done. Run the Metabase provision script next: ./config/metabase/provision.sh'
