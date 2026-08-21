# Tests/FanOut — FanOutTask (TaskType 21) integration testleri

Senaryonun tam anlatımı (akış şeması, neden var, MockLab seed'i, yük testi, eşikler):
[`api-tests/fan-out-documents/README.md`](../../../../api-tests/fan-out-documents/README.md).
Burada yalnızca test sınıfının kendi sözleşmesi var.

## Testler

| Test | İddia |
| --- | --- |
| `FiveDocuments_AllSucceed_ReachCompletedWithTheFullResultSet` | 5 doküman, hepsi başarılı; `documentResults` 5 satır, **index sırasında**; `{resultKey}Summary` = `{5,5,0,false}`; akış `documents-completed`. |
| `TheWholeBatch_ProducesExactlyOneInstanceDataVersion` | Batch **tek** InstanceData sürümü üretti. |
| `TheWholeBatch_ProducesExactlyOneInstanceDataVersion_EvenWhenItemsFail` | Aynısı kısmi başarısızlık altında. **Asıl regresyon riski burada:** her başarısız item'ın hatasını anında persist eden bir implementasyon mutlu yolu bozmadan degişmezi bozar. |
| `TwoOfFiveFail_RoutesToPartialFailure_AndTheFailedRowsCarryErrorCodes` | `allSettled` altında instance Faulted **değil**, `documents-partial-failure`'a dallanır; özet `failed: 2`; başarısız satırların hepsi bir hata kodu taşır. |

## Tek-yazim degismezi nasıl assert ediliyor

Orchestration host'unda sürüm geçmişi ucu **yok** (yalnız monitoring host'unda, port 4203 —
SDK stack'i onu başlatmıyor). Bu yüzden akış kendi sürüm işaretlerini instance verisine yazıyor:

Batch, `documents-processing` state girişinde iki damga task'ı arasında **sarılı** koşuyor:

- `versionBeforeFanOutBatch` — onEntry **order 1**; batch'ten hemen önceki `LatestData.Version`.
- *(order 2 = batch)*
- `versionAfterFanOut` — onEntry **order 3**; batch'ten hemen sonraki sürüm.

Aralarında başka hiçbir şey çalışmaz (transition yok, state değişimi yok). Her task sonucu bir
patch artırdığı için iki damga arasında tam **iki** yazıma izin var — önce-damgasının kendi
sonucu, ve tüm batch:

```
patch(versionAfterFanOut) - patch(versionBeforeFanOutBatch) == 2     ✔  (1 damga + 1 batch)
                                                            == 1 + N ✘  item başına yazım
```

Ayrıca bağımsız bir sonda: `GET .../instances/{id}/data?version=X` var olmayan sürüm için **404
vermez**, latest'a **düşmez** — `200` + `data: null` döner. Test patch hattının tamamını sayar:
önce-damgasının satırı (`+1` sabitini **varsaymak yerine doğrular**), batch'in satırı, head, ve
head'in bir ötesinin **olmadığı**.

> **Neden ayrı bir önce-damgası var?** Bu işi eskiden fan-out mapping'inin `OutputHandler`'ı
> yapıyordu. O üye artık opsiyonel (vnext `4bd8941b`: default interface implementation `null`
> döner ⇒ runtime'ın varsayılan paketlemesi devreye girer) ve senaryo bu geri-düşüşü fiilen test
> edebilmek için handler'ı override etmeyi bıraktı. Varsayılan paketleme senaryo enstrümantasyonu
> taşıyamaz, damga da bu yüzden kendi task'ına taşındı. **Handler'ı geri eklemeyin** — eklerseniz
> testler runtime'ın çıktısını değil senaryonun kendi ürettiği çıktıyı doğrulamış olur.

> **Bu iki testi ayrı ayrı değiştirmeyin.** `documents-processing` onEntry sırası
> (damga → fan-out → damga) ölçümün tamamıdır. Araya bir task, bir transition ya da bir state
> değişimi sokan her düzenleme farkı sessizce büyütür ve assertion "geçiyor" görünmeye devam
> ederken hiçbir şeyi denetlemez.

## Kapsam açığı — item journal (bilinçli)

Item'lar `{fanOutTaskKey}#{index}` anahtarlı `InstanceTask` satırlarına yazılır
(`FanOutTaskExecutor`: `JournalTaskKey = $"{task.Key}#{item.Index}"`), ama bu satırlar yalnızca
monitoring host'unun `.../instances/{id}/tasks` ucundan (`taskDefinitionKey`) görünür. SDK ne o
host'u başlatıyor ne de ucu sarmalıyor, bu yüzden assertion **uydurulmadı — yok**.

Opt-in doğrulama:

```bash
python3 api-tests/fan-out-documents/fanout-load.py --monitor-url http://localhost:4203
```

## Çalıştırma

```bash
dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~FanOut"
```

FanOutTask henüz release edilmedi; container image eski kodu taşır. Lokalde derlenen runtime'a karşı
koşmak için `tests/Core.IntegrationTests/test.runsettings` içinde `VNEXT_BASE_URL`'i açın ve vNext
çalışma alanında altyapı + 4 app'i ayağa kaldırın.
