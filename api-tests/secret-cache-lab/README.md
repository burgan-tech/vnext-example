# secret-cache-lab — ScriptBase secret cache (in-process + TTL)

## Neyi denetliyor

`ScriptBase.GetSecretAsync` ile okunan bir secret'ın **ilk okumada Vault'a gittiğini, aynı TTL
penceresi içindeki sonraki okumaların (aynı transition içinde ve sonraki request'lerde) hiç
Vault'a gitmeden process içi bundle'dan servis edildiğini**, ve **TTL dolduğunda Vault'taki
güncel değerin canlı olarak alındığını** uçtan uca doğrular.

Ölçüm iki bağımsız kanıta dayanır:

1. **Vault audit device** (`vnext-vault:/tmp/vault-audit.log`) — `secret/data/workflow-secret`
   yoluna yapılan gerçek `read` isteklerinin sayısı. Yer gerçeği budur; zamanlama değil.
2. **Okuma başına süre** — mapping `DateTime.UtcNow.Ticks` ile her `GetSecretAsync` çağrısını
   mikrosaniye cinsinden ölçüp instance data'ya yazar. Soğuk okuma ~5–80 ms, cache hit ~0–10 µs.

## Neden var

vNext runtime'ında `ScriptSecretCache` geliştirmesi (`Scripting:SecretCache`, varsayılan
TTL 30 sn) script secret fonksiyonlarının **her çağrıda vault'a gitmesi** sorununu çözmek için
eklendi — yük testlerinde vault, script yoğunluğu altında darboğaz oluyordu
(vnext branch `claude/scriptbase-secret-cache-y86e03`, 2026-08-20; ayrıca senkron
`GetSecret` için kilitsiz L1 probe eklendi).

Bu senaryo iki riski birden pinler:

- **Cache gerçekten devrede mi** — yoksa geliştirme sadece kağıt üstünde mi kaldı (bir DI kaydı
  eksikse `ScriptBase` sessizce doğrudan Dapr'a düşer; davranış doğru kalır, kazanç kaybolur).
- **Bayatlık sınırlı mı** — secret rotasyonundan sonra eski değerin dönmesi **kabul edilen**
  davranış, ama en fazla TTL kadar. TTL dolduğunda taze değer gelmezse bu kalıcı bayatlıktır ve
  üretimde rotasyonu sessizce kırar.

## Akış şeması

```
start-secret-cache-lab ──► probe-idle (Initial)
                              │
                              ├── probe-secret  ──► probe-idle   (kritik adım)
                              │      onExecute: secret-probe-script-task
                              │      → SecretProbeMapping.csx: aynı secret'ı 3 kez okur,
                              │        her okumanın süresini ve okunan değeri instance data'ya yazar
                              │
                              └── finish-lab ──► probe-done (Finish/Success)
```

Instance data (her `probe-secret` sonrası):

```json
{ "probeRound": 1, "secretValue": "…", "readAtUtc": "…", "microsPerRead": [7143.0, 3.0, 1.0] }
```

Bileşenler:

| Dosya | Rol |
|---|---|
| `core/Workflows/secret-cache-lab/secret-cache-lab.json` | Akış |
| `core/Workflows/secret-cache-lab/src/SecretProbeMapping.csx` | 3 ölçümlü secret okuma |
| `core/Tasks/secret-cache-lab/secret-probe-script-task.json` | Script task (type 7) |
| `core/Workflows/secret-cache-lab/secret-cache-lab.http` | Elle deneme |

## Nasıl çalıştırılır

Ön koşullar:

1. Altyapı ayakta (özellikle `vnext-vault`):

```bash
cd etc/docker && ./run-docker.sh
```

2. Lokal runtime ayakta — **image ile değil**, geliştirme dalından derlenmiş hâliyle:

```bash
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http
```

```bash
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host --launch-profile http
```

Sonra testi koş (bileşenleri de publish etmek için `--publish`):

```bash
python3 api-tests/secret-cache-lab/secret-cache-behaviour-test.py --ttl 30 --publish
```

Parametreler:

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `--ttl` | `30` | Runtime'daki `Scripting:SecretCache:TtlSeconds`. Farklı yapılandırdıysan buraya da ver. |
| `--publish` | kapalı | Task + workflow bileşenlerini `POST /api/v1/definitions/publish` ile yükler. |
| `--keep-secret` | kapalı | Test sonunda Vault'taki orijinal değeri geri yazma. |

Script Vault audit device'ını gerekiyorsa kendisi açar (`-path=probe`), Vault değerini test
başında değiştirir ve sonunda orijinaline geri alır.

## Beklenen sonuç / başarı kriteri

Toplam 12 assert; hepsi geçmeli.

| Faz | Beklenen |
|---|---|
| 1 — soğuk okuma + aynı transition'da 2 sıcak okuma | Vault read delta **= 1**; `micros[0] ≥ 500`, `micros[1:] < 500`; soğuk/sıcak oranı ≥ 5x |
| 2 — TTL içinde ikinci transition | Vault read delta **= 0** (cache request'ler arası yaşıyor, process-wide) |
| 3 — Vault'ta rotasyon, TTL dolmadan okuma | **Eski** değer döner, Vault read delta **= 0** (bilinçli staleness penceresi) |
| 4 — TTL dolduktan sonra okuma | **Yeni** değer döner, Vault read delta **= 1**, ilk okuma yine yavaş |

Faz 3'ün "eski değer" beklentisi bir bug değil, sözleşmedir: rotasyon sonrası bayatlık penceresi
en fazla TTL kadardır ve TTL yapılandırılabilir (`Enabled=false` veya `TtlSeconds<=0` cache'i
tamamen devre dışı bırakır).

### Ölçülen sonuç (2026-08-20, lokal runtime, TTL 30 sn)

```
faz1  microsPerRead=[7143.0, 3.0, 1.0]  vaultReadDelta=1
faz2  microsPerRead=[5.0, 1.0, 0.0]     vaultReadDelta=0
faz3  microsPerRead=[8.0, 0.0, 1.0]     vaultReadDelta=0   (eski değer)
faz4  microsPerRead=[6235.0, 6.0, 1.0]  vaultReadDelta=1   (yeni değer)
== Sonuc: 12/12 gecti
```

Soğuk okuma sıcak okumadan ~2400x pahalı; TTL penceresi içinde Vault'a hiç gidilmiyor.

## Bilinen kısıtlar

- Cache **process içi**dir (secret materyali bilerek Redis'e/dağıtık cache'e verilmez). Çok
  replikalı ortamda her replika kendi TTL penceresini yaşar; rotasyon sonrası tüm replikaların
  tazelenmesi en fazla TTL kadar sürer.
- Script task'ın hangi process'te (orchestration mı execution mı) koştuğu cache'in hangi
  process'te ısındığını belirler; lokalde her ikisi de tek instance olduğu için ölçüm nettir.
- Zaman eşikleri (`HIT_MAX_MICROS = 500 µs`) yüklü bir makinede gevşek kalabilir; asıl kanıt
  Vault audit sayacıdır, zamanlama destekleyici kanıttır.
- Bu senaryonun integration test (xUnit) karşılığı **bilinçli olarak yok**: doğrulama Vault
  audit log'una ve saat ölçümüne dayanıyor, ikisi de SDK'nın assertion yüzeyinde yok.
