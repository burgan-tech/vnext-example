# declarative-mapping-lab — bildirimsel task mapping'leri (vnext#1229)

## Neyi denetliyor

**Tek iddia:** Bir task binding'i C# `IMapping` (`.csx`) yerine bildirimsel `input` / `output`
bloklarıyla (`select`, `into`, `set` — değerler Dynamic Expresso ifadesi) isteği şekillendirebilir ve
sonucu instance verisine yazabilir; publish bu blokları derleyip hatayı JSON konumuyla reddeder.

Runtime sözleşmesi: vnext `docs/runtime/declarative-task-mappings.md`. Şema: vnext-schema
`inputMappingBlock` / `outputMappingBlock` (`mapping` ile karşılıklı dışlayıcı).

## Akış

```
start-declarative-mapping-lab → ready (Initial)
   ├─ run-transition-mapping  onExecutionTasks: dml-http-echo  input + output  → transition-mapped (Finish)
   ├─ run-entry-mapping       (task'sız)                                       → entry-mapped (Finish)
   │                          entry-mapped.onEntries: dml-http-echo  output select $.body, into YOK
   └─ run-bad-cast            onExecutionTasks: dml-http-echo  output "gold".AsInt32()  → transition-mapped
```

`dml-http-echo` (Http, tip 6) MockLab `POST /api/dml/echo`'yu çağırır. Route istek gövdesindeki
`customerId`, `customer.name`, `source` alanlarını ve `x-channel` header'ını `echo` altında geri
döndürür, ayrıca sabit `score: 87`, `level: "gold"` verir. Böylece **yanıtın kendisi input bloğunun ne
yazdığını**, instance verisi de output bloğunun ne yazdığını kanıtlar.

## Beklenen sonuç

| Test | İddia |
| --- | --- |
| `TransitionTask_InputAndOutputBlocks_WriteTheExpectedData` | Input: `select $.data.customer into $.body.customer`, `set` ile `$.body.customerId` ve `$.headers.x-channel`; task'ın statik gövdesi (`source: static`) korunur. Output: `echo` alt ağacı `into $.dml.echo`, ardından `set`: `result.body.score.AsInt32() + 10` = 97, `result.statusCode` = 200, `instance.currentState` = `ready` (ChangeState'ten önce), `instance.key`, `now`, `user.available` = true (kullanıcı hop'u), `input.amount` = 5 (transition gövdesi). Eksik ara üye (`data.nope.deeper`) hiçbir şey yazmaz, hata da vermez. Instance `transition-mapped`'te `C` |
| `EntryTask_SelectWithoutInto_MergesAtTheDataRoot` | `onEntries` binding'i: `select $.body` + `into` yok → seçilen nesne verinin köküne merge olur (`score`, `level`, `echo` kökte); `instance.currentState` girişte `entry-mapped`; başlangıç verisi korunur |
| `OutputExpressionError_FaultsTheInstance_AndWritesNothing` | `"gold".AsInt32()` eksik değer değil hatadır: mapping başarısız olur, boundary olmadığı için instance `F` (vnext#1257); delta'nın hiçbir parçası yazılmaz |
| `Publish_RejectsInvalidBindings` (×2) | `mapping` + `output` birlikte → 400, mesaj `cannot carry both a 'mapping' script`; derlenmeyen ifade → 400, konum `output.set['$.dml.x']`. İkisinde de konum `states[ready].transitions[run-transition-mapping].onExecutionTasks[0]` |

## Dikkat noktaları

- **Task URL'i sabit** (`http://localhost:3001`, MockLab varsayılanı). Diğer lab'lar `API_BASEURL`
  yer tutucusunu C# InputHandler'da config'ten çözer; bildirimsel binding'in URL yüzeyi yok (input
  hedefleri yalnız `$.body` / `$.headers` / `$.query`). Bu yüzden senaryo host'ları **lokal binary**
  olarak koşan runtime ister (`run-docker.sh up core`); container modunda (`dev`/`stage`) çalışmaz.
- **MockLab şablonu null-güvenli olmalı.** Şablon, eksik bir nesnenin üyesini okursa
  (`request.body.customer.name`, `customer` yokken) MockLab `X-Mocklab-Template-Error` döndürüp
  gövdeyi ham şablon metni olarak yollar; HTTP task'ı bunu **string** olarak alır. O zaman
  `select $.body` (into'suz) "must select a JSON object" hatasıyla düşer, `result.body.level` ise
  eksik üye sayılıp **sessizce atlanır**. Seed bu yüzden `customerName`'i `{{ if … }}` ile korur.
- HTTP task'ı `query` yüzeyini desteklemez (yalnız `DaprService`); `$.query` burada denenmez.

## Nasıl çalıştırılır

Lokalde derlenen runtime'a karşı (container image eski kodu taşır), MockLab ayakta:

```bash
docker compose up -d     # repo kökünde, infra'dan SONRA; seed değiştiyse önce `docker compose down -v`
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~DeclarativeMappingLab" -v minimal
```

MockLab yoksa MockLab'e bağlı 3 test **skip** eder; publish testleri yine koşar. Bileşenleri SDK
`LocalDomainPublisher` publish eder. Şema: yayımlanmış vnext-schema bu blokları henüz tanımıyor;
`npm run validate` için lokal vnext-schema dalını bağla.

## Doğrulama durumu

✅ **5/5 yeşil** (2026-11-24, vnext `b1394d8c`, `claude/issue-1227-analysis-50e96f`). Postgres: transition
vakasında output tek bir `InstancesData` satırı (tek append); bad-cast instance'ı `F`, verisi yalnız
başlangıç verisi.
