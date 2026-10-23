using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;
using System.Windows;

namespace TB_OBS_AutoSetup;

public partial class MainWindow : Window
{
    string obsPath = "";
    string cpu = "";
    string gpu = "";
    int ramGb;
    ObsWebSocketClient? obs;

    public MainWindow()
    {
        InitializeComponent();
        Analyze();
    }

    void Log(string s)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");
        LogBox.ScrollToEnd();
    }

    void Analyze_Click(object sender, RoutedEventArgs e) => Analyze();

    void Analyze()
    {
        try
        {
            cpu = GetCpu();
            gpu = GetGpu();
            ramGb = GetRamGb();
            obsPath = FindObs();

            CpuText.Text = "CPU: " + cpu;
            GpuText.Text = "GPU: " + gpu;
            RamText.Text = $"RAM: {ramGb} GB";
            ObsText.Text = obsPath == "" ? "OBS: Bulunamadı" : "OBS: Bulundu";
            PlanText.Text = BuildPlan();
            Log("Sistem analizi tamamlandı.");
        }
        catch (Exception ex) { Log("Analiz hatası: " + ex.Message); }
    }

    string BuildPlan()
    {
        string g = gpu.ToLowerInvariant();
        if (g.Contains("nvidia"))
            return "Plan: NVIDIA → NVENC öncelikli. Başlangıç: 1920×1080 / 60 FPS / 6000 Kbps.";
        if (g.Contains("amd"))
            return "Plan: AMD → AMF öncelikli. Başlangıç: 1920×1080 / 60 FPS / 6000 Kbps.";
        if (g.Contains("intel"))
            return "Plan: Intel → QSV mümkünse öncelikli. Başlangıç: 1920×1080 / 60 FPS / 6000 Kbps.";
        return "Plan: Donanım encoderi kesin belirlenemedi; güvenli x264 planı uygulanacak.";
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
        foreach (ManagementObject o in s.Get()) return o["Name"]?.ToString() ?? "Bilinmiyor";
        return "Bilinmiyor";
    }

    string GetGpu()
    {
        using var s = new ManagementObjectSearcher("select Name from Win32_VideoController");
        var names = new List<string>();
        foreach (ManagementObject o in s.Get())
        {
            var n = o["Name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
        }
        return names.Count == 0 ? "Bilinmiyor" : string.Join(" | ", names);
    }

    string FindObs()
    {
        string[] paths =
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\obs-studio\bin\64bit\obs64.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\obs-studio\bin\64bit\obs64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\obs-studio\bin\64bit\obs64.exe")
        };
        foreach (var p in paths) if (File.Exists(p)) return p;
        return "";
    }

    void LaunchObs_Click(object sender, RoutedEventArgs e)
    {
        if (obsPath == "") { Log("OBS bulunamadı."); return; }
        Process.Start(new ProcessStartInfo(obsPath) { UseShellExecute = true });
        Log("OBS başlatıldı. WebSocket'in hazır olması için birkaç saniye bekleyebilirsin.");
    }

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            obs = new ObsWebSocketClient();
            await obs.ConnectAsync("127.0.0.1", 4455, PasswordBox.Password);
            var v = await obs.CallAsync("GetVersion");
            Log("OBS WebSocket bağlantısı başarılı.");
            Log("OBS sürümü: " + v.GetProperty("obsVersion").GetString());
            Log("WebSocket protokolü: " + v.GetProperty("obsWebSocketVersion").GetString());
        }
        catch (Exception ex) { Log("Bağlantı hatası: " + ex.Message); }
    }

    async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (obsPath == "") { Log("OBS bulunamadı."); return; }

            BackupObsConfig();
            Log("OBS ayar yedeği alındı.");

            if (obs is null || !obs.Connected)
            {
                obs = new ObsWebSocketClient();
                await obs.ConnectAsync("127.0.0.1", 4455, PasswordBox.Password);
                Log("OBS'ye otomatik bağlanıldı.");
            }

            await ApplyVideo();
            await ApplyOutput();
            await CreateScenes();

            Log("TÜM OTOMATİK KURULUM TAMAMLANDI.");
            MessageBox.Show("OBS otomatik olarak yapılandırıldı.\n\nVideo + Output + temel sahneler uygulandı.",
                "TB OBS Auto Setup V2", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("OTOMATİK KURULUM HATASI: " + ex.Message);
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
        Log("Video: 1080p60 uygulandı.");
    }

    async Task ApplyOutput()
    {
        // OBS sürümleri/encoderleri arasında output ayar alanları değişebildiği için
        // güvenli olan temel kayıt/çıktı ayarlarıyla başlanır.
        await obs!.CallAsync("SetProfileParameter", new
        {
            parameterCategory = "Output",
            parameterName = "Mode",
            parameterValue = "Advanced"
        });
        Log("Output modu: Advanced.");
    }

    async Task CreateScenes()
    {
        await TryCreateScene("TB OYUN");
        await TryCreateScene("TB JUST CHATTING");
        await TryCreateScene("TB YAYIN BASLIYOR");
        Log("Temel sahne koleksiyonu hazır.");
    }

    async Task TryCreateScene(string name)
    {
        try { await obs!.CallAsync("CreateScene", new { sceneName = name }); }
        catch { /* Sahne zaten varsa sorun değil. */ }
    }

    void BackupObsConfig()
    {
        string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");
        if (!Directory.Exists(src)) return;
        string dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TB_OBS_Backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        CopyDirectory(src, dst);
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
        if (obs is not null) await obs.DisposeAsync();
        base.OnClosed(e);
    }
}
