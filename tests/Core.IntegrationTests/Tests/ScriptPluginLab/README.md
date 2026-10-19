# script-plugin-lab — script mapping'den plugin DLL ile veritabanı (ADO.NET) erişimi (PostgreSQL + SQL Server)

## Neyi denetliyor

Bir veritabanı sürücüsünün **script plugin'i** olarak kullanılması: geliştirici DLL'leri runtime'ın
`Scripting:Sandbox:PluginDirectory` dizinine koyar, mapping `scripts.allowedAssemblies` ile izin verir ve
script içinden SQL çalıştırır. Beş iddia:

1. **Helper'sız mapping plugin'i çalıştırıyor.** `spl-direct-query` plugin'i (`ScriptPluginLab.Sql`)
   doğrudan çağırır, PostgreSQL'e parametreli salt-okunur bir sorgu atar (`select @n::int + 1` → `42`).
2. **Helper üzerinden mapping plugin'i çalıştırıyor.** `spl-helper-query` aynı sorguyu `spl-db-helper`
   (sys-mappings) helper'ı üzerinden atar — bir ekibin "veritabanı helper'ı" nasıl tüketiliyorsa öyle.
3. **Plugin süreçte TEK kopya.** `SqlProbe.LoadId` her yüklenen kopyada bir kez üretilir; iki yolun aynı
   değeri döndürmesi, plugin'in helper seti başına değil süreç başına bir kez yüklendiğini (tek bağlantı
   havuzu) gösterir.
4. **İsteğin iptal sinyali script'e ulaşıyor.** `ScriptContext.CancellationToken` isteğin token'ıdır
   (`CanBeCanceled == true`); script sürücü çağrısına geçirir.
5. **Gerçek bir sürücü paketi sarmalayıcısız çalışıyor: Microsoft.Data.SqlClient + Dapper → SQL Server.**
   Operatör paketlerin kendi DLL zincirini (29 DLL) dizine koyar; `spl-sqlserver-query` script'i
   `SqlConnection`'ı ve Dapper'ı **doğrudan** kullanır. Üç çağrı: düz ADO.NET (`SqlCommand` + tipli parametre),
   Dapper parametreli sorgu, `CommandType.StoredProcedure` ile SP çağrısı (`sys.sp_executesql`). Üçü de `42`.

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
| `core/Functions/script-plugin-lab/spl-sqlserver-query.json` | Helper'sız mapping → **Microsoft.Data.SqlClient + Dapper doğrudan** (SQL Server) |
| `plugins/ScriptPluginLab.SqlServer/` | Kodsuz taşıyıcı proje: yalnız SqlClient + Dapper bağımlılık zincirini `dotnet publish -r linux-x64` ile toplar |
| `core/Functions/script-plugin-lab/build-script-plugin-lab.py` | `.csx` kaynaklarından bileşen JSON'larını üretir |

Npgsql bilerek `out/`'a **kopyalanmaz**: runtime onu zaten taşır ve host'ta olan bir assembly her zaman
host kopyasından çözülür. Plugin'in Npgsql sürümü runtime'ınkinden (10.0.x) yüksek olmamalıdır. Dapper
ise runtime'da yoktur — sürücünün üçüncü taraf bağımlılık zincirinin plugin dizininden çözüldüğünü o kanıtlar.

## Nasıl koşulur

Plugin'i derle (yalnız ilk sefer ya da plugin değişince):

```bash
./plugins/build-plugins.sh
```

SQL Server senaryosu için yerel bir SQL Server (ARM Mac'te Azure SQL Edge):

```bash
docker run -d --name vnext-sqlserver-lab --network bbt-development -p 14333:1433 \
  -e ACCEPT_EULA=1 -e MSSQL_SA_PASSWORD='<yerel parola>' mcr.microsoft.com/azure-sql-edge:latest
```

Runtime'ı ortam değişkenleriyle başlat (vnext checkout'unda; değişkenler `run-docker.sh`'ın başlattığı
host'lara geçer):

```bash
export Scripting__Sandbox__PluginDirectory="$PWD/../vnext-example/plugins/out"
export ScriptPluginLab__ConnectionString="Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=<yerel parola>"
export ScriptPluginLab__SqlServerConnectionString="Server=localhost,14333;User Id=sa;Password=<yerel parola>;TrustServerCertificate=True;Encrypt=True"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0   # SqlClient invariant modu reddeder; Alpine imajlarda ICU gerekir
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
| `configured: false` | `ScriptPluginLab__ConnectionString` (PostgreSQL testleri) ya da `ScriptPluginLab__SqlServerConnectionString` (SQL Server testleri) host'ta yok | Skip |

## Kanıt (2026-10-13)

| Runtime | Sonuç |
|---|---|
| vnext `claude/adonet-script-mapping-1e2aec` (plugin load context + `CancellationToken`) | **4/4 yeşil**, skip yok. İki yol aynı `loadId`, PostgreSQL 18.1, `next = 42`, `tokenCanBeCanceled = true` |
| vnext `c606709d` (değişiklik öncesi) | 4/4 kırmızı — mapping'ler `ScriptContext.CancellationToken`'ı bulamıyor (CS1061). Token'sız geçici kopyalarla: helper yolu çalışıyor, **helper'sız yol `Could not load file or assembly 'ScriptPluginLab.Sql'` ile 500** |

| vnext `master` @ `66859312` (yerel host'lar, macOS) — SQL Server genişlemesi (2026-10-19) | **7/7 yeşil**, skip yok. SQL Server 15.00.2000 (Azure SQL Edge): `adoNext = dapperNext = procedureResult = 42`. Host süreci `Microsoft.Data.SqlClient.dll` ve `Dapper.dll`'i `plugins/out`'tan açmış (host'un kendi bin'inde yoklar). `spl-sqlserver-query` 30 çağrı: avg 8.3 ms, p95 10.2 ms, max 30 ms (sıcak) |
| Aynı runtime, **Alpine orchestration imajı** (vnext `claude/db-migrator-libgssapi-error-c57fad`: `icu-libs icu-data-full krb5-libs`), yerel orchestration host'unun yerine container, `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` | **7/7 yeşil**. Container süreci `libicu*.so.76.1`'i ve `Microsoft.Data.SqlClient.dll`/`Dapper.dll`'i `/app/assemblies`'ten yüklemiş; `spl-sqlserver-query` 30 çağrı avg 3.0 ms, p95 3.9 ms, max 6.4 ms |
| Aynı Alpine imajı, invariant açık (imaj varsayılanı) | SQL Server 3/3 kırmızı, PostgreSQL 4/4 yeşil — `stage: open`, `NotSupportedException: Globalization Invariant Mode is not supported.` |
| Aynı runtime, bileşen 1.0.1 (`System.ComponentModel.Primitives` izni yok) | SQL Server 3/3 kırmızı — derleme `CS0012: The type 'Component' is defined in an assembly that is not referenced` |

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
- **SqlClient için iki framework assembly'sine de izin ver.** `allowedAssemblies`: `Microsoft.Data.SqlClient`,
  `Dapper`, `System.Data.Common` **ve** `System.ComponentModel.Primitives`. Sonuncusu yoksa her `SqlConnection`
  kullanımı derlemede `CS0012` (`DbConnection : Component`) ile düşer — sandbox yalnız izin verilen assembly'leri
  referans alır, dolaylı framework tiplerini kendiliğinden eklemez.
- **SqlClient zincirindeki açık paketleri yükselt.** SqlClient 6.1.1 `System.Security.Cryptography.Pkcs` 9.0.4'ü
  (yüksek önemli açık) çeker; taşıyıcı proje onu 10.0.13'e sabitler. Operatör kendi zincirini toplarken aynısını yapmalı.
- **Alpine imajlarda (orchestration/inbox/outbox/migrator) ICU şart.** SqlClient
  `Globalization Invariant Mode is not supported` der; `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` ICU'suz imajda
  süreci açılışta öldürür. vnext imajları `icu-libs icu-data-full` taşır (vnext `docs/custom-script-helpers.md` § 4).
  Alpine imajında plugin'li koşu yapıldı (Kanıt tablosu). Orchestration'ı container olarak koşmak için yerel
  host'u durdur, imajı `-p 4201:4201` ile `bbt-development` ağında başlat ve uygulamaya
  `DAPR_HTTP_ENDPOINT=http://vnext-orchestration-dapr:42110` / `DAPR_GRPC_ENDPOINT=…:42111` ver: sidecar uygulamaya
  zaten `host.orb.local:4201` üzerinden ulaşır.
- Plugin sorguyu `context.CancellationToken` ile çağırmazsa iptal edilen istek sorguyu durdurmaz;
  `CommandTimeout` tek sınır olur.
