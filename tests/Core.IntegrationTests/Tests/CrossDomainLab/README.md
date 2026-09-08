# CrossDomainLab — cross-domain SubFlow, trigger task'ları ve fonksiyon descent'i (core → partner)

## Neyi denetliyor

`core` domain'indeki `xd-parent` akışı, `partner` domain'indeki bileşenleri **Dapr Name Resolution +
Service Invocation** üzerinden tüketir (`ServiceDiscovery:Provider=dapr`). Suite şunu pinler: parent
üzerinden yapılan her okuma/yazma partner içeriğiyle cevaplanır (descent) ve altı cross-domain task
tipi (11 Start, 12 DirectTrigger, 13 GetInstanceData, 14 SubProcess, 15 GetInstances, 19 GetInstance)
`useDapr:true` ile partner'a ulaşır.

## Neden var

Cross-domain adres çözümlemesi Discovery registry HTTP'sinden Dapr'a taşındı
(vnext `feature/dapr-name-resolution`, 2026-09). vnext-example'da o güne kadar **hiç** cross-domain
örnek yoktu (`config.domain` her yerde `core`, `useDapr` hiç geçmiyordu); yeni transport'un tek uçtan
uca regresyon fixture'ı bu suite'tir. Tasarım: `labs/cross-domain/VNEXT-BUILD-PLAN.md`.

## Akış

```
core/xd-parent (F)
xd-initial ─auto→ xd-hub ─enter-subflow→ xd-subflow(4: S → partner/xd-child) ─auto→ xd-after-subflow
  ─spawn-subprocess(14)→ xd-after-spawn ─start-remote(11)→ xd-after-start ─trigger-remote(12)→ xd-after-trigger
  ─read-remote(19+13)→ xd-after-read ─list-remote(15)→ xd-completed

partner/xd-child (S):  child-initial ─auto→ child-review [view, child-approve: schema + roles xd-approver] → child-completed
partner/xd-remote (F): remote-initial ─auto→ remote-waiting ─remote-advance→ remote-done
partner/xd-worker (P): worker-initial ─auto→ worker-done
```

Kritik adımlar: `xd-subflow` parent'ı subflow ömrü boyunca Busy tutar → testler **gözlenen** (leaf) state'i
bekler, status'u değil. Her task ayrı transition'da: kırmızı bir test tek bir task tipini gösterir.

## Sınıflar

| Sınıf | AC | Ölçtüğü |
|---|---|---|
| `SubflowDescentTests` | 01–06 | child start (partner'da), `state`/`view`/`schema`/`authorize` descent'i, `data?extensions=` descent'i (gövde parent'ta kalır — runtime kararı), parent üzerinden `child-approve` forward + parent resume |
| `TriggerTaskTests` | 07–11 | 14 fire-and-forget worker, 11 sync start (+`remoteInstanceId`/`remoteKey`), 12 `remote-advance`, 19+13 okuma (`remoteState`, `remoteData.testId`), 15 `attributes.testId` filtresiyle liste |

`CrossDomainLabFixture` partner bileşenlerini (`partner/`, `vnext.partner.config.json`) bir kez yayınlar —
harici-stack modunda SDK'nın `OnAfterEnvironmentReadyAsync` hook'u çağrılmadığı için fixture'da.
`VNEXT_PARTNER_BASE_URL` yoksa tüm testler **skip** (`Xunit.SkippableFact`).

## Çalıştırma

Ön koşul: lab ayakta — `labs/cross-domain/lab.sh up` (core :4201, partner :4211, discovery :4231;
detay `labs/cross-domain/README.md`). Runtime kodunu değiştirdiysen `lab.sh images` → `down` → `up`.

```bash
cd tests/Core.IntegrationTests
dotnet test --settings test.runsettings --filter "FullyQualifiedName~CrossDomainLab"
```

`test.runsettings`: `VNEXT_BASE_URL=http://localhost:4201`, `VNEXT_PARTNER_BASE_URL=http://localhost:4211`.
Script gövdeleri (`.csx`) değiştiğinde `python3 labs/cross-domain/encode-scripts.py` ile `code`
alanlarını yenile (runtime `location`'dan değil `code`'dan derler).

**Publish sürüm-değişmezdir.** Aynı sürüm yeniden yayınlanınca runtime 409 `Instance:100002` döner;
publisher çıktısındaki `FAIL Conflict` satırları değişmemiş bileşenler için **normaldir** ve testler
önceki yayına karşı koşar. Bir bileşeni değiştirdiysen `version`'ı patch bump'la ve onu tam sürümle
referanslayan yerleri güncelle (ör. `xd-child-ext 1.0.1` → `xd-child.extensions[]` → `xd-child 1.0.1`
→ `xd-parent.subFlow.process.version` → `xd-parent 1.0.1`); aksi hâlde düzeltmen canlıya hiç çıkmaz.

## Başarı kriteri / bilinen kısıtlar

- 11 test yeşil (her biri kendi `testId`'siyle yeni parent açar).
- `data` fonksiyonu gövdeyi subflow'a **indirmez**, yalnız `?extensions=` iner — AC-04 bunu pinler;
  runtime değiştirilirse test bilerek kırılır.
- vnext-schema 0.0.52 `useDapr`'ı yalnız task 15/19'da tanır → `core/Tasks/cross-domain-lab/` içindeki
  11/12/13/14 dosyaları `npm run validate`'te "must match then schema" verir; runtime alanı okur, alan
  bilinçli korunuyor. `partner/` `npm run validate` kapsamında değil (kabul edilmiş).
- `Discovery.Resolve/partner` span etiketleri (`vnext.discovery.provider=dapr`,
  `vnext.dapr.app_id=vnext-app-partner`) manuel doğrulanır (OpenObserve :5080); test assert etmez.
- Hata enjeksiyonu (callee kapalı → `ERR_DIRECT_INVOKE` → `remote_network_error`) ikinci faz.
