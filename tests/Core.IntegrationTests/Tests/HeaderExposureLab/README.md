# header-exposure-lab

## Neyi denetliyor

`Workflow:HeaderExposure:Deny` (vnext#1260, Chair kararı 2026-11-23): listedeki request header'ları

- transition kaydına (`InstanceTransitions.Header`) **yazılmaz**,
- transition geçmişi yanıtından (`GET …/instances/{id}/transitions`) **çıkarılır** — liste ayarlanmadan önce yazılmış
  satırlar dahil (saklanan satır yeniden yazılmaz),
- hiçbir script/mapping'in `context.Headers`'ında **görünmez**,

ama runtime'ın kendi okumaları (rol çözümü, kimlik, iletilen credential) header'ı geldiği gibi görmeye devam eder.
Yalnız Deny; liste boşsa (varsayılan) hiçbir şey değişmez.

## Akış

`core/Workflows/header-exposure-lab/` — tek state (`open`). Start transition'ı ve `echo-headers` ($self, yalnız
`morph-idm.maker` allow) aynı `EchoHeadersMapping`'i koşar: mapping'in gördüğü header adlarını
`startHeaderNames` / `echoHeaderNames` olarak, `x-exposure-probe` ve `role` görünüp görünmediğini
`*SawProbe` / `*SawRole` olarak instance verisine yazar. Task: `role-matrix-exec-task` (script). Fixture
`build-header-exposure-lab.py` ile üretilir; base64'ü elle düzenleme.

## Nasıl çalıştırılır

Liste runtime config'i olduğu için bir koşu bir tarafı kapsar. `VNEXT_HEADER_EXPOSURE_DENY` suite'e runtime'ın hangi
listeyle başladığını söyler; diğer taraf skip olur.

```bash
# 1) Liste boş (varsayılan) — vnext'te
cd etc/docker && ./run-docker.sh up core
# vnext-example'da
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~HeaderExposureLab"

# 2) Liste açık — vnext'te host'ları yeniden başlat
./run-docker.sh down core
Workflow__HeaderExposure__Deny__0=x-exposure-probe Workflow__HeaderExposure__Deny__1=role \
  ./run-docker.sh up core --no-build --skip-migrate
# vnext-example'da
VNEXT_HEADER_EXPOSURE_DENY="x-exposure-probe,role" dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~HeaderExposureLab"
```

Koşudan sonra host'ları listesiz başlat; aksi halde sonraki suite'ler `role` header'ını mapping'de göremez.

## Testler

| Test | Koşu | Pinlenen |
|---|---|---|
| `WithAnEmptyList_MappingsAndHistorySeeEveryHeader` | liste boş | start ve echo mapping'i probe'u ve `role`'ü görür; geçmişin iki satırı probe'u taşır |
| `ADeniedHeader_IsAbsentFromEveryMapping` | liste açık | iki mapping de probe'u ve `role`'ü görmez; `x-request-id` görünür |
| `ADeniedHeader_IsNotInTransitionHistory` | liste açık | iki geçmiş satırında probe ve `role` yok, `x-request-id` var |
| `TheRuntime_StillResolvesRoles_FromADeniedHeader` | liste açık | `authorize?transitionKey=echo-headers`: maker 200, approver 403 — rol, mapping'in göremediği `role` header'ından çözülüyor |

## Beklenen sonuç / kayıt

**Koşuldu 2026-11-23** (vnext `claude/issue-112-analysis-55b8ce` lokal build, yalnız core, provider `default`):

- Liste boş: lab 1/1 (3 skip), RoleMatrixLab + SubflowCarriedHeaders + InstanceCallCredential ile birlikte
  145 geçti / 0 kırmızı / 18 skip (17 cross-domain partner yok, 1 `ApplyToResume_Off` ayrı config ister).
- Liste açık (`x-exposure-probe`, `role`): 148/148 yeşil (19 skip: aynı 18 + varsayılan-liste testi). `role` header'ı
  listedeyken RoleMatrixLab'ın yetkilendirme testlerinin hepsi geçti — runtime'ın rol okuması etkilenmiyor.
- Postgres `header_exposure_lab."InstanceTransitions"`: liste açıkken yazılan 6 satırda probe 0, `role` 0,
  `x-request-id` 6. Liste boşken yazılmış eski instance'ın 2 satırı probe'u DB'de hâlâ taşıyor, geçmiş yanıtında 0.
- Trace doğrulaması yapılmadı (openobserve / elasticsearch MCP bağlı değildi).

## Bilinen kısıtlar

- Tek koşu iki tarafı birden kapsayamaz; liste host başlangıcında okunur.
- Cross-domain bir child'ın kendi listesi kendi domain config'inden gelir; bu lab yalnız core'u ölçer.
