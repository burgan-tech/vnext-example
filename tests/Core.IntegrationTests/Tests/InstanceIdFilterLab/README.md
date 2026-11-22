# InstanceIdFilterLab — instance listesini `id` ile filtreleme (vnext#1080)

## Ne kapsar

Instance listesinin uuid `id` kolonuna göre filtrelenmesi; liste uç noktası ve `GetInstances` task'ının `SetFilter`'ı.

Düzeltmeden önce kolon builder'ı operandı metin olarak bağlıyordu: her `id` filtresi 500 dönüyordu
(PostgreSQL `42883: operator does not exist: uuid = text`). TTS ekibi elindeki instance id'siyle instance'ı bulamıyordu.

| Test | Beklenen |
|---|---|
| `List_IdEq_ReturnsThatInstance` | `{"id":{"eq":"<id>"}}` → 200, yalnız o instance (kabul kriteri 1) |
| `List_IdIn_ReturnsBothInstances` | `{"id":{"in":["<id1>","<id2>"]}}` → 200, ikisi (kabul kriteri 2) |
| `List_IdNeAndNin_ExcludeByUuid` | `ne` / `nin`, testin kendi `testId`'siyle AND'lenmiş → dışlananlar dönmez |
| `List_IdNotAGuid_Answers400` | Guid olmayan değer (tek başına ya da `in` listesinde) → 400 `Validation:900011` (kabul kriteri 3) |
| `List_IdUnsupportedOperator_Answers400` | `gt`, `startswith` → 400 `Validation:900011`, metin olarak koşturulmaz |
| `Task_SetFilterIdIn_FindsBothInstances` | Aynı `in` filtresi `GetInstances` task'ından → iki instance (kabul kriteri 4) |
| `Task_SetFilterIdNotAGuid_FailsAsClientError` | Task'ta Guid olmayan id → okuma başarısız, `statusCode` 400 (liste uç noktasıyla aynı sınıflama) |

## Bileşenler

- `core/Workflows/instance-id-filter-lab/idf-target.json` — popülasyon; `idle`'da doğar ve kalır.
- `core/Workflows/instance-id-filter-lab/idf-reader.json` — `ready` state'inde aynı-state `read-list` transition'ı;
  OnExecute'taki `idf-get-instances` task'ı filtreyi transition body'sinden alır (`IdfReaderListMapping.csx`) ve sonucu
  `readerList { isSuccess, statusCode, error, count, ids }` olarak kaydeder. Task'ta `Ignore` error boundary'si
  (action 3, `*`) vardır: boundary'siz bir task hatası instance'ı Faulted yapar (vnext#1257), oysa reddedilen filtrenin
  fault etmeyip `readerList`'e düşmesi istenir. Yerel DB'de eski tanım kalmışsa SDK güncellemez; `idf-reader` satırını ve
  `*sys-flows:core:idf-reader*` Redis anahtarlarını silip host'ları yeniden başlat.
- `core/Tasks/instance-id-filter-lab/idf-get-instances.json` — GetInstances (type 15).
- Mapping'i değiştirince `python3 core/Workflows/instance-id-filter-lab/build-instance-id-filter-lab.py` ile workflow
  JSON'larını yeniden üret; base64'ü elle düzenleme.

## İzolasyon

Her test kendi `testId`'siyle üç `idf-target` instance'ı açar. `eq`/`in` doğrudan o id'leri hedefler, `ne`/`nin`
testin `testId`'siyle AND'lenir; paylaşılan veritabanında yalnız kendi satırlarını görür.

## Bağımlılık

Yok — HTTP task, MockLab ya da subflow kullanmaz. Mapping hedef domain'i body'den (`TestDomain.Name`) aldığı için yan
domain'de de koşar (`VNEXT_DOMAIN`).

## Koşum

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~InstanceIdFilterLab" -v minimal
```
