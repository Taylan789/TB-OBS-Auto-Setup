# TB OBS Auto Setup V34

TB OBS, OBS'yi herkes için anlaşılır hale getiren Türkçe AI yardımcı uygulamasıdır.

## V34 performans mimarisi

- OBS WebSocket bağlantısı tek ve kalıcıdır.
- Ayrı bir receive loop OBS mesajlarını sürekli dinler.
- Request/response mesajları `requestId` ile eşleştirilir.
- Aynı anda birden fazla OBS isteği bekleyebilir; yalnızca WebSocket gönderimleri sıraya alınır.
- OBS event geldiğinde AI/uygulama cache'i geçersizleşir. Böylece gereksiz polling azalır.
- AI geçmişi son 16 mesajla sınırlandırılır.
- Mutasyonlardan sonra cache temizlenir.
- OBS bağlantısı koparsa bekleyen istekler güvenli şekilde sonlandırılır.

## AI

TB AI yerel Ollama servisi üzerinden çalışır. Kullanıcıdan OpenAI API key istenmez.

Varsayılan servis:
`http://127.0.0.1:11434`

İlk kurulumda uygun model yoksa uygulama `qwen3:4b` indirmeyi deneyebilir.

## OBS WebSocket

OBS 28 ve üzeri sürümlerde WebSocket yerleşik gelir.

1. OBS'yi aç.
2. Araçlar -> WebSocket Sunucu Ayarları.
3. WebSocket sunucusunu etkinleştir.
4. Portu kontrol et (varsayılan 4455).
5. Şifre kullanıyorsan TB OBS bağlantı ekranına gir.
6. TB OBS'den bağlan.

## Güvenlik

AI yalnızca uygulamanın tanımladığı OBS tool'larını çalıştırabilir. Rastgele Windows komutu çalıştırmaz.
Stream key gibi gizli bilgiler AI geçmişine gönderilmemelidir.

## Build

GitHub Actions Windows üzerinde .NET 8 self-contained single-file EXE üretir.
Assembly adı `TB_OBS_Easy.exe`'dir.


## V34.1 build fix

Added explicit `using System.IO;` imports to `ObsWebSocketClient.cs` and `HardwareInfoService.cs` so builds do not depend on generated implicit global usings for `MemoryStream`, `InvalidDataException`, `File`, and `Path`. No runtime behavior was changed.
