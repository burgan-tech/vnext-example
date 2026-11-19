# ViewLoadDataLab — geçiş view bağlamasının `loadData`'sı

State function'ın her `transitions[]` girdisindeki `view.loadData`, geçişin view bağlamasında beyan
edilen `loadData`'yı taşır. İstemci (`vnext-client-sdk-core` `transitionForm.ts` →
`loadData: !!t.view?.loadData`) formu yalnız akış yazarı istediğinde kayıt verisiyle doldurur.

Kaynak: burgan-tech/vnext-client-sdk-core#120 → burgan-tech/vnext#1266. Düzeltmeden önce subflow
dışındaki her geçiş `true` dönüyordu (beyan `false` olsa bile); subflow penceresinde parent'ın eklediği
paylaşılan geçiş hep `false` dönüyordu; çocuğun kendi girdileri, çocuğun state function'ı aynı kusurla
üretildiği için `true` taşıyordu.

## Kural

| Durum | `view.loadData` |
|---|---|
| Bağlama `loadData: false` / `true` beyan ediyor | beyan edilen değer |
| Bağlama `loadData` beyan etmiyor | yolun eski değeri: kendi geçişleri `true`, subflow penceresinde parent'ın eklediği geçiş `false` |
| Geçişin view'u yok (`hasView: false`) | `true` (değişmedi) |
| Birden çok girdi (`views[]`) | **ilk** girdinin beyanı — state function kural değerlendirmez; state'in kendi view linki de böyle |
| Subflow'un kendi girdisi | çocuğun beyanı (çocuğun state function'ından olduğu gibi) |

`StateFunctionCache.ResponseShapeVersion` v15: park etmiş long-poller düzeltilmiş gövdeyi bir kez 200
ile alır.

## Akışlar

```
view-load-data-lab        vld-review [Initial, Human]
                            vld-load-false       view loadData:false
                            vld-load-true        view loadData:true
                            vld-load-undeclared  view, loadData yok
                            vld-no-view          view yok
                            vld-multi            views[ {rule ilk-girdi → false}, {fallback → true} ]
                            vld-ask-sub ───────> vld-sub [SubFlow S → child]
                          shared vld-shared-note → $self, view loadData:true, availableIn [vld-review, vld-sub]
view-load-data-lab-child  child-review [Initial, Human]
                            child-load-false     view loadData:false
                            child-load-true      view loadData:true
```

Bileşenler `core/Workflows/view-load-data-lab/build-view-load-data-lab.py` ile üretilir. Değişiklikte
script'teki `VERSION` patch bump edilir — publish version-immutable.

## Testler

| Test | Kanıtladığı |
|---|---|
| `StateFunction_TransitionViewLinks_CarryTheBindingsLoadData` | `vld-load-false` → false, `vld-load-true` → true, beyansız → true, view'sız → `hasView:false`/true, `vld-multi` → false (ilk girdi), paylaşılan geçiş subflow dışında → true |
| `StateFunction_DuringSubFlow_ChildAndParentAddedEntriesCarryTheirBindingsLoadData` | subflow penceresinde `child-load-false` → false, `child-load-true` → true, parent'ın eklediği `vld-shared-note` (`kind: sharedTransition`) → true |

## Çalıştırma

```bash
cd ../vnext-example
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~ViewLoadDataLab" -v minimal
```

Lokal runtime (`VNEXT_BASE_URL`, `./run-docker.sh up core`) vnext#1266 düzeltmesiyle derlenmiş olmalı;
eski runtime'da iki test de kırmızıdır (ayırt edici koşu 2026-11-19).

## Tuzaklar

- Subflow'a geçişi `RunAcceptedAsync` ile değil `RunAsync` ile tetikle: SubFlow state'indeki parent
  çocuk yaşadıkça tasarım gereği Busy kalır.
- `vld-multi` runtime'ın okuduğu `views[]` biçimini kullanır. Yayımlanmış vnext-schema çoklu view'u
  `{ rules, default }` olarak tanımlıyor, bu yüzden `view-load-data-lab.json` `npm run validate`'ten
  düşer; SDK bileşenleri doğrudan publish ettiği için test engeli değildir.
- Beyansız bağlamanın `true` kalması bilinçlidir (vnext#1266 konseyi: beyansız varsayılan
  değiştirilmez). Platform genelinde varsayılanı değiştirmek ayrı bir ürün kararıdır.
