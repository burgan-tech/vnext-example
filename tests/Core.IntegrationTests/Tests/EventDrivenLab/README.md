# event-driven-lab

Olay tetiklemeli akış: instance bir broker mesajıyla **doğar**, ikinci bir mesajla **ilerler**.
Testin hiçbir adımı akışı sürmek için runtime'ın HTTP API'sini çağırmaz.

## Neden eklendi

`POST .../instances/events` ucunun bu senaryodan önce **hiçbir entegrasyon testi yoktu** — agent
council `2026-09-11-route-trace-coverage` bunu adı konmuş bir boşluk olarak kaydetmişti. Uca
doğrudan POST atan bir test yalnız mapping script'ini kanıtlardı; üretim trafiğini gerçekten taşıyan
tesisatı (topic, subscription route'u, CloudEvent açma, `triggerType: 3` kapısı) kanıtlamazdı.
Bu yüzden iki mesaj da broker'a yayınlanır.

İkincil amaç: `Event.Intake` span'i bu ucu ölçüyor ve "testi olmayan route'a span gönderilmez"
kuralı gereği span ile senaryo aynı commit'te gelir.

## Yapılandırma

Doküman: https://burgan-tech.github.io/vnext-docs/docs/how-to/event-driven-workflows

| Parça | Yer |
| --- | --- |
| Akış-seviyesi `event.mapping` (action=start) | `core/Workflows/event-driven-lab/src/StartEventMapping.csx` |
| `triggerType: 3` geçiş + kendi mapping'i | `approve-by-event`, `src/ApproveEventMapping.csx` |
| Subscription'lar | `tests/.../DaprComponents/orchestration/subscription-event-driven-lab-*.yaml` |

Topic'ler: `core.event-driven-lab` (start) ve `core.event-driven-lab.approve` (transition).
Approve subscription'ı `sync=true` taşır — teslimat pipeline dinlenme noktasına ulaşana kadar
bloklar, böylece test belirsiz bir pencere boyunca yoklamak yerine hemen doğrulayabilir.

`InstanceKey` korelasyon sözleşmesidir: start mapping'i onu payload'daki `orderId`'den üretir,
approve mapping'i aynı değerle **aktif** instance'ı bulur. Yayınlanan mesajda instance id yoktur.

## Neyi pinler

1. Instance olayla doğar ve `startedBy = "event"` verisiyle `awaiting-approval`'da bekler.
2. İkinci olay onu `approved`'a taşır **ve mapping'lenmiş gövdeyi yazar** — yalnız state kontrol eden
   bir assertion, payload'ı düşüren bir olayı yakalayamazdı.
3. Aktif instance'ı olmayan bir onay olayı **ack'lenir**, yeniden denenmez. Uç broker'ın protokolünü
   yanıtlar; "eşleşme yok" bir hata gibi görünürse broker asla karşılanamayacak bir mesajı sonsuza
   kadar yeniden teslim eder.

## Çalıştırma

Ayakta olan bir stack'e karşı:

```bash
VNEXT_BASE_URL=http://localhost:4201 VNEXT_DAPR_HTTP_URL=http://localhost:42110 \
  dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~EventDrivenLab"
```

`VNEXT_DAPR_HTTP_URL` orkestrasyon sidecar'ının Dapr HTTP portudur (domain başına
`ai-docs/local-environments/<domain>.md` içinde yazılı), çünkü abonelikleri test edilen sidecar odur.

## Bilinen tuzak

**Dapr declarative subscription'ları YALNIZ açılışta okur.** Subscription YAML'ı sidecar ayaktayken
eklerseniz mesaj sessizce hiçbir yere gitmez — hata da log da yok. `docker restart
vnext-orchestration-dapr` ve log'da `Found Subscription: …` satırını görün.
