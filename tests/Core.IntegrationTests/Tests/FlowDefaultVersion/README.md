# FlowDefaultVersion — integration test

Bir sınıf, **altı çalışan test + bir Skip**; fixture flow'lar `default-version-lab-a … f` (domain `core`) ve registry flow'u
`sys-version-registry`.

| Class | Test | What it checks |
|---|---|---|
| `FlowDefaultVersionTests` | `NoRegistry_StartsLatest` | Registry kaydı olmayan flow'da versiyonsuz start en yeni sürümü (1.1.0) başlatır; `flowVersion == "1.1.0"` |
| `FlowDefaultVersionTests` | `FullPin_StartsPinned_AndPendingNeedsExplicit` | Pointer `1.0.0`: versiyonsuz → 1.0.0; `?version=1.0` → 1.0.0; `?version=1.1` → 404 `Instance:100051`; `?version=1.1.0` → 1.1.0 |
| `FlowDefaultVersionTests` | `LinePin_FollowsHotfix` | Pointer `"1.0"`: versiyonsuz start hat içinde kalır (1.0.1, yeniden koşuda 1.0.2); `1.0.2` yayınlanınca ≤ 10 sn içinde 1.0.2; 1.1.0 hiç seçilmez |
| `FlowDefaultVersionTests` | `Flip_And_Rollback_DoNotMoveRunningInstances` | 1.0.0'da doğan instance, pointer 1.1.0'a çevrilip geri alınınca (ve transition koşunca) `flowVersion == "1.0.0"` kalır; flip sırasında doğan 1.1.0 instance'ı rollback'ten sonra da 1.1.0 kalır |
| `FlowDefaultVersionTests` | `InvalidPointer_IsRejectedAtWrite` | `set-default "9.9"` → 409 `Instance:100052`; registry instance'ı `active`/`A` kalır, data `defaultVersion` değişmez |
| `FlowDefaultVersionTests` | `PointerFlip_UnderLoad_NeverStartsAnUnactivatedVersion` | Pointer 1.0.0 ↔ 1.0.1 çevrilirken 8 eşzamanlı versiyonsuz start döngüsü (~3 sn): hiçbir start 1.1.0'a düşmez (her `flowVersion` 1.0.0 veya 1.0.1); son flip'ten sonra ≤ 10 sn içinde start 1.0.0'a döner |

## Why this exists

Bir flow'un "varsayılan sürümü" artık `sys-version-registry` instance'ındaki pointer ile belirlenir
(instance key = flow key; data `{ "type": "Flow", "defaultVersion": "1.0.0"|"1.0"|"1"|null, "reason"? }`).
Kayıt yoksa davranış eskisi gibi (latest). Pointer varsa versiyonsuz start / `latest` etkin varsayılana
çözülür; `?version=M` / `M.m` pointer'ı aşmayan en iyi eşleşmeye; yalnız pointer'ın üstünde kalan sürüm
404 `Instance:100051`; açık artifact sürümü (`?version=1.1.0`) tavan tanımaz. Yayınlanmış hiçbir sürümle
eşleşmeyen pointer yazımı 409 `Instance:100052` ile reddedilir. Çalışan instance'lar pointer değişiminden
etkilenmez. Pointer değişimi flow'un cache generation'ını commit sonrası artırır; yayılma pod başına ~5 sn,
bu yüzden testler sabit bekleme yerine start'ı sınırlı (10 sn) yeniden deneyerek yoklar.

Yük testinin nedeni: pointer değişimi cache generation'ını commit **sonrası** artırır; bu pencerede
registry instance'ı `set-default` yüzünden Busy'dir. Pointer okuması yalnız Active registry'yi görseydi,
bu pencerede gelen versiyonsuz start pointer'ı yok sayıp hiç aktive edilmemiş en yeni sürümü (1.1.0)
çözümler ve cache'lerdi. Runtime artık pointer'ı registry instance'ının durumundan bağımsız okur.

## The fixture

- **Registry kopyası** — `fixtures/version-registry.schema.json` ve `fixtures/sys-version-registry.workflow.json`,
  vnext-sys-flow reposundan (`core/Schemas/version-registry.json`, `core/Workflows/sys-version-registry.json`,
  branch `flow-default-version`, commit `fa08068`) **birebir** kopyalanmıştır. Core sistem paketi npm'den
  yayınlandığı için bu flow runtime'da henüz yok; test önce schema'yı, sonra workflow'u
  `api/v1/definitions/publish` ile bir kez yayınlar ("already" cevabı sorun değil). **`@burgan-tech/vnext-core`
  bu flow'u taşımaya başlayınca bu kopyayı ve `EnsureRegistryAsync`'i sil.** `core/` dışında durur ki SDK
  yayıncısı kendiliğinden göndermesin (bkz. ImplicitStartLab, VersionPinLab).
- `core/Workflows/flow-default-version-lab/default-version-lab-{a..f}.json` **1.0.0** — `lab-initial` →
  manual `finish` → `lab-finished` (Finish). Her test kendi flow anahtarını kullanır, böylece testler tek
  registry kaydı için yarışmaz. SDK 1.0.0'ı `core/` altından yayınlar.
- **1.0.1 / 1.1.0 (ve `-c` için 1.0.2)** `core/` altında YOK; test 1.0.0 dosyasından kurar ve
  `definitions/publish` ile yayınlar. Yayın sürüm-immutable; tekrar koşuda "already" kabul edilir.
- Registry instance'ları domain'de (`core`) yaşar; her test sonunda pointer `null`'a (Automatic) çekilir
  (`set-default`), böylece yeniden koşu temiz başlar. Kayıt zaten varsa ikinci `create` yerine mevcut
  instance'a `set-default` uygulanır.

## What each test asserts, in order

### `NoRegistry_StartsLatest`
1. 1.0.1 ve 1.1.0'ı yayınla. 2. `-a`'yı versiyonsuz başlat. 3. `flowVersion == "1.1.0"`.

### `FullPin_StartsPinned_AndPendingNeedsExplicit`
1. Pointer `1.0.0`; start 1.0.0'a oturana kadar yokla. 2. Versiyonsuz → 1.0.0; `?version=1.0` → 1.0.0.
3. `?version=1.1` → 404 + `100051`. 4. `?version=1.1.0` → 1.1.0.

### `LinePin_FollowsHotfix`
1. Pointer `"1.0"`; start hat içinde (1.0.1/1.0.2) olmalı, 1.1.0 olmamalı. 2. `1.0.2`'yi yayınla.
3. ≤ 10 sn içinde versiyonsuz start 1.0.2 verir; 1.1.0 bekleyen sürüm olarak kalır.

### `Flip_And_Rollback_DoNotMoveRunningInstances`
1. Pointer 1.0.0, instance A başlat (1.0.0). 2. Pointer 1.1.0 → yeni start 1.1.0 (instance B). 3. A
`finish` koşar; `flowVersion` hâlâ 1.0.0. 4. Pointer 1.0.0'a geri; A 1.0.0, B 1.1.0 kalır.

### `InvalidPointer_IsRejectedAtWrite`
1. Pointer `1.0.0`. 2. `set-default` `"9.9"` (`sync=true`) → 409 + `100052`. 3. Registry instance'ı
`active` / `A`, data `defaultVersion` hâlâ `1.0.0`.

## Pitfalls

- **Sürüm biçimi:** SDK'nın yayınladığı 1.0.0 `1.0.0-pkg.1.0.0+core` olarak saklanır, test'in yayınladıkları düz `1.0.1`/`1.1.0`.
  Testler `flowVersion`'ı artifact kısmıyla (`-pkg.` ve `+` öncesi) karşılaştırır.
- **Bilinen açık — null ile temizleme çalışmaz:** `set-default` `defaultVersion: null` ile 200 döner ama instance-data
  merge'ü mevcut anahtarda null'u "değişiklik yok" sayar; satır yazılmaz, pointer kalır. Bu yüzden testler izolasyon için
  temizlemeye güvenmez: her test kendi flow anahtarında pointer'ı başta açıkça set eder, `NoRegistry` hiç registry kaydı
  almayan `-a`'yı kullanır. Boşluk `ClearingWithNull_IsAKnownGap` (Skip) ile işaretlidir. Registry kaydı artık tek Initial
  state'te (`active`) durur, `register` hop'u yoktur.

- Pointer değişimi cache generation'ını commit sonrası job ile artırır; hemen ardından yapılan start eski
  çözümü görebilir — sabit `Delay` değil `WaitForVersionAsync` (≤ 10 sn) kullan. Yoklama probe'ları gerçek
  instance başlatır; testler yalnız beklenen sürümde oturan probe'u sayar.
- Registry kaydı kalıcıdır; bir test yarıda ölürse pointer kalabilir. Sonraki koşu `set-default` ile
  üzerine yazar, cleanup `null`'a çeker.
- `LinePin_FollowsHotfix` 1.0.2'yi bir kez yayınlar; sonraki koşuda ilk adım zaten 1.0.2 görebilir (assert
  1.0.1 **veya** 1.0.2'yi kabul eder).
- Registry'ye yazılan body `{ "attributes": { type, defaultVersion } }` zarfıdır; start'ta `key` = flow anahtarı.
- Kapsam dışı: **cross-domain** testleri (`CrossDomain_FloatingRefFollowsCalleeDefault` /
  `CrossDomain_ExactRefIgnoresCalleeDefault`, subflow + subprocess correlation) bu suite'te yok;
  partner domain gerektirir — cross-domain lab'da ele alınır.
- Çalıştırma: `dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~FlowDefaultVersion"`.
