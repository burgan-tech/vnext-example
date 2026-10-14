# script-plugin-lab — script mapping'den plugin DLL ile veritabanı (ADO.NET) erişimi

## Neyi denetliyor

Bir veritabanı sürücüsünün **script plugin'i** olarak kullanılması: geliştirici DLL'leri runtime'ın
`Scripting:Sandbox:PluginDirectory` dizinine koyar, mapping `scripts.allowedAssemblies` ile izin verir ve
script içinden SQL çalıştırır. Dört iddia:

1. **Helper'sız mapping plugin'i çalıştırıyor.** `spl-direct-query` plugin'i (`ScriptPluginLab.Sql`)
   doğrudan çağırır, PostgreSQL'e parametreli salt-okunur bir sorgu atar (`select @n::int + 1` → `42`).
2. **Helper üzerinden mapping plugin'i çalıştırıyor.** `spl-helper-query` aynı sorguyu `spl-db-helper`
   (sys-mappings) helper'ı üzerinden atar — bir ekibin "veritabanı helper'ı" nasıl tüketiliyorsa öyle.
3. **Plugin süreçte TEK kopya.** `SqlProbe.LoadId` her yüklenen kopyada bir kez üretilir; iki yolun aynı
   değeri döndürmesi, plugin'in helper seti başına değil süreç başına bir kez yüklendiğini (tek bağlantı
   havuzu) gösterir.
4. **İsteğin iptal sinyali script'e ulaşıyor.** `ScriptContext.CancellationToken` isteğin token'ıdır
   (`CanBeCanceled == true`); script sürücü çağrısına geçirir.

## Neden var

Script mapping içinden ADO.NET sorgusu çalıştırma ihtiyacı (çekirdek bankacılık SQL Server SP'si). vnext'in
plugin yolu yalnız helper setlerinde çalışıyordu ve her helper seti plugin'in **kendi kopyasını** yüklüyordu:

- Helper'sız bir mapping plugin tipini kullandığında derleme geçiyor, ilk çalıştırmada
  `Could not load file or assembly 'ScriptPluginLab.Sql'` ile düşüyordu.
- Her helper seti (ve helper'ın her yeniden publish'i) sürücünün ayrı kopyasını, dolayısıyla ayrı bağlantı
  havuzunu açıyordu; eskiler bırakılmıyordu.
- Plugin dizini süreç başına bir kez okunuyordu; pod plugin Job'ından önce açılırsa restart gerekiyordu.

Runtime tarafı: vnext `ScriptPluginLoadContext` (plugin'ler süreç başına bir kez), `ScriptPluginDirectory`
(bulunamayan isimde yeniden okuma, `Scripting:Sandbox:PluginRescanIntervalSeconds`, varsayılan 30) ve
`ScriptContext.CancellationToken`. Belge: vnext `docs/custom-script-helpers.md` § 4.

## Bileşenler

| Bileşen | Ne |
|---|---|
| `plugins/ScriptPluginLab.Sql/` | Plugin kaynağı: `SqlProbe.QueryAsync` — Npgsql (ADO.NET) + Dapper; hiç exception atmaz, `SqlProbeResult` döner |
| `plugins/build-plugins.sh` | Plugin'i derler, `ScriptPluginLab.Sql.dll` + `Dapper.dll`'i **düz** olarak `plugins/out/`'a koyar |
| `core/Tasks/script-plugin-lab/spl-script.json` | ScriptTask (tip 7) |
| `core/Mappings/script-plugin-lab/spl-db-helper.json` | Helper: `ScriptPluginLab.Helpers.SplDb.NextAsync` |
| `core/Functions/script-plugin-lab/spl-direct-query.json` | Helper'sız mapping → plugin |
| `core/Functions/script-plugin-lab/spl-helper-query.json` | Mapping → helper → plugin |
| `core/Functions/script-plugin-lab/build-script-plugin-lab.py` | `.csx` kaynaklarından üç JSON'u üretir |

Npgsql bilerek `out/`'a **kopyalanmaz**: runtime onu zaten taşır ve host'ta olan bir assembly her zaman
host kopyasından çözülür. Plugin'in Npgsql sürümü runtime'ınkinden (10.0.x) yüksek olmamalıdır. Dapper
ise runtime'da yoktur — sürücünün üçüncü taraf bağımlılık zincirinin plugin dizininden çözüldüğünü o kanıtlar.

## Nasıl koşulur

Plugin'i derle (yalnız ilk sefer ya da plugin değişince):

```bash
./plugins/build-plugins.sh
```

Runtime'ı iki ortam değişkeniyle başlat (vnext checkout'unda; değişkenler `run-docker.sh`'ın başlattığı
host'lara geçer):

```bash
export Scripting__Sandbox__PluginDirectory="$PWD/../vnext-example/plugins/out"
export ScriptPluginLab__ConnectionString="Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=<yerel parola>"
cd etc/docker && ./run-docker.sh up core
```

`ScriptPluginLab:ConnectionString` yerel geliştirme veritabanını gösterir; sorgu salt-okunurdur, hiçbir
şey yazmaz. Bağlantı cümlesi bileşene ya da instance verisine hiç girmez — host config'inden okunur.

Suite:

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~ScriptPluginLab" -v minimal
```

Bileşen değişikliği: `build-script-plugin-lab.py` içindeki `VERSION`'ı artır ve betiği çalıştır (publish
sürüm-değişmezdir, aynı sürüm 409 `Instance:100002` döner).

## Skip koşulları

| Durum | Neden | Görünen |
|---|---|---|
| Function 404 | Plugin dizini verilmemiş: runtime, çözemediği `allowedAssemblies` adını publish'te reddeder (SDK `[DomainPublisher] FAIL 400` yazar, diğer suite'ler etkilenmez) | Skip, mesaj README'yi gösterir |
| `configured: false` | `ScriptPluginLab__ConnectionString` host'ta yok | Skip |

## Kanıt (2026-10-13)

| Runtime | Sonuç |
|---|---|
| vnext `claude/adonet-script-mapping-1e2aec` (plugin load context + `CancellationToken`) | **4/4 yeşil**, skip yok. İki yol aynı `loadId`, PostgreSQL 18.1, `next = 42`, `tokenCanBeCanceled = true` |
| vnext `c606709d` (değişiklik öncesi) | 4/4 kırmızı — mapping'ler `ScriptContext.CancellationToken`'ı bulamıyor (CS1061). Token'sız geçici kopyalarla: helper yolu çalışıyor, **helper'sız yol `Could not load file or assembly 'ScriptPluginLab.Sql'` ile 500** |

## Tuzaklar

- **Host'un zaten taşıdığı sürücü plugin olamaz.** Npgsql'i `out/`'a koysan da helper'sız script host
  kopyasını kullanır; plugin yolunu sınamak için sürücü host'ta olmayan bir assembly olmalı (burada
  `ScriptPluginLab.Sql` + Dapper). Microsoft.Data.SqlClient gibi gerçek bir sürücüde bütün bağımlılık
  zinciri dizine konmalı; NuGet paketinin `lib/` altındaki kopyası değil, `runtimes/unix/lib/...`
  altındaki (ya da `dotnet publish -r linux-x64` çıktısı) kullanılmalı.
- **Plugin dizini sonradan doldurulursa** runtime en fazla `PluginRescanIntervalSeconds` (30 sn) içinde
  görür; restart gerekmez. Ama publish o sırada reddedildiyse bileşenleri yeniden publish et.
- **Yüklenmiş bir plugin DLL'ini yeni sürümle değiştirmek (ya da silmek) restart ister.** Süreç ilk
  yüklediği kopyayı tutar; `./plugins/build-plugins.sh` ile plugin'i değiştirdiysen runtime'ı yeniden
  başlat. Yeni eklenen bir DLL için restart gerekmez.
- Plugin sorguyu `context.CancellationToken` ile çağırmazsa iptal edilen istek sorguyu durdurmaz;
  `CommandTimeout` tek sınır olur.
