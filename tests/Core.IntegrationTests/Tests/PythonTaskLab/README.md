# python-task-lab — PythonTask (tip 23) container runtime modeli

## Neyi denetliyor

vNext'te Python artık hiçbir vNext image'ında yok. Domain sahibi Python kullanmak isterse Helm'de
`pythonRuntime`'ı açar ve kendi image'ını `ghcr.io/burgan-tech/vnext/python-runtime` base image'ından
türetir (handler'lar + pin'li paketler). Orchestration host'u task'ı **Execution hop'u olmadan**, Dapr
service invocation ile doğrudan bu container'a gönderir (`POST v1/invoke`, contract v1 — vnext
`docs/runtime/python-task.md`). Bu senaryo zincirin tamamını uçtan uca denetler:

| Vaka (transition) | Task → handler | Beklenen |
|---|---|---|
| `case-sum` | `ptl-sum` → `ptl_sum` | Transition gövdesindeki `values`, mapping'in `SetInput`'u ile authored input'un yerine geçer → `total = 10` |
| `case-static-input` | `ptl-sum` → `ptl_sum` | Gövdede `values` yok → authored `config.input` (`[1,1]`) gider → `total = 2` |
| `case-context` | `ptl-context` → `ptl_context` | Handler context'i `domain=core`, `workflow`, `instanceId`, `taskKey`, `timeoutMs = 7000` taşır |
| `case-dependency` | `ptl-dateutil` → `ptl_dateutil` | Image build'inde kurulan `python-dateutil` import edilebilir |
| `case-fail` | `ptl-fail` → `ptl_fail` | 422, `handler_error: ValueError: ptl rejected by design`; boundary yok → instance `landed` |
| `case-unknown` | `ptl-unknown` → `ptl_missing` | 404, `unknown_handler: …` |
| `case-timeout` | `ptl-slow` (`timeoutSeconds: 1`, handler 3 sn uyur) | 504, `timeout: …` (runtime'ın kendi süresi; Orchestration'ın 408'i `timeoutSeconds + 5` sn sonra) |
| `case-invalid-output` | `ptl-nan` → `ptl_nan` | 422, `invalid_output: …` (NaN strict JSON değil) |
| `case-fail-abort` | `ptl-fail` + task seviyesi Abort (`errorCodes: ["422"]`) | Instance fault'lar, incident `boundaryAction = Abort`, `statusCode = 422` |

Her vakada paylaşılan `PtlResultProjection.csx` sonucu `ptl*` alanlarına yazar (başarısız çağrıda da
koşar): `ptlIsSuccess`, `ptlStatusCode`, `ptlErrorMessage`, `ptlData`, `ptlTaskType = python` (diğer wire tipleri gibi küçük harf).

## Nasıl koşulur

```bash
# 1) vnext infra + host'lar, Python runtime'ı gösteren app-id ile
cd ../vnext/etc/docker
Workflow__PythonRuntime__AppId=vnext-python-runtime-app ./run-docker.sh up core
# 2) domain runtime'ı (vnext checkout'undan base image'ı lokal derler)
cd ../../../vnext-example && labs/python-runtime/lab.sh up ../vnext
# 3) test
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~PythonTaskLab" -v minimal
```

`lab.sh up` sonunda iki satır basar: runtime'ın manifest'i (handler listesi) ve sidecar üzerinden
`v1/health` — ikincisi Dapr adresleme yolunun çalıştığını gösterir.

## Dikkat noktaları

- **Bağımlılık yoksa skip.** Runtime `PYTHON_RUNTIME_URL` (varsayılan `http://localhost:8089`)
  `/v1/health`'e cevap vermezse 9 vakanın tamamı skip eder.
- **Host env'i olmadan 503.** Orchestration `Workflow__PythonRuntime__AppId` olmadan başlatıldıysa
  runtime ayakta olsa bile her vaka 503 (`Python runtime is not enabled for this domain`) ile
  kırmızıdır — bu bir ortam hatasıdır, runtime regresyonu değil. 503 yolu vnext unit testlerinde
  (`PythonTaskExecutorTests`) pinli; burada ayrı bir runtime başlatması istediği için yok.
- **Orchestration 408'i** (runtime hiç cevap vermezse) burada yok: bunun için runtime'ın `timeoutMs`'i
  yok sayması gerekir; unit testte pinli.
- Fixture değişirse `build-python-task-lab.py`'deki `VERSION`'ı artır (publish sürüm-immutable).

## Son koşu (2026-11-04)

vnext `claude/python-runtime-wrapper` (472421c0 üzerine commit'lenmemiş değişiklikler), lokal derlenmiş
host'lar, `run-docker.sh up core` + `lab.sh up`. **9/9 yeşil, 0 skip.**

`InstanceTasks` (son koşu, `InvocationResult`):

| Task | HTTP | Sonuç | Süre |
|---|---|---|---|
| `ptl-sum` (gövdeden) | 200 | `{"count": 3, "total": 10}` | 6 ms |
| `ptl-sum` (authored input) | 200 | `{"count": 2, "total": 2}` | 5 ms |
| `ptl-context` | 200 | `domain=core`, `taskKey=ptl-context`, … | 6 ms |
| `ptl-dateutil` | 200 | `{"year": 2026, "weekday": "Tuesday"}` | 6 ms |
| `ptl-fail` | 422 | `handler_error: ValueError: ptl rejected by design` | 5 ms |
| `ptl-unknown` | 404 | `unknown_handler: handler 'ptl_missing' is not registered` | 4 ms |
| `ptl-slow` | 504 | `timeout: handler did not finish within 1000 ms` | 1011 ms |
| `ptl-nan` | 422 | `invalid_output: handler result is not strict JSON …` | 6 ms |

`InstanceIncidents`: `case-fail-abort` → `Abort | 422`, instance `F`.

Trace (Elastic APM, 2 koşu, 18 çağrı): `vnext-app` (Orchestration) → `Dapr invoke vnext-python-runtime-app`
→ sidecar'larda `CallLocal/vnext-python-runtime-app/v1/invoke`. Timeout dışındaki 16 çağrı: p50 2.9 ms,
ortalama 7.1 ms, en yüksek 57.3 ms (ilk, soğuk çağrı); iki timeout çağrısı 1004–1007 ms. Aynı pencerede
`vnext-execution-app`'te yalnız başlangıç `dapr/config` ve `dapr/subscribe` var — Python çağrısı Execution'a
hiç uğramıyor. Runtime erişim logu: 8×200, 6×422, 2×404, 2×504.
