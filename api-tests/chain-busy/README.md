# chain-busy — A → B → C zincir davranış testleri

Üç seviyeli iç içe subflow zinciri (`chain-busy-root` → `chain-busy-middle` → `chain-busy-leaf`)
üzerinde accept-time chain reserve, `sharedTransition`, `cancel` ve `updateData` davranışlarını
uçtan uca doğrular. Entegrasyon testlerine taşınmak üzere tasarlandı: her case bağımsız,
tekrarlanabilir ve DB'den okunan sayaçlarla kanıtlanır.

## Akış

| Instance | Rol | Dinlenme yeri |
| --- | --- | --- |
| `chain-busy-root` (A) | kök, `type: F` | `root-waiting` (SubFlow state) — açık korelasyon boyunca `Busy` |
| `chain-busy-middle` (B) | ara relay, `type: S` | `middle-waiting` (SubFlow state) — aynı sebeple `Busy` |
| `chain-busy-leaf` (C) | yaprak, `type: S` | `leaf-waiting` — `Active`, girdi bekler |

Zincir tamamen auto transition ile kurulur: A başlatıldığında C `leaf-waiting`'de bekler hâle gelir.
State function en derindeki aktif subflow'un status'unu raporladığı için **client'ın gördüğü tek
sinyal C'dir**.

Gözlemlenebilirlik: her onEntry / onExit / onExecute bir sayaç task'ı çalıştırır ve sayaç instance
verisine yazılır; `leaf-waiting`'de 30 dakikalık bir scheduled transition kurulur (asla ateşlenmez,
amacı ARMED bir `InstanceJob` bırakmaktır — `ExecuteAt` sabit kalıyorsa zamanlayıcı yeniden
kurulmamış demektir).

## Case'ler

| Case | Ne doğrular |
| --- | --- |
| `accept-busy` | Async accept, 202 dönmeden önce zinciri leaf'e kadar `Busy`'e çeker. |
| `start-onentry` | Start, başlangıç state'inin onEntry'sini çalıştırır (hem üst seviye hem subflow start). |
| `shared-self` | C'nin `$self` shared transition'ı: OnExecute çalışır **ve** state yaşam döngüsü de koşar — onEntry/onExit birer artar, zamanlayıcı yeniden kurulur. |
| `shared-parent` | A'nın **kendi** shared transition'ı: aktif subflow varken bile A karşılar, aşağı forward **edilmez**. |
| `shared-forward` | Yalnız C'de tanımlı shared transition A'ya gönderilir → zincirden aşağı forward edilir. |
| `updatedata-self` | C'ye updateData: OnEntry/OnExit **yok**, scheduled transition **yeniden kurulmaz**. Yaşam döngüsü atlaması yalnız `updateData`'ya aittir — `shared-self` ile zıt yönde davranması sınırın kanıtıdır. |
| `cancel-top-down` | A'ya cancel → B ve C'ye kaskad, üçü de `*-cancelled`. |
| `cancel-bottom-up` | C'ye cancel → yukarı doğru tamamlanma; A'da açık korelasyon kalmaz. |

## Ön koşullar

Docker altyapısı + **dört** servis ayakta olmalı:

```bash
cd etc/docker && ./run-docker.sh            # altyapı (vnext deposunda)
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host   # 4201
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host           # 4202
```

`cancel-top-down` aşağı yönlü kaskadı **distributed event** ile yapar (`Instance.Cancel` →
`ChildSubflowCancelRequestedEvent`). Bu yüzden Outbox ve Inbox worker'ları da gerekir — onlar
olmadan olay hiç işlenmez ve case sessizce başarısız olur:

```bash
ASPNETCORE_URLS=http://localhost:4401 DAPR_APP_ID=vnext-worker-outbox \
  DAPR_HTTP_PORT=44110 DAPR_GRPC_PORT=44111 \
  dotnet run --project workers/BBT.Workflow.Workers.Outbox
```

```bash
ASPNETCORE_URLS=http://localhost:4501 DAPR_APP_ID=vnext-worker-inbox \
  DAPR_HTTP_PORT=45110 DAPR_GRPC_PORT=45111 \
  dotnet run --project workers/BBT.Workflow.Workers.Inbox
```

Script çalışırken bu iki worker'ı yoklar ve ayakta değilse uyarır.

## Çalıştırma

```bash
python3 api-tests/chain-busy/chain-busy-behaviour-test.py --publish --iterations 3
```

```bash
python3 api-tests/chain-busy/chain-busy-behaviour-test.py --case shared-self --case updatedata-self --iterations 10
```

`--publish` flow'ları leaf-first publish eder (bir üst seviye referansını çözebilsin diye) ve
`re-initialize` çağırır. `--list` case adlarını yazar.

`--settle` (varsayılan 2 sn) case'ler arasında bekler. Bu **gerekli**: `cancel-top-down`'ın aşağı
yönlü kaskadı Outbox → pub/sub → Inbox üzerinden gider ve nihai tutarlıdır. Bir önceki case'in
olayları hâlâ drenaj hâlindeyken yeni bir kaskad başlarsa gecikip zaman aşımına uğrayabiliyor —
bekleme sıfırlandığında (`--settle 0`) bu case ~3'te 1 düşerken, 2 saniyeyle iki ardışık 32/32
koşusu alındı. İzole çalıştırıldığında (`--case cancel-top-down`) bekleme olmadan da 8/8 geçiyor.

Akışları değiştirdiğinizde JSON'ları elle düzenlemeyin — üretici script'i çalıştırın ve sürümü
bir üst değere alın (aynı sürüm 409/`100002` ile dedupe edilir):

```bash
python3 core/Workflows/chain-busy/build-chain-busy.py
```

## Beklenen çıktı

```
  [accept-busy     ] PASS  client: A -> B
  [cancel-bottom-up] PASS  root=C/root-done  middle=C/middle-done  leaf=C/leaf-cancelled  acikKorelasyon=0
  [cancel-top-down ] PASS  root=C/root-cancelled  middle=C/middle-cancelled  leaf=C/leaf-cancelled
  [shared-forward  ] PASS  leafOnlyMarks 0->1  rootSharedMarks 0->0  middleSharedMarks 0->0
  [shared-parent   ] PASS  rootSharedMarks 0->1  leafOnlyMarks 0->0  root state root-waiting
  [shared-self     ] PASS  leafOnlyMarks 0->1  leafEntries 1->2  leafExits 0->1
  [start-onentry   ] PASS  rootInitialEntries=1  leafInitialEntries=1  (root-waiting girisi=1)
  [updatedata-self ] PASS  leafUpdates 0->1  entries 1->1  exits 0->0  sched sabit
```

## Akışı kurarken çıkan tuzak — task journal tekilliği

Task journal'ı **`(TransitionId, TaskId)`** üzerinden tekildir; `TaskId` task **tanımının**
key'idir ve `order` anahtarın **parçası değildir** (`InstanceTask.CreateExecutionKey`,
`TaskExecutionEngine`). `TaskCoordinator` da atlanacakları task key'i üzerinden süzer
(`GetCompletedTaskIdsAsync(transitionId)`). Sonuç: **aynı transition içinde aynı task tanımı ikinci
kez kullanılırsa "zaten tamamlandı" sayılıp sessizce atlanır** — `order` değiştirmek bunu çözmez.

Bu iki kez ısırdı:

1. `leaf-waiting` başlangıçta initial state'ti; start onu `leaf-waiting → leaf-waiting` koşturunca
   onExit ve onEntry aynı transition'a düşüyor ve onEntry hiç yazılamıyordu (`leafEntries` sürekli 0).
   Çözüm: `leaf-initial` eklenip `leaf-waiting`'e auto transition ile girilmesi.
2. `$self` shared transition artık tam yaşam döngüsü koştuğu için OnExit(X) + OnEntry(X) **yine**
   aynı transition'a düşüyor. Çözüm: hook başına ayrı task tanımı —
   `chain-entry-script-task` (onEntry), `chain-exit-script-task` (onExit),
   `subflow-script-task` (onExecute). Üçü aynı transition'da çakışmadan koşar.
