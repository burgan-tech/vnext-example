# parallel-variable-key-lab — aynı order'da aynı task iki kez, `variableKey` ile ayrı slot

## Neyi denetliyor

**Tek iddia:** Bir task girişi yanıtını `context.TaskResponse` içinde tek bir slot'a yazar:
girişte `variableKey` varsa o, yoksa `ToVariableName(task.key)` (`pvk-spawn-child` → `pvkSpawnChild`).
Aynı `order`'daki girişler paralel koşar ve slot'a göre birleştirilir. Aynı task aynı order'da iki
kez listelendiğinde `variableKey` her koşuya kendi slot'unu verir; birleştirme artık çakışmaz.
`variableKey` yazılmamış giriş eski davranışla `ToVariableName(task.key)` slot'una düşer.

Publish tarafı: aynı order'da aynı slot'a düşen iki giriş (iki çıplak giriş ya da aynı
`variableKey`) ve biçimi geçersiz bir `variableKey` 400 ile reddedilir.

## Neden var

Preprod'da aynı SubProcess task'ı bir state'in `onEntries`'inde aynı order'da iki kez koşan bir akış
`Parallel tasks produced conflicting output for key 'subprocessTaskSendNotification'` ile düştü
(2026-10-04). İki koşu da `ToVariableName(task.key)` slot'una yazıyordu; iki çocuğun start yanıtı
farklı olduğu için `ScriptContext.MergeParallelBranch` çakışma attı. Düzeltme: vnext
`feature/task-variable-key` — task girişine opsiyonel `variableKey`, publish'te aynı-order slot
çakışması reddi (`WorkflowValidator`).

## Akış şekli

```
pvk-parent  --start--> spawning [Initial] --auto-spawned (triggerKind 10)--> spawned [Intermediate, Active'te bekler]
              spawning.onEntries:
                order 1  pvk-spawn-child   variableKey: primaryChild     ┐ paralel
                order 1  pvk-spawn-child   variableKey: secondaryChild   ┘ (hatayı üreten şekil)
                order 2  pvk-spawn-child   (variableKey yok → slot pvkSpawnChild)
                order 3  pvk-record-slots  (Script: üç slot'tan başlatılan instance id'lerini data'ya yazar)

pvk-reuse   --start--> spawning [Initial] --auto-spawned (triggerKind 10)--> spawned [Intermediate, Active'te bekler]
              spawning.onEntries:
                order 1  pvk-spawn-child   (variableKey yok → slot pvkSpawnChild; firstChildId'yi yazar)
                order 2  pvk-spawn-child   (variableKey yok → pvkSpawnChild'ı YENİDEN yazar) ┐ paralel
                order 2  pvk-spawn-child   variableKey: otherChild                          ┘
                order 3  pvk-record-slots  (Script: reusedChildId = pvkSpawnChild, otherChildId = otherChild)

pvk-child   --start--> waiting [Initial]   (bilinçli olarak bitmez)
```

Akışlar `core/Workflows/parallel-variable-key-lab/build-parallel-variable-key-lab.py` ile üretilir;
`src/*.csx`'i düzenleyip script'i yeniden koş, base64'ü elle değiştirme.

- `PvkSpawnChildMapping.csx` — üç spawn girişinin ortak mapping'i: her koşuda yeni bir idempotency
  key (`Guid`), `sync=false`. İki order-1 koşusu bu yüzden iki ayrı çocuk ve iki farklı start yanıtı
  üretir.
- `PvkRecordSlotsMapping.csx` — Script task'ın `OutputHandler`'ı `context.TaskResponse["primaryChild"]`,
  `["secondaryChild"]`, `["pvkSpawnChild"]` slot'larından `data.value.id` (yedek `data.id`) okur ve
  `primaryChildId` / `secondaryChildId` / `legacyChildId` olarak instance data'ya yazar. Önceki
  order'ların slot'ları ortak context'e birleştirildiği için order 3'teki script onları görür.

- `PvkSpawnFirstMapping.csx` — `PvkSpawnChildMapping`'in InputHandler'ı ile aynı; OutputHandler'ı start
  yanıtından (`data.value.id`, yedek `data.id`) `firstChildId` yazar.
- `PvkRecordReuseMapping.csx` — `pvkSpawnChild` ve `otherChild` slot'larını okuyup `reusedChildId` /
  `otherChildId` olarak yazar.

`pvk-child`'ın tek state'i Initial: sabitlenmiş vnext-schema 0.0.52 Initial'sız akışı reddettiği
için (bkz. implicit-start-lab) çocuk Intermediate yerine Initial `waiting`'te bekler. Test için fark
yok, yalnız state anahtarı okunur.

## Testler

| Test | Kanıtladığı |
|---|---|
| `SameTaskTwiceAtOneOrder_WithVariableKeys_EachRunKeepsItsOwnSlot` | parent `spawned`'a ulaşır, `F` değil; üç slot dolu ve üç id birbirinden farklı; her çocuk `waiting`'te |
| `ParallelGroupReusingAnEarlierSlot_OverwritesIt` | `pvk-reuse`: paralel grup, önceki order'ın bıraktığı `pvkSpawnChild` slot'unu yeniden yazar (ardışık koşudaki gibi üzerine yazar); instance `spawned`'a ulaşır, `F` değil; `firstChildId` / `reusedChildId` / `otherChildId` üçü farklı (reused != first, yazımın değiştirdiğini kanıtlar); üç çocuk `waiting`'te. Slot-aware merge öncesi "conflicting output" ile düşerdi |
| `Publish_SameTaskTwiceAtOneOrder_WithoutVariableKey_Returns400` | order-1 girişlerinden `variableKey` silinince 400; gövdede `pvkSpawnChild` ve `variableKey` |
| `Publish_SameVariableKeyTwiceAtOneOrder_Returns400` | iki order-1 girişi `variableKey: "child"` → 400; gövdede `'child'` |
| `Publish_InvalidVariableKeyFormat_Returns400` | `variableKey: "primary-child"` → 400; gövdede ham değer |

Hatalı tanımlar `core/` altında tutulmaz. Test, diskteki `pvk-parent.json`'ı bir probe anahtarı
(`pvk-parent-probe-<tag>`) ve koşu başına yeni bir sürümle kopyalayıp doğrudan
`api/v1/definitions/publish`'e gönderir; böylece SDK publisher onları her fixture açılışında
göndermez ve 409 "already exists" reddin yerine geçemez.

Runtime'ın beklenen hata metni (paralel birleştirme, düzeltmeden önce):
`Parallel tasks produced conflicting output for key '<slot>'.`

## Nasıl koşulur

Ön koşul: vnext `feature/task-variable-key`'den derlenmiş lokal runtime
(`etc/docker/run-docker.sh up core`, :4201). MockLab gerekmez.

```bash
cd ../vnext-example
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~ParallelVariableKeyLab" -v minimal
```

## Geçme kriteri ve bilinen sınırlar

5/5 yeşil (son durum aşağıda). İlk 4 testin geçmişi: ✅ 4/4 yeşil (2026-10-04, runtime `e7c867eb`, `feature/task-variable-key`, lokal
build, `http://localhost:4201`). Ayırt edici koşu (base `cddab84c`, `variableKey` desteği yok): 0/4 —
pozitif test parent `F` (`Parallel tasks produced conflicting output for key 'pvkSpawnChild'`), üç
publish testi 200 döndü. Doğrulama postgres + host loglarıyla yapıldı: parent'ın son data satırında
üç farklı çocuk id'si, order 1'de `pvk-spawn-child#0` / `#1` ayrı journal satırı, orchestration
logunda "conflicting output" yok.

Tekrar koşu (2026-10-04, runtime `dd671c18`): senaryo 4/4 yeşil. OpenObserve span'leri (stream `vnext`):
order 1'deki iki `Task.Execute.pvk-spawn-child` ~5 ms arayla başlayıp paralel koştu (468 / 464 ms,
`OK`), order 2'deki spawn onlardan sonra (32 ms), `pvk-record-slots` en son (79 ms);
`Step.RunOnEntryTasks` 610 ms. "conflicting output" log sayısı 0.
Regresyon kontrolü (`CrossDomainLab` hariç tam suite): 355 geçti / 39 kaldı / 9 atlandı (403). 30 farklı
kırmızı testten 29'u master runtime'ında (`cddab84c`) da kırmızı (RoleMatrixLab, HumanTaskChain,
DataIntegrityLab, AccountOpening/ErrorBoundaryLab rol testleri, TaskInvocationLab DaprService).
Tek fark `SubStateRelayTests.EffectiveState_FollowsTheGrandchild_AcrossTwoLevels`: tam suite yükü
altında kırmızı, aynı runtime'da tek başına 3/3 ve sınıfıyla birlikte yeşil — zamanlama flake'i,
regresyon değil.

Slot-aware birleştirme koşusu (2026-10-05, runtime `8046dbd6`, `ScriptContext.MergeParallelBranches`):
senaryo **5/5 yeşil**. Ayırt edici koşu (base `cddab84c`): 0/5 — `pvk-reuse` instance'ı `spawning`'de `F`
(`Parallel tasks produced conflicting output for key 'pvkSpawnChild'`, ardından `ParallelExecutionFailed`),
publish testleri 200, `SameTaskTwiceAtOneOrder` `F` (master'da `variableKey` yok). OpenObserve, pvk-reuse
trace'i `4f5c8cc95f9f65cb2de3fbdf4b067abc`: order 1'deki spawn 124.1 ms, order 2'deki iki spawn paralel
(51.7 / 46.8 ms, ~46.8 ms örtüşme, `OK`); `Task.Execute.pvk-spawn-child` 15 dk'da n=6, ort 262.9 ms,
p95/maks 655.6 ms (maks, `SameTaskTwice` testindeki paralel order-1 çifti); rebuild'den sonra
"conflicting output" log sayısı 0. Postgres: `pvk_reuse` instance'ının son data satırında (1.0.2)
`firstChildId` / `reusedChildId` / `otherChildId` üçü farklı; üç `pvk-child` `waiting`/`A`.
Tam suite (`CrossDomainLab` hariç): 357 geçti / 38 kaldı / 9 atlandı (404). Kırmızı 29 farklı testin
tamamı master'da da kırmızı olan liste; yeni kırmızı yok, önceki koşudaki `SubStateRelayTests` flake'i bu
koşuda yeşil.

Çocuklar bilinçli olarak bitmez; her koşu `core` şemasında üç `pvk-child` instance'ı `waiting`'te
bırakır.
