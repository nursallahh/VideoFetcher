# Video Fetcher Katkı Rehberi

Katkıda bulunmak istediğiniz için teşekkürler!

## Kodlama Standartları
1. **Mimari:** `VideoFetcher.Core` katmanı UI ve işletim sistemine özgü bağımlılıklardan arındırılmış kalmalıdır.
2. **SOLID & DI:** Yeni servisler arayüz (`VideoFetcher.Core/Interfaces`) üzerinden tanımlanmalı ve `ServiceCollectionExtensions` içinde kaydedilmelidir.
3. **Asenkronluk & İptal:** Tüm G/Ç ve süreç çağrıları `async/await` kullanmalı ve `CancellationToken` kabul etmelidir.
4. **Test:** Yeni eklenen her ayrıştırıcı veya iş kuralı için `tests/VideoFetcher.UnitTests` altında xUnit testi yazılmalıdır.

## Pull Request Süreci
1. Repo'yu fork edin ve feature branch açın (`feat/ozellik-adi`).
2. `dotnet format --verify-no-changes` ve `dotnet test` komutlarının hatasız geçtiğinden emin olun.
3. PR açıklamasında değişikliğin amacını ve test sonuçlarını paylaşın.
