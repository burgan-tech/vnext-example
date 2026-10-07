# HistoryNoneLab — integration test

`attributes.history: "none"` (vnext#1006) ile işaretli tek seferlik (one-shot) akışlar. Domain `core`,
yalnız script task'lar — MockLab gerekmez.

| Test | Neyi denetliyor |
|---|---|
| `OneShot_CompletesOnBufferedData_AndWritesNoHistory` (×2) | `hn-oneshot` Finish'e ulaşır; seed, `variableKey` ile aynı order'da iki paralel slot ve onları okuyan özet, kural tabanlı rota — hepsi **bellekteki tampondan** okunur. `transitions` geçmişi ve `functions/tasks` boş 200 |
| `FullHistoryControl_StillWritesHistory` | Aynı şekil `history` olmadan (`hn-full-control`) geçmiş yazmaya devam eder |
| `RestAtNonFinishState_FaultsWithNotTerminalIncident_AndKeepsTheData` | Hiçbir otomatik kuralı tutmayan `hn-gate` → instance Faulted, incident `Instance:100046`, start verisi yazılmış |
| `TaskFault_WritesTheBufferedData_AndRetryIs409` | Abort boundary'li task hatası → Faulted, veri yazılmış; `POST …/retry` → 409 `Instance:100045` |
| `Publish_RejectsAShapeThatIsNotOneShot` (×14) | Manual/scheduled/event transition, shared, cancel/exit/updateData, timeout, longPoll, Wizard, Human, Finish yok, transition'sız state, döngü → 400 |
| `Publish_RejectsAnUnknownHistoryValue` / `Publish_AcceptsAWorkflowLevelEventStart` | Geçersiz değer 400; workflow seviyesi `event` start kabul |

## Neden var

Sık çalışan ve sonradan bakılmayan akışlar uzun yaşayan süreçlerle aynı depolama bedelini
ödüyordu (her append'te tam `InstancesData` satırı, her geçişte `InstanceTransitions`, task journal).
vnext#1006 (2026-10-07) `history: none` ile bu geçmişi kaldırır; veri tamponlanır ve tek satır yazılır.

## Akış şekli

`core/Workflows/history-none-lab/`, `build-history-none-lab.py` ile `./src/*.csx`'ten üretilir:

```
hn-oneshot:  start → hn-collect [o1 seed · o2 left‖right (variableKey) · o3 summary] ─auto→ hn-route ─rule→ hn-done-high | hn-done-low
hn-stuck:    start → hn-gate ─(iki kural da false)→ … (Finish'e ulaşılamaz → fault)
hn-fault:    start → hn-boom [o1 throw] → fault
```

## Nasıl koşulur

```bash
VNEXT_BASE_URL=http://localhost:4201 dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~HistoryNoneLab"
```

## Geçme kriteri + kanıt

Testlerin yeşili tek başına kanıt değildir. Postgres (MCP) ile satır sayıları:

```sql
-- hn-oneshot instance'ı: 1 / 0 / 0 beklenir
SELECT count(*) FROM hn_oneshot."InstancesData"      WHERE "InstanceId" = '<id>';
SELECT count(*) FROM hn_oneshot."InstanceTransitions" WHERE "InstanceId" = '<id>';
SELECT count(*) FROM hn_oneshot."InstanceTasks";
-- kontrol: hn-full-control için transitions > 0
SELECT count(*) FROM hn_full_control."InstanceTransitions" WHERE "InstanceId" = '<id>';
```

Trace (OpenObserve / Elasticsearch) erişilebilir değilse sonuç "trace'e karşı doğrulanmadı" olarak etiketlenir.
