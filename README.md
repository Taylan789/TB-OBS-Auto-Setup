# TB OBS Auto Setup V9 — One-Click Live Center

Bu proje Windows için hazırlanmış bir WPF uygulamasıdır. Amaç yalnızca OBS sahneleri oluşturmak değil; OBS bağlantısını kontrol etmek, yayın kaynaklarını ve cihazlarını hazırlamak, ön kontrol yapmak ve kullanıcı son onayıyla canlı yayını başlatmaktır.

## GitHub'a yükleme

1. ZIP dosyasını bilgisayarında **klasöre çıkart**.
2. GitHub'daki `TB-OBS-Auto-Setup` deposunu aç.
3. ZIP'in içindeki dosya ve klasörleri depo ana dizinine yükle; `App.xaml` ve `TB_OBS_AutoSetup.csproj` doğrudan depo ana dizininde olmalı. ZIP dosyasını tek başına GitHub'a yüklemek yeterli değildir.
4. `.github/workflows/build.yml` dosyası da `.github/workflows/` altında bulunmalı.
5. GitHub'da **Actions → Build Windows EXE → Run workflow** seç.
6. Workflow başarıyla bittiğinde `TB-OBS-AutoSetup-win-x64` artifact'ını indirip ZIP'ten çıkar. İçindeki EXE tek başına çalışacak şekilde yayımlanır; hedef bilgisayara .NET Runtime kurmak gerekmez.

## Tek tıkta yayına geçiş

1. OBS Studio kurulu olmalı.
2. OBS'de **Araçlar → WebSocket Sunucu Ayarları** kısmından WebSocket'i etkinleştir. Varsayılan port genellikle `4455` olur. Şifre ayarlıysa uygulamaya gir.
3. OBS'de yayın servisini daha önce ayarladıysan uygulamadaki Custom RTMP alanını boş bırak. Uygulamanın Custom RTMP alanını kullanacaksan yayın platformunun verdiği sunucu adresini ve anahtarı kendin girip kutuyu işaretle.
4. Kaynak türünü seç: **PC oyunu (Game Capture)** veya **PS5 / HDMI yakalama kartı**.
5. **TEK TIK: HAZIRLA + CANLIYA GEÇ** düğmesine bas. Program OBS'yi başlatmayı/bağlanmayı dener, kaynakları hazırlar, ön kontrolleri yapar ve canlı yayından hemen önce onay ister. Onay verildiğinde OBS'nin `StartStream` isteğini gönderir ve yayın durumunu doğrular.

## Uygulamanın oluşturduğu/ayarladığı şeyler

- `TB OYUN`, `TB JUST CHATTING`, `TB YAYIN BASLIYOR`, `TB BRB`, `TB YAYIN BITIYOR` sahneleri.
- Seçilen moda göre Game Capture veya HDMI yakalama kartı kaynağı.
- Mevcut cihaz listesi üzerinden kamera, mikrofon ve masaüstü sesi kaynaklarını hazırlama girişimi.
- Upload ölçümü, güvenli bitrate önerisi ve seçilen çözünürlük/FPS ayarları.
- MKV kayıt önerisi, isteğe bağlı yayınla birlikte kayıt ve Replay Buffer başlatma.
- OBS Health/istatistik kontrolü, kaynak envanteri ve gizli yayın anahtarı içermeyen tanılama raporu.
- OBS temel yapılandırmasının yedeği ve güvenli geri yükleme.

## Güvenlik ve sınırlar

- Uygulama mevcut OBS yayın anahtarını okumaya çalışmaz. İsteğe bağlı Custom RTMP alanına anahtarı kendin girersen yalnızca OBS hedefini yapılandırmak için kullanılır; OBS bu hedefi kendi normal ayarları gibi saklar.
- Uygulama tarafından hazırlanan yedek ve tanılama raporuna `service.json` veya stream key eklenmez.
- Yayına geçmeden önce **son onay ekranı** gösterilir. Bu, yanlış hedefe veya yanlış görüntüyle yayına çıkma riskini azaltır.
- OBS API'si kameradan/HDMI kartından gerçekten görüntü geldiğini ya da mikrofonun fiziksel ses aldığını her zaman kesin doğrulayamaz. İlk yayından önce OBS önizlemesinde görüntüyü ve ses mikserini kontrol et.
- Desteklenmeyen kaynak türü, filtre veya OBS profil parametresi otomatik zorlanmaz; logda neden belirtilir.
- Uygulama mevcut kullanıcı sahnelerini silmez; kendi yönettiği kaynaklarda `TB ` önekini kullanır.

## Derleme durumu

Kaynak paketindeki GitHub Actions workflow Windows runner üzerinde .NET 8 ile `win-x64` self-contained EXE üretir. Bu geliştirme ortamında Windows/OBS oturumu olmadığı için burada gerçek OBS ile uçtan uca yayın testi yapıldığı iddia edilmemektedir. Derleme sonucu ve gerçek cihaz/OBS davranışı GitHub Actions ve kendi OBS kurulumunda doğrulanmalıdır.
