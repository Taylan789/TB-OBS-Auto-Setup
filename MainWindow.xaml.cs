using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TB_OBS_AutoSetup;

public partial class MainWindow : Window
{
    private const string AppTitle = "TB OBS Auto Setup V9 One-Click Live Center";
    private const string Prefix = "TB ";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private string obsPath = "";
    private string cpu = "Bilinmiyor";
    private string gpu = "Bilinmiyor";
    private string obsVersion = "Bilinmiyor";
    private string websocketVersion = "Bilinmiyor";
    private int ramGb;
    private int cpuCores;
    private int canvasWidth = 1920;
    private int canvasHeight = 1080;
    private ObsWebSocketClient? obs;
    private string lastBackup = "";
    private double lastUploadMbps;
    private int recommendedBitrateKbps;
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private readonly HashSet<string> inputKinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> sceneNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> inputNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> inputKindByName = new(StringComparer.OrdinalIgnoreCase);
    private bool setupRunning;
    private bool preparedForLive;
    private bool recordStartedByApp;
    private bool streamStartRequested;
    private bool customRtmpAppliedThisRun;

    private sealed record DeviceChoice(string Name, string Value);

    public MainWindow()
    {
        InitializeComponent();
        Analyze();
        Log("TB OBS Auto Setup V9 One-Click Live Center hazır.");
        Log("Tek tuş akışı: hedefi/cihazları hazırla → ön kontrol → son onay → StartStream. OBS'de kayıtlı yayın anahtarı okunmaz; özel RTMP anahtarı yalnızca kullanıcı girerse OBS'ye uygulanır.");
        UpdateBitrateRecommendation();
    }

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Log(message));
            return;
        }

        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        if (LogBox.Text.Length > 450_000)
        {
            string current = LogBox.Text;
            int cut = current.Length - 300_000;
            int nextLine = current.IndexOf('\n', Math.Max(0, cut));
            LogBox.Text = nextLine >= 0 ? current[(nextLine + 1)..] : current[^300_000..];
        }
        LogBox.ScrollToEnd();
    }

    private void Analyze_Click(object sender, RoutedEventArgs e) => Analyze();

    private void Analyze()
    {
        try
        {
            cpu = GetCpu();
            gpu = GetGpu();
            ramGb = GetRamGb();
            cpuCores = Environment.ProcessorCount;
            obsPath = FindObs();

            CpuCard.Text = Shorten(cpu, 42);
            GpuCard.Text = Shorten(gpu, 42);
            RamCard.Text = ramGb > 0 ? $"{ramGb} GB • {cpuCores} thread" : $"RAM bilinmiyor • {cpuCores} thread";
            EncoderCard.Text = GetEncoderPlan();
            PlanText.Text = obsPath.Length == 0 ? "OBS bulunamadı" : "Sistem hazır";
            Log("Donanım ve OBS yolu taraması tamamlandı.");
            Log("Donanım önerisi: " + GetEncoderPlan() + ". Bu bir öneridir; program encoder'ı yalnızca GPU adına bakarak değiştirmez.");
            if (obsPath.Length == 0)
                Log("OBS otomatik bulunamadı. Normal kurulum veya Steam OBS Studio kurulumu kontrol edilmeli.");
            else
                Log("OBS yolu: " + obsPath);
        }
        catch (Exception ex)
        {
            Log("Analiz hatası: " + ex.Message);
        }
    }

    private string GetEncoderPlan()
    {
        string name = gpu.ToLowerInvariant();
        if (name.Contains("nvidia")) return "NVENC adayı";
        if (name.Contains("amd") || name.Contains("radeon")) return "AMD HW adayı";
        if (name.Contains("intel")) return "QSV adayı";
        return cpuCores >= 8 ? "x264 adayı" : "720p30 önerisi";
    }

    private static string Shorten(string value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? "Bilinmiyor" : value.Length > maxLength ? value[..maxLength] + "…" : value;

    private int GetRamGb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("select TotalPhysicalMemory from Win32_ComputerSystem");
            foreach (ManagementObject item in searcher.Get())
            {
                if (item["TotalPhysicalMemory"] is not null)
                    return (int)Math.Round(Convert.ToDouble(item["TotalPhysicalMemory"]) / 1024d / 1024d / 1024d);
            }
        }
        catch (Exception ex) { Log("RAM sorgusu yapılamadı: " + ex.Message); }
        return 0;
    }

    private string GetCpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("select Name from Win32_Processor");
            foreach (ManagementObject item in searcher.Get()) return item["Name"]?.ToString() ?? "Bilinmiyor";
        }
        catch (Exception ex) { Log("CPU sorgusu yapılamadı: " + ex.Message); }
        return "Bilinmiyor";
    }

    private string GetGpu()
    {
        try
        {
            var names = new List<string>();
            using var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController");
            foreach (ManagementObject item in searcher.Get())
            {
                string? name = item["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            if (names.Count == 0) return "Bilinmiyor";
            // Prefer a discrete GPU for advice when WMI lists multiple adapters.
            return names.FirstOrDefault(x => x.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                ?? names.FirstOrDefault(x => x.Contains("AMD", StringComparison.OrdinalIgnoreCase) || x.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                ?? names.FirstOrDefault(x => x.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                ?? names[0];
        }
        catch (Exception ex)
        {
            Log("GPU sorgusu yapılamadı: " + ex.Message);
            return "Bilinmiyor";
        }
    }

    private string FindObs()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] paths =
        {
            Path.Combine(programFiles, "obs-studio", "bin", "64bit", "obs64.exe"),
            Path.Combine(programFilesX86, "obs-studio", "bin", "64bit", "obs64.exe"),
            Path.Combine(programFilesX86, "Steam", "steamapps", "common", "OBS Studio", "bin", "64bit", "obs64.exe"),
            Path.Combine(programFiles, "Steam", "steamapps", "common", "OBS Studio", "bin", "64bit", "obs64.exe"),
            Path.Combine(local, "Programs", "obs-studio", "bin", "64bit", "obs64.exe"),
            Path.Combine(AppContext.BaseDirectory, "obs64.exe"),
            Path.Combine(AppContext.BaseDirectory, "bin", "64bit", "obs64.exe")
        };
        return paths.FirstOrDefault(File.Exists) ?? "";
    }

    private void LaunchObs_Click(object sender, RoutedEventArgs e)
    {
        if (obsPath.Length == 0)
        {
            Log("OBS bulunamadı. Önce Sistem Analizi'ne bas; OBS farklı bir klasördeyse program şu an otomatik tespit edemiyor.");
            return;
        }
        try
        {
            if (Process.GetProcessesByName("obs64").Length == 0)
                Process.Start(new ProcessStartInfo(obsPath) { UseShellExecute = true });
            Log("OBS açık veya başlatma isteği gönderildi. WebSocket bağlantısının hazır olması birkaç saniye sürebilir.");
        }
        catch (Exception ex) { Log("OBS başlatma hatası: " + ex.Message); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (setupRunning) { Log("Kurulum devam ediyor; bağlantı işlemi sıraya alınmadı."); return; }
        try
        {
            await EnsureConnected();
            try { await RefreshObsInventory(); }
            catch (Exception ex) { Log("Bağlantı var fakat envanter alınamadı: " + ex.Message); }
            try { await HealthCheck(); }
            catch (Exception ex) { Log("Bağlantı başarılı; ilk health okuması atlandı: " + ex.Message); }
        }
        catch (Exception ex)
        {
            SetConnection(false);
            Log("OBS bağlantı hatası: " + ex.Message);
        }
    }

    private async Task EnsureObsRunningAndConnectedAsync()
    {
        bool running = Process.GetProcessesByName("obs64").Length > 0;
        if (!running)
        {
            if (obsPath.Length == 0)
                throw new InvalidOperationException("OBS otomatik bulunamadı. OBS Studio'yu kur veya OBS'yi bir kez aç; program özel kurulum yolunu tespit edemedi.");
            try
            {
                Process.Start(new ProcessStartInfo(obsPath) { UseShellExecute = true });
                Log("Tek tuş akışı OBS'yi başlattı; WebSocket hazır olana kadar bağlantı denenecek.");
            }
            catch (Exception ex) { throw new InvalidOperationException("OBS başlatılamadı: " + ex.Message); }
        }

        Exception? lastError = null;
        for (int attempt = 1; attempt <= 12; attempt++)
        {
            try
            {
                await EnsureConnected();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < 12) await Task.Delay(750);
            }
        }

        throw new InvalidOperationException("OBS açıldı ancak WebSocket bağlantısı kurulamadı. OBS > Araçlar > WebSocket Sunucu Ayarları bölümünden sunucuyu etkinleştir, portu 4455 ve şifreyi kontrol et. Son hata: " + (lastError?.Message ?? "bilinmiyor"));
    }

    private async Task EnsureConnected()
    {
        await connectionLock.WaitAsync();
        try
        {
            if (obs is not null && obs.Connected) return;
            if (obs is not null)
            {
                await obs.DisposeAsync();
                obs = null;
            }

            string host = HostBox.Text.Trim();
            if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
                throw new InvalidOperationException("OBS WebSocket portu 1-65535 arasında olmalı. Varsayılan port 4455.");

            var candidate = new ObsWebSocketClient();
            try
            {
                await candidate.ConnectAsync(host, port, PasswordBox.Password);
                obs = candidate;
                var version = await obs.CallAsync("GetVersion");
                obsVersion = ReadString(version, "obsVersion", "Bilinmiyor");
                websocketVersion = ReadString(version, "obsWebSocketVersion", "Bilinmiyor");
                SetConnection(true);
                Log($"OBS bağlantısı başarılı. OBS {obsVersion} • WebSocket {websocketVersion} • {host}:{port}");
            }
            catch
            {
                await candidate.DisposeAsync();
                obs = null;
                SetConnection(false);
                throw;
            }
        }
        finally { connectionLock.Release(); }
    }

    private void SetConnection(bool connected)
    {
        ConnectionText.Text = connected ? "● OBS BAĞLI" : "● OBS BAĞLI DEĞİL";
        ConnectionText.Foreground = connected ? Brushes.LightGreen : Brushes.Gray;
    }

    private async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        await PrepareForLiveAsync(showCompletionMessage: true);
    }

    private async void PrepareAndGoLive_Click(object sender, RoutedEventArgs e)
    {
        if (setupRunning) return;
        bool ready = preparedForLive || await PrepareForLiveAsync(showCompletionMessage: false);
        if (!ready) return;
        await StartStreamingWorkflowAsync();
    }

    private async void GoLive_Click(object sender, RoutedEventArgs e)
    {
        if (setupRunning) return;
        // Treat the Go Live button as a smart action: when setup is missing or any
        // device/profile selection changed, prepare first instead of merely failing preflight.
        bool ready = preparedForLive || await PrepareForLiveAsync(showCompletionMessage: false);
        if (!ready) return;
        await StartStreamingWorkflowAsync();
    }

    private async void StopLive_Click(object sender, RoutedEventArgs e)
    {
        if (setupRunning) return;
        setupRunning = true;
        try
        {
            await EnsureConnected();
            var stream = await obs!.CallAsync("GetStreamStatus");
            bool isStreaming = ReadRequiredBool(stream, "outputActive", "OBS yayın durumu");
            if (isStreaming)
            {
                if (MessageBox.Show("Canlı yayını durdurmak istiyor musun?", AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                await obs.CallAsync("StopStream");
                bool stopped = false;
                for (int i = 0; i < 15; i++)
                {
                    await Task.Delay(500);
                    var status = await obs.CallAsync("GetStreamStatus");
                    if (!ReadRequiredBool(status, "outputActive", "OBS yayın durumu")) { stopped = true; break; }
                }
                Log(stopped ? "Canlı yayın durduruldu ve OBS durumundan doğrulandı." : "Yayını durdurma isteği gönderildi; OBS henüz durduğunu doğrulamadı.");
            }
            else Log("OBS zaten canlı yayın yapmıyor.");

            if (recordStartedByApp)
            {
                try
                {
                    var record = await obs.CallAsync("GetRecordStatus");
                    if (ReadRequiredBool(record, "outputActive", "OBS kayıt durumu"))
                        await obs.CallAsync("StopRecord");
                    recordStartedByApp = false;
                    Log("Bu uygulamanın başlattığı yerel kayıt da durduruldu.");
                }
                catch (Exception ex) { Log("Yayın durdu fakat yerel kayıt durdurulamadı: " + ex.Message); }
            }
            PlanText.Text = "Yayın kapalı";
            HealthCard.Text = "YAYIN KAPALI";
            ReadinessText.Text = "Yayın durduruldu.";
        }
        catch (Exception ex)
        {
            Log("Yayını durdurma hatası: " + ex.Message);
            MessageBox.Show("Yayın durdurulurken hata oluştu:\n\n" + ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { setupRunning = false; }
    }

    private async Task<bool> PrepareForLiveAsync(bool showCompletionMessage)
    {
        if (setupRunning) return false;
        setupRunning = true;
        preparedForLive = false;
        customRtmpAppliedThisRun = false;
        lastBackup = "";
        try
        {
            PlanText.Text = "OBS kontrol ediliyor…";
            ReadinessText.Text = "OBS bağlantısı ve çıkış durumu kontrol ediliyor…";
            Log("════════ V9 YAYIN HAZIRLIĞI BAŞLADI ════════");
            await EnsureObsRunningAndConnectedAsync();
            await RefreshObsInventory();

            if (await IsOutputRunning())
                throw new InvalidOperationException("OBS yayın veya kayıt yapıyor. Ayarları değiştirmeden önce aktif çıkışları durdur.");

            lastBackup = BackupObsConfig();
            if (lastBackup.Length == 0 || !File.Exists(Path.Combine(lastBackup, "manifest.json")))
                throw new InvalidOperationException("Güvenli OBS ayar yedeği oluşturulamadı. Ayar değiştirilmedi.");
            Log("Geri dönüş noktası alındı (service.json ve yayın anahtarı yedeğe dahil edilmedi): " + lastBackup);

            // Configure a user-supplied RTMP destination only when the explicit checkbox is selected.
            // The key is never written to log files, exported diagnostics, or application-created backups.
            await ConfigureOptionalCustomRtmpAsync();

            // When the user did not provide an upload measurement, attempt one automatically.
            // If the network test fails we keep the existing OBS bitrate and expose a warning rather than guessing.
            if (lastUploadMbps <= 0 && !TryParseMbps(UploadBox.Text, out _))
            {
                try
                {
                    PlanText.Text = "Upload hızı ölçülüyor…";
                    lastUploadMbps = await MeasureUploadMbps(4 * 1024 * 1024);
                    UploadBox.Text = lastUploadMbps.ToString("0.0", CultureInfo.InvariantCulture);
                    Log($"Otomatik upload ölçümü: {lastUploadMbps:0.0} Mbps.");
                }
                catch (Exception ex) { Log("Otomatik upload testi tamamlanamadı; mevcut OBS bitrate'i korunacak: " + ex.Message); }
            }
            UpdateBitrateRecommendation();

            PlanText.Text = "Video ve kayıt ayarları…";
            await ApplyVideoSafely();
            await ApplyRecordingDirectory();
            await ConfigureRecordingSafety();

            PlanText.Text = "Sahneler ve cihazlar hazırlanıyor…";
            await CreateProfessionalScenes();
            await ConfigureReadyAudioSources();
            await ConfigureAudioFilters();
            await ApplySelectedDeviceSettings();
            await RefreshObsInventory();

            if (!sceneNames.Contains("TB OYUN"))
                throw new InvalidOperationException("TB OYUN sahnesi oluşturulamadı. Yayına geçmeden önce OBS sahne kaydını kontrol et.");

            await obs!.CallAsync("SetCurrentProgramScene", new { sceneName = "TB OYUN" });
            var currentScene = await obs.CallAsync("GetCurrentProgramScene");
            string currentSceneName = ReadString(currentScene, "currentProgramSceneName", "");
            if (!string.Equals(currentSceneName, "TB OYUN", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OBS program sahnesi TB OYUN olarak doğrulanamadı.");
            Log("Canlı yayın için program sahnesi TB OYUN olarak ayarlandı.");

            try { await HealthCheck(); }
            catch (Exception ex) { Log("Kurulum sonrası health verisi alınamadı: " + ex.Message); }

            var preflight = await CheckLiveReadinessAsync();
            if (preflight.Blockers.Count > 0)
            {
                ReadinessText.Text = "Eksik: " + string.Join(" • ", preflight.Blockers);
                PlanText.Text = "Yayın için eksik adım var";
                Log("Yayın ön kontrolü engel buldu: " + string.Join(" | ", preflight.Blockers));
                MessageBox.Show("Sahneler hazırlanmış olabilir ancak yayın başlatılmaya hazır değil:\n\n" + string.Join("\n• ", preflight.Blockers) + "\n\nLog bölümünde ayrıntı var. Eksikleri giderip tekrar dene.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            preparedForLive = true;
            string warningsText = preflight.Warnings.Count == 0 ? "Zorunlu kontroller geçti." : "Kontrol et: " + string.Join(" • ", preflight.Warnings);
            ReadinessText.Text = "HAZIRLIK TAMAM • " + warningsText;
            ReadinessText.Foreground = preflight.Warnings.Count == 0 ? Brushes.LightGreen : Brushes.Gold;
            PlanText.Text = "Yayına hazır — başlatma onayı bekleniyor";
            Log("════════ V9 YAYIN HAZIRLIĞI TAMAMLANDI ════════");
            Log("Aktif sahne TB OYUN; seçili kaynak modu: " + GetSelectedSourceMode());
            Log(customRtmpAppliedThisRun
                ? "Bu hazırlıkta kullanıcı tarafından verilen Custom RTMP hedefi OBS'ye uygulandı."
                : "OBS'de kayıtlı yayın hedefi kullanılacak; uygulama kayıtlı yayın anahtarını okumaz.");
            if (showCompletionMessage)
            {
                MessageBox.Show(
                    "OBS yayına hazırlandı.\n\n" + warningsText + "\n\nÖnizlemede oyunun/yakalama kartının geldiğini ve mikrofondan ses geldiğini kontrol et. Ardından YAYINI BAŞLAT düğmesine basabilirsin.",
                    AppTitle, MessageBoxButton.OK, preflight.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            return true;
        }
        catch (Exception ex)
        {
            PlanText.Text = "Hazırlık tamamlanamadı";
            ReadinessText.Text = "Hazırlık tamamlanamadı; log bölümüne bak.";
            Log("YAYIN HAZIRLIĞI DURDURULDU: " + ex.Message);
            if (lastBackup.Length > 0) Log("Geri dönüş noktası: " + lastBackup);
            MessageBox.Show("OBS hazırlığı tamamlanamadı.\n\n" + ex.Message + "\n\nProgram canlı yayına geçmedi.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { setupRunning = false; }
    }

    private async Task StartStreamingWorkflowAsync()
    {
        if (setupRunning) return;
        setupRunning = true;
        try
        {
            await EnsureObsRunningAndConnectedAsync();
            await ConfigureOptionalCustomRtmpAsync();
            await RefreshObsInventory();
            var preflight = await CheckLiveReadinessAsync();
            if (preflight.Blockers.Count > 0)
            {
                preparedForLive = false;
                ReadinessText.Text = "Yayın başlatılamaz: " + string.Join(" • ", preflight.Blockers);
                ReadinessText.Foreground = Brushes.OrangeRed;
                Log("YAYIN ENGELLENDİ: " + string.Join(" | ", preflight.Blockers));
                MessageBox.Show("Ön kontrol başarısız. Yayın başlatılmadı:\n\n• " + string.Join("\n• ", preflight.Blockers), AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string platform = (PlatformBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Platform";
            string mode = GetSelectedSourceMode();
            string bitrate = recommendedBitrateKbps > 0 ? recommendedBitrateKbps + " Kbps öneri" : "OBS'deki mevcut bitrate";
            var confirmationText = new StringBuilder();
            confirmationText.AppendLine("Canlı yayın şimdi başlatılacak.");
            confirmationText.AppendLine();
            confirmationText.AppendLine("Platform profili: " + platform);
            confirmationText.AppendLine("Görüntü kaynağı: " + mode);
            confirmationText.AppendLine("Bitrate: " + bitrate);
            if (RecordAlongsideCheckBox.IsChecked == true)
                confirmationText.AppendLine("Yerel kayıt: yayınla birlikte MKV kayıt da başlatılacak.");
            if (ReplayBufferAlongsideCheckBox.IsChecked == true)
                confirmationText.AppendLine("Replay Buffer: yayın başlayınca açılması denenecek.");
            confirmationText.AppendLine();
            confirmationText.AppendLine(customRtmpAppliedThisRun
                ? "Yayın hedefi: bu oturumda senin girdiğin Custom RTMP hedefi OBS'ye uygulandı. Anahtar giriş alanı temizlendi; OBS hedefi kendi ayarında saklar."
                : "Yayın hedefi: OBS'de önceden kayıtlı yayın servisi kullanılacak. Uygulama kayıtlı stream key'i okumaz. OBS > Ayarlar > Yayın bölümünde hedefin doğru olduğundan emin ol.");
            if (preflight.Warnings.Count > 0)
            {
                confirmationText.AppendLine();
                confirmationText.AppendLine("Kontrol etmen gerekenler:");
                foreach (string warning in preflight.Warnings) confirmationText.AppendLine("• " + warning);
            }
            confirmationText.AppendLine();
            confirmationText.Append("Yayına çıkmak istediğine emin misin?");
            if (MessageBox.Show(confirmationText.ToString(), AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                Log("Canlı başlatma kullanıcı tarafından iptal edildi; OBS hazır durumda bırakıldı.");
                return;
            }

            var streamBefore = await obs!.CallAsync("GetStreamStatus");
            if (ReadRequiredBool(streamBefore, "outputActive", "OBS yayın durumu"))
            {
                Log("Yayın zaten aktif; ikinci StartStream isteği gönderilmedi.");
                PlanText.Text = "Zaten canlı yayındasın";
                return;
            }

            streamStartRequested = true;
            PlanText.Text = "OBS yayın başlatıyor…";
            await obs.CallAsync("StartStream");
            bool streamActive = false;
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                var status = await obs.CallAsync("GetStreamStatus");
                if (ReadRequiredBool(status, "outputActive", "OBS yayın durumu"))
                {
                    streamActive = true;
                    break;
                }
            }
            if (!streamActive)
                throw new InvalidOperationException("OBS StartStream isteğini aldı ancak yayın 15 saniye içinde aktif görünmedi. OBS'deki servis, yayın anahtarı, ağ bağlantısı ve OBS logunu kontrol et.");

            Log("CANLI YAYIN BAŞLADI ve OBS durumundan doğrulandı.");
            PlanText.Text = "CANLIDASIN";
            HealthCard.Text = "CANLI";
            HealthCard.Foreground = Brushes.LightGreen;
            ReadinessText.Text = "CANLI YAYIN AKTİF • OBS çıkışı doğrulandı.";
            ReadinessText.Foreground = Brushes.LightGreen;

            if (ReplayBufferAlongsideCheckBox.IsChecked == true)
            {
                // Replay Buffer is optional: failure must never stop an already-live stream.
                await TryStartReplayBuffer();
            }

            if (RecordAlongsideCheckBox.IsChecked == true)
            {
                try
                {
                    var record = await obs.CallAsync("GetRecordStatus");
                    if (!ReadRequiredBool(record, "outputActive", "OBS kayıt durumu"))
                    {
                        await obs.CallAsync("StartRecord");
                        bool activeRecord = false;
                        for (int i = 0; i < 10; i++)
                        {
                            await Task.Delay(300);
                            var check = await obs.CallAsync("GetRecordStatus");
                            if (ReadRequiredBool(check, "outputActive", "OBS kayıt durumu")) { activeRecord = true; break; }
                        }
                        recordStartedByApp = activeRecord;
                        Log(activeRecord ? "Yerel MKV kayıt da başlatıldı." : "Yayın başladı ancak yerel kayıt aktifliği doğrulanamadı.");
                    }
                    else Log("Yerel kayıt zaten aktif; uygulama bu kaydı kendisi başlatmadığı için durdurma kontrolünü devralmayacak.");
                }
                catch (Exception ex) { Log("Yayın başladı fakat yerel kayıt başlatılamadı: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            Log("CANLI BAŞLATMA HATASI: " + ex.Message);
            PlanText.Text = "Yayın başlatılamadı";
            ReadinessText.Text = "Yayın başlatılamadı. OBS servis/yayın ayarlarını ve logunu kontrol et.";
            ReadinessText.Foreground = Brushes.OrangeRed;
            MessageBox.Show("OBS canlı yayını başlatamadı.\n\n" + ex.Message + "\n\nOBS > Ayarlar > Yayın bölümünde doğru platform ve yayın anahtarı ayarlı mı kontrol et. Uygulama bu bilgileri okumaz.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            streamStartRequested = false;
            setupRunning = false;
        }
    }

    private sealed class LivePreflightResult
    {
        public List<string> Blockers { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    private async Task<LivePreflightResult> CheckLiveReadinessAsync()
    {
        var result = new LivePreflightResult();
        var streamStatus = await obs!.CallAsync("GetStreamStatus");
        if (ReadRequiredBool(streamStatus, "outputActive", "OBS yayın durumu"))
        {
            result.Blockers.Add("OBS yayını zaten aktif; ikinci kez yayın başlatılamaz.");
            return result;
        }

        var mode = GetSelectedSourceMode();
        string expectedSource = mode.StartsWith("PS5", StringComparison.OrdinalIgnoreCase) ? "TB CAPTURE CARD" : "TB GAME CAPTURE";
        if (!sceneNames.Contains("TB OYUN")) result.Blockers.Add("TB OYUN sahnesi OBS'de bulunamadı.");
        if (!inputNames.Contains(expectedSource)) result.Blockers.Add($"Seçili mod için {expectedSource} kaynağı yok. Önce 'Yayına Hazırla' çalıştır.");
        else
        {
            string expectedKind = mode.StartsWith("PS5", StringComparison.OrdinalIgnoreCase) ? "dshow_input" : "game_capture";
            if (inputKindByName.TryGetValue(expectedSource, out string? actualKind) && actualKind.Length > 0 &&
                !InputKindsCompatible(actualKind, expectedKind))
                result.Blockers.Add($"{expectedSource} yanlış OBS kaynak türü ({actualKind}). Doğru tür {expectedKind}; kaynak çakışmasını OBS'de düzelt.");
            if (!await HasSceneSource("TB OYUN", expectedSource)) result.Blockers.Add($"{expectedSource}, TB OYUN sahnesine bağlı değil.");
        }

        if (mode.StartsWith("PS5", StringComparison.OrdinalIgnoreCase) && inputNames.Contains("TB CAPTURE CARD"))
        {
            try
            {
                var settingsResponse = await obs.CallAsync("GetInputSettings", new { inputName = "TB CAPTURE CARD" });
                if (settingsResponse.TryGetProperty("inputSettings", out var settings))
                {
                    string deviceId = ReadString(settings, "video_device_id", "");
                    if (deviceId.Length == 0) deviceId = ReadString(settings, "last_video_device_id", "");
                    if (deviceId.Length == 0) result.Blockers.Add("HDMI yakalama kartına cihaz atanmadı. Cihaz listesinden capture card'ı seçip 'Yayına Hazırla' düğmesine tekrar bas.");
                }
                else result.Blockers.Add("Yakalama kartı ayarları okunamadı.");
            }
            catch (Exception ex) { result.Blockers.Add("Yakalama kartı doğrulanamadı: " + ex.Message); }
        }

        try
        {
            var currentScene = await obs.CallAsync("GetCurrentProgramScene");
            string current = ReadString(currentScene, "currentProgramSceneName", "");
            if (!string.Equals(current, "TB OYUN", StringComparison.OrdinalIgnoreCase))
            {
                if (sceneNames.Contains("TB OYUN"))
                {
                    await obs.CallAsync("SetCurrentProgramScene", new { sceneName = "TB OYUN" });
                    var verify = await obs.CallAsync("GetCurrentProgramScene");
                    if (!string.Equals(ReadString(verify, "currentProgramSceneName", ""), "TB OYUN", StringComparison.OrdinalIgnoreCase))
                        result.Blockers.Add("TB OYUN program sahnesine geçilemedi.");
                }
                else result.Blockers.Add("Yayın sahnesi seçilemedi.");
            }
        }
        catch (Exception ex) { result.Blockers.Add("Program sahnesi doğrulanamadı: " + ex.Message); }

        string microphone = "";
        string desktop = "";
        try
        {
            var special = await obs.CallAsync("GetSpecialInputs");
            microphone = ReadString(special, "mic1", "");
            desktop = ReadString(special, "desktop1", "");
        }
        catch (Exception ex) { result.Warnings.Add("OBS global ses kaynakları okunamadı: " + ex.Message); }

        bool hasMic = microphone.Length > 0 || inputNames.Contains("TB MICROPHONE");
        bool hasDesktop = desktop.Length > 0 || inputNames.Contains("TB DESKTOP AUDIO");
        bool consoleAudioPossible = mode.StartsWith("PS5", StringComparison.OrdinalIgnoreCase) && inputNames.Contains("TB CAPTURE CARD");
        if (!hasMic) result.Warnings.Add("Mikrofon kaynağı bulunamadı; yayında sesin olmayabilir.");
        if (!hasDesktop && !consoleAudioPossible) result.Warnings.Add("Masaüstü sesi bulunamadı; oyun sesi gelmeyebilir.");

        string micToCheck = microphone.Length > 0 ? microphone : inputNames.Contains("TB MICROPHONE") ? "TB MICROPHONE" : "";
        if (micToCheck.Length > 0)
        {
            try
            {
                var mute = await obs.CallAsync("GetInputMute", new { inputName = micToCheck });
                if (mute.TryGetProperty("inputMuted", out var muteFlag) && muteFlag.ValueKind == JsonValueKind.True)
                    result.Warnings.Add("Mikrofon sessizde (mute). Yayına çıkmadan aç.");
            }
            catch { result.Warnings.Add("Mikrofonun mute durumu okunamadı; OBS ses mikserini kontrol et."); }
        }

        if (consoleAudioPossible && CaptureDeviceBox.Items.Count > 1)
            result.Warnings.Add("Birden fazla görüntü cihazı var; seçili cihazın gerçekten HDMI capture kartı olduğunu önizlemede doğrula.");
        if (inputNames.Contains("TB MICROPHONE") && MicrophoneDeviceBox.Items.Count > 1)
            result.Warnings.Add("Mikrofon cihazı seçeneklerinde birden fazla aday var; seçiminin fiziksel mikrofonun olduğundan emin ol.");
        if (inputNames.Contains("TB DESKTOP AUDIO") && DesktopAudioDeviceBox.Items.Count > 1)
            result.Warnings.Add("Masaüstü ses çıkışında birden fazla cihaz var; doğru hoparlör/kulaklık seçili mi kontrol et.");

        if (inputNames.Contains("TB WEBCAM"))
        {
            try
            {
                var camera = await obs.CallAsync("GetInputSettings", new { inputName = "TB WEBCAM" });
                if (camera.TryGetProperty("inputSettings", out var cameraSettings) && ReadString(cameraSettings, "video_device_id", "").Length == 0)
                    result.Warnings.Add("Webcam cihazı henüz seçilmemiş olabilir; kamera isteğe bağlıdır.");
            }
            catch { result.Warnings.Add("Webcam durumu doğrulanamadı; kamera isteğe bağlıdır."); }
        }

        string configuredBitrate = await ReadProfileParameterValue("AdvOut", "VBitrate");
        if (recommendedBitrateKbps <= 0)
        {
            if (int.TryParse(configuredBitrate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int currentRate) && currentRate >= 1500)
                result.Warnings.Add($"Upload ölçümü yok; OBS'deki mevcut bitrate {currentRate} Kbps. İnternet hattına uygun olduğunu kontrol et.");
            else result.Warnings.Add("Upload ölçümünden güvenilir öneri çıkmadı ve mevcut Advanced bitrate doğrulanamadı. Yayından önce OBS Çıkış ayarını kontrol et.");
        }
        else if (int.TryParse(configuredBitrate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int actualRate) && actualRate > 0 && Math.Abs(actualRate - recommendedBitrateKbps) > 250)
        {
            result.Warnings.Add($"Önerilen bitrate {recommendedBitrateKbps} Kbps ancak OBS profili {actualRate} Kbps gösteriyor; değerin uygulandığını kontrol et.");
        }
        if (result.Blockers.Count == 0)
            result.Warnings.Add("Son göz kontrolü: OBS önizlemede görüntü, ses mikserinde mikrofon ve oyun/capture kartı sesi görünmeli. Siyah görüntü veya sessizlik sadece API ile kesin teşhis edilemez.");
        return result;
    }

    private async Task<bool> HasSceneSource(string sceneName, string sourceName)
    {
        try
        {
            var result = await obs!.CallAsync("GetSceneItemId", new { sceneName, sourceName });
            return result.TryGetProperty("sceneItemId", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() >= 0;
        }
        catch { return false; }
    }

    private string GetSelectedSourceMode()
        => (SourceModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "PS5 / HDMI yakalama kartı";

    private void SetupSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            preparedForLive = false;
            if (ReadinessText is not null) ReadinessText.Text = "Profil/cihaz seçimi değişti; yayına geçmeden önce yeniden hazırla.";
        }
    }

    private async Task<bool> IsOutputRunning()
    {
        var record = await obs!.CallAsync("GetRecordStatus");
        var stream = await obs.CallAsync("GetStreamStatus");
        bool recording = ReadRequiredBool(record, "outputActive", "OBS kayıt durumu");
        bool streaming = ReadRequiredBool(stream, "outputActive", "OBS yayın durumu");
        return recording || streaming;
    }

    private async Task ApplyVideoSafely()
    {
        if (await IsOutputRunning())
            throw new InvalidOperationException("Video ayarları uygulanmadan önce OBS çıkışı yeniden aktif oldu. Diğer yapılandırma adımları durduruldu.");

        string selected = (ResolutionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1920x1080";
        (int outputWidth, int outputHeight) = selected switch
        {
            "1664x936" => (1664, 936),
            "1280x720" => (1280, 720),
            _ => (1920, 1080)
        };
        int fps = (FpsBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "30" ? 30 : 60;

        try
        {
            var current = await obs!.CallAsync("GetVideoSettings");
            int currentBaseWidth = GetInt(current, "baseWidth");
            int currentBaseHeight = GetInt(current, "baseHeight");
            if (currentBaseWidth <= 0 || currentBaseHeight <= 0)
            {
                Log("OBS canvas ölçüsü doğrulanamadı; video ayarı değiştirilmedi.");
                return;
            }

            // Preserve the user's base canvas; the dropdown changes scaled output only.
            canvasWidth = currentBaseWidth;
            canvasHeight = currentBaseHeight;
            int currentOutputWidth = GetInt(current, "outputWidth");
            int currentOutputHeight = GetInt(current, "outputHeight");
            int currentFpsN = GetInt(current, "fpsNumerator");
            int currentFpsD = Math.Max(1, GetInt(current, "fpsDenominator"));

            if (currentOutputWidth == outputWidth && currentOutputHeight == outputHeight && currentFpsN == fps && currentFpsD == 1)
            {
                ProfileText.Text = $"{outputWidth}x{outputHeight} • {fps} FPS";
                Log($"Video çıkışı zaten uygun: {outputWidth}x{outputHeight} / {fps} FPS. Canvas {canvasWidth}x{canvasHeight} korundu.");
            }
            else
            {
                await obs.CallAsync("SetVideoSettings", new
                {
                    baseWidth = canvasWidth,
                    baseHeight = canvasHeight,
                    outputWidth,
                    outputHeight,
                    fpsNumerator = fps,
                    fpsDenominator = 1
                });
                ProfileText.Text = $"{outputWidth}x{outputHeight} • {fps} FPS";
                Log($"Video çıkışı uygulandı: {outputWidth}x{outputHeight} / {fps} FPS • Canvas korunuyor: {canvasWidth}x{canvasHeight}");
            }
        }
        catch (Exception ex)
        {
            Log("Video ayarı değiştirilemedi; OBS'nin mevcut video ayarları korunuyor: " + ex.Message);
        }

        await ApplyKnownProfileParameter("Output", "Mode", "Advanced", "Output modu Advanced");
        await InspectCurrentEncoder();

        if (recommendedBitrateKbps >= 1500)
            await ApplyKnownProfileParameter("AdvOut", "VBitrate", recommendedBitrateKbps.ToString(CultureInfo.InvariantCulture), $"Video bitrate {recommendedBitrateKbps} Kbps");
        else
        {
            string currentBitrate = await ReadProfileParameterValue("AdvOut", "VBitrate");
            Log(currentBitrate.Length > 0
                ? $"Upload önerisi yok; OBS'deki mevcut VBitrate ({currentBitrate} Kbps) korunuyor."
                : "Upload ölçümüne göre güvenilir bitrate oluşmadı ve mevcut VBitrate okunamadı; bitrate tahmin edilerek değiştirilmedi.");
        }

        await ApplyKnownProfileParameter("AdvOut", "KeyframeInterval", "2", "Keyframe aralığı 2 saniye");
        await ApplyKnownProfileParameter("AdvOut", "ABitrate", "160", "Ses bitrate'i 160 Kbps");
    }

    private async Task ConfigureOptionalCustomRtmpAsync()
    {
        if (UseCustomRtmpCheckBox.IsChecked != true) return;
        customRtmpAppliedThisRun = false;

        string server = StreamServerBox.Text.Trim();
        string key = StreamKeyBox.Password;
        if (server.Length == 0 || key.Length == 0)
            throw new InvalidOperationException("Custom RTMP seçili. Platform panelinden alınan sunucu adresini ve yayın anahtarını iki alan da dolu olacak şekilde gir. Bilgiler eksik olduğu için OBS hedefi değiştirilmedi.");
        if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) ||
            !(uri.Scheme.Equals("rtmp", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("rtmps", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("RTMP sunucu adresi geçersiz. Adres rtmp:// veya rtmps:// ile başlamalı.");
        if (await IsOutputRunning())
            throw new InvalidOperationException("OBS yayın/kayıt çıkışı aktifken yayın hedefi değiştirilemez.");

        try
        {
            await obs!.CallAsync("SetStreamServiceSettings", new
            {
                streamServiceType = "rtmp_custom",
                streamServiceSettings = new { server, key }
            });

            // The key is intentionally never inspected, logged, or included in any report or backup.
            StreamKeyBox.Clear();
            StreamServerBox.Clear();
            UseCustomRtmpCheckBox.IsChecked = false;
            customRtmpAppliedThisRun = true;
            Log("Kullanıcının verdiği özel RTMP hedefi OBS'ye uygulandı. Yayın anahtarı log/rapor/yedeğe yazılmadı; giriş alanı temizlendi. OBS hedefini kendi ayarlarında normal şekilde saklar.");
        }
        catch (Exception ex)
        {
            // Do not include the stream key or the server field in error logs.
            throw new InvalidOperationException("Özel RTMP hedefi OBS'ye uygulanamadı. OBS sürümünü, adresi ve WebSocket bağlantısını kontrol et. Yayın anahtarı güvenlik nedeniyle hata loguna eklenmedi. Teknik hata: " + ex.GetType().Name);
        }
    }

    private void CustomRtmp_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        preparedForLive = false;
        if (ReadinessText is not null)
            ReadinessText.Text = "Yayın hedefi değişti; canlıya geçmeden önce hazırlığı yeniden çalıştır.";
    }

    private async Task ApplyKnownProfileParameter(string category, string name, string value, string successMessage)
    {
        try
        {
            var current = await obs!.CallAsync("GetProfileParameter", new { parameterCategory = category, parameterName = name });
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty("parameterValue", out var oldValue) &&
                string.Equals(oldValue.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                Log("Ayar zaten uygun: " + name + " = " + value);
                return;
            }

            if (await IsOutputRunning())
            {
                Log($"OBS çıkışı aktif olduğu için profil parametresi değiştirilmedi: {name}.");
                return;
            }
            await obs.CallAsync("SetProfileParameter", new { parameterCategory = category, parameterName = name, parameterValue = value });
            var verify = await obs.CallAsync("GetProfileParameter", new { parameterCategory = category, parameterName = name });
            if (verify.ValueKind == JsonValueKind.Object && verify.TryGetProperty("parameterValue", out var actual) &&
                string.Equals(actual.ToString(), value, StringComparison.OrdinalIgnoreCase))
                Log("Doğrulandı: " + successMessage);
            else
                Log("OBS ayarı kabul etti fakat okuma doğrulaması yapılamadı: " + name);
        }
        catch (Exception ex) { Log($"Profil ayarı uygulanmadı ({name}); OBS değeri korunmuş olabilir: {ex.Message}"); }
    }

    private async Task<string> ReadProfileParameterValue(string category, string name)
    {
        try
        {
            var current = await obs!.CallAsync("GetProfileParameter", new { parameterCategory = category, parameterName = name });
            return current.ValueKind == JsonValueKind.Object && current.TryGetProperty("parameterValue", out var value)
                ? value.ToString()
                : "";
        }
        catch { return ""; }
    }

    private async Task InspectCurrentEncoder()
    {
        try
        {
            var current = await obs!.CallAsync("GetProfileParameter", new { parameterCategory = "AdvOut", parameterName = "Encoder" });
            string value = current.ValueKind == JsonValueKind.Object && current.TryGetProperty("parameterValue", out var element)
                ? element.ToString()
                : "okunamadı";
            Log("Mevcut OBS encoder kimliği: " + value + ". Otomatik değiştirilmedi; encoder kimlikleri OBS sürümü ve eklentiye göre değişebilir.");
        }
        catch (Exception ex) { Log("Mevcut encoder okunamadı; değiştirme yapılmadı: " + ex.Message); }
    }

    private async Task ApplyRecordingDirectory()
    {
        try
        {
            if (await IsOutputRunning())
                throw new InvalidOperationException("OBS çıkışı aktif.");
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "TB OBS Recordings");
            Directory.CreateDirectory(directory);
            await obs!.CallAsync("SetRecordDirectory", new { recordDirectory = directory });
            Log("Kayıt klasörü: " + directory);
        }
        catch (Exception ex) { Log("Kayıt klasörü otomatik ayarlanmadı: " + ex.Message); }
    }

    private async Task ConfigureRecordingSafety()
    {
        if (await IsOutputRunning())
            throw new InvalidOperationException("OBS çıkışı aktif; kayıt profil ayarları değiştirilmedi.");
        await ApplyKnownProfileParameter("AdvOut", "RecType", "Standard", "Kayıt türü Standard");
        await ApplyKnownProfileParameter("AdvOut", "RecFormat2", "mkv", "Kayıt formatı MKV");
        Log("MKV önerisi uygulandıysa kayıt beklenmedik kapandığında dosya bozulma riski MP4'e göre daha düşüktür.");
    }

    private async Task RefreshObsInventory()
    {
        var scenes = await obs!.CallAsync("GetSceneList");
        var inputs = await obs.CallAsync("GetInputList");
        sceneNames.Clear();
        inputNames.Clear();
        inputKindByName.Clear();

        if (scenes.TryGetProperty("scenes", out var sceneArray) && sceneArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sceneArray.EnumerateArray())
                if (item.TryGetProperty("sceneName", out var name)) sceneNames.Add(name.GetString() ?? "");
        }

        if (inputs.TryGetProperty("inputs", out var inputArray) && inputArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in inputArray.EnumerateArray())
            {
                string name = ReadString(item, "inputName", "");
                string kind = ReadString(item, "inputKind", "");
                if (name.Length == 0) continue;
                inputNames.Add(name);
                if (kind.Length > 0) inputKindByName[name] = kind;
            }
        }

        inputKinds.Clear();
        try
        {
            // Use exact/versioned IDs. Unversioned IDs may not be accepted by CreateInput on newer OBS builds.
            var kinds = await obs.CallAsync("GetInputKindList", new { unversioned = false });
            if (kinds.TryGetProperty("inputKinds", out var kindArray) && kindArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var kind in kindArray.EnumerateArray())
                    if (kind.ValueKind == JsonValueKind.String) inputKinds.Add(kind.GetString() ?? "");
            }
            if (inputKinds.Count == 0)
                Log("OBS desteklediği kaynak türlerini boş döndürdü; yeni kaynak oluşturma adımları güvenli şekilde atlanacak.");
        }
        catch (Exception ex)
        {
            Log("Kaynak türleri listesi okunamadı; yeni kaynak oluşturulmayacak: " + ex.Message);
        }

        SceneCountText.Text = $"{sceneNames.Count} sahne / {inputNames.Count} kaynak";
    }

    private string? FindAvailableInputKind(params string[] candidates)
    {
        // Prefer a requested exact ID first. Newer OBS builds can publish versioned IDs
        // (for example text_gdiplus_v2) when GetInputKindList is asked for exact IDs.
        foreach (string candidate in candidates)
            if (inputKinds.Contains(candidate)) return candidate;

        foreach (string candidate in candidates)
        {
            string baseName = StripInputKindVersion(candidate);
            string prefix = baseName + "_v";
            string? versioned = inputKinds
                .Where(kind => kind.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(kind => ParseInputKindVersion(kind))
                .FirstOrDefault();
            if (versioned is not null) return versioned;
        }
        return null;
    }

    private static string StripInputKindVersion(string value)
    {
        int marker = value.LastIndexOf("_v", StringComparison.OrdinalIgnoreCase);
        if (marker > 0 && marker + 2 < value.Length && value[(marker + 2)..].All(char.IsDigit))
            return value[..marker];
        return value;
    }

    private static int ParseInputKindVersion(string value)
    {
        int marker = value.LastIndexOf("_v", StringComparison.OrdinalIgnoreCase);
        return marker > 0 && int.TryParse(value[(marker + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)
            ? version
            : 0;
    }

    private static bool InputKindsCompatible(string actual, string expected)
        => string.Equals(StripInputKindVersion(actual), StripInputKindVersion(expected), StringComparison.OrdinalIgnoreCase);

    private async Task CreateProfessionalScenes()
    {
        string[] scenes = { "TB OYUN", "TB JUST CHATTING", "TB YAYIN BASLIYOR", "TB BRB", "TB YAYIN BITIYOR" };
        foreach (string scene in scenes) await EnsureScene(scene);

        string? gameKind = FindAvailableInputKind("game_capture");
        string? webcamKind = FindAvailableInputKind("dshow_input");
        string? textKind = FindAvailableInputKind("text_gdiplus");
        string? colorKind = FindAvailableInputKind("color_source_v3", "color_source");
        bool consoleMode = GetSelectedSourceMode().StartsWith("PS5", StringComparison.OrdinalIgnoreCase);

        if (consoleMode)
        {
            if (webcamKind is null)
            {
                Log("OBS DirectShow görüntü kaynağını (dshow_input) listelemedi; HDMI yakalama kartı oluşturulamadı.");
            }
            else
            {
                JsonElement defaults = await GetDefaultInputSettings(webcamKind);
                object settings = MergeSettings(defaults, new Dictionary<string, object?> { ["audio_output_mode"] = "capture" });
                await EnsureInputInScene("TB OYUN", "TB CAPTURE CARD", webcamKind, settings, updateSettings: false);
                await RefreshObsInventory();
                await PopulateDeviceComboAsync("TB CAPTURE CARD", "video_device_id", CaptureDeviceBox);
                await ApplyDeviceSelectionAsync("TB CAPTURE CARD", "video_device_id", CaptureDeviceBox, includeLastVideoDevice: true);
                try
                {
                    await obs!.CallAsync("SetInputSettings", new { inputName = "TB CAPTURE CARD", inputSettings = new { audio_output_mode = "capture" }, overlay = true });
                    Log("HDMI yakalama kartının sesi OBS mikserine aktarılacak şekilde ayarlandı (kaynak destekliyorsa).");
                }
                catch (Exception ex) { Log("Yakalama kartı ses modu ayarlanamadı; OBS kaynak ayarından kontrol et: " + ex.Message); }
                Log("PS5 modu: TB CAPTURE CARD seçildi. HDMI kartına PS5 görüntüsünü bağla ve önizlemeyi kontrol et.");
            }
        }
        else
        {
            if (gameKind is not null)
                await EnsureInputInScene("TB OYUN", "TB GAME CAPTURE", gameKind,
                    new { capture_mode = "any_fullscreen", allow_transparency = false }, updateSettings: false);
            else
                Log("OBS game_capture türünü listelemedi; PC oyun kaynağı oluşturulamadı.");
        }

        await LogGlobalAudioDevices();

        if (webcamKind is not null)
        {
            JsonElement defaults = await GetDefaultInputSettings(webcamKind);
            await EnsureInputInScene("TB JUST CHATTING", "TB WEBCAM", webcamKind, defaults, updateSettings: false);
            await EnsureInputInScene("TB OYUN", "TB WEBCAM", webcamKind, defaults, updateSettings: false);
            await RefreshObsInventory();
            await PopulateDeviceComboAsync("TB WEBCAM", "video_device_id", CameraDeviceBox);
            await ApplyDeviceSelectionAsync("TB WEBCAM", "video_device_id", CameraDeviceBox, includeLastVideoDevice: true);
            await PositionSceneItem("TB OYUN", "TB WEBCAM", Math.Max(160, canvasWidth - 190), Math.Max(100, canvasHeight - 105), 0.28, 0.28);
            Log("Webcam kaynağı oyun ve Just Chatting sahnelerine eklendi. Doğru kamerayı listeden seç ve önizlemede doğrula.");
        }
        else
            Log("DirectShow webcam kaynağı bulunamadı; kamera adımı atlandı.");

        if (colorKind is not null)
        {
            object background = new { color = 0xFF0B1018u, width = canvasWidth, height = canvasHeight };
            await EnsureInputInScene("TB YAYIN BASLIYOR", "TB START BACKGROUND", colorKind, background, updateSettings: true);
            await EnsureInputInScene("TB BRB", "TB BRB BACKGROUND", colorKind, background, updateSettings: true);
            await EnsureInputInScene("TB YAYIN BITIYOR", "TB END BACKGROUND", colorKind, background, updateSettings: true);
            Log("Başlangıç, mola ve bitiş sahnelerinin arka planları doğrulandı.");
        }
        else
            Log("OBS Color Source türünü listelemedi; arka plan kaynakları atlandı.");

        if (textKind is not null)
        {
            await EnsureInputInScene("TB JUST CHATTING", "TB CHAT LABEL", textKind, MakeTextSettings("JUST CHATTING", 42), updateSettings: true);
            await PositionSceneItem("TB JUST CHATTING", "TB CHAT LABEL", canvasWidth / 2.0, Math.Max(70, canvasHeight * 0.12));
            await EnsureInputInScene("TB YAYIN BASLIYOR", "TB START TEXT", textKind, MakeTextSettings("YAYIN BAŞLIYOR", 64), updateSettings: true);
            await PositionSceneItem("TB YAYIN BASLIYOR", "TB START TEXT", canvasWidth / 2.0, canvasHeight / 2.0);
            await EnsureInputInScene("TB BRB", "TB BRB TEXT", textKind, MakeTextSettings("KISA BİR MOLA", 64), updateSettings: true);
            await PositionSceneItem("TB BRB", "TB BRB TEXT", canvasWidth / 2.0, canvasHeight / 2.0);
            await EnsureInputInScene("TB YAYIN BITIYOR", "TB END TEXT", textKind, MakeTextSettings("YAYIN SONA ERİYOR", 58), updateSettings: true);
            await PositionSceneItem("TB YAYIN BITIYOR", "TB END TEXT", canvasWidth / 2.0, canvasHeight / 2.0);
        }
        else Log("OBS Windows Text (GDI+) kaynağını listelemedi; yazı kaynakları atlandı.");

        // Do not let a stale source from a previous mode cover the selected source or duplicate capture-device audio.
        await SetSceneSourceEnabled("TB OYUN", "TB GAME CAPTURE", !consoleMode);
        await SetSceneSourceEnabled("TB OYUN", "TB CAPTURE CARD", consoleMode);
        Log("TB sahneleri ve temel görüntü kaynakları tamamlandı. Seçilmeyen giriş modu sahne üzerinde devre dışı bırakıldı; kaynak silinmedi.");
    }

    private async Task SetSceneSourceEnabled(string sceneName, string sourceName, bool enabled)
    {
        if (!sceneNames.Contains(sceneName) || !inputNames.Contains(sourceName)) return;
        try
        {
            var item = await obs!.CallAsync("GetSceneItemId", new { sceneName, sourceName });
            if (!item.TryGetProperty("sceneItemId", out var idElement) || !idElement.TryGetInt32(out int id)) return;
            await obs.CallAsync("SetSceneItemEnabled", new { sceneName, sceneItemId = id, sceneItemEnabled = enabled });
            Log($"{sceneName}: {sourceName} {(enabled ? "aktif" : "devre dışı")}.");
        }
        catch (Exception ex) { Log($"Sahne kaynağı durumu değiştirilemedi ({sourceName}): {ex.Message}"); }
    }

    private static Dictionary<string, object?> MergeSettings(JsonElement defaults, Dictionary<string, object?> overrides)
    {
        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (defaults.ValueKind == JsonValueKind.Object)
            foreach (var property in defaults.EnumerateObject()) merged[property.Name] = property.Value.Clone();
        foreach (var item in overrides) merged[item.Key] = item.Value;
        return merged;
    }

    private async Task<List<DeviceChoice>> ReadDeviceChoicesAsync(string sourceName, string propertyName)
    {
        var choices = new List<DeviceChoice>();
        try
        {
            var response = await obs!.CallAsync("GetInputPropertiesListPropertyItems", new { inputName = sourceName, propertyName });
            if (response.TryGetProperty("propertyItems", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("itemEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) continue;
                    string value = GetJsonScalar(item, "itemValue");
                    string name = ReadString(item, "itemName", value);
                    if (value.Length == 0 || name.Length == 0) continue;
                    if (!choices.Any(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase)))
                        choices.Add(new DeviceChoice(name, value));
                }
            }
        }
        catch (Exception ex) { Log($"Cihaz listesi okunamadı ({sourceName}/{propertyName}): {ex.Message}"); }
        return choices;
    }

    private async Task PopulateDeviceComboAsync(string sourceName, string propertyName, ComboBox combo)
    {
        string previous = GetSelectedDeviceValue(combo);
        List<DeviceChoice> choices = await ReadDeviceChoicesAsync(sourceName, propertyName);
        combo.Items.Clear();
        foreach (DeviceChoice choice in choices)
            combo.Items.Add(new ComboBoxItem { Content = choice.Name, Tag = choice.Value });
        if (combo.Items.Count > 0)
        {
            int preserved = -1;
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), previous, StringComparison.OrdinalIgnoreCase))
                { preserved = i; break; }
            }

            int preferred = 0;
            if (sourceName.Equals("TB CAPTURE CARD", StringComparison.OrdinalIgnoreCase))
            {
                string[] captureHints = { "capture", "hdmi", "elgato", "cam link", "usb video", "video capture", "px-500", "usb2.0 video" };
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    string label = (combo.Items[i] as ComboBoxItem)?.Content?.ToString() ?? "";
                    if (captureHints.Any(hint => label.Contains(hint, StringComparison.OrdinalIgnoreCase)))
                    { preferred = i; break; }
                }
            }
            combo.SelectedIndex = preserved >= 0 ? preserved : preferred;
            Log($"{sourceName} için {combo.Items.Count} cihaz seçeneği bulundu. Seçim: {(combo.SelectedItem as ComboBoxItem)?.Content}.");
            if (combo.Items.Count > 1) Log($"Cihaz kontrolü: otomatik seçim isim benzerliğine göre yapıldı. Canlıya çıkmadan OBS önizlemesinde görüntüyü doğrula.");
        }
        else Log($"{sourceName} için cihaz seçeneği gelmedi. OBS kaynağı özellikleri penceresinden cihazı elle seçmen gerekebilir.");
    }

    private static string GetSelectedDeviceValue(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private static string GetJsonScalar(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private async Task ApplyDeviceSelectionAsync(string sourceName, string propertyName, ComboBox combo, bool includeLastVideoDevice = false)
    {
        string value = GetSelectedDeviceValue(combo);
        if (value.Length == 0 || !inputNames.Contains(sourceName)) return;
        var settings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [propertyName] = value };
        if (includeLastVideoDevice) settings["last_video_device_id"] = value;
        try
        {
            await obs!.CallAsync("SetInputSettings", new { inputName = sourceName, inputSettings = settings, overlay = true });
            var verify = await obs.CallAsync("GetInputSettings", new { inputName = sourceName });
            string actual = verify.TryGetProperty("inputSettings", out var inputSettings) ? ReadString(inputSettings, propertyName, "") : "";
            if (string.Equals(actual, value, StringComparison.OrdinalIgnoreCase))
                Log($"Cihaz atandı ve doğrulandı: {sourceName} ← {(combo.SelectedItem as ComboBoxItem)?.Content}.");
            else
                Log($"Cihaz atama isteği gönderildi fakat değer doğrulanamadı: {sourceName}. OBS özelliklerini açıp kontrol et.");
        }
        catch (Exception ex) { Log($"Cihaz atanamadı ({sourceName}): {ex.Message}"); }
    }

    private async Task ApplySelectedDeviceSettings()
    {
        await ApplyDeviceSelectionAsync("TB CAPTURE CARD", "video_device_id", CaptureDeviceBox, includeLastVideoDevice: true);
        await ApplyDeviceSelectionAsync("TB WEBCAM", "video_device_id", CameraDeviceBox, includeLastVideoDevice: true);
        await ApplyDeviceSelectionAsync("TB MICROPHONE", "device_id", MicrophoneDeviceBox);
        await ApplyDeviceSelectionAsync("TB DESKTOP AUDIO", "device_id", DesktopAudioDeviceBox);
    }

    private async Task ConfigureReadyAudioSources()
    {
        string[] allScenes = { "TB OYUN", "TB JUST CHATTING", "TB YAYIN BASLIYOR", "TB BRB", "TB YAYIN BITIYOR" };
        string globalMic = "";
        string globalDesktop = "";
        try
        {
            var special = await obs!.CallAsync("GetSpecialInputs");
            globalMic = ReadString(special, "mic1", "");
            globalDesktop = ReadString(special, "desktop1", "");
        }
        catch (Exception ex) { Log("OBS global ses kaynakları okunamadı: " + ex.Message); }

        if (globalMic.Length == 0)
        {
            string? micKind = FindAvailableInputKind("wasapi_input_capture");
            if (micKind is null) Log("OBS global mikrofon ayarlı değil ve WASAPI mikrofon kaynağı mevcut değil.");
            else
            {
                JsonElement defaults = await GetDefaultInputSettings(micKind);
                foreach (string scene in allScenes) await EnsureInputInScene(scene, "TB MICROPHONE", micKind, defaults, updateSettings: false);
                await RefreshObsInventory();
                await PopulateDeviceComboAsync("TB MICROPHONE", "device_id", MicrophoneDeviceBox);
                await ApplyDeviceSelectionAsync("TB MICROPHONE", "device_id", MicrophoneDeviceBox);
                Log("OBS global mikrofonu ayarlı olmadığı için sahne bazlı TB MICROPHONE kaynağı oluşturuldu.");
            }
        }
        else Log("OBS global mikrofon kaynağı bulundu: " + globalMic + ". Tekrar mikrofon kaynağı oluşturulmadı.");

        bool consoleMode = GetSelectedSourceMode().StartsWith("PS5", StringComparison.OrdinalIgnoreCase);
        if (globalDesktop.Length == 0 && !consoleMode)
        {
            string? desktopKind = FindAvailableInputKind("wasapi_output_capture");
            if (desktopKind is null) Log("OBS global masaüstü sesi ayarlı değil; WASAPI çıkış kaynağı mevcut değil.");
            else
            {
                JsonElement defaults = await GetDefaultInputSettings(desktopKind);
                foreach (string scene in allScenes) await EnsureInputInScene(scene, "TB DESKTOP AUDIO", desktopKind, defaults, updateSettings: false);
                await RefreshObsInventory();
                await PopulateDeviceComboAsync("TB DESKTOP AUDIO", "device_id", DesktopAudioDeviceBox);
                await ApplyDeviceSelectionAsync("TB DESKTOP AUDIO", "device_id", DesktopAudioDeviceBox);
                Log("OBS global masaüstü sesi ayarlı olmadığı için TB DESKTOP AUDIO kaynağı oluşturuldu.");
            }
        }
        else if (globalDesktop.Length > 0)
            Log("OBS global masaüstü sesi bulundu: " + globalDesktop + ". Çift ses üretmemek için yeni desktop kaynağı eklenmedi.");
        else if (consoleMode)
            Log("PS5 modunda HDMI yakalama kartının sesini kullanacağız; OBS global desktop kaynağı otomatik eklenmedi.");
    }

    private static object MakeTextSettings(string text, int size) => new
    {
        text,
        font = new { face = "Arial", size, style = "Bold", flags = 1 },
        color = 0xFFFFFFFFu,
        align = "center",
        valign = "center",
        outline = true,
        outline_color = 0xFF000000u,
        outline_size = 2
    };

    private async Task EnsureScene(string name)
    {
        if (sceneNames.Contains(name)) return;
        try
        {
            await obs!.CallAsync("CreateScene", new { sceneName = name });
            sceneNames.Add(name);
            Log("Sahne oluşturuldu: " + name);
        }
        catch (Exception ex)
        {
            try { await RefreshObsInventory(); }
            catch (Exception inventoryError) { Log("Sahne envanteri yenilenemedi: " + inventoryError.Message); }
            if (sceneNames.Contains(name)) Log("Sahne işlem sırasında oluşmuş; mevcut olan kullanılıyor: " + name);
            else Log("Sahne oluşturulamadı: " + name + " — " + ex.Message);
        }
    }

    private async Task<JsonElement> GetDefaultInputSettings(string kind)
    {
        try
        {
            var result = await obs!.CallAsync("GetInputDefaultSettings", new { inputKind = kind });
            if (result.TryGetProperty("defaultInputSettings", out var settings)) return settings.Clone();
        }
        catch (Exception ex) { Log(kind + " varsayılan ayarları okunamadı: " + ex.Message); }
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private async Task EnsureInputInScene(string scene, string source, string kind, object settings, bool updateSettings)
    {
        if (!sceneNames.Contains(scene))
        {
            Log("Kaynak atlandı; hedef sahne yok: " + scene);
            return;
        }

        bool exists = inputNames.Contains(source);
        if (exists && inputKindByName.TryGetValue(source, out string? foundKind) && foundKind.Length > 0 &&
            !InputKindsCompatible(foundKind, kind))
        {
            Log($"Mevcut kaynak beklenen kaynak türü değil; yanlış kaynağı sahneye bağlamamak için atlandı: {source} (mevcut {foundKind}, gereken {kind}). Kaynağı OBS'de yeniden adlandırıp hazırlığı tekrar çalıştır.");
            return;
        }

        if (!exists)
        {
            if (inputKinds.Count == 0 || !inputKinds.Contains(kind))
            {
                Log($"Kaynak atlandı; OBS kaynak türünü kesin olarak doğrulayamadı: {source} ({kind}).");
                return;
            }

            try
            {
                await obs!.CallAsync("CreateInput", new
                {
                    sceneName = scene,
                    inputName = source,
                    inputKind = kind,
                    inputSettings = settings,
                    sceneItemEnabled = true
                });
                Log("Kaynak oluşturuldu: " + source);
            }
            catch (Exception ex)
            {
                Log($"Kaynak oluşturma isteği başarısız oldu: {source} ({kind}) — {ex.Message}");
            }

            try { await RefreshObsInventory(); }
            catch (Exception ex) { Log("Kaynak sonrası envanter doğrulanamadı: " + ex.Message); }
            exists = inputNames.Contains(source);
            if (!exists)
            {
                Log($"Kaynak OBS envanterinde görünmedi; sahneye ekleme denemesi yapılmadı: {source}.");
                return;
            }
        }
        else if (updateSettings)
        {
            try
            {
                await obs!.CallAsync("SetInputSettings", new { inputName = source, inputSettings = settings, overlay = true });
                Log("Kaynak ayarları doğrulandı/güncellendi: " + source);
            }
            catch (Exception ex) { Log("Mevcut kaynak güncellenemedi; korunuyor: " + source + " — " + ex.Message); }
        }

        await EnsureSceneItem(scene, source);
    }

    private async Task EnsureSceneItem(string scene, string source)
    {
        if (!sceneNames.Contains(scene)) { Log($"{source}: hedef sahne envanterde yok; ekleme atlandı."); return; }
        if (!inputNames.Contains(source)) { Log($"{source}: kaynak OBS envanterinde yok; ekleme atlandı."); return; }

        try
        {
            var current = await obs!.CallAsync("GetSceneItemId", new { sceneName = scene, sourceName = source });
            if (GetInt(current, "sceneItemId") >= 0 && current.TryGetProperty("sceneItemId", out _)) return;
        }
        catch { /* OBS returns a request error when no scene item exists. Create it below. */ }

        try
        {
            await obs!.CallAsync("CreateSceneItem", new { sceneName = scene, sourceName = source, sceneItemEnabled = true });
            var verify = await obs.CallAsync("GetSceneItemId", new { sceneName = scene, sourceName = source });
            if (verify.TryGetProperty("sceneItemId", out _)) Log($"Kaynak sahneye bağlandı: {scene} ← {source}");
            else Log($"OBS kaynak bağlama isteğini kabul etti fakat doğrulayamadım: {scene} ← {source}");
        }
        catch (Exception ex)
        {
            try
            {
                await RefreshObsInventory();
                var verify = await obs!.CallAsync("GetSceneItemId", new { sceneName = scene, sourceName = source });
                if (verify.TryGetProperty("sceneItemId", out _)) Log($"Kaynak zaten sahnede: {scene} ← {source}");
                else Log($"Kaynak sahneye bağlanamadı: {scene} ← {source} — {ex.Message}");
            }
            catch { Log($"Kaynak sahneye bağlanamadı: {scene} ← {source} — {ex.Message}"); }
        }
    }

    private async Task PositionSceneItem(string scene, string source, double x, double y, double scaleX = 1.0, double scaleY = 1.0)
    {
        try
        {
            var result = await obs!.CallAsync("GetSceneItemId", new { sceneName = scene, sourceName = source });
            if (!result.TryGetProperty("sceneItemId", out var idElement) || !idElement.TryGetInt32(out int id))
            {
                Log("Kaynak konumu ayarlanamadı; scene item ID yok: " + source);
                return;
            }

            await obs.CallAsync("SetSceneItemTransform", new
            {
                sceneName = scene,
                sceneItemId = id,
                sceneItemTransform = new
                {
                    positionX = x,
                    positionY = y,
                    rotation = 0.0,
                    scaleX,
                    scaleY,
                    alignment = 0,
                    boundsType = 0,
                    boundsAlignment = 0,
                    boundsWidth = 0.0,
                    boundsHeight = 0.0,
                    cropLeft = 0,
                    cropTop = 0,
                    cropRight = 0,
                    cropBottom = 0
                }
            });
        }
        catch (Exception ex) { Log("Kaynak konumu değiştirilemedi; OBS varsayılan konumu kullanılacak: " + source + " — " + ex.Message); }
    }

    private async Task LogGlobalAudioDevices()
    {
        try
        {
            var result = await obs!.CallAsync("GetSpecialInputs");
            string desktop = ReadString(result, "desktop1", "");
            string microphone = ReadString(result, "mic1", "");
            Log(desktop.Length > 0 ? "Desktop Audio: " + desktop : "Desktop Audio OBS global ses ayarlarında tanımlı değil.");
            Log(microphone.Length > 0 ? "Mikrofon: " + microphone : "OBS global mikrofon kaynağı tanımlı değil; filtreler atlanacak.");
            Log("OBS global ses kaynakları bütün sahnelerde ortak kullanılır; bunlar ayrı scene item olarak tekrar eklenmedi.");
        }
        catch (Exception ex) { Log("Global ses envanteri alınamadı: " + ex.Message); }
    }

    private async Task ConfigureAudioFilters()
    {
        try
        {
            var result = await obs!.CallAsync("GetSpecialInputs");
            string microphone = ReadString(result, "mic1", "");
            if (microphone.Length == 0 && inputNames.Contains("TB MICROPHONE")) microphone = "TB MICROPHONE";
            if (microphone.Length == 0)
            {
                Log("Filtreler atlandı; OBS global veya TB MICROPHONE kaynağı bulunamadı.");
                return;
            }

            await EnsureFilter(microphone, "TB Noise Suppression", "noise_suppress_filter");
            await EnsureFilter(microphone, "TB Expander", "expander_filter");
            await EnsureFilter(microphone, "TB Compressor", "compressor_filter");
            Log("Ses filtre zinciri kontrol edildi. İlk yayından önce mikrofonda konuşarak seviyeleri dinle.");
        }
        catch (Exception ex) { Log("Mikrofon filtreleri atlandı: " + ex.Message); }
    }

    private async Task EnsureFilter(string source, string filterName, string filterKind)
    {
        try
        {
            var filters = await obs!.CallAsync("GetSourceFilterList", new { sourceName = source });
            if (filters.TryGetProperty("filters", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (string.Equals(ReadString(item, "filterName", ""), filterName, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Filtre zaten mevcut: " + filterName);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("Filtre listesi okunamadı; tekrar oluşturarak çoğaltma riski alınmadı: " + filterName + " — " + ex.Message);
            return;
        }

        try
        {
            await obs!.CallAsync("CreateSourceFilter", new { sourceName = source, filterName, filterKind, filterSettings = new { } });
            Log("Mikrofon filtresi eklendi: " + filterName);
        }
        catch (Exception ex) { Log("Filtre eklenemedi: " + filterName + " — " + ex.Message); }
    }

    private async Task TryStartReplayBuffer()
    {
        try
        {
            var status = await obs!.CallAsync("GetReplayBufferStatus");
            bool active = status.TryGetProperty("outputActive", out var activeElement) && activeElement.ValueKind == JsonValueKind.True;
            if (active) { Log("Replay Buffer zaten aktif."); return; }
            await obs.CallAsync("StartReplayBuffer");
            Log("Replay Buffer başlatıldı.");
        }
        catch (Exception ex) { Log("Replay Buffer başlatılamadı. OBS Kayıt ayarlarında Replay Buffer desteğini kontrol et: " + ex.Message); }
    }

    private async void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (setupRunning) return;
        try { await EnsureConnected(); await TryStartReplayBuffer(); }
        catch (Exception ex) { Log("Replay Buffer hatası: " + ex.Message); }
    }

    private async void Health_Click(object sender, RoutedEventArgs e)
    {
        if (setupRunning) return;
        try { await EnsureConnected(); await HealthCheck(); }
        catch (Exception ex) { Log("Health check hatası: " + ex.Message); }
    }

    private async Task HealthCheck()
    {
        var stats = await obs!.CallAsync("GetStats");
        var stream = await obs.CallAsync("GetStreamStatus");
        var record = await obs.CallAsync("GetRecordStatus");
        bool active = ReadRequiredBool(stream, "outputActive", "OBS yayın durumu") || ReadRequiredBool(record, "outputActive", "OBS kayıt durumu");

        double fps = GetDouble(stats, "activeFps");
        double renderMs = GetDouble(stats, "averageFrameRenderTime");
        long renderSkipped = GetLong(stats, "renderSkippedFrames");
        long renderTotal = GetLong(stats, "renderTotalFrames");
        long outputSkipped = GetLong(stats, "outputSkippedFrames");
        long outputTotal = GetLong(stats, "outputTotalFrames");
        double cpuUsage = GetDouble(stats, "cpuUsage");
        double renderDrop = renderTotal > 0 ? renderSkipped * 100d / renderTotal : 0;
        double outputDrop = outputTotal > 0 ? outputSkipped * 100d / outputTotal : 0;

        string health = !active ? "READY" : "GOOD";
        if (active && (renderDrop > 1 || outputDrop > 1 || renderMs > 16.7)) health = "CHECK";
        if (active && (renderDrop > 5 || outputDrop > 5 || renderMs > 25)) health = "BAD";
        HealthCard.Text = health;
        HealthCard.Foreground = health == "GOOD" || health == "READY" ? Brushes.LightGreen : health == "CHECK" ? Brushes.Gold : Brushes.OrangeRed;
        StatsText.Text = $"{(active ? "OUTPUT ACTIVE" : "OUTPUT IDLE / RENDER ONLY")}\nFPS {fps:0.0} • Render {renderMs:0.0} ms\nRender skip {renderDrop:0.00}% • Output skip {outputDrop:0.00}%\nOBS CPU {cpuUsage:0.0}%";
        Log($"Health {(active ? "aktif çıkış" : "boşta/render-only")}: FPS={fps:0.0}, render={renderMs:0.0}ms, render skip={renderDrop:0.00}%, output skip={outputDrop:0.00}%");
    }

    private async void Performance_Click(object sender, RoutedEventArgs e)
    {
        if (setupRunning) return;
        try
        {
            await EnsureConnected();
            var streamBefore = await obs!.CallAsync("GetStreamStatus");
            var recordBefore = await obs.CallAsync("GetRecordStatus");
            bool outputActive = ReadRequiredBool(streamBefore, "outputActive", "OBS yayın durumu") || ReadRequiredBool(recordBefore, "outputActive", "OBS kayıt durumu");
            var before = await obs.CallAsync("GetStats");
            Log(outputActive ? "5 saniyelik aktif çıkış örneklemesi başladı." : "5 saniyelik render test başladı; yayın/kayıt aktif değilken gerçek encoder yükü ölçülmez.");
            await Task.Delay(TimeSpan.FromSeconds(5));
            var after = await obs.CallAsync("GetStats");

            long renderSkipped = Math.Max(0, GetLong(after, "renderSkippedFrames") - GetLong(before, "renderSkippedFrames"));
            long renderTotal = Math.Max(0, GetLong(after, "renderTotalFrames") - GetLong(before, "renderTotalFrames"));
            long outputSkipped = Math.Max(0, GetLong(after, "outputSkippedFrames") - GetLong(before, "outputSkippedFrames"));
            long outputTotal = Math.Max(0, GetLong(after, "outputTotalFrames") - GetLong(before, "outputTotalFrames"));
            double renderDrop = renderTotal > 0 ? renderSkipped * 100d / renderTotal : 0;
            double outputDrop = outputTotal > 0 ? outputSkipped * 100d / outputTotal : 0;
            double fps = GetDouble(after, "activeFps");
            double render = GetDouble(after, "averageFrameRenderTime");

            Log($"Test sonucu: FPS {fps:0.0} • Render {render:0.0}ms • Render skip {renderDrop:0.00}% • Output skip {outputDrop:0.00}%");
            if (!outputActive)
                Log("Değerlendirme: bu sonuç yalnızca OBS render tarafını gösterir. Oyun açıkken ve test yayını/kaydı yapılırken yeniden ölç.");
            else if (renderDrop < 1 && outputDrop < 1 && render < 16.7)
                Log("Değerlendirme: ölçüm aralığında iyi görünüyor; uzun test ve gerçek oyun yükü gerekir.");
            else if (renderDrop < 5 && outputDrop < 5)
                Log("Değerlendirme: uyarı düzeyi; oyun yükünde çözünürlük/FPS düşürmeyi değerlendir.");
            else
                Log("Değerlendirme: yüksek gecikme/kayıp görüldü; önce OBS istatistik penceresi ve log dosyası incelenmeli.");
        }
        catch (Exception ex) { Log("Performans testi hatası: " + ex.Message); }
    }

    private async void UploadTest_Click(object sender, RoutedEventArgs e)
    {
        if (setupRunning) return;
        try
        {
            Log("Upload testi başlıyor. İki örnek alınacak; daha düşük değer öneride kullanılacak.");
            double first = await MeasureUploadMbps(4 * 1024 * 1024);
            double second = await MeasureUploadMbps(8 * 1024 * 1024);
            lastUploadMbps = Math.Min(first, second);
            UploadBox.Text = lastUploadMbps.ToString("0.0", CultureInfo.InvariantCulture);
            UpdateBitrateRecommendation();
            Log($"Upload örnekleri: {first:0.0} ve {second:0.0} Mbps. Güvenli öneri için {lastUploadMbps:0.0} Mbps kullanıldı.");
        }
        catch (Exception ex) { Log("Upload testi başarısız: " + ex.Message + ". Hızı elle girebilir veya sonra tekrar deneyebilirsin."); }
    }

    private static async Task<double> MeasureUploadMbps(int bytes)
    {
        byte[] data = new byte[bytes];
        Random.Shared.NextBytes(data);
        using var content = new ByteArrayContent(data);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://speed.cloudflare.com/__up?bytes={bytes}") { Content = content };
        var stopwatch = Stopwatch.StartNew();
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        stopwatch.Stop();
        response.EnsureSuccessStatusCode();
        return bytes * 8d / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds) / 1_000_000d;
    }

    private void Platform_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateBitrateRecommendation();
        if (IsLoaded) preparedForLive = false;
    }
    private void Bitrate_Click(object sender, RoutedEventArgs e)
    {
        UpdateBitrateRecommendation();
        Log(recommendedBitrateKbps >= 1500
            ? $"Güvenli bitrate önerisi: {recommendedBitrateKbps} Kbps. OBS'ye otomatik uygulamadan önce platform sınırını kontrol et."
            : "Güvenilir bitrate önerisi oluşmadı; ölçülen upload yetersiz veya girilmemiş olabilir.");
    }

    private void UpdateBitrateRecommendation()
    {
        double upload = lastUploadMbps;
        if (TryParseMbps(UploadBox?.Text ?? "", out double typed) && typed > 0) upload = typed;
        if (upload <= 0)
        {
            recommendedBitrateKbps = 0;
            if (BitrateRecommendation is not null) BitrateRecommendation.Text = "Öneri: önce upload testi yap veya upload değerini Mbps olarak gir. Ölçüm yoksa bitrate değiştirilmez.";
            if (BitrateCard is not null) BitrateCard.Text = "ÖLÇÜM YOK";
            return;
        }

        string platform = (PlatformBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Kick";
        double platformCapMbps = platform switch
        {
            "Twitch" => 6.0,
            "YouTube" => 9.0,
            "Kick" => 8.0,
            _ => 8.0
        };
        // Keep 35% headroom for game/network variation and round down to 250 Kbps steps.
        double safeMbps = Math.Min(upload * 0.65, platformCapMbps);
        int target = (int)(Math.Floor(safeMbps * 1000.0 / 250.0) * 250.0);
        BitrateCard.Text = target > 0 ? $"{target / 1000.0:0.00} Mbps" : "ÇOK DÜŞÜK";

        if (target < 1500)
        {
            recommendedBitrateKbps = 0;
            BitrateRecommendation.Text = $"Upload {upload:0.0} Mbps. Teorik hedef yaklaşık {target} Kbps; kararlı yayın için düşük. 720p30 veya daha iyi upload önerilir. Otomatik bitrate değiştirilmez.";
        }
        else
        {
            recommendedBitrateKbps = target;
            BitrateRecommendation.Text = $"Öneri: {target} Kbps • ölçüm {upload:0.0} Mbps • %35 pay bırakıldı. Platform ve codec sınırını ayrıca kontrol et.";
        }
    }

    private static bool TryParseMbps(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.GetCultureInfo("tr-TR"), out result);
    }

    private async void Inventory_Click(object sender, RoutedEventArgs e)
    {
        if (setupRunning) return;
        try
        {
            await EnsureConnected();
            await RefreshObsInventory();
            Log("--- SAHNE ENVANTERİ ---");
            foreach (string name in sceneNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Log("• " + name);
            Log("--- KAYNAK ENVANTERİ ---");
            foreach (string name in inputNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string kind = inputKindByName.TryGetValue(name, out string? value) ? value : "tür bilinmiyor";
                Log($"• {name} [{kind}]");
            }
            Log($"Envanter: {sceneNames.Count} sahne / {inputNames.Count} kaynak / {inputKinds.Count} desteklenen kaynak türü");
        }
        catch (Exception ex) { Log("Envanter hatası: " + ex.Message); }
    }

    private sealed class BackupManifest
    {
        public string CreatedAt { get; set; } = "";
        public string Scope { get; set; } = "OBS basic config; service.json excluded";
        public List<string> Files { get; set; } = new();
    }

    private string BackupObsConfig()
    {
        string obsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");
        string source = Path.Combine(obsRoot, "basic");
        if (!Directory.Exists(source))
        {
            Log("OBS basic ayar klasörü yok; güvenli yedek oluşturulamadı.");
            return "";
        }

        string backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TB_OBS_Backups");
        string backupPath = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
        string copiedRoot = Path.Combine(backupPath, "basic");
        Directory.CreateDirectory(copiedRoot);
        var manifest = new BackupManifest { CreatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) };

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (IsSensitiveBackupFile(relative)) continue;
            string destination = SafeCombine(copiedRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
            manifest.Files.Add(relative);
        }

        if (manifest.Files.Count == 0)
        {
            try { Directory.Delete(backupPath, true); } catch { }
            Log("Yedek klasöründe güvenli olarak kopyalanabilecek OBS ayarı bulunamadı.");
            return "";
        }

        Directory.CreateDirectory(backupPath);
        File.WriteAllText(Path.Combine(backupPath, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return backupPath;
    }

    private static bool IsSensitiveBackupFile(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);
        return fileName.Equals("service.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("service.json.bak", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeCombine(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Yedek manifestinde geçersiz dosya yolu bulundu.");
        return full;
    }

    private void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TB_OBS_Backups");
        Directory.CreateDirectory(root);
        try { Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true }); }
        catch (Exception ex) { Log("Yedek klasörü açılamadı: " + ex.Message); }
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Process.GetProcessesByName("obs64").Length > 0)
            {
                MessageBox.Show("Geri yüklemeden önce OBS'yi tamamen kapat.", "Rollback", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TB_OBS_Backups");
            if (!Directory.Exists(backupRoot)) { MessageBox.Show("Henüz yedek yok."); return; }
            string? latest = Directory.GetDirectories(backupRoot)
                .Where(path => File.Exists(Path.Combine(path, "manifest.json")) && Directory.Exists(Path.Combine(path, "basic")))
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (latest is null) { MessageBox.Show("V9 güvenli yedeği bulunamadı. Eski sürüm yedekleri otomatik geri yüklenmeye uygun kabul edilmedi."); return; }

            if (MessageBox.Show("Seçili güvenli OBS yedeği geri yüklenecek:\n\n" + latest + "\n\nOBS kapalı olmalı. Devam et?", "Rollback", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            // Snapshot the current safe OBS configuration before restoring.
            string safetyCopy = BackupObsConfig();
            if (safetyCopy.Length == 0) throw new InvalidOperationException("Geri yükleme öncesi güvenlik kopyası alınamadı; geri yükleme başlatılmadı.");

            BackupManifest? manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(latest, "manifest.json")));
            if (manifest is null || manifest.Files.Count == 0) throw new InvalidDataException("Yedek manifesti boş veya bozuk.");
            string targetBasic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "basic");
            string sourceBasic = Path.Combine(latest, "basic");
            Directory.CreateDirectory(targetBasic);
            var retained = new HashSet<string>(manifest.Files, StringComparer.OrdinalIgnoreCase);

            // Remove extra non-secret files from the restored OBS basic config, but always preserve service.json.
            foreach (string file in Directory.EnumerateFiles(targetBasic, "*", SearchOption.AllDirectories).ToArray())
            {
                string relative = Path.GetRelativePath(targetBasic, file);
                if (!IsSensitiveBackupFile(relative) && !retained.Contains(relative))
                {
                    try { File.Delete(file); } catch (Exception ex) { Log("Eski dosya temizlenemedi: " + relative + " — " + ex.Message); }
                }
            }

            foreach (string relative in manifest.Files)
            {
                if (IsSensitiveBackupFile(relative)) continue;
                string sourceFile = SafeCombine(sourceBasic, relative);
                string destination = SafeCombine(targetBasic, relative);
                if (!File.Exists(sourceFile)) throw new FileNotFoundException("Yedek dosyası eksik.", sourceFile);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(sourceFile, destination, true);
            }

            Log("Güvenli rollback tamamlandı: " + latest);
            Log("Yayın servisi kimlik dosyası (service.json) korunarak bırakıldı.");
            MessageBox.Show("Rollback tamamlandı. OBS'yi yeniden başlatabilirsin.\n\nYayın servis dosyası yedek tarafından değiştirilmedi.", "Rollback", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("Rollback hatası: " + ex.Message);
            MessageBox.Show(ex.Message, "Rollback hatası", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("TB OBS Auto Setup V9 — Tanılama raporu");
            builder.AppendLine("Tarih: " + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            builder.AppendLine("Gizlilik: Bu rapor WebSocket şifresi, stream key, service.json içeriği veya kaynak ayarlarını içermez.");
            builder.AppendLine("Windows mimarisi: " + (Environment.Is64BitOperatingSystem ? "x64" : "x86"));
            builder.AppendLine("CPU: " + cpu);
            builder.AppendLine("GPU öneri kaynağı: " + gpu);
            builder.AppendLine("RAM: " + (ramGb > 0 ? ramGb + " GB" : "Bilinmiyor"));
            builder.AppendLine("İşlemci thread: " + cpuCores);
            builder.AppendLine("OBS yolu: " + (obsPath.Length > 0 ? obsPath : "Bulunamadı"));
            builder.AppendLine("OBS sürümü: " + obsVersion);
            builder.AppendLine("OBS WebSocket sürümü: " + websocketVersion);
            builder.AppendLine("Bağlantı: " + (obs?.Connected == true ? "Bağlı" : "Bağlı değil"));
            builder.AppendLine("Ölçülen upload (Mbps): " + (lastUploadMbps > 0 ? lastUploadMbps.ToString("0.0", CultureInfo.InvariantCulture) : "ölçülmedi"));
            builder.AppendLine("Bitrate önerisi (Kbps): " + (recommendedBitrateKbps > 0 ? recommendedBitrateKbps.ToString(CultureInfo.InvariantCulture) : "öneri yok"));
            builder.AppendLine("Canvas: " + canvasWidth + "x" + canvasHeight);
            builder.AppendLine();
            builder.AppendLine("Sahneler:");
            foreach (string name in sceneNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) builder.AppendLine(" - " + name);
            builder.AppendLine();
            builder.AppendLine("Kaynak adları ve türleri:");
            foreach (string name in inputNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string kind = inputKindByName.TryGetValue(name, out string? value) ? value : "bilinmiyor";
                builder.AppendLine($" - {name} [{kind}]");
            }

            var dialog = new SaveFileDialog
            {
                Title = "Tanılama raporunu kaydet",
                FileName = "TB_OBS_Diagnostics_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt",
                Filter = "Metin raporu (*.txt)|*.txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                OverwritePrompt = true
            };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(false));
                Log("Tanılama raporu yerel diske kaydedildi: " + dialog.FileName);
            }
        }
        catch (Exception ex) { Log("Tanılama raporu oluşturulamadı: " + ex.Message); }
    }

    private static string ReadString(JsonElement element, string propertyName, string fallback)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : 0;
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
    }

    private static long GetLong(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : 0;
    }

    private static bool ReadRequiredBool(JsonElement element, string propertyName, string description)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        throw new InvalidDataException(description + " okunamadı; güvenlik amacıyla işlem durduruldu.");
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (obs is not null) await obs.DisposeAsync();
        Http.Dispose();
        base.OnClosed(e);
    }
}
