# vNext Build Plan — Cross-Domain Flow Default Version (`flow-default-version-xd`)

> Durum: **ONAYLI** — kullanıcı, 2026-10-22 ("Plan onaylı, uygula ve lab'da koş"). Runtime: vnext `claude/flow-default-version-27620d` (#1106), lab imajları o dalın
> worktree'sinden (`VNEXT_SRC_DIR=<worktree> lab.sh images`). Lab topolojisi ve bağlam:
> [VNEXT-BUILD-PLAN.md](VNEXT-BUILD-PLAN.md) (core :4201, partner :4211, credit :4221, discovery :4231).

## 0. Özet & Kapsam

**Ne:** #1106'nın EC2 senaryosunu iki domain arasında birebir sınamak: `core`'daki onboarding akışı,
`partner`'daki contract akışını SubFlow ve SubProcess olarak tüketir; contract'ın default versiyonu
**partner'ın** `sys-version-registry` kaydından gelir (çözümleme çağrılan tarafta).

**Neden:** Tek-domain senaryosu (`FlowDefaultVersion`) çözücüyü kanıtlıyor; çağrılan tarafta çözümleme,
çağıranın floating/tam referans farkı, pilotun iki domain'de birlikte yürümesi ve subflow kaydının çocuğun
gerçek versiyonunu tutması domain sınırında hiç koşmadı.

**Kapsam dışı:** iki domain'in default'unun atomik değişimi (tasarım gereği yok); credit (3. hop);
canary/yüzde yönlendirme.

## 1. Kabul Kriterleri

Ön koşul: partner'da `fdv-xd-contract` `1.2.0` (paketten) ve `1.3.0` (fixture'dan) yayınlı; partner
registry'de `fdv-xd-contract` default'u `1.2.0`.

- **XV-01 Floating referans çağrılanın default'unu izler** — core `fdv-xd-onboarding` (SubFlow, referans
  `"1"`) başlatılır → partner'daki çocuk `1.2.0` ile başlar (1.3.0 yayında ama aktif değil); core'daki
  subflow kaydı (`activeCorrelations[].subFlowVersion`) çocuğun çözülmüş versiyonunu (`1.2.0-pkg…`) taşır.
- **XV-02 Tam referans default'u yok sayar** — core `fdv-xd-onboarding-exact` (referans `"1.2.0"`) → çocuk
  `1.2.0`; partner default'u `1.3` yapıldıktan sonra da `1.2.0`.
- **XV-03 İki domain'de pilot** — core registry'de `fdv-xd-onboarding` default'u `1.0.0`; fixture
  `fdv-xd-onboarding` `1.1.0`'ı (contract referansı tam `"1.3.0"`) yayınlar → bekler. Versiyonsuz start →
  onboarding `1.0.0`, çocuk `1.2.0`; `?version=1.1.0` start → onboarding `1.1.0`, çocuk `1.3.0`.
- **XV-04 Çağrılanın default'u değişir** — partner `set-default "1.3"` → en geç 10 sn içinde yeni bir
  `fdv-xd-onboarding` (referans `"1"`) çocuğu `1.3.0` ile başlar. Değişimden önce başlamış çocuk `1.2.0`'da
  kalır ve onun ebeveynindeki subflow kaydı hâlâ `1.2.0-pkg…` gösterir.
- **XV-05 SubProcess aynı kurala uyar** — core `fdv-xd-onboarding-sp` (SubProcess task, versiyon `"1"`)
  XV-01 ve XV-04'teki sonuçları SubProcess yolundan da verir.
- **XV-06 Otomatik moda dönüş** — partner `set-default "latest"` → yeni çocuk `1.3.0` (en son yayınlanan).
- **XV-07 Gözlem** — partner orchestration'ında `Cache.Get/sys-flows:partner:fdv-xd-contract:res:…`
  span'leri `vnext.flow.version.source` = `default` / `ceiling` taşır (OpenObserve, `vnext` stream'i).

Her test kendi registry kaydını açık bir değerle kurar (temizleme `"latest"`; `null` mevcut anahtarda
etkisiz) — tekrar koşulabilir.

## 2. Bileşen Envanteri

| Yol | Tür | Not |
|---|---|---|
| `partner/Workflows/flow-default-version-xd/fdv-xd-contract.json` | workflow S, `1.2.0` | `contract-waiting` (Initial) ─`sign`→ `contract-done` (Finish); çocuk aktif kalsın diye test `sign`'ı yalnız gerektiğinde gönderir |
| `core/Workflows/flow-default-version-xd/fdv-xd-onboarding.json` (+ mapping .csx) | workflow F, `1.0.0` | `onb-start` ─auto→ `onb-contract` (`stateType:4`, `subFlow.process` = `{domain:"partner", flow:"sys-flows", key:"fdv-xd-contract", version:"1"}`) ─auto→ `onb-done` |
| `core/Workflows/flow-default-version-xd/fdv-xd-onboarding-exact.json` | workflow F, `1.0.0` | aynı, `version:"1.2.0"` |
| `core/Workflows/flow-default-version-xd/fdv-xd-onboarding-sp.json` + `core/Tasks/flow-default-version-xd/fdv-xd-start-contract.json` | workflow F + task 14 | SubProcess, `useDapr:true`, versiyon `"1"` |
| Test-yerel fixture (core/ dışında): `fdv-xd-onboarding` `1.1.0` (contract referansı `"1.3.0"`), `fdv-xd-contract` `1.3.0`, `sys-version-registry` + `version-registry` şeması (FlowDefaultVersion fixture kopyası) | fixture | registry akışı **core ve partner'ın ikisine de** yayınlanır (core paketi henüz taşımıyor) |
| `tests/Core.IntegrationTests/Tests/CrossDomainLab/FlowDefaultVersionCrossDomainTests.cs` | test | `CrossDomainLabTestBase`, `SkippableFact`; partner URL yoksa skip |
| `TEST-SCENARIOS.md` satırı + CrossDomainLab README bölümü | doküman | aynı commit |

Script'ler yazıldıktan sonra `python3 labs/cross-domain/encode-scripts.py <klasörler>`.

## 3. Altyapı

- Lab zaten bu dalın imajlarıyla ayakta (`lab.sh status`); yeni imaj gerekmez, yalnız bileşen yayını.
- Yeni Dapr bileşeni / env yok.

## 4. Doğrulama

`dotnet test --filter "FullyQualifiedName~FlowDefaultVersionCrossDomain"` lab'a karşı; ardından CrossDomainLab
+ FlowDefaultVersion + VersionPinLab regresyon koşusu; XV-07 için OpenObserve HTTP sorgusu.
