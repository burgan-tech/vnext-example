# display-labels-lab — okuma yüzeylerinde görünen metin (labels) ve hedef state

## Neyi denetliyor

**Tek iddia:** Client'ın çizdiği her okuma yüzeyi, anlattığı şeyin görünen metnini tanımdaki
biçimiyle (`[{ language, label }]`, iki dil de, caller'a göre tek dile indirgenmeden) taşır. Client
ne iç anahtar gösterir ne de anahtarı bulmak için workflow tanımını okur.

- **state function:** `stateType` / `stateSubType` / `stateLabels` (gösterilen state); her
  `transitions[]` girdisinde `labels` ve `target` nesnesi `{ key, stateType, stateSubType, labels, subFlow }`
  (`$self` çözülmüş); `timeout.target` aynı nesne.
- **view / schema / master:** bileşenin `labels`'ı (schema'da `attributes.labels`).
- **catalog:** her `functions[]` girdisinde fonksiyon bileşeninin `labels`'ı.
- **subflow penceresi:** üst akış poll edilirken gövde ALT akışın state'ini anlatır (`stateSubType`,
  `stateLabels`) ve alt akışın transition girdilerini alt akışın tarif ettiği gibi geçirir.

## Neden var

burgan-tech/vnext-client-sdk-core#43: `transitions[]` girdisi `labels` ve hedef state taşımıyordu,
geçiş listesi kullanıcıya `to-3` / `sub-done` gibi anahtar gösteriyordu; client tanımı ayrıca okuyup
eşleme tablosu kuruyordu (`workflowLabels.ts`). Aynı iş view/schema/master/catalog'a ve timeout
bloğuna genişletildi. Schema ve function bileşenlerinin `attributes.labels`'ı vnext-schema'da hep
opsiyonel olarak vardı ama runtime yüklerken düşürüyordu. vnext branch
`feature/transition-labels-target` (`ResponseShapeVersion` v13→v14, master/schema cache şekil
sürümü v2). Tarih: 2026-10-03.

## Akış şekli

```
display-labels-lab        dl-review [Initial, Human] --dl-approve (schema)--> dl-approved [Finish, Success]
                                                     --dl-ask-sub---------> dl-sub [SubFlow S → child]
                          shared dl-note → $self (availableIn: dl-review)
                          timeout dl-abandoned PT10M → dl-expired [Finish, Timeout]  (koşuda hiç ateşlenmez)
                          master schema, state view, function til-cached-echo (zaten etiketli)
display-labels-lab-child  child-review [Initial, Human] --child-done--> child-finished [Finish, Success]
```

Bileşenler `core/Workflows/display-labels-lab/build-display-labels-lab.py` ile üretilir (tek script
SubFlow mapping'i, şema `subFlow.mapping`'i zorunlu tutuyor). Değişiklikte script'teki `VERSION`
patch bump edilir — publish version-immutable.

Yayımlanmış vnext-schema (0.0.52) tam bir Initial state ve transition'larda en az bir label
istiyor; bu yüzden inceleme state'leri Initial ve her transition etiketli. Etiketsiz durumun
(alanın JSON'a hiç yazılmaması) kanıtı vnext unit testlerinde.

## Testler

| Test | Kanıtladığı |
|---|---|
| `StateFunction_DescribesTheCurrentStateTransitionsAndTargets` | `stateType`=`initial`, `stateSubType`=`human`, `stateLabels`; `dl-approve` hedefi `finish`/`success`+labels; `dl-ask-sub` hedefi `subFlow` + `subFlow: display-labels-lab-child`; `dl-note` `$self` → `dl-review`; `timeout.target` nesne, `finish`/`timeout`+labels |
| `LinkedFunctions_ReturnTheirComponentsLabels` | state gövdesindeki href'ler izlenerek view, transition schema, master ve catalog yanıtlarının bileşen `labels`'ı |
| `StateFunction_DuringSubFlow_DescribesTheChildsState` | subflow penceresinde gövde `child-review`/`human`/child labels; `child-done` girdisinin labels'ı ve hedefi alt akışın tarifiyle |

## Çalıştırma

```bash
cd ../vnext-example
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~DisplayLabelsLab" -v minimal
```

Lokal runtime (`VNEXT_BASE_URL`, `./run-docker.sh up core`) vnext `feature/transition-labels-target`
ile derlenmiş olmalı; eski runtime'da alanlar yoktur ve testler kırmızıdır.

## Tuzaklar

- Subflow'a geçişi `RunAcceptedAsync` ile değil `RunAsync` ile tetikle: SubFlow state'indeki üst akış
  alt akış yaşadıkça tasarım gereği Busy kalır, "Busy bitene kadar bekle" alt akışın bitmesini bekler.
- Link izlerken yalnız `http(s)` ile başlayan href'i mutlak say: Unix'te `/api/...` da `file://` URI
  olarak parse edilir ve `?` `%3F`'ye kaçar (404).
