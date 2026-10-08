# HistoryNoneSubflowLab — integration test

`history: none` parent'ın SubFlow kuralı (vnext#1006): `none` parent yalnız `none` bir `S` child
başlatabilir; `full` parent `none` child başlatabilir. Domain `core`, yalnız script task'lar.

| Test | Neyi denetliyor |
|---|---|
| `NoneParent_NoneChild_Completes_AndTheHandoffCarriesTheParentData` | `hns-parent` tamamlanır; `parentToken` devir anına kadar yalnız parent'ın tamponundaydı, child'ın input mapping'i parent'ı veritabanından okur — `childResult == parentToken` **devir yazımının** kanıtı. Parent geçmişi boş |
| `NoneParent_FullChild_FaultsTheParent` | `hns-parent-bad` → `hns-child-full` başlamayı reddeder (`Instance:100044`), parent `hns-sub`'da Faulted + incident. Parent `sync=false` başlatılır |
| `FullParent_NoneChild_Completes` | `hns-parent-full` → `hns-child` (none) tamamlanır, parent geçmiş yazar |

## Neden var

`none` akış veriyi bellekte tutar; SubFlow devri aşamayı bitirir ve child'ın input mapping'i, output
mapping ve resume parent'ı veritabanından yeniden yükler. Bu yüzden devirde yazım şart, ve `none`
parent'ın tam geçmişli bir child ile karışmaması runtime'da (child start'ında) zorlanır.

## Akış şekli

`core/Workflows/history-none-subflow-lab/`, `build-history-none-subflow-lab.py` ile üretilir:

```
hns-parent (none):   start → hns-prepare [parentToken] ─auto→ hns-sub (S: hns-child) ─auto→ hns-done
hns-child (S, none): start → hns-child-work [childEcho = parentToken] ─auto→ hns-child-done
```

## Nasıl koşulur

```bash
VNEXT_BASE_URL=http://localhost:4201 dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~HistoryNoneSubflowLab"
```

## Geçme kriteri + kanıt

```sql
-- hns-parent: veri ≤ 3 (1 + 2·N, N = 1), transition 0; hns-child: veri 1
SELECT count(*) FROM hns_parent."InstancesData"       WHERE "InstanceId" = '<parent-id>';
SELECT count(*) FROM hns_parent."InstanceTransitions" WHERE "InstanceId" = '<parent-id>';
SELECT count(*) FROM hns_child."InstancesData"        WHERE "InstanceId" = '<child-id>';
-- reddedilen child hiç yaratılmaz
SELECT count(*) FROM hns_child_full."Instances";
```
