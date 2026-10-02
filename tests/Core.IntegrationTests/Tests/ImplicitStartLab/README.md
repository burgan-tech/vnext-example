# implicit-start-lab — Initial state'siz workflow ve örtük `$start`

## Neyi denetliyor

**Tek iddia:** Bir workflow Initial state (`stateType: 1`) beyan etmek zorunda değildir. Beyan
etmezse runtime örtük bir kaynak state kullanır (anahtar **`$start`**, `WellKnownStateKeys.Start`).
Instance orada doğar, giriş noktasını yalnız `startTransition.target` belirler, ve `$start` hiçbir
zaman dinlenme noktası olmaz. Bıraktığı tek iz ilk history satırındaki `fromState == "$start"`.
Initial beyan eden akışlar eskisi gibi çalışır. Publish hâlâ iki Initial'ı ve hedefsiz bir start
transition'ı reddeder.

## Neden var

burgan-tech/vnext-client-sdk-core#64 ("initial state kavramı kalksın"). vnext branch
`64-initial-state-kavrami-kalksin`: `9ce40444` örtük `$start`, `21d6875e` validator (en fazla bir
Initial, `$start` rezerve anahtar, start target'ı beyan edilmiş bir state olmalı). Şema tarafı:
vnext-schema `feature/optional-initial-state` (`minContains: 0`). Tarih: 2026-10-02.

## Akış şekli

```
implicit-start-lab         ($start) --start-implicit--> step-1 [Wizard] --next--> done [Finish]
implicit-start-lab-child   ($start) --start-implicit-child--> step-1 [Intermediate] --next--> done
implicit-start-lab-parent  waiting [Initial, BEYAN EDİLMİŞ] --auto--> in-child [SubFlow S → child]
                                                                   --auto (child bitince)--> done
```

Akışlar `core/Workflows/implicit-start-lab/build-implicit-start-lab.py` ile üretilir. Tek script
SubFlow mapping'i (`src/ImplicitStartLabSubFlowMapping.csx`), çünkü şema `subFlow.mapping`'i zorunlu
tutuyor.

Kritik adım: start transition'ın `$start`'tan çıkması. Sync başlatma tek pipeline'da tamamlanır.
Async başlatma bir job adı kurar ve o ad source state'i taşır.

## Testler

| Test | Kanıtladığı |
|---|---|
| `Start_WithoutInitialState_Sync_LandsOnWizard` | `sync=true` → `step-1`/`A`; ilk history satırı `$start → step-1`, `start-implicit` |
| `Start_WithoutInitialState_Async_PollNeverFails` | `sync=false` start kabul edilir; state poll'ları yalnız 200/304 döner; `$start` görülürse transitions boştur; `step-1`'de durur |
| `Start_WithoutInitialState_ThenTransition_Completes` | `next` → `done`/`C` |
| `SubFlowChild_WithoutInitialState_StartsAndParentCompletes` | Initial'lı parent, Initial'sız child'ı başlatır; child'ın ilk satırı `$start`'tan; child bitince parent `C` |
| `Publish_WithTwoInitialStates_Returns400` | 400, gövdede "at most one initial state" |
| `Publish_WithMissingStartTarget_IsRejected` (absent / empty) | 4xx (ölçülen: 400 `App:900006`, `Target can not be null, empty or white space!`) |

Hatalı tanımlar `core/` altında tutulmaz. Test, diskteki akışı bir probe anahtarı ve koşu başına
yeni bir sürümle kopyalayıp doğrudan `api/v1/definitions/publish`'e gönderir. Böylece SDK
publisher'ı onları her fixture açılışında göndermez.

## Nasıl koşulur

Ön koşul: bu branch'ten derlenmiş lokal runtime (`etc/docker/run-docker.sh up core`, :4201).
MockLab gerekmez.

```bash
cd ../vnext-example
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~ImplicitStartLab" -v minimal
```

`npm run validate`, sabitlenmiş `@burgan-tech/vnext-schema` 0.0.52 ile Initial'sız iki akışı
reddeder (`minContains: 1`). vnext-schema'nın `minContains: 0` sürümü yayımlanana kadar bu
bekleniyor. SDK publisher ham JSON gönderdiği için testler bundan etkilenmez.

## Geçme kriteri ve bilinen sınırlar

7/7 yeşil (6 test, biri iki case'li theory).

**Bilinen kırmızı (2026-10-02, runtime `21d6875e`):** `Start_WithoutInitialState_Async_PollNeverFails`.
`sync=false` start **500** döner. `JobName.ForAsyncTransition(instanceId, sourceState: "$start", ...)`
→ `JobName.ValidateKey` `$` karakterini reddeder ("must ... contain only [A-Za-z0-9_-] to form a
valid Dapr job name"). Instance satırı exception'dan önce yazıldığı için `$start`/`A` durumunda,
sıfır transition ile yetim kalır. Varsayılan `sync=false` olduğundan, sync parametresi vermeyen her
client Initial'sız bir akışı başlatamaz. Bu bir runtime kusuru, test tarafında çözülmez.
