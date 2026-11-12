# caller-roles-lab — `context.Caller` in automatic rules, timers and SubFlow output mappings

## Neyi denetliyor

vnext `script-caller-roles` (konsey `2026-11-12-script-caller-roles-background`, Chair override): çağıranın
**runtime'ın çözdüğü** rolleri seçim kurallarının dışında üç yüzeyde de okunabilir:

| Yüzey | Kimin rolü | Provider hatası |
| --- | --- | --- |
| Automatic-transition `rule` | hop'u yürüten çağıran (istek, async job, inline auto zinciri); System hop'larında yok | adım `Authorization:110006` ile düşer, **DefaultAutoTransition alınmaz** |
| Timer script'i | state'e GİRİLEN hop'un çağıranı (arming anı); tetiklenen timer'ın yeniden kurulmasında yok | adım `110006` ile düşer |
| SubFlow output mapping | child'ı sonlandıran (taşınan header'lar), parent'ı başlatan değil | completion yeniden teslim edilir, parent **fault olmaz** |

## Akışlar

```
caller-roles-auto-lab    start → gate ─ to-approved (rule: rule-schema.approver, priority 10) → approved
                                      └ to-standard (DefaultAutoTransition, triggerKind 10)  → standard
caller-roles-timer-lab   start → wait ─ expire (timer: approver 48 sa, diğer 24 sa; IsAvailable guard) → expired
caller-roles-parent-lab  start → parent-wait (SubFlow S: caller-roles-child-lab, output mapping
                                 settledByApprover = Caller.HasRoleAsync(approver)) ─ auto → parent-done
caller-roles-child-lab   start → child-review ─ child-complete → child-done
```

Bileşenler `.csx`'ten üretilir: `python3 core/Workflows/caller-roles-lab/build-caller-roles-lab.py`.

## Beklenen sonuç

| Test | İddia |
| --- | --- |
| `AutoRule_Approver_TakesTheRoleRule` | `role: rule-schema.approver` ile start → `approved`/`C` |
| `AutoRule_OtherRole_TakesTheDefaultAutoTransition` | başka rol → `standard`/`C` |
| `AutoRule_MorphIdmApprover_TakesTheRoleRule` (morph-idm) | rol header'ı yok, IDM approver → `approved` |
| `AutoRule_ProviderDown_NeverFallsThroughToTheDefault` (morph-idm) | `idm-broken` → `standard`'a **düşmez** |
| `Timer_Approver_ArmsTheLongWindow` / `Timer_OtherRole_ArmsTheShortWindow` | state fonksiyonundaki `executeAtUtc` ≈ şimdi + 48 / 24 sa |
| `SubFlowOutputMapping_SeesTheChildSettlersRole` (2 vaka) | child'ı approver tamamlarsa parent verisinde `settledByApprover = true`, viewer ise `false` |
| `SubFlowOutputMapping_ProviderDown_ParentIsNotFaulted` (morph-idm) | `idm-broken` ile tamamlanan child: 15 sn sonra parent `parent-wait`, `F` değil, çıktı persist edilmemiş |

Header testleri iki provider'da da koşar; morph-idm testleri `VNEXT_CALLER_ROLE_PROVIDER=morph-idm` ve runtime
`CallerRoleProvider__Provider=morph-idm` + MockLab `morph-idm-roles-collection` ister (komut:
[`RuleSchemaLab/README.md`](../RuleSchemaLab/README.md) § Çağıran rolleriyle seçim).

## Ek ölçüm (konsey koşulu C-3)

IDM gecikmesi 2 sn iken `caller-roles-auto-lab` start'ları sırasında `pg_stat_activity`'de
`idle in transaction` = 0 olmalı (auto adımı Busy iken transaction tutmaz).

## Doğrulama durumu

⏳ **Henüz koşulmadı** (2026-11-12). Akışlar `npm run validate`'ten lokal bağlı vnext-schema#169 ile geçti; testler
derleniyor. Koşum ve C-3 ölçümü vnext#1298 birleşmeden önce yapılacak.
