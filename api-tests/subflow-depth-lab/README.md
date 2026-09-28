# subflow-depth-lab — iç içe SubFlow derinliğine göre transition gecikmesi

Bu lab tek bir soruya cevap arar: root instance'a gönderilen `sync=true` bir transition, arada
kaç seviye SubFlow varsa o kadar yavaşlıyor mu, ve her seviye kaç milisaniye ekliyor? Beş seviye
iç içe SubFlow kullanan bir domain transition'ların yavaş olduğunu bildirdi (2026-09-28). O güne
kadar en derin lab üç seviyeydi (`subflow-orchestration`, `chain-busy`) ve hiçbiri derinliğe
göre süre ölçmüyordu.

Kod okumasına göre maliyet derinlikle doğrusal büyür ama her seviyenin sabit payı yüksektir.
Transition her seviyede yeniden bir pipeline'dan geçer: `ForwardToActiveSubflowStep` (order 10)
post-commit'e bir forward kuyruklar, `ForwardToSubflowJobHandler` alt seviyeyi `sync=true` ile
çağırır ve dönüşü bekler. Bunun üstüne yukarı dönerken her seviyede bir post-commit settle, yaprak
state değiştirdiğinde de her ataya bir `sub:state-changed` relay'i eklenir. Bu lab, bu payların
gerçekte ne kadar tuttuğunu ölçer. Tahmine dayanarak runtime'da değişiklik yapılmaz.

## Akış

`core/Workflows/subflow-depth-lab/` altında beş workflow var: `subflow-depth-lab-l1` (type F)
ile `l2`…`l5` (type S). Her ara seviye `lK-initial` durumundan auto transition'la
`lK-waiting` SubFlow state'ine geçer ve orada bir alt seviyeyi başlatır. Alt seviye bitince auto
transition'la `lK-done` durumuna iner. Yaprak (`l5`) `l5-waiting` durumunda Active bekler; üzerinde
iki manuel transition var. `leaf-ping` task'siz bir self-loop'tur (`l5-waiting → l5-waiting`),
istendiği kadar tekrar gönderilebilir. `leaf-finish` ise yaprağı bitirir ve tamamlanma zincir
boyunca yukarı doğru resume eder. Akışlarda hiç task yoktur, dolayısıyla ölçülen sürenin tamamı
runtime'ın forward, settle ve relay maliyetidir.

Derinlik, zincirin hangi seviyeden başlatıldığıyla seçilir: `d=5` için `l1`, `d=1` için `l5`
başlatılır. Type `S` bir akışın doğrudan API'den başlatılması desteklenen bir durumdur.

JSON'lar üreticiden çıkar. Bir değişiklik yapacaksanız üreticiyi düzenleyip yeniden çalıştırın:

```bash
python3 core/Workflows/subflow-depth-lab/build-subflow-depth-lab.py
```

## Çalıştırma

Ölçüm **lokal derlenmiş runtime'a** karşı yapılmalı, container image'ı eski kodu taşır. Sırayla:

```bash
cd ../vnext/etc/docker && ./run-docker.sh up core      # infra + 4 host, /health bekler
cd ../../../vnext-example
wf domain use core && wf domain active                  # "core" yazmalı
wf check && wf sync                                     # subflow-depth-lab-l1..l5 yüklenir
python3 api-tests/subflow-depth-lab/depth-latency.py \
    --base-url http://localhost:4201 --depths 1,2,3,4,5 --instances 5 --pings 10 --warmup 2
```

Script yalnız Python standart kütüphanesini kullanır, ayrıca bir kurulum gerekmez. Parametreler
şöyle: `--depths` hangi derinliklerin koşulacağı, `--instances` derinlik başına kurulan zincir
sayısı, `--pings` zincir başına ölçülen `leaf-ping` sayısı, `--warmup` ise ölçüme alınmayan ilk
ping sayısı (script ve component cache ısınması için).

## Sonucun okunması

Script derinlik başına client tarafından ölçülen p50, p95, p99 ve max değerlerini, zinciri kapatan
`leaf-finish` çağrısının p50'sini ve en sonda **seviye başı eğimi** (ms/level, en sığ ve en derin
derinliğin p50 farkının seviye farkına bölümü) basar. Asıl bakılacak sayı bu eğimdir, çünkü düzeltme
adaylarının hepsi seviye başı sabit maliyeti hedefler. Karşılaştırma aynı makinede önce ve sonra
yapılmalı.

Script bir onay testi değil, bir ölçüm aracıdır; gecikme için bir eşiği yoktur. Herhangi bir ping
2xx dışında dönerse ya da zincir 30 saniyede kurulmazsa çıkış kodu 1 olur ve hatalar listelenir.
Bir hatayı regresyon saymadan önce loglarda hata imzasına bakın. Çoğu zaman sebep ortamdır:
workflow'lar yüklenmemiştir ya da domain yanlış seçilmiştir.

Client süresi zamanın nereye gittiğini göstermez. Seviye başı dağılım için aynı pencerede
OpenObserve'da trace'lere bakın (stream `vnext`, `type: traces`) ve `SubFlow.Forward`,
`Transition.Settle`, `vnext.subflow.depth` span'lerini derinliğe göre kırın. Tek bir activation
episode ayrı bir trace'tir ve bekleyen HTTP span'inin altında görünmez; ortadaki boşluk bir
takılma değil, başka yerde trace'lenen episode'un beklenmesidir (`docs/runtime/trace-lanes.md`).

## Bilinen kısıtlar

Ölçüm tek makinede, broker gecikmesi olmadan alınır ve bütün seviyeler aynı domaindedir.
Cross-domain zincirde her seviyeye bir Dapr HTTP hop eklenir, bu lab onu ölçmez. Integration testi
henüz yok. Bir runtime değişikliği bu lab'la doğrulanacaksa `Tests/SubflowDepthLab` altına zincir
kurma, ping ve unwind doğruluğunu kontrol eden bir test eklenmeli.
