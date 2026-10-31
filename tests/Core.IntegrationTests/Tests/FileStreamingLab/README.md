# FileStreamingLab — x-storage on real object stores (#137, #142, #145)

Exercises the x-storage changes of vnext `claude/vnext-file-upload-streaming-f7c134` against real stores. The runtime
reaches every store through a Dapr `bindings.aws.s3` component on its orchestration sidecar; it carries no store SDK.

| Component | Reached through | Store | Why |
|---|---|---|---|
| `fsl-minio` | runtime sidecar (daprd 1.16.19 locally), http | MinIO `fsl` | Streamed uploads, reads, ranges; `DaprBinding` list/get/delete on the same component |
| `fsl-stub` | runtime sidecar, https (`insecureSSL`) | dumb S3 (`labs/file-streaming/stub/stub.py`) behind socat TLS | #145: whatever the sidecar stores, the runtime reads back the original |
| `fsl-stub-dapr` | the runtime sidecar and, for the repro, an app-less **daprd 1.18.2** (`fsl-dapr118`) | the same dumb store | #145 repro: 1.18 frames TLS uploads (aws-chunked + CRC32 trailer) that the store keeps; the runtime must serve the original |

The dumb store keeps every request body as received — it does not decode `Content-Encoding: aws-chunked`, which is
what the bank's S3 does. Its admin API (`GET|PUT :9100/_admin/objects/{bucket}/{key}`) shows the stored size, sha256
and the PUT's request headers, and lets a test change the stored bytes.

## Run

```bash
cd labs/file-streaming && ./lab.sh up ../../../vnext            # stores + certs + bucket; copies 3 Dapr components
cd ../../../vnext/etc/docker && env $(../../../vnext-example/labs/file-streaming/lab.sh env | xargs) ./run-docker.sh up core
cd ../../../vnext-example && dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~FileStreamingLab"
labs/file-streaming/lab.sh down ../vnext                         # stores down, components removed
```

`lab.sh env` prints the one host setting, `Workflow:TaskInvocation:Modes:daprbinding=Local` (the binding components
live on the orchestration sidecar, so `DaprBinding` tasks run from there). Restart `vnext-orchestration-dapr` after
`up` so it loads the components. Without the lab every test skips.

## Tests (`FileStreamingLabTests`)

| Test | Issue | Pins |
|---|---|---|
| `Inline_FilePickerRow_KeepsDocumentType_AndTheBytesLiveInMinio` | #137 | A row's `documentType` survives the swap (store and echo); the echo's stored handle wins its own members |
| `StreamedUpload_LargerThanTheJsonBodyLimit_IsClaimed_AndServedWithRanges` | streaming | 30 MiB `POST …/files?path=files[]` → ticket (size/eTag from the bytes) → start claims it → owner = instance; `functions/file` full read, `Range` 206, `HEAD` |
| `StreamedUpload_IsSingleUse_AndBoundToItsPath` | streaming | A ticket claimed once; an upload for another path is refused (400 `Instance:100048`) |
| `Transport_DecidesTheChannel_PerField` | streaming | `stream` field refuses inline content; the upload endpoint refuses an `inline` field and an unknown path |
| `RuntimeSidecarOverTls_ToAStoreThatDoesNotDecodeAwsChunked_ReadsBackTheOriginal` | #145 | Written by the runtime's sidecar to the dumb TLS store: stored as the file (1.16.x) or framed (1.17+), read back as the 6265-byte original either way |
| `ObjectFramedByDapr118_IsServedAsTheOriginal` | #145 | The object daprd 1.18 framed (6309 bytes) is served as the 6265-byte original |
| `ObjectThatNoLongerMatchesItsHandle_Is503_NotServed` | #145 | A changed object is 503 `Instance:100047`, never served |
| `Script_ReadsAFile_ProcessesItWithStreams_AndWritesItBack` | #142-A | `GetFileAsync` → `OpenRead` + `BinaryReader` → `MemoryStream` + `BinaryWriter` → `PutFileAsync` → served bytes = the transform |
| `BindingTask_ListsGetsAndDeletes_TheInstancesObject` | #142-B/D | `list` (json) and `get` (base64) reach the output mapping; `delete` with `FileObjectKey` succeeds; the handle that still names it reads `503` (a missing object looks like an outage through a binding) |
| `RawFunction_ServesTheStoredPdf_AsItsBytes` | #142-E / #99 | A `rawResponse` function returning a `ScriptFile` answers the PDF bytes as `application/pdf` |

Components: `core/Workflows/file-streaming-lab` (builder `build-file-streaming-lab.py`, `src/*.csx`),
`core/Schemas/file-streaming-lab`, `core/Tasks/file-streaming-lab`, `core/Functions/file-streaming-lab`.

## Results (2026-10-31)

| Runtime | FileStreamingLab | FileOffloadLab | ForgeFileFormLab |
|---|---|---|---|
| vnext `claude/vnext-file-upload-streaming-f7c134` @ `2a6f28a5` (Dapr bindings only) | 10/10 | 19/19 | 4/4 |
| same branch @ `3d846aff`, `5ed4fa42` (with the since-removed s3 provider) | 10/10 | 19/19 | 4/4 |
| vnext `9ddb7944` (base, distinguishing run) | **0/10** | 19/19 | 4/4 |

On the base runtime every test fails with the behaviour the issues describe: the object daprd 1.18 framed is served
as 6309 bytes, `documentType` is dropped, the raw PDF answers `application/json`, the binding `list` fails with the
generic "the Dapr endpoint indicated a failure", a `stream` field accepts inline content, and `POST …/files` is 404. Evidence beyond the test summary: no `fsl_flow."InstancesData"` row carries
`content` (Postgres, 0 of 112 after the Dapr-only run); the 1.16.19 sidecar stored the 6265-byte file unframed on the
dumb TLS store; claimed upload tickets are gone from Redis, unclaimed ones expire; OpenObserve shows
`POST …/files` server spans (30 MiB upload, max 143 ms) and `Files.Offload` spans.
