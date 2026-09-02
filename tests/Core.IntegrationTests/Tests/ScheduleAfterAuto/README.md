# schedule-after-auto — pipeline epilogue sıralaması (Auto → Schedule)

## Neyi denetliyor

**Tek iddia:** Bir state hem koşullu bir automatic transition hem de scheduled (timer) transition
taşıyorsa ve auto koşulu sağlanmışsa, runtime o state'in timer'ını **hiç arm etmez** — Dapr job
yok, `InstanceJob` satırı yok, dolayısıyla state function'ın `transitions` dizisinde o hop için
`kind: "scheduled"` girdisi **hiç görünmez**. Auto kazanan yoksa timer eskisi gibi arm edilir,
`executeAtUtc` ile listelenir ve süresi dolunca **gerçekten ateşlenip** instance'ı taşır.

Runtime karşılığı: `LifecycleOrder.Auto` (80) artık `LifecycleOrder.Schedule` (90)'dan **önce**
koşuyor ve `ScheduleTransitionsStep`, `context.Directives.NextTransition` doluysa
`StepOutcome.ContinueNoWork()` ile hiçbir şey arm etmeden çıkıyor.

## Neden var

vnext planı [`docs/superpowers/plans/2026-09-02-schedule-after-auto.md`](https://github.com/burgan-tech/vnext)
(2026-09-03 tarihinde koşuldu, vnext branch `feature/schedule-after-auto`).

Eski sıralamada `ScheduleTransitionsStep (80)` hedef state'in timer'larını arm ediyor, ardından
`RunAutomaticTransitionsStep (90)` auto koşullarını değerlendiriyordu. Auto kazandığında zincirlenen
hop kendi pipeline'ında `CancelScheduledJobsStep (39)` ile o timer'ları hemen siliyordu: auto'nun
kazandığı **her** hop'ta boşuna bir enqueue + persist + bir sonraki hop'ta cancel — yani Dapr
scheduler ve DB üzerinde saf churn.

**Bu senaryonun kritik tasarım detayı da buradan geliyor.** Churn'ün doğal sonucu olarak, iki
sıralama **dinlenme durumunda ayırt edilemez**: eskisi de arm-edip-siliyordu, yenisi hiç arm
etmiyor; ikisinde de sonuçta armed job yok. Bu yüzden `auto-advance`'ın `onExecute` task'ı
(`AutoAdvanceMapping.csx`) **bilinçli olarak ~2.5 saniye** sürer: zincirlenen hop'u, cancel adımı
(39) gelmeden önce açık tutar. Test bu pencere boyunca state function'ı poll'lar ve "armed girdi
**hiçbir an** görülmedi" iddiasını kurar — eski sıralamanın kıracağı iddia budur. Delay'i
kaldırırsanız yol 1 sessizce **totolojiye** düşer.

## Akış şeması

```
start-schedule-after-auto  (onExecute: SeedMapping → mode, autoAdvances=0, timeoutFired=0)
        │
        ▼
   ┌──────────────────────────── gate (Initial) ────────────────────────────┐
   │  auto-advance   (triggerType 1)  rule: AutoGateRule → mode == "auto"   │
   │                                  onExecute: AutoAdvanceMapping (~2.5s) │
   │  gate-timeout   (triggerType 2)  timer: GateTimeoutTimer → 8 sn        │
   │                                  onExecute: TimeoutMapping             │
   └───────────────────────────────────────────────────────────────────────-┘
        │ mode = "auto"                              │ mode = "park"
        │ (auto kazanır, timer ARM EDİLMEZ)          │ (auto kazanmaz, timer arm edilir)
        ▼                                            ▼  8 sn sonra ateşlenir
   advanced (Intermediate, Active'de park)      gate-timedout (Finish, subType 8)
        │ finish-advanced (manual)
        ▼
   advanced-done (Finish)
```

Kritik adımlar:

- **`gate` tek state olarak hem auto hem scheduled taşır** — senaryonun bütün anlamı bu
  birlikteliktedir; birini kaldırmak testi anlamsız kılar.
- **`advanced` bilinçli olarak Finish DEĞİL** ve timer taşımaz: tamamlanmış bir instance'ın
  transition listesi davranışla ilgisiz sebeplerle boşalır, gözlem noktası körelir.
- **`gate` üzerinde onEntry/onExit hook'u yok**: start transition'ının `onExecute`'u ile aynı task
  key + aynı order çakışması (`task-journal ExecutionKey = (transition, task, order)`) böylece
  yapısal olarak imkânsız.

## Nasıl çalıştırılır

Bu senaryo **lokalde derlenen runtime'a** karşı koşulmalıdır — davranış henüz release edilmedi,
container image eski kodu taşır.

1. vNext çalışma alanında altyapı (ayakta değilse):
   ```bash
   cd etc/docker && ./run-docker.sh
   ```
2. Dört app, her biri ayrı terminalde ve **mutlaka** `--launch-profile http` ile:
   ```bash
   dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http  # 4201
   dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host --launch-profile http          # 4202
   dotnet run --project workers/BBT.Workflow.Workers.Inbox --launch-profile http
   dotnet run --project workers/BBT.Workflow.Workers.Outbox --launch-profile http
   ```
   Migration varsa bir kez: `dotnet run --project workers/BBT.Workflow.DbMigrator --launch-profile DbMigrator`.
3. `tests/Core.IntegrationTests/test.runsettings` içinde `VNEXT_BASE_URL` **açık** olmalı
   (repoda halihazırda `http://localhost:4201` olarak commit'li). Set edilmezse SDK Testcontainers
   ile kendi image'lı stack'ini kaldırır — geliştirme testinde istemediğimiz şey tam olarak budur.
4. Testler (bileşenleri SDK `LocalDomainPublisher` kendisi publish eder, ekstra adım yok):
   ```bash
   dotnet test tests/Core.IntegrationTests \
     --settings tests/Core.IntegrationTests/test.runsettings \
     --filter "FullyQualifiedName~ScheduleAfterAuto" -v minimal
   ```

Akış JSON'ı `.csx` kaynaklarından üretilir; base64 blob'ları elle düzenlemeyin:

```bash
python3 core/Workflows/schedule-after-auto/build-schedule-after-auto.py
```

MockLab gerekmez — akış yalnız script task (type 7) kullanır, HTTP task yoktur.

## Beklenen sonuç / başarı kriteri

| Test | İddia |
| --- | --- |
| `AutoWinner_SuppressesTimerArming_AndTheTimerNeverFires` | `mode: "auto"` ile başlatılan instance `advanced`'e zincirlenir; zincir boyunca (≥2 poll) `gate-timeout` için **hiç** `kind: "scheduled"` girdisi görülmez; `autoAdvances == 1`; timer süresi (8 sn) + 5 sn geçtikten sonra da instance `advanced`'de, `timeoutFired == 0` ve armed girdi yok |
| `NoAutoWinner_ArmsTheScheduledTransition_AndItFires` | `mode: "park"` ile başlatılan instance `gate`'te bekler; state function `gate-timeout` girdisini makul bir `executeAtUtc` ile gösterir; timer ateşlenir, instance `gate-timedout`'a geçer, `timeoutFired == 1`, `autoAdvances == 0` |

Doğrulama durumu: **2/2 yeşil** (2026-09-03, lokal runtime, vnext `feature/schedule-after-auto`
@ `702a03b6`), iki koşu üst üste, ~24 sn.

### Bilinen kısıtlar

- **Yol 1'in "arm hiç olmadı" iddiası pencere-bağımlıdır.** Delay'e dayanır; `probes >= 2`
  assertion'ı pencerenin kapanmadığını bekçiler ama makine aşırı yüklüyse iddia zayıflar
  (yanlış-yeşil yönünde, yanlış-kırmızı yönünde değil).
- **Guard'ın kendi log'u gözlenmiyor.** `ScheduledTransitionsSkippedForChainedNext` Debug
  seviyesindedir; app'ler Information ile koşarken log'a düşmez. Doğrulama tümüyle davranış
  üzerinden yapılır.
- **`InstanceJob` satırı doğrudan sorgulanmıyor** — SDK'da job sorgu yüzeyi yok; gözlem noktası
  state function'ın `kind: "scheduled"` girdileridir (public API, DB erişimi gerekmez).
- Job-set değişiklikleri fingerprint ETag'e bilinçli olarak katılmaz (vnext issue #864), bu yüzden
  armed girdi gözlemi poll ile yapılır, ETag/304 davranışına yaslanmaz.
