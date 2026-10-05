# script-race-lab

Script assembly'sinin **çift-derleme yarışı** için fixture. Parent `scripts.helpers` bildirir, bu yüzden
subflow output mapping'i helper setinin paylaşılan, singleton ömürlü `AssemblyLoadContext`'inde derlenir
(`SubflowOutputMappingService`, `parentWorkflow.Scripts` ile derler). Parent ve child tamamen otomatik
olduğundan N paralel start, tek bir emit penceresine düşen N completion — aynı assembly adıyla N
derleme — demektir. Fix'siz runtime'da kaybedenler `FileLoadException` alır ve parent **kalıcı**
fault'lanır (`Instance:100030`); fix'li runtime'da her parent tamamlanır.

## Neyi denetliyor

- **Paylaşılan ALC'de eşzamanlı derleme**: 30 örtüşen start'ın hiçbiri `F` olmamalı.
- **`scripts.helpers` çözümü subflow output mapping'inde**: `RaceOutputMapping` helper'ı çağırıp
  `raceStamp`'i (`race:` önekli) parent verisine yazar; damgasız `C` hiçbir şey kanıtlamaz.
- **SubFlow lifecycle**: `race-subflow` (SubFlow `S`) çocuğu başlatır, çocuk kendiliğinden biter, parent
  output mapping'i koşturup `race-done`'a resume eder.

## Akış

```
script-race-lab-parent (F) v1.0.6     helpers: race-helper 1.0.0 (sys-mappings)
start ─▶ race-initial ─auto─▶ race-subflow (SubFlow S → script-race-lab-child v1.0.6,
                                            output mapping: RaceOutputMapping.csx)
                     ─auto─▶ race-done (Finish/Success)
script-race-lab-child (S) v1.0.6
start ─▶ child-initial ─auto─▶ child-done (Finish/Success)
```

Hiçbir task yok; bütün iş auto transition'lar ve subflow output mapping'idir.

## Testler

| Test | Kanıtladığı | Not |
|---|---|---|
| `Smoke_SingleInstance_CompletesAndCarriesTheHelperStamp` | Tek instance `race-done` / `C`, `raceStamp` `race:` ile başlar | Fixture'ın sağlıklı olduğunun kanıtı |
| `ParallelStarts_AllComplete_WithoutAnAssemblyLoadFault` | 30 paralel start'ın hepsi terminal, hiçbiri `F`, hepsinde `raceStamp` var | Fault'ta mesaj `Instance:100030` + `Assembly with same name is already loaded` bekler |

## Bağımlılıklar ve ön koşullar

| Bağımlılık | Durum |
|---|---|
| Runtime | `core`, `VNEXT_BASE_URL` (`test.runsettings`: `http://localhost:4201`), lokal build |
| System package | Gerekmez — helper `core/Mappings/script-race-lab/`, akışlar `core/Workflows/script-race-lab/` altında |
| MockLab | Gerekmez — akışta HTTP task yok |
| Dapr | Akışa özgü bileşen yok |
| Caller rolleri / header'lar | Gerekmez |
| Cross-domain | Gerekmez |
| morph-idm provider | Gerekmez |

## Nasıl çalıştırılır

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~ScriptRaceLab"

# Yük sürücüsü (stdlib-only; JMeter karşılığı jmeter/tests/script-race-lab.jmx)
python3 api-tests/script-race-lab/race-load.py --publish --parallel 30 --timeout 240
```

`api-tests/script-race-lab/publish.py` bileşenleri **helper → child → parent** sırasıyla publish eder
(entegrasyon suite'i bunu SDK publisher'ıyla kendisi yapar; script JMeter ve elle doğrulama içindir).

## Dikkat noktaları / bilinen istisnalar

- **Yarış yalnız soğuk cache'te üretilir.** Compile cache anahtarı kaynak hash'idir; mapping bir kez
  derlendikten sonra aynı kaynakla tekrar koşmak yarışı tetiklemez ve test **totolojik yeşil** verir.
  Ayırt edici bir koşu için `python3 core/Workflows/script-race-lab/build-script-race-lab.py --nonce N`
  ile yeni nonce basın.
- **Nonce sürüme bağlıdır** (varsayılan `1.0.<nonce>`; diskteki artefaktlar `1.0.6`). `definitions/publish`
  aynı key+version'ı içerik değişse de 409 ile reddeder; sürümü artırmadan nonce bumplarsanız yeni kaynak
  runtime'a hiç ulaşmaz.
- **Settle sinyali terminal status'tur, "Busy değil" değil.** Açık SubFlow korelasyonu taşıyan parent
  çocuğun ömrü boyunca Busy'dir; Busy ilerleme hakkında bilgi taşımaz.
- **Start'lar örtüşmelidir.** Yarış penceresi tek bir Roslyn emit'idir; seri başlatan bir sürücü hiçbir
  şey kanıtlamaz. Yarış tetiklenmiyorsa ayarlanacak iki düğme: `ParallelStarts` ve build script'inin
  `--filler` değeri (emit maliyeti).
- **Helper yalnız parent'ta bildirilmelidir.** Helper'ı child'a taşımak output mapping'in derlendiği yolu
  değiştirmez ve yarışı imkânsız kılar.
