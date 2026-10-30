# auto-priority-lab — automatic transition `priority` sırası (vnext#1133)

## Neyi denetliyor

**Tek iddia:** Bir state birden çok automatic transition taşıyorsa runtime bunları **`priority`
artan sırasıyla** değerlendirir (küçük önce; alan yoksa 100; eşitte bildirim sırası korunur) ve
`DefaultAutoTransition` (`triggerKind: 10`, kuralsız, `priority` taşımaz) priority'den bağımsız
olarak **her zaman en son** değerlendirilir.

## Neden var

Önceden automatic transition'lar bildirim sırasıyla değerlendiriliyordu; iki kural birden doğruysa
kazanan, akış yazarının JSON dizisindeki sıraya bağlıydı. `priority` bu sırayı açıkça ifade eder.
Transition değerlendirmesi çekirdek süreçtir, bu yüzden uçtan uca pinlenir.

## Akış

```
start-auto-priority-lab
        │
        ▼
   evaluate (Initial) — bildirim sırası ≠ priority sırası
        ├─ to-low       priority 30   rule: always true               → low
        ├─ to-high      priority 10   rule: true (skipHigh ise false) → high
        └─ to-fallback  default (triggerKind 10, priority YOK)        → fallback
```

`low`, `high`, `fallback` Finish state'leridir. Akış JSON'ı `.csx` kaynaklarından üretilir:

```bash
python3 core/Workflows/auto-priority-lab/build-auto-priority-lab.py
```

## Beklenen sonuç

| Test | İddia |
| --- | --- |
| `BothRulesTrue_HigherPriorityWinsDespiteDeclarationOrder` | İki kural da doğru: instance `high`'da `Completed`; history'de `to-high` var, `to-fallback`/`fallback` yok. `priority` yok sayılsa `low`'da biterdi |
| `HigherPriorityRuleFalse_NextPriorityWins` | `skipHigh: true`: `to-high` false döner, instance `low`'da `Completed`; history'de `to-low` var, `fallback` yok |

`fallback` her zaman doğru olduğundan, default transition ilk değerlendirilseydi iki test de
`fallback`'te biterdi; bu yüzden "default her zaman en son" iddiası her iki testin
"`fallback` görülmedi" assert'iyle de pinlenir.

## Nasıl çalıştırılır

Bu senaryo **lokalde derlenen runtime'a** karşı koşulmalıdır (container image eski kodu taşır).
Ayrıntı: [`ScheduleAfterAuto/README.md`](../ScheduleAfterAuto/README.md) § Nasıl çalıştırılır ve
`TEST-SCENARIOS.md` § Koşum Öncesi Kontrol.

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~AutoPriorityLab" -v minimal
```

MockLab gerekmez; akış hiç task kullanmaz. Bileşenleri SDK `LocalDomainPublisher` publish eder.

## Doğrulama durumu

2026-10-29, lokal `run-docker.sh up core` runtime'ı:

- **Bu dal** (vnext `claude/auto-transition-priority-order-f8cc92` @ `371c4655`): 2/2 yeşil.
  Komşular ScheduleAfterAuto + MoneyTransfer + SubflowOrchestration + ImplicitStartLab 36/36
  (MoneyTransfer MockLab ayaktayken).
- **Master tabanı** (`3d922fa8`): `BothRulesTrue_…` kırmızı, instance `low`'da `Completed`
  (`priority` yok sayılıyor, dizi sırası kazanıyor); `HigherPriorityRuleFalse_…` yeşil.
- Postgres `auto_priority_lab."InstanceTransitions"`: bu dalda auto hop `to-high` (iki kural doğru)
  ve `to-low` (`skipHigh`); master'da iki instance da `to-low`. `to-fallback` hiçbir koşuda yok.

## Bilinen kısıtlar

- `npm run validate` yayımlanmış `@burgan-tech/vnext-schema` `priority` alanını bilene kadar
  lokal bağlı şemayla doğrulanır.
