# TB OBS Auto Setup V28 — OBS For Everyone

V28'in ana özelliği **TB AI**: kullanıcı doğal Türkçe ile OBS hakkında konuşur ve AI gerektiğinde OBS WebSocket üzerinden gerçek işlemleri yapar.

## Örnek komutlar
- PS5 için yayın sahnesi oluştur.
- Aktif sahneyi Oyun yap.
- Kamerayı sağ alta koy.
- Şu kaynağı gizle.
- Kaydı başlat.
- Yayını başlat.
- 1080p 60 FPS yap.
- OBS durumumu kontrol et.

## Mimari
Kullanıcı → OpenAI Responses API → güvenli TB OBS tools → OBS WebSocket → doğrulama.

AI doğrudan rastgele kod çalıştırmaz. Yalnızca uygulamanın tanımladığı OBS tool'larını çağırabilir.

## Kurulum
1. OBS 28+ aç ve obs-websocket'i etkinleştir.
2. OBS WebSocket portunu 4455 olarak bırak veya sonraki sürümde bağlantı ayarlarını kullan.
3. Uygulamayı çalıştır.
4. OpenAI API key'ini TB AI ekranına gir.
5. OBS'ye bağlan.
6. AI'ya doğal Türkçe komut ver.

> Not: Bu sürüm Windows üzerinde GitHub Actions ile publish edilmek üzere hazırlanmıştır. Bu geliştirme ortamında Windows/.NET SDK bulunmadığı için burada gerçek `dotnet publish` doğrulaması yapılmamıştır.
