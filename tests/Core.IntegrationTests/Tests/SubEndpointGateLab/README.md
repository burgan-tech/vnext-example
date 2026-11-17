# SubEndpointGateLab — integration test

Runtime'ın kendi çağrıları için var olan iki gizli uç, `POST {domain}/workflows/{workflow}/sub/instances/start`
ve `POST {domain}/workflows/{workflow}/instances/{instance}/complete`, opsiyonel bir **domain-config
kapısının** arkasında (vnext#1265, `docs/runtime/sub-endpoint-gate.md`). Runtime kendi çağrısını kısa ömürlü
bir **runtime-caller token**'la (`x-vnext-runtime-token`, HMAC, route değerlerine bağlı) kanıtlar; token'sız
bir çağıran `Workflow:SubEndpointGate:Grants`'a göre karar görür. Domain `core`; akışlar
`subflow-carried-headers-lab`'ınkiler (`sfch-parent` / `sfch-child`), yeni akış yok.

## Neden var

vnext#1265 (2026-11-17): iki uç yalnız Swagger'dan gizliydi, ayrı bir yetkisi yoktu ve platform gateway'leri
`/<domain>/*` joker route'larıyla runtime'a yönlendirebiliyordu. Kapıdan önce, vnext master `76ca97ab` üzerinde
lokal core'da ölçüldü (`api-tests/sub-endpoint-gate/sub-endpoint-gate-probe.py open`):

- düz bir client'ın `sub/instances/start`'ı **200** döndü ve child'ı yarattı (`TrustedPayload = true` gövdeyle);
- açık bir parent/child korelasyonuna sahte bir `complete`, parent'ı gerçek child hâlâ `sfch-child-wait`'te iken
  `sfch-a`'dan `sfch-b`'ye taşıdı ve parent **fault** oldu (ikinci child aynı key ile başlatılamadı).

Kapı yalnız rollere bakamazdı: aynı-domain completion'ın **Inbox backup**'ı kullanıcısız gelir, cross-domain
completion relay'i çoğu zaman kullanıcısızdır, ve allow grant rolsüz bir çağıranı reddeder (parent park olur).
Bu yüzden runtime kendi çağrısını token'la kanıtlar.

**Sınırlar:** bu senaryo yalnız `core`; cross-domain SubFlow start (C1), SubProcess (C2) ve completion relay (C3)
CrossDomainLab'ta, kapı açık lab koşusuyla kanıtlanır (`labs/cross-domain/compose.sub-endpoint-gate.yml`).
Mod tespit edilmez, `VNEXT_IT_SUB_ENDPOINT_GATE` ile beyan edilir.

**Geçme ölçütü:** her modda kendi testleri yeşil; Enforce'ta ayrıca runtime çağrısı için `vnext.gate.decision`
yalnız `admitted_runtime` (OpenObserve), istemci dışında `refused` 0.

| Test | Mod | Neyi denetliyor |
|---|---|---|
| `Enforce_ClientSubStart_IsRefused_BeforeAnythingIsWritten` | enforce | Client'ın (sahte token'la da) `sub/instances/start`'ı **403 `Authorization:110001`**; verilen id ile child **yaratılmaz** (GET 404) |
| `Enforce_ForgedCompletion_IsRefused_AndTheParentStaysPut` | enforce | Açık bir parent/child korelasyonuna client'ın sahte `complete`'i 403; parent `sfch-a`/`B`'de kalır, gerçek child `sfch-child-wait`'te; ardından gerçek `finish` parent'ı `C`'ye taşır |
| `Observe_ClientSubStart_IsAdmitted` | observe | Observe reddetmez (yalnız `would refuse` loglar): child yaratılır |
| `Off_ClientSubStart_BehavesAsBefore` | off | Kapı yapılandırılmamışken uç eskisi gibi: 200 + child var |
| `RuntimeChain_ManualChild_StillCompletes` | hepsi | Elle biten child A + kendiliğinden biten child B: aynı-domain completion relay'i ve **Inbox backup** (C4, kullanıcısız) kapıdan geçer, parent `C` |
| `RuntimeChain_TimerChild_StillCompletes_WithNoUserOnTheCompletion` | hepsi | Child A 2 sn'lik timer'la biter: settle eden hop'ta **hiç kullanıcı yok**; yalnız token geçirebilir, parent `C` |
| `TokenHeader_SentByAClient_NeverReachesTheTransitionRecord` | hepsi | Client'ın gönderdiği `x-vnext-runtime-token` ingress'te silinir: transition kaydının header'ında (history API) ne adı ne değeri var, `x-request-id` var |

## Mod anahtarı

Kapının modu dışarıdan okunamaz; çağıran runtime'ın nasıl başlatıldığını `VNEXT_IT_SUB_ENDPOINT_GATE` ile bildirir:

| Değer | Runtime ortamı (Orchestration, Execution, Inbox host'larının hepsi) |
|---|---|
| (yok) | varsayılan — kapı `Off` |
| `observe` | `Workflow__RuntimeCaller__KeyFile=<ring.json>`, `Workflow__RuntimeCaller__Required=true`, `Workflow__SubEndpointGate__Mode=Observe`, `Workflow__SubEndpointGate__Grants__0__Role=$Everyone`, `Workflow__SubEndpointGate__Grants__0__Grant=deny` |
| `enforce` | aynısı, `Mode=Enforce` |

`ring.json`: `{ "activeKeyId": "lab-k1", "keys": { "lab-k1": "<base64, ≥32 bayt>" } }` — lokal üretilir, commit'lenmez.
`etc/docker/run-docker.sh up core` host'ları kabuğun ortamını miras alarak başlatır; değişkenleri o komuttan önce export et.

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~SubEndpointGateLab" -v minimal
```

Çalışan moda ait olmayan mod testleri **skip** olur (enforce: 2, off/observe: 3).

## Koşu kaydı

**2026-11-17, son ağaç (review turları sonrası; host'lar son değişiklikten sonra derlendi, `up core --no-build`):**

| Koşu | Sonuç |
|---|---|
| Kapı Off — `sub-endpoint-gate-probe.py open` | 6/6 (master ile aynı) |
| Kapı Off — **tüm suite** | **699 geçti / 9 kırmızı / 127 skip** — 9 kırmızı master build'dekiyle aynı → yeni kırmızı 0 |
| Kapı Enforce — `sub-endpoint-gate-probe.py enforce` | 6/6 reddedildi, yazım yok |
| Kapı Enforce — SubEndpointGateLab + SubflowCarriedHeadersLab + SubflowOrchestration + ContractSigning | 38 geçti / 0 kırmızı / 3 skip; span'lerde `admitted_runtime` 21 (`inbox`, `valid`), `refused` 4 (kasıtlı client çağrıları) |

Aşağıdaki kayıtlar aynı günün önceki build'ine aittir (review düzeltmelerinden önce; davranış değişmedi).

**2026-11-17 — runtime: vnext `1265-enhancement-add-a-role-gate-to-sub-start-and-complete-endpoints` (base
`76ca97ab` + çalışma ağacı), lokal core (`run-docker.sh up core`), MockLab açık.**

| Koşu | Sonuç |
|---|---|
| Kapı Off — bu senaryo | 4 geçti / 3 skip |
| Kapı Enforce (`$Everyone` deny, ring 3 host'ta) — bu senaryo | 5 geçti / 2 skip |
| Kapı Enforce — **tüm suite** | **700 geçti / 9 kırmızı / 126 skip**. 9 kırmızının hepsi master build'de de kırmızı (aynı vnext-example, vnext `76ca97ab`, 2026-11-17): AccountOpening ×6 (`set-or-get-cache` sistem paketinde yok), AuthorizationChainLab ack ×2, InstanceIdFilterLab `NotAGuid` ×1 — **yeni kırmızı 0** |

Doğrulama (MCP'ler kapalıydı; OpenObserve REST + psql + host logları):
- Enforce suite penceresinde span'lerde `vnext.gate.decision`: `admitted_runtime` **85** (hepsi `vnext.runtime_caller.class=inbox`,
  verdict `valid`) — aynı-domain completion backup'ı (C4) her seferinde imzalı geçti; `refused` yalnız **2** (bu senaryonun kasıtlı
  client çağrıları: token `missing` ve `malformed`); `would_refuse` 0.
- `Inbox.Forward` span'leri: **1280**, hepsi `vnext.forward.outcome=ok` (`gate_refused` 0).
- Token sızıntısı: OpenObserve log `body`'sinde `x-vnext-runtime-token` / `v1;k=` **0**; host loglarında (10 518 `RequestHeaders`
  kaydı dahil) **0**; Postgres'te tüm flow şemalarının `InstanceTransitions`/`InstanceTasks`/job/inbox/outbox metin kolonlarında
  (13 409 kolon) **0** — pozitif kontrol: `sfch_parent."InstanceTransitions"."Header"`'da `x-request-id` 134 satırda var.

**2026-11-17 — cross-domain lab, kapı Enforce her domain'de** (`labs/cross-domain/lab.sh` imajları vnext çalışma
ağacından, etiket `gate-1265`, provider `dapr`; `VNEXT_LAB_SUB_ENDPOINT_GATE=Enforce` + `VNEXT_LAB_RUNTIME_CALLER_RING`):

- 12 imzalayan konteynerin (4 domain × Orchestration/Execution/Inbox) hepsi açılışta `ActiveKeyId: lab-k1` logladı.
- `CrossDomainLab` + `HumanTaskChain`: **67 geçti / 4 kırmızı / 6 skip**. 4 kırmızının hepsi `FileOffloadCrossDomainTests` — lab
  sidecar'ında `vnext-blob-local` binding'i yok (partner logu: `couldn't find output binding vnext-blob-local`), bilinen şablon açığı
  (2026-11-14 aynı 4).
- Span'lerde `vnext.gate.decision=admitted_runtime`, verdict `valid`, çağıran sınıfı ve domain'iyle:

  | Callee | Çağıran sınıfı (domain) | Adet | Kapsadığı |
  |---|---|---|---|
  | partner | `subflow` (core) | 39 | C1 cross-domain SubFlow start |
  | credit | `subflow` (partner) | 9 | C1, ikinci sınır (partner'ın kendi runtime'ı imzalar) |
  | partner | `subprocess` (core) | 11 | C2 cross-domain SubProcess |
  | core | `relay` (partner) | 19 | C3 cross-domain completion relay |
  | core | `inbox` (core) | 2 | C4 aynı-domain Inbox backup'ı |

  Runtime çağrısı için `refused` / `would_refuse` **0**. Tek ret: partner'a doğrudan client `sub/instances/start` probu → 403
  `Authorization:110001`.
- Token sızıntısı: OpenObserve log `body` **0**, 5 konteynerin logları **0**, `vNext_Core` / `vNext_Partner` / `vNext_Credit`
  veritabanlarında **0**; Inbox'ta gate-refused forward **0**.
- Kapı Observe'e alınıp bu senaryo: 4 geçti / 3 skip; core'da tek `50051` satırı: `Reason: grant_denied, CallerClass: -,
  CallerDomain: -, TokenReason: missing` (client sub-start'ı).
