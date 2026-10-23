using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace TB_OBS_AutoSetup;

public partial class MainWindow : Window
{
    string obsPath = "";
    string cpu = "";
    string gpu = "";
    int ramGb;
    int cpuCores;
    ObsWebSocketClient? obs;
    string lastBackup = "";

    public MainWindow()
    {
        InitializeComponent();
        Analyze();
        Log("TB OBS Auto Setup V4 Ultimate hazır.");
        Log("Yeni modüller: upload testi, OBS health, performance, replay buffer, audio filters, rollback.");
    }

    void Log(string text)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
            LogBox.ScrollToEnd();
        });
    }

    void Analyze_Click(object sender, RoutedEventArgs e) => Analyze();

    void Analyze()
    {
        try
        {
            cpu = GetCpu();
            gpu = GetGpu();
            ramGb = GetRamGb();
            cpuCores = Environment.ProcessorCount;
            obsPath = FindObs();

            CpuCard.Text = Shorten(cpu);
            GpuCard.Text = Shorten(gpu);
            RamCard.Text = $"{ramGb} GB • {cpuCores} thread";
            EncoderCard.Text = GetEncoderPlan();
            PlanText.Text = obsPath == "" ? "OBS bulunamadı" : "Sistem hazır";
            Log("✓ Donanım ve OBS analizi tamamlandı.");
            Log("• Öneri: " + GetEncoderPlan());
        }
        catch (Exception ex)
        {
            Log("Analiz hatası: " + ex.Message);
        }
    }

    string GetEncoderPlan()
    {
        string g = gpu.ToLowerInvariant();
        if (g.Contains("nvidia")) return "NVENC • 1080p60";
        if (g.Contains("amd")) return "AMF • 1080p60";
        if (g.Contains("intel")) return "QSV • 1080p60";
        return cpuCores >= 8 ? "x264 • 1080p60" : "x264 • 720p60";
    }

    string Shorten(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Bilinmiyor";
        return value.Length > 42 ? value.Substring(0, 42) + "…" : value;
    }

    int GetRamGb()
    {
        using var s = new ManagementObjectSearcher("select TotalPhysicalMemory from Win32_ComputerSystem");
        foreach (ManagementObject o in s.Get())
        {
            if (o["TotalPhysicalMemory"] is not null)
            {
                double bytes = Convert.ToDouble(o["TotalPhysicalMemory"]);
                return (int)Math.Round(bytes / 1024d / 1024d / 1024d);
            }
        }
        return 0;
    }

    string GetCpu()
    {
        using var s = new ManagementObjectSearcher("select Name from Win32_Processor");
        foreach (ManagementObject o in s.Get())
            return o["Name"]?.ToString() ?? "Bilinmiyor";
        return "Bilinmiyor";
    }

    string GetGpu()
    {
        using var s = new ManagementObjectSearcher("select Name from Win32_VideoController");
        foreach (ManagementObject o in s.Get())
        {
            var name = o["Name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        return "Bilinmiyor";
    }

    string FindObs()
    {
        string[] paths =
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\obs-studio\bin\64bit\obs64.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\obs-studio\bin\64bit\obs64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\obs-studio\bin\64bit\obs64.exe")
        };

        foreach (var path in paths)
            if (File.Exists(path)) return path;

        return "";
    }

    void LaunchObs_Click(object sender, RoutedEventArgs e)
    {
        if (obsPath == "")
        {
            Log("OBS bulunamadı.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(obsPath) { UseShellExecute = true });
            Log("✓ OBS başlatıldı.");
        }
        catch (Exception ex)
        {
            Log("OBS başlatma hatası: " + ex.Message);
        }
    }

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureConnected();
            await HealthCheck();
        }
        catch (Exception ex)
        {
            SetConnection(false);
            Log("Bağlantı hatası: " + ex.Message);
        }
    }

    async Task EnsureConnected()
    {
        if (obs is not null && obs.Connected) return;

        obs = new ObsWebSocketClient();
        await obs.ConnectAsync("127.0.0.1", 4455, PasswordBox.Password);

        var version = await obs.CallAsync("GetVersion");
        SetConnection(true);
        Log("✓ OBS WebSocket bağlantısı başarılı.");

        if (version.ValueKind != JsonValueKind.Undefined &&
            version.TryGetProperty("obsVersion", out var ov))
        {
            Log("• OBS: " + ov.GetString());
        }

        if (version.ValueKind != JsonValueKind.Undefined &&
            version.TryGetProperty("obsWebSocketVersion", out var wv))
        {
            Log("• WebSocket: " + wv.GetString());
        }
    }

    void SetConnection(bool connected)
    {
        ConnectionText.Text = connected ? "● OBS BAĞLI" : "● OBS BAĞLI DEĞİL";
        ConnectionText.Foreground = connected ? Brushes.LightGreen : Brushes.Gray;
    }

    async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (obsPath == "")
            {
                Log("OBS bulunamadı.");
                return;
            }

            PlanText.Text = "Kurulum çalışıyor…";
            lastBackup = BackupObsConfig();
            if (lastBackup != "")
                Log("✓ OBS ayarlarının yedeği alındı: " + lastBackup);

            await EnsureConnected();

            await ApplyVideo();
            await ApplyOutput();
            await ApplyRecordingDirectory();
            await CreateProfessionalScenes();
            await ConfigureAudioFilters();
            await TryStartReplayBuffer();
            await HealthCheck();

            PlanText.Text = "Kurulum tamamlandı";
            Log("========================================");
            Log("✓ ULTIMATE OBS KURULUMU TAMAMLANDI.");
            Log("✓ Video + output + sahneler + kaynaklar + audio zinciri + kayıt yolu kontrol edildi.");

            MessageBox.Show(
                "OBS Ultimate profil hazırlığı tamamlandı.\n\n" +
                "Sahneler, kaynaklar, ses filtreleri ve video ayarları uygulandı.\n" +
                "OBS ayarlarının yedeği alındı.\n\n" +
                "Not: Webcam cihazı ve bazı encoder/bitrate seçenekleri donanıma göre OBS içinde değişebilir.",
                "TB OBS Auto Setup V4",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PlanText.Text = "Kurulum hatası";
            Log("KURULUM HATASI: " + ex.Message);
            MessageBox.Show(ex.Message, "Kurulum Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    async Task ApplyVideo()
    {
        await obs!.CallAsync("SetVideoSettings", new
        {
            baseWidth = 1920,
            baseHeight = 1080,
            outputWidth = 1920,
            outputHeight = 1080,
            fpsNumerator = 60,
            fpsDenominator = 1
        });

        Log("✓ Video: 1920x1080 / 60 FPS");
    }

    async Task ApplyOutput()
    {
        try
        {
            await obs!.CallAsync("SetProfileParameter", new
            {
                parameterCategory = "Output",
                parameterName = "Mode",
                parameterValue = "Advanced"
            });
            Log("✓ Output modu: Advanced");
        }
        catch (Exception ex)
        {
            Log("! Advanced output ayarı atlandı: " + ex.Message);
        }
    }

    async Task ApplyRecordingDirectory()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "TB OBS Recordings");

            Directory.CreateDirectory(dir);

            await obs!.CallAsync("SetRecordDirectory", new { recordDirectory = dir });
            Log("✓ Kayıt klasörü: " + dir);
        }
        catch (Exception ex)
        {
            Log("! Kayıt klasörü ayarlanamadı: " + ex.Message);
        }
    }

    async Task CreateProfessionalScenes()
    {
        string[] scenes =
        {
            "TB OYUN",
            "TB JUST CHATTING",
            "TB YAYIN BASLIYOR",
            "TB BRB",
            "TB YAYIN BITIYOR"
        };

        foreach (var scene in scenes)
            await TryCreateScene(scene);

        await AddGameCapture("TB OYUN");
        await AddSpecialAudio("TB OYUN");

        await AddWebcam("TB JUST CHATTING");
        await AddSpecialAudio("TB JUST CHATTING");
        await AddTextSource("TB JUST CHATTING", "TB CHAT LABEL", "JUST CHATTING");

        await AddColorSource("TB YAYIN BASLIYOR", "TB START BACKGROUND");
        await AddTextSource("TB YAYIN BASLIYOR", "TB START TEXT", "YAYIN BAŞLIYOR");

        await AddColorSource("TB BRB", "TB BRB BACKGROUND");
        await AddTextSource("TB BRB", "TB BRB TEXT", "KISA BİR MOLA");

        await AddColorSource("TB YAYIN BITIYOR", "TB END BACKGROUND");
        await AddTextSource("TB YAYIN BITIYOR", "TB END TEXT", "YAYIN SONA ERİYOR");

        try
        {
            await obs!.CallAsync("SetCurrentProgramScene", new { sceneName = "TB OYUN" });
        }
        catch { }

        Log("✓ 5 profesyonel sahne hazır.");
    }

    async Task TryCreateScene(string name)
    {
        try
        {
            await obs!.CallAsync("CreateScene", new { sceneName = name });
            Log("✓ Sahne oluşturuldu: " + name);
        }
        catch
        {
            Log("• Sahne zaten mevcut: " + name);
        }
    }

    async Task AddGameCapture(string scene)
    {
        string source = "TB GAME CAPTURE";

        try
        {
            await obs!.CallAsync("CreateInput", new
            {
                sceneName = scene,
                inputName = source,
                inputKind = "game_capture",
                inputSettings = new
                {
                    capture_mode = "any_fullscreen",
                    allow_transparency = false
                },
                sceneItemEnabled = true
            });
            Log("✓ Game Capture eklendi.");
        }
        catch
        {
            await AddExistingSourceToScene(scene, source);
        }
    }

    async Task AddWebcam(string scene)
    {
        string source = "TB WEBCAM";

        try
        {
            var defaults = await obs!.CallAsync("GetInputDefaultSettings",
                new { inputKind = "dshow_input" });

            JsonElement settings = defaults.ValueKind != JsonValueKind.Undefined &&
                                   defaults.TryGetProperty("defaultInputSettings", out var d)
                ? d
                : JsonDocument.Parse("{}").RootElement;

            await obs.CallAsync("CreateInput", new
            {
                sceneName = scene,
                inputName = source,
                inputKind = "dshow_input",
                inputSettings = settings,
                sceneItemEnabled = true
            });

            Log("✓ Webcam kaynağı eklendi (OBS varsayılan cihazı).");
        }
        catch
        {
            await AddExistingSourceToScene(scene, source);
            Log("! Webcam mevcut kaynak olarak bağlanmaya çalışıldı.");
        }
    }

    async Task AddSpecialAudio(string scene)
    {
        try
        {
            var result = await obs!.CallAsync("GetSpecialInputs");

            foreach (string property in new[] { "desktop1", "mic1" })
            {
                if (result.ValueKind != JsonValueKind.Undefined &&
                    result.TryGetProperty(property, out var value))
                {
                    string? source = value.GetString();
                    if (!string.IsNullOrWhiteSpace(source))
                        await AddExistingSourceToScene(scene, source);
                }
            }

            Log("✓ Mikrofon / masaüstü ses kaynakları bağlandı.");
        }
        catch (Exception ex)
        {
            Log("! Ses kaynakları eklenemedi: " + ex.Message);
        }
    }

    async Task ConfigureAudioFilters()
    {
        try
        {
            var result = await obs!.CallAsync("GetSpecialInputs");
            if (result.ValueKind == JsonValueKind.Undefined ||
                !result.TryGetProperty("mic1", out var micElement))
            {
                Log("! Mikrofon kaynağı bulunamadı; filtreler atlandı.");
                return;
            }

            string? mic = micElement.GetString();
            if (string.IsNullOrWhiteSpace(mic))
            {
                Log("! Mikrofon kaynağı boş; filtreler atlandı.");
                return;
            }

            await TryCreateFilter(mic, "TB Noise Suppression", "noise_suppress_filter");
            await TryCreateFilter(mic, "TB Compressor", "compressor_filter");
            await TryCreateFilter(mic, "TB Expander", "expander_filter");

            Log("✓ Mikrofon filtre zinciri hazırlandı.");
        }
        catch (Exception ex)
        {
            Log("! Audio filter işlemi atlandı: " + ex.Message);
        }
    }

    async Task TryCreateFilter(string source, string filterName, string filterKind)
    {
        try
        {
            await obs!.CallAsync("CreateSourceFilter", new
            {
                sourceName = source,
                filterName = filterName,
                filterKind = filterKind,
                filterSettings = new { }
            });
            Log($"✓ Filtre eklendi: {filterName}");
        }
        catch
        {
            Log($"• Filtre mevcut/uygulanamadı: {filterName}");
        }
    }

    async Task AddTextSource(string scene, string source, string text)
    {
        try
        {
            await obs!.CallAsync("CreateInput", new
            {
                sceneName = scene,
                inputName = source,
                inputKind = "text_gdiplus",
                inputSettings = new { text = text },
                sceneItemEnabled = true
            });
            Log("✓ Metin kaynağı: " + text);
        }
        catch
        {
            await AddExistingSourceToScene(scene, source);
        }
    }

    async Task AddColorSource(string scene, string source)
    {
        try
        {
            await obs!.CallAsync("CreateInput", new
            {
                sceneName = scene,
                inputName = source,
                inputKind = "color_source",
                inputSettings = new
                {
                    color = 4278848015u,
                    width = 1920,
                    height = 1080
                },
                sceneItemEnabled = true
            });
            Log("✓ Arka plan kaynağı eklendi.");
        }
        catch
        {
            await AddExistingSourceToScene(scene, source);
        }
    }

    async Task AddExistingSourceToScene(string scene, string source)
    {
        try
        {
            await obs!.CallAsync("GetSceneItemId", new
            {
                sceneName = scene,
                sourceName = source
            });
            return;
        }
        catch { }

        try
        {
            await obs!.CallAsync("CreateSceneItem", new
            {
                sceneName = scene,
                sourceName = source,
                sceneItemEnabled = true
            });
        }
        catch (Exception ex)
        {
            Log("! Kaynak bağlanamadı: " + source + " — " + ex.Message);
        }
    }

    async Task TryStartReplayBuffer()
    {
        try
        {
            var status = await obs!.CallAsync("GetReplayBufferStatus");
            bool active = status.TryGetProperty("outputActive", out var activeValue) && activeValue.GetBoolean();

            if (active)
            {
                Log("✓ Replay Buffer zaten aktif.");
                return;
            }

            await obs.CallAsync("StartReplayBuffer");
            Log("✓ Replay Buffer başlatıldı.");
        }
        catch (Exception ex)
        {
            Log("! Replay Buffer başlatılamadı. OBS'de Replay Buffer ayarı/hotkey gerekebilir: " + ex.Message);
        }
    }

    async void Replay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureConnected();
            await TryStartReplayBuffer();
        }
        catch (Exception ex)
        {
            Log("Replay Buffer hatası: " + ex.Message);
        }
    }

    async void Health_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureConnected();
            await HealthCheck();
        }
        catch (Exception ex)
        {
            Log("Health check hatası: " + ex.Message);
        }
    }

    async Task HealthCheck()
    {
        var stats = await obs!.CallAsync("GetStats");

        double fps = GetDouble(stats, "activeFps");
        double renderMs = GetDouble(stats, "averageFrameRenderTime");
        long renderSkipped = GetLong(stats, "renderSkippedFrames");
        long renderTotal = GetLong(stats, "renderTotalFrames");
        long outputSkipped = GetLong(stats, "outputSkippedFrames");
        long outputTotal = GetLong(stats, "outputTotalFrames");
        double cpuUsage = GetDouble(stats, "cpuUsage");

        double renderDrop = renderTotal > 0 ? renderSkipped * 100.0 / renderTotal : 0;
        double outputDrop = outputTotal > 0 ? outputSkipped * 100.0 / outputTotal : 0;

        string health = "GOOD";
        if (renderDrop > 1.0 || outputDrop > 1.0 || renderMs > 16.7) health = "CHECK";
        if (renderDrop > 5.0 || outputDrop > 5.0 || renderMs > 25) health = "BAD";

        HealthCard.Text = health;
        HealthCard.Foreground = health == "GOOD" ? Brushes.LightGreen :
                                health == "CHECK" ? Brushes.Gold : Brushes.OrangeRed;

        StatsText.Text = $"FPS {fps:0.0} • Render {renderMs:0.0} ms\n" +
                         $"Render skip {renderDrop:0.00}% • Output skip {outputDrop:0.00}%\n" +
                         $"OBS CPU {cpuUsage:0.0}%";

        Log($"✓ OBS Health: FPS={fps:0.0}, render={renderMs:0.0}ms, render skip={renderDrop:0.00}%, output skip={outputDrop:0.00}%");
    }

    static double GetDouble(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        return 0;
    }

    static long GetLong(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            return value.GetInt64();
        return 0;
    }

    async void Performance_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureConnected();

            Log("Performance test başladı. OBS açık kalmalı; yayın başlatılmaz.");
            var before = await obs!.CallAsync("GetStats");
            await Task.Delay(5000);
            var after = await obs.CallAsync("GetStats");

            double beforeFps = GetDouble(before, "activeFps");
            double afterFps = GetDouble(after, "activeFps");
            double beforeRender = GetDouble(before, "averageFrameRenderTime");
            double afterRender = GetDouble(after, "averageFrameRenderTime");

            long beforeRS = GetLong(before, "renderSkippedFrames");
            long afterRS = GetLong(after, "renderSkippedFrames");
            long beforeRT = GetLong(before, "renderTotalFrames");
            long afterRT = GetLong(after, "renderTotalFrames");

            long beforeOS = GetLong(before, "outputSkippedFrames");
            long afterOS = GetLong(after, "outputSkippedFrames");
            long beforeOT = GetLong(before, "outputTotalFrames");
            long afterOT = GetLong(after, "outputTotalFrames");

            long renderSkipped = Math.Max(0, afterRS - beforeRS);
            long renderTotal = Math.Max(0, afterRT - beforeRT);
            long outputSkipped = Math.Max(0, afterOS - beforeOS);
            long outputTotal = Math.Max(0, afterOT - beforeOT);

            double renderDrop = renderTotal > 0 ? renderSkipped * 100.0 / renderTotal : 0;
            double outputDrop = outputTotal > 0 ? outputSkipped * 100.0 / outputTotal : 0;

            Log($"✓ 5sn test: FPS {beforeFps:0.0} → {afterFps:0.0}");
            Log($"✓ Render time: {beforeRender:0.0}ms → {afterRender:0.0}ms");
            Log($"✓ Render skip: {renderDrop:0.00}% • Output skip: {outputDrop:0.00}%");

            if (renderDrop < 1 && outputDrop < 1 && afterRender < 16.7)
                Log("✓ Sonuç: yayın için sağlıklı görünüyor.");
            else if (renderDrop < 5 && outputDrop < 5)
                Log("! Sonuç: kabul edilebilir, ancak sahne/oyun yükü izlenmeli.");
            else
                Log("! Sonuç: performans yükü yüksek. FPS/çözünürlük/encoder düşürmek gerekebilir.");
        }
        catch (Exception ex)
        {
            Log("Performance test hatası: " + ex.Message);
        }
    }

    async void UploadTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Log("Upload testi başlıyor (küçük test paketi, yayın başlatılmaz)…");

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            byte[] data = new byte[4 * 1024 * 1024];
            new Random().NextBytes(data);

            using var content = new ByteArrayContent(data);
            var sw = Stopwatch.StartNew();
            using var response = await client.PostAsync("https://speed.cloudflare.com/__up", content);
            sw.Stop();

            response.EnsureSuccessStatusCode();

            double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
            double mbps = data.Length * 8d / seconds / 1_000_000d;

            Log($"✓ Upload test sonucu: yaklaşık {mbps:0.0} Mbps");
            Log("• Önerilen yayın bitrate'i bunun tamamı değil; ağ payı bırakılmalıdır.");
        }
        catch (Exception ex)
        {
            Log("! Upload testi çalışmadı: " + ex.Message);
            Log("• Bu test harici speed-test sunucusuna erişim gerektirir.");
        }
    }

    string BackupObsConfig()
    {
        string source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio");

        if (!Directory.Exists(source))
        {
            Log("OBS ayar klasörü bulunamadı; yedek atlandı.");
            return "";
        }

        string target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TB_OBS_Backups",
            DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        CopyDirectory(source, target);
        return target;
    }

    void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TB_OBS_Backups");

        Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
        Log("✓ Backup klasörü açıldı.");
    }

    void Rollback_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "TB_OBS_Backups");

            if (!Directory.Exists(root))
            {
                MessageBox.Show("Henüz yedek bulunamadı.", "Rollback", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string[] dirs = Directory.GetDirectories(root);
            if (dirs.Length == 0)
            {
                MessageBox.Show("Henüz yedek bulunamadı.", "Rollback", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Array.Sort(dirs);
            string latest = dirs[^1];

            var answer = MessageBox.Show(
                "En son OBS yedeği geri yüklenecek:\n\n" + latest +
                "\n\nOBS tamamen kapalı olmalıdır. Devam edilsin mi?",
                "OBS Rollback",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;

            if (Process.GetProcessesByName("obs64").Length > 0)
            {
                MessageBox.Show("Önce OBS'yi tamamen kapat.", "Rollback", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "obs-studio");

            if (Directory.Exists(target))
            {
                string safety = target + "_before_rollback_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                CopyDirectory(target, safety);
                Log("✓ Rollback öncesi güvenlik yedeği: " + safety);
            }

            CopyDirectory(latest, target);
            Log("✓ Son OBS yedeği geri yüklendi.");
            MessageBox.Show("Rollback tamamlandı. OBS'yi yeniden başlatabilirsin.", "Rollback", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("Rollback hatası: " + ex.Message);
            MessageBox.Show(ex.Message, "Rollback Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);

        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (obs is not null)
            await obs.DisposeAsync();

        base.OnClosed(e);
    }
}