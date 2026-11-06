# SyncSubProcessStateLab

**Issue:** burgan-tech/vnext#1182 — a sync SubProcess task's output carried the child's `status` but
not the state it stopped in, so a mapping that branches on "is the child in `otp-waiting` or
`approved`?" needed an extra instance GET or state-function call.

## What it pins

| Test | Asserts |
|---|---|
| `SyncSubProcess_TaskOutputCarriesTheChildsRestState` | `ssp-parent` starts `ssp-child` through a **sync** SubProcess task (`ssp-start-child`, type 14). Its output mapping reads `response.data.value.currentState` / `effectiveState` (same-domain shape) and records them; both equal `ssp-otp-waiting`, the state the child **rests** in after auto-chaining out of `ssp-received`. |
| `SyncStartAndTransition_ResponsesCarryTheRestState` | A direct `?sync=true` start answers `currentState` / `effectiveState` = `ssp-otp-waiting`; a `?sync=true` `approve` answers `ssp-approved`. |
| `AsyncStart_ResponseStaysIdentityOnly` | A `?sync=false` start (202) carries neither field. |

## Flows

- `core/Workflows/sync-subprocess-state-lab/` — `ssp-child`, `ssp-parent`; regenerate with
  `build-sync-subprocess-state-lab.py` after editing `src/*.csx`.
- `core/Tasks/sync-subprocess-state-lab/ssp-start-child.json` — the SubProcess task.

## Dependencies

Core only; no MockLab, no partner domain.

## Not covered here

The cross-domain shape (`response.data.currentState`, the remote `sub/instances/start` body passed
through flat) needs the cross-domain lab; the mapping already reads both shapes.
