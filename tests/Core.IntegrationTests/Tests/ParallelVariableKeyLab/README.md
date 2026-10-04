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

`pvk-child`'ın tek state'i Initial: sabitlenmiş vnext-schema 0.0.52 Initial'sız akışı reddettiği
için (bkz. implicit-start-lab) çocuk Intermediate yerine Initial `waiting`'te bekler. Test için fark
yok, yalnız state anahtarı okunur.

## Testler

| Test | Kanıtladığı |
|---|---|
| `SameTaskTwiceAtOneOrder_WithVariableKeys_EachRunKeepsItsOwnSlot` | parent `spawned`'a ulaşır, `F` değil; üç slot dolu ve üç id birbirinden farklı; her çocuk `waiting`'te |
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

4/4 yeşil. Durum: ✅ 4/4 yeşil (2026-10-04, runtime `e7c867eb`, `feature/task-variable-key`, lokal
build, `http://localhost:4201`). Ayırt edici koşu (base `cddab84c`, `variableKey` desteği yok): 0/4 —
pozitif test parent `F` (`Parallel tasks produced conflicting output for key 'pvkSpawnChild'`), üç
publish testi 200 döndü. Doğrulama postgres + host loglarıyla yapıldı: parent'ın son data satırında
üç farklı çocuk id'si, order 1'de `pvk-spawn-child#0` / `#1` ayrı journal satırı, orchestration
logunda "conflicting output" yok. OpenObserve / Elastic APM span kontrolü yapılamadı (MCP bağlantısı
yoktu), span süreleri ölçülmedi.

Çocuklar bilinçli olarak bitmez; her koşu `core` şemasında üç `pvk-child` instance'ı `waiting`'te
bırakır.
