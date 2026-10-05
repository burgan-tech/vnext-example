# smoke

Ortamın ayakta ve orchestration API'sinin erişilebilir olduğunu doğrulayan tek testlik suite. Domain
testlerinden önce altyapı sorunlarını yakalamak için vardır: `/health` 200 dönmüyorsa geri kalan her
kırmızı önce ortam sorunu olarak okunmalıdır.

> **Sınıfın yeri:** test sınıfı bu klasörde **değil**, bir üst dizinde durur —
> `tests/Core.IntegrationTests/Tests/SmokeTests.cs` (namespace `Core.IntegrationTests.Tests`, sınıf
> `SmokeTests`). Bu klasör yalnız bu README'yi taşır; dosyayı taşımak namespace'i ve filtreleri değiştirir.

## Neyi denetliyor

- Orchestration host'unun `GET /health`'e **200** dönmesi.
- Dolaylı olarak SDK fixture'ının (`VNextTestEnvironment`) kurulabilmesi: suite koşmadan önce SDK
  `InitializeAsync` içinde `core/**` bileşenlerini publish eder (`EnableDomainPublish = true`) ve
  MockLab seed dizinini (`etc/docker/config/seed`) gösterir. Publish başarısızsa smoke testi de
  koşmadan düşer.

Workflow, state, transition ya da instance data iddiası **yoktur**.

## Akış

Akış yok — test hiçbir workflow başlatmaz.

```
test ─▶ GET {VNEXT_BASE_URL}/health ─▶ 200 OK
```

## Testler

| Test | Kanıtladığı | Not |
|---|---|---|
| `HealthEndpoint_Returns200` | Orchestration `/health` 200 | `IntegrationTestBase`'ten türer (`WorkflowTestBase` değil), caller header'ı göndermez |

## Bağımlılıklar ve ön koşullar

| Bağımlılık | Durum |
|---|---|
| Runtime | `core`, `VNEXT_BASE_URL` (`test.runsettings`: `http://localhost:4201`), lokal build; set değilse SDK Testcontainers stack'i kaldırır |
| System package | Gerekmez |
| MockLab | Gerekmez (test çağırmaz; fixture seed dizinini yalnız yapılandırır) |
| Dapr | Gerekmez (doğrudan; `/health`'in kendisi host'un bağımlılıklarını yansıtabilir) |
| Caller rolleri / header'lar | Gerekmez |
| Cross-domain | Gerekmez |
| morph-idm provider | Gerekmez |

## Nasıl çalıştırılır

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~Core.IntegrationTests.Tests.SmokeTests"
```

Python scripti yok. Elle eşdeğeri: `curl -i http://localhost:4201/health`.

## Dikkat noktaları / bilinen istisnalar

- **Filtreyi tam sınıf adıyla verin.** `FullyQualifiedName~Smoke` başka suite'lerdeki `Smoke_*` test
  metotlarını da seçer (ör. `ScriptRaceLabTests.Smoke_SingleInstance_CompletesAndCarriesTheHelperStamp`).
- **Yeşil smoke, domain'in hazır olduğunu kanıtlamaz.** `/health` sistem paketinin
  (`@burgan-tech/vnext-core-runtime`) yüklendiğini, MockLab'in ya da Dapr scheduler'ın ayakta olduğunu
  söylemez; bunlara bağlı suite'ler kendi README'lerindeki ön koşullara bakmalıdır.
- `wf check`'in "API: Not accessible" demesi bilinen bir tuhaflıktır; `/health` 200 ise ona güvenin.
