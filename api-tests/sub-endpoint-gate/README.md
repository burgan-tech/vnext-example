# sub-endpoint-gate — live probe (vnext#1265)

## What this covers

`tests/Core.IntegrationTests/Tests/SubEndpointGateLab` pins the gate inside the test suite. This probe is the
**reproduction**: what a plain authenticated client can do with the runtime-internal endpoints
`POST {domain}/workflows/{workflow}/sub/instances/start` and
`POST {domain}/workflows/{workflow}/instances/{instance}/complete`, with and without the gate.

| File | What it is |
|---|---|
| `sub-endpoint-gate-probe.py` | Six checks, exit code 0/1. Standard library only. |

The checks:

1. A client calls `sub/instances/start` for `sfch-child`.
2. A parent `sfch-parent` is started; its child A parks in `sfch-child-wait`.
3. The client posts a forged `complete` for that open correlation, with injected child data and carried headers.

## Run

Prerequisites:
- a core runtime on `VNEXT_BASE_URL` (default `http://localhost:4201`);
- the `subflow-carried-headers-lab` flows published. Any `SubEndpointGateLab` or `SubflowCarriedHeadersLab` run publishes them.

```bash
python3 -I api-tests/sub-endpoint-gate/sub-endpoint-gate-probe.py open      # gate Off or Observe
python3 -I api-tests/sub-endpoint-gate/sub-endpoint-gate-probe.py enforce   # gate Enforce
```

Pass criterion:
- **`open`:** the client's sub-start answers 200 and creates the child. The forged `complete` is processed: 200, or 500 when the early resume faults the parent. The parent leaves `sfch-a` while the real child still waits.
- **`enforce`:** both calls answer 403 `Authorization:110001`, the child is never created, the parent stays in `sfch-a`, and the real child is untouched.

## Results

| Date | Runtime | Mode | Result |
|---|---|---|---|
| 2026-11-17 | vnext master `76ca97ab` (before the gate) | open | sub-start 200 + child created; forged `complete` moved the parent `sfch-a` → `sfch-b` and **faulted** it (500). The second child collided on the key while child A was still waiting |
| 2026-11-17 | vnext `1265-…` working tree, gate Off | open | identical to master |
| 2026-11-17 | vnext `1265-…` working tree, gate Enforce | enforce | 6/6: both 403 `Authorization:110001`, nothing written, parent stays in `sfch-a` |
| 2026-11-17 | vnext `1265-…` final tree (after review), gate Off | open | 6/6, identical to master |
| 2026-11-17 | vnext `1265-…` final tree (after review), gate Enforce | enforce | 6/6 |
