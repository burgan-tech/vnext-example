# resource-lock-lab — transition düzeyi resource lock (vnext#1143)

## Neyi denetliyor

Üç iddia:

1. **Kilit commit'te bırakılır.** Tutucu terminal state'e ulaştığında kilit serbest kalır ve aynı
   anahtarı bir sonraki instance alabilir. Bırakma, unit of work'ün `OnCompleted` kancasında olur —
   inline değil; hâlâ geri alınabilecek bir transition "bölge boş" diye ilan etmemelidir.
2. **Yarışı kaybeden öldürülmez, yönlendirilir.** Kilit tutulurken ikinci instance
   `Execution:200010` alır; `rl-idle` state'inin `errorBoundary`'si bunu `rl-lock-busy` paylaşımlı
   transition'ına yönlendirir ve instance `rl-waiting`'de **fault etmeden** park eder. Kilit adımı
   OnExecute/ChangeState/OnEntry'den önce çalıştığı için instance state'inden hiç çıkmamıştır;
   yönlendirme bu yüzden tutarlıdır.
3. **Farklı anahtarlar çekişmez.** Sabit bir anahtar ya da sessizce tek değere çöken bir anahtar
   çözümlemesi yukarıdaki testlerin ikisini de geçer, ama üretimde throughput'u yarıya indirir.

Ayrıca, yalnız Postgres lease provider'ında: **Extend kirayı alındığı TTL'in ötesine taşır.**

## Neden var

Önceden kilit yarışını kaybeden instance **fault ediyordu** ve çekişilen iş kayboluyordu — yeniden
denemek yeni bir instance başlatmak demekti. #1143 iki çıkış yolu ekledi: `errorBoundary` ile
yönlendirme (bu lab'ın kapsadığı) ve `onConflict: "Reject"` (aşağıdaki kapsam açığı). Kilitleme
çekirdek süreçtir; davranış uçtan uca pinlenmelidir.

## Akış

```
start-resource-lock-lab
        │
        ▼
   rl-idle (Initial)                errorBoundary: Execution:200010 → rl-lock-busy
        ├─ rl-acquire        Acquire ttl=300  onConflict=Abort → rl-holding
        └─ rl-acquire-short  Acquire ttl=5    onConflict=Abort → rl-holding-short
                                              (kaybeden) ──────→ rl-waiting
   rl-holding          ─ rl-finish            → rl-done (Finish, kilit serbest)
   rl-holding-short    ─ rl-extend   Extend ttl=300 → rl-holding-extended
                       ─ rl-finish-short      → rl-done
   rl-holding-extended ─ rl-finish-extended   → rl-done
   rl-waiting          ─ rl-give-up           → rl-abandoned (Finish)
```

Kilit anahtarı instance verisindeki `lockKey`'den çözülür
(`core/Workflows/resource-lock-lab/src/LockKeyMapping.csx` → `rl-lab:{lockKey}`), böylece iki
instance aynı değerle başlatılarak çekiştirilir, farklı değerlerle bağımsız bırakılır.

## Nasıl çalıştırılır

```bash
cd tests/Core.IntegrationTests
dotnet test --settings test.runsettings --filter "FullyQualifiedName~ResourceLockLab" -v minimal
```

Extend testini de koşmak için runtime `ResourceLock:Provider=postgres` ile ayağa kaldırılmalı ve
koşuma `VNEXT_RESOURCE_LOCK_PROVIDER=postgres` verilmeli; aksi hâlde o test **skip** olur.

## Beklenen sonuç

| Test | İddia |
| --- | --- |
| `AfterTheHolderFinishes_TheSameKeyCanBeTakenAgain` | Tutucu `rl-done`'a ulaştıktan sonra ikinci instance aynı anahtarla `rl-holding`'e geçer. Bırakma atlanırsa ikinci instance `rl-waiting`'e yönlenir |
| `WhileHeld_TheLoserIsReroutedByTheBoundary_AndNeitherInstanceFaults` | Kaybeden `rl-waiting`'de ve `F` değil; tutucu `rl-holding`'de ve `F` değil — çekişme kazananı rahatsız etmez |
| `DifferentKeys_DoNotContend` | İki instance farklı anahtarlarla aynı anda `rl-holding`'de |
| `Extend_KeepsTheLeasePastItsOriginalTtl` | 5 sn TTL ile alınıp 300'e uzatılan kira, orijinal TTL geçtikten sonra hâlâ tutuluyor: çekişen `rl-waiting`'e düşüyor. **Postgres provider yoksa skip** |

## Dikkat noktaları

- **`onConflict: "Reject"` bu lab'da YOK.** Yayınlanmış şema (`@burgan-tech/vnext-schema` 0.0.57)
  `onConflict`'i `["Abort"]` ile sınırlıyor; `Reject` vnext-schema#162 ile geliyor. Onu kullanan bir
  akış bu deponun kendi `npm run validate`'inden geçmiyor, bu yüzden vaka o şema yayınlandığında
  eklenecek. Runtime yolu unit testlerle kaplı (`TransitionPipelineTests`, zincirli hop dâhil).
- **Extend yalnız Postgres provider'ında var.** Dapr'ın lock building block'unda extend yoktur —
  provider ayarının var olma sebebi tam olarak budur. Varsayılan runtime'da test skip eder; bu bir
  kırmızı değildir.
- Kilit çakışması 409'u, `WorkflowTestBase.RunAsync`'in tekrar denediği **status lock** 409'undan
  farklıdır (o `Failed to acquire lock` gövdesiyle gelir). Resource-lock çakışması
  `Execution:200010` taşır ve tekrar denenmez; testler bu yüzden `RunAsync`'in dönüşünü bekleyip
  state'e bakar.
