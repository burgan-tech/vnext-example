# VersionPinLab — integration test

Bir sınıf, **üç test**; `version-pin-lab-child`, `version-pin-lab-subflow-parent`, `version-pin-lab-subprocess-parent` (domain `core`).

| Class | Test | What it checks |
|---|---|---|
| `VersionPinLabTests` | `Start_ReturnsResolvedFlowVersion` | `instances/start?version=1.0` (1.0.0 ve 1.0.3 yayında) cevabı çözümlenen sürümü taşır: `flowVersion == "1.0.3"` |
| `VersionPinLabTests` | `SubFlowCorrelation_StoresChildResolvedVersion` | `subFlow.process.version: "1.0"` olan SubFlow state'inde parent'ın correlation'ı `subFlowVersion == "1.0.3"` tutar; child instance'ı 1.0.3'te koşar; child bitince parent fault'suz `sf-completed`'a gider |
| `VersionPinLabTests` | `SubProcessCorrelation_StoresChildResolvedVersion` | Aynısı, child `SubProcessTask` (TaskType 14, trigger version `"1.0"`) ile başlatıldığında |

## Why this exists

Parent'ın okuma yolları (state function, descent, instance read) child'ı bulmak için correlation'daki
`SubFlowVersion`'ı **yeniden çözümler**. Yazara ait kısmi referans (`"1.0"`) saklanırsa, sonradan
yayınlanan bir `1.0.x` bu okumaları, çalışan child instance'ın doğduğu sürümden **farklı** bir sürüme
yönlendirir. Bu yüzden correlation, child'ın gerçekten koştuğu çözümlenmiş sürümü (`1.0.3`) tutmalı;
start cevabı da (`flowVersion`) aynı çözümlenmiş sürümü döndürmeli ki istemci neyin başladığını bilsin.
Pinlenen: start cevabındaki `flowVersion`, SubFlow state'inin ve SubProcess task'ının correlation
`subFlowVersion`'ı, child instance'ının kendi `flowVersion`'ı.

## The fixture

`core/Workflows/version-pin-lab/`, `build-version-pin-lab.py` ile `./src/*.csx`'ten üretilir
(`.csx`'i düzenle, script'i tekrar çalıştır; base64'e elle dokunma):

- `version-pin-lab-child.json` **1.0.0** — `child-initial` → manual `finish` → `child-finished`
  (Finish). Test bitirene kadar `Active` kalır. SDK bunu `core/` altından otomatik yayınlar.
- **1.0.3 varyantı `core/` altında YOK.** Test onu 1.0.0 dosyasından kurar (sürüm + etiket farkı) ve
  `api/v1/definitions/publish` ile bir kez yayınlar; SDK yayıncısı kendiliğinden göndermesin diye
  `core/` dışında tutulur (bkz. ImplicitStartLab'in probe yaklaşımı). Yayın sürüm-immutable olduğundan
  suite tekrar koşunca 409 / "already" cevabı sorun sayılmaz.
- `version-pin-lab-subflow-parent.json` — `sf-initial` → auto → `sf-subflow` (`stateType: 4`,
  `subFlow.type: "S"`, `process.version: "1.0"`) → auto → `sf-completed`.
- `version-pin-lab-subprocess-parent.json` — `sp-initial`'in onEntry'si
  `core/Tasks/version-pin-lab/version-pin-lab-start-subprocess.json` (TaskType 14) task'ını çalıştırır;
  mapping `SetVersion("1.0")` der. `sp-waiting`'de manual `complete` bekler (child bittikten sonra
  parent'ın fault'suz devam ettiğini göstermek için).

## What each test asserts, in order

### `Start_ReturnsResolvedFlowVersion`
1. 1.0.3'ü yayınla (bir kez). 2. Child'ı `?version=1.0&sync=true` ile başlat. 3. Cevapta
`flowVersion == "1.0.3"`.

### `SubFlowCorrelation_StoresChildResolvedVersion`
1. Parent'ı başlat; state function'ın `activeCorrelations`'ında `subFlowName == version-pin-lab-child`
girdisini bekle. 2. `subFlowVersion == "1.0.3"`. 3. Child instance'ı okunur: `flowVersion == "1.0.3"`.
4. Child'da `finish`; parent `sf-completed`'a gider ve fault'lu değildir.

### `SubProcessCorrelation_StoresChildResolvedVersion`
Aynı üç adım, parent `sp-waiting`'e ulaştıktan sonra; child bitince parent fault'suz kalır, `complete`
ile `sp-completed`'a geçer.

## Pitfalls

- `subFlowVersion`'ın yeri: parent'ın state function cevabında `activeCorrelations[]`. Aynı alan
  instance okumasında farklı bir yolda olabilir; yol değişirse testin okuma yeri güncellenir.
- 1.0.3 yayını yapılmadan parent başlarsa çözümleme 1.0.0'a düşer; her test `EnsureChild103PublishedAsync`
  ile başlar.
- Çalıştırma: `dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~VersionPinLab"`.
