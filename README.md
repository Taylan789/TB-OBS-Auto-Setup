# TB OBS Auto Setup

Windows için OBS otomatik kurulum yardımcı programı.

## GitHub Actions ile EXE

Bu proje GitHub Actions üzerinde Windows runner kullanarak .NET 8 ile derlenir. Kullanıcının bilgisayarına .NET kurulması gerekmez.

1. `.github/workflows/build-windows.yml` workflow'unu çalıştır.
2. İşlem tamamlanınca Actions > workflow run > Artifacts bölümünden `TB-OBS-AutoSetup-win-x64` paketini indir.
3. ZIP içindeki `TB_OBS_AutoSetup.exe` dosyasını Windows'ta çalıştır.

EXE self-contained ve single-file olarak yayınlanır.

## Güvenlik

Program stream key veya hesap parolası keşfetmez ve kaydetmez. OBS WebSocket parolası yalnızca yerel bağlantı için kullanıcı tarafından girilir.
