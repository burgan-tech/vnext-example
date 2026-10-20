# busy-subtype-armed-timer — interaction wait: updateData kapısı ve zaman aşımı

## Neyi denetliyor

**Interaction wait** = `subType: 5` (Busy) olan **ve** `interaction` tanımlayan bir state. Yazar, client'ı
yönlendirmek için instance'ı orada bilerek Busy'de bekletir ve dışarıdan bir tetik — veri ile state'in
otomatik transition'ını açan bir `updateData` — bekler. O Busy'nin **sahibi yoktur**. İki çıkış yolu var:

1. **Tetik gelir.** updateData'nın verisi otomatik transition'ı sağlar; continuation instance'ı
   rezerve etmeye çalışır, Busy'de sahip bulamaz ve **devralır** — otomatik transition çalışır.
   Kurulu ama vakti gelmemiş zaman aşımı sahip **sayılmaz**.
2. **Tetik gelmez.** State'in zamanlanmış transition'ı ateşlenir, Busy ön kontrolünü atlar ve park
   edilmiş Busy'yi **devralır** — instance bekleme state'inden çıkar.

Kapsam sınırı: `interaction` **taşımayan** Busy alt-tipli bir state'te zaman aşımı eskisi gibi
"busy" diye reddedilir ve instance park halinde kalır. Bu bilinçli olarak test ediliyor.

Runtime karşılığı: `InstanceJob.IsLiveTransitionOwner` (yalnız vakti gelmiş timer sahip),
`State.IsInteractionWait`, `TransitionPipeline.IsInteractionWaitTimerAsync`; log
`ScheduledTransitionTookOverInteractionWait` (EventId 10181).

## Neden var

QA ortamında `vnext-digital-investment` / `international-account-process-subflow` akışı
(2026-10-20) `contract-initial-wait` state'inde takıldı: state'e girildikten 2 sn sonra gelen contract
callback'inin updateData'sı `intl-contract-is-ready` kapısını açtı, ama runtime state'in **2 saat
sonrasına** kurulu `intl-contract-timeout` timer'ını Busy'nin sahibi sandı ve continuation'ı düşürdü
(`UpdateData … dropped its auto-transition continuation intl-contract-is-ready: Instance:100031`).
Senaryonun kontrol testi ikinci kusuru buldu: o timer ateşlendiğinde de "busy" diye reddediliyor,
job tüketiliyor ve instance **kalıcı** olarak park halinde kalıyordu.

## Akış şeması

```
start (SeedMapping: timerSeconds, contractReady=false, sayaçlar=0)
   │
   ▼
 route (Initial) ── go-interaction ──▶ contract-wait  (Busy subtype, interaction terminate:false)
              ├──── go-ack ─────────▶ ack-wait       (Busy subtype, interaction terminate:true)
              └──── go-plain ───────▶ plain-wait     (Busy subtype, interaction YOK)

 her bekleme state'i:
   <x>-ready    (auto, rule: contractReady == true) ──▶ contract-signed ── finish-signed ──▶ signed-done
   <x>-timeout  (scheduled, timerSeconds)           ──▶ contract-timedout (Finish, subType 8)

 updateData: update-contract-data ($self) — body.contractReady → instance data
```

- **`route` manuel transition'larla seçer**: yalnız manuel hop step 75'te (long-poll terminate) durur;
  AutoChain profili o adımı hariç tutar, otomatik bir yönlendirme `ack-wait`'i hiç bekletmezdi.
- **`contract-signed` Finish değil ve timer taşımaz**: instance state'ten çıktıktan sonra bekleme
  state'inin timer'ının iptal kaldığı ve ateşlenmediği gözlenebilsin diye.
- Akış JSON'ı `.csx` kaynaklarından üretilir — base64'ü elle düzenlemeyin:
  `python3 core/Workflows/busy-subtype-armed-timer/build-busy-subtype-armed-timer.py`

## Nasıl çalıştırılır

Lokal derlenen runtime'a karşı (`run-docker.sh up core`, vnext ≥ bu düzeltmeyi içeren commit).
MockLab gerekmez — yalnız script task.

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~BusySubtypeArmedTimer" -v minimal
```

## Beklenen sonuç / başarı kriteri

| Test | İddia |
| --- | --- |
| `UpdateData_InInteractionWait_AdvancesThroughTheAutoGate_BeforeTheTimer` | QA reproduksiyonu: `contract-wait`'te Busy + kurulu 30 sn timer; `contractReady=true` updateData'sı 20 sn içinde `contract-signed`'a taşır, `readyAdvances=1`, `timeoutFired=0`, status `A`, scheduled girdi yok; timer süresi + 5 sn sonra da `timeoutFired=0` |
| `UpdateData_ThatLeavesTheGateClosed_KeepsTheInstanceWaitingWithItsTimer` | `contractReady=false`: instance `contract-wait`/`B`'de kalır, timer kurulu kalır, fault yok |
| `Timer_InInteractionWait_TakesTheInstanceOut_WhenNoUpdateDataArrives` | 6 sn timer ateşlenir, park edilmiş Busy'yi devralır: `contract-timedout`/`C`, `timeoutFired=1` |
| `Timer_AfterTheLongPollAck_InInteractionWait_TakesTheInstanceOut` | `ack-wait` ack'e kadar timer kurmaz; ack sonrası instance `ack-wait`/`B`'de dinlenir; 15 sn timer onu `contract-timedout`'a taşır, ack'ten ≥13 sn sonra (120 sn'lik ack fallback'i değil) |
| `Timer_InBusySubtypeWithoutInteraction_IsRefused_AndTheInstanceStaysParked` | Kapsam sınırı: `plain-wait`'te 6 sn timer "busy" diye reddedilir; süre + 10 sn sonra instance `plain-wait`/`B`, `timeoutFired=0` |

Doğrulama durumu: **5/5 yeşil** (2026-10-20, lokal runtime, vnext
`claude/instance-busy-subtype-resolve-skip-47ce3a`). Host log'unda 9 adet
`Scheduled transition … took over the parked Busy … in interaction wait …` (6 `contract-wait`,
3 `ack-wait`), hiç `dropped its auto-transition continuation` yok. `plain-wait` için log:
`Job … plain-timeout failed … Instance is busy` (beklenen). Trace doğrulaması yapılmadı
(OpenObserve/Elasticsearch MCP'leri kapalıydı); kanıt host log'u + postgres `InstanceJobs`.

### Bilinen kısıtlar

- **Kurulu timer state function'dan güvenilir gözlenemez.** Job kümesi değişiklikleri state
  fingerprint'ine girmez (vnext #864): state/status değişmeden kurulan (ack sonrası) ya da tüketilen
  (`plain-wait` reddi) timer, cache'lenmiş gövdede eski haliyle görünebilir. Bu iki test bu yüzden
  timer'ı gözlemez, saate ve sonuca bakar.
- **`plain-wait` testi bugünkü kapsamı sabitler**, bir hedefi değil: interaction'sız Busy alt-tipte
  timer reddedilip job tüketiliyor ve instance kalıcı park kalıyor. Kapsam genişletilirse bu test
  bilinçli olarak çevrilmelidir.
