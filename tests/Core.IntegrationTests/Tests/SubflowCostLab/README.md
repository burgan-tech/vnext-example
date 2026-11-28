# subflow-cost-lab — forced-sync transition into a SubFlow

## Neyi denetliyor

`subflow-cost-lab-forced-sync` akışında `f-init`'ten çıkan manuel `enter-sub` transition'ı tanımda
`executionType: S` taşır ve `f-sub` SubFlow state'ine girer (çocuk: `subflow-cost-lab-child`). Çocuk
hemen biter, output mapping parent'a `childCompleted: true` yazar, parent otomatik olarak `f-mid`'e
geçip Active kalır.

Tanım sync'i zorladığı için runtime isteği çağıran ne gönderirse göndersin senkron yürütür ve **200 +
zenginleştirilmiş gövde** ile cevaplar. Test bunu iki çağrı biçimiyle doğrular:

| Çağrı | Beklenen |
|---|---|
| `PATCH …/transitions/enter-sub?sync=false` | 200, `currentState: f-mid`, `attributes` içinde `fromStart`, `fromTransition`, `childCompleted` |
| `PATCH …/transitions/enter-sub?sync=true` | aynı |

## Neden var

SubFlow'a giren bir hop'un yanıtı, parent'ın post-commit settle'ının yeniden yüklediği instance'tan
üretiliyordu. Settle son veri satırını **çağıranın** moduna göre yükler
(`PostCommitParentMutationService.NeedsLatestDataForSettle`), etkin moda göre değil. `sync=false`
gönderen bir çağıran için satır yüklenmiyor, yanıt da bu instance'tan üretildiği için **200 ama
`attributes` yok** dönüyordu. `sync=true` çağıran doğru gövdeyi alıyordu. Düzeltme (vnext, subflow cost
stack 3/N): çağıran sync değilse yanıt satırı yeniden okur.

## Nasıl çalıştırılır

```bash
cd core/Workflows/subflow-cost-lab && python3 build-subflow-cost-lab.py   # JSON'ları üretir
wf domain use core && wf sync
VNEXT_BASE_URL=http://localhost:4201 dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~ForcedSyncSubflowResponseTests"
```

## Sonuç

2026-11-28, düzeltmeli yerel runtime: 2/2 yeşil. Düzeltmeden önceki build'de aynı şekil (manuel
`executionType: S` transition → aynı çocuk SubFlow → Active rest) elle çağrıldığında `sync=false` 200
döndü ama `attributes` boştu, `sync=true` doluydu.
