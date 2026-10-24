using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.Windows.Threading;

namespace TB_OBS_AutoSetup;

public partial class MainWindow : Window
{
    private readonly ObsWebSocketClient obs = new();
    private readonly AiObsService ai;
    private readonly HardwareInfoService hardware = new();
    private readonly SemaphoreSlim obsRefreshLock = new(1, 1);
    private readonly DispatcherTimer connectionTimer;
    private CancellationTokenSource? aiRequestCts;

    public MainWindow()
    {
        InitializeComponent();
        ai = new AiObsService(obs);
        connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        connectionTimer.Tick += async (_, _) => await MonitorConnectionAsync();
        connectionTimer.Start();
        Closed += async (_, _) =>
        {
            connectionTimer.Stop();
            aiRequestCts?.Cancel();
            ai.Dispose();
            obsRefreshLock.Dispose();
            await obs.DisposeAsync();
        };
        AppendChat("TB AI", "Hazırım. OBS'yi bilmene gerek yok. Ne yapmak istediğini normal bir cümleyle yaz. AI bilgisayarında çalışır; API anahtarı istemez.");
        Loaded += async (_, _) =>
        {
            await CheckAiAsync();
            RefreshSystemInfo();
            await TryAutoConnectObsAsync();
        };
    }


    private void RefreshSystemInfo()
    {
        SystemSummary.Text = hardware.Summary();
        ObsInstallText.Text = hardware.FindObsPath() is { Length: > 0 } p ? p : "OBS kurulumu bulunamadı";
        ObsProcessText.Text = hardware.IsObsRunning ? "OBS çalışıyor" : "OBS çalışmıyor";
    }

    private async Task TryAutoConnectObsAsync(bool showErrors = false)
    {
        if (obs.Connected) return;
        if (!hardware.IsObsRunning)
        {
            AutoObsStatus.Text = "OBS açık değil. İstersen OBS'yi otomatik açabilirsin.";
            return;
        }
        try
        {
            AutoObsStatus.Text = "OBS bulundu. WebSocket bağlantısı deneniyor...";
            await obs.ConnectAsync(HostBox.Text.Trim(), 4455, ObsPasswordBox.Password);
            StatusText.Text = "Bağlı";
            StatusDot.Fill = Brushes.LightGreen;
            ConnectionDetail.Text = "OBS otomatik olarak bulundu ve bağlandı.";
            Footer.Text = "OBS bağlı • otomatik bağlantı";
            AutoObsStatus.Text = "OBS otomatik bağlandı.";
            await RefreshObsAsync();
            AiStatus.Text = "OBS bağlı. AI hazır.";
        }
        catch (Exception ex)
        {
            AutoObsStatus.Text = showErrors
                ? "OBS bulundu fakat WebSocket bağlantısı kurulamadı. OBS WebSocket'i açıp tekrar dene."
                : "OBS bağlantısı bekleniyor...";
            if (showErrors) ConnectionDetail.Text = ex.Message;
        }
    }

    private void RefreshSystem_Click(object sender, RoutedEventArgs e) => RefreshSystemInfo();

    private async void AutoConnect_Click(object sender, RoutedEventArgs e)
    {
        RefreshSystemInfo();
        if (!hardware.IsObsRunning)
        {
            if (!hardware.LaunchObs())
            {
                AutoObsStatus.Text = "OBS otomatik bulunamadı. OBS'yi kendin açman gerekiyor.";
                return;
            }
            AutoObsStatus.Text = "OBS açılıyor...";
            await Task.Delay(2500);
        }
        await TryAutoConnectObsAsync();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out int port))
        {
            MessageBox.Show("Port numarası sayı olmalı. Genellikle 4455 kullanılır.", "OBS bağlantısı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ConnectionDetail.Text = "Bağlanılıyor...";
            await obs.ConnectAsync(HostBox.Text.Trim(), port, ObsPasswordBox.Password);
            StatusText.Text = "Bağlı";
            StatusDot.Fill = Brushes.LightGreen;
            Footer.Text = $"OBS bağlı • {HostBox.Text.Trim()}:{port}";
            ConnectionDetail.Text = "Bağlantı başarılı. Artık TB AI OBS durumunu okuyabilir ve izin verilen işlemleri yapabilir.";
            await RefreshObsAsync();
            AiStatus.Text = "OBS bağlı. AI hazır.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Bağlanamadı";
            StatusDot.Fill = (Brush)FindResource("Danger");
            ConnectionDetail.Text = "Bağlantı kurulamadı: " + ex.Message;
            MessageBox.Show(ex.Message, "OBS bağlantısı", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RefreshObsAsync()
    {
        if (!obs.Connected) return;
        if (!await obsRefreshLock.WaitAsync(0)) return;
        try
        {
            var scenes = await obs.CallAsync("GetSceneList");
            string? selected = ScenesList.SelectedItem as string;
            ScenesList.Items.Clear();
            if (scenes.TryGetProperty("scenes", out var arr))
                foreach (var s in arr.EnumerateArray())
                    if (s.TryGetProperty("sceneName", out var n)) ScenesList.Items.Add(n.GetString());

            var current = scenes.TryGetProperty("currentProgramSceneName", out var cp) ? cp.GetString() : "";
            if (!string.IsNullOrWhiteSpace(selected) && ScenesList.Items.Contains(selected))
                ScenesList.SelectedItem = selected;
            else if (!string.IsNullOrWhiteSpace(current) && ScenesList.Items.Contains(current))
                ScenesList.SelectedItem = current;

            ControlStatus.Text = $"Bağlı • Aktif sahne: {current} • {ScenesList.Items.Count} sahne";
        }
        finally
        {
            obsRefreshLock.Release();
        }
    }

    private async Task MonitorConnectionAsync()
    {
        try
        {
            if (obs.Connected)
            {
                StatusText.Text = "Bağlı";
                StatusDot.Fill = Brushes.LightGreen;
                return;
            }

            if (!hardware.IsObsRunning)
            {
                StatusText.Text = "OBS kapalı";
                StatusDot.Fill = (Brush)FindResource("Danger");
                return;
            }

            // OBS açıkken bağlantı koptuysa sessizce yeniden dene; kullanıcıyı popup ile rahatsız etme.
            await TryAutoConnectObsAsync(showErrors: false);
        }
        catch { }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await RefreshObsAsync(); }
        catch (Exception ex) { ControlStatus.Text = "Yenileme hatası: " + ex.Message; }
    }

    private async void Scene_Changed(object sender, SelectionChangedEventArgs e)
    {
        SourcesList.Items.Clear();
        if (!obs.Connected || ScenesList.SelectedItem is not string scene) return;
        try
        {
            var r = await obs.CallAsync("GetSceneItemList", new { sceneName = scene });
            if (r.TryGetProperty("sceneItems", out var a))
                foreach (var i in a.EnumerateArray())
                    if (i.TryGetProperty("sourceName", out var n)) SourcesList.Items.Add(n.GetString());
        }
        catch (Exception ex) { ControlStatus.Text = ex.Message; }
    }

    private async void SetScene_Click(object sender, RoutedEventArgs e)
    {
        if (!obs.Connected || ScenesList.SelectedItem is not string scene) return;
        try { await obs.CallAsync("SetCurrentProgramScene", new { sceneName = scene }); await RefreshObsAsync(); }
        catch (Exception ex) { ControlStatus.Text = ex.Message; }
    }

    private async void ToggleSource_Click(object sender, RoutedEventArgs e)
    {
        if (!obs.Connected || ScenesList.SelectedItem is not string scene || SourcesList.SelectedItem is not string source) return;
        try
        {
            var r = await obs.CallAsync("GetSceneItemList", new { sceneName = scene });
            foreach (var i in r.GetProperty("sceneItems").EnumerateArray())
            {
                if (i.GetProperty("sourceName").GetString() != source) continue;
                bool enabled = i.GetProperty("sceneItemEnabled").GetBoolean();
                await obs.CallAsync("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = i.GetProperty("sceneItemId").GetInt64(), sceneItemEnabled = !enabled });
                break;
            }
            await Scene_RefreshOnly();
        }
        catch (Exception ex) { ControlStatus.Text = ex.Message; }
    }

    private async Task Scene_RefreshOnly()
    {
        if (ScenesList.SelectedItem is not string s) return;
        SourcesList.Items.Clear();
        var r = await obs.CallAsync("GetSceneItemList", new { sceneName = s });
        foreach (var i in r.GetProperty("sceneItems").EnumerateArray()) SourcesList.Items.Add(i.GetProperty("sourceName").GetString());
    }

    private async void DeleteSource_Click(object sender, RoutedEventArgs e)
    {
        if (!obs.Connected || ScenesList.SelectedItem is not string scene || SourcesList.SelectedItem is not string source) return;
        if (MessageBox.Show($"'{source}' kaynağını bu sahneden kaldırmak istiyor musun?", "Kaynak", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var r = await obs.CallAsync("GetSceneItemList", new { sceneName = scene });
            foreach (var i in r.GetProperty("sceneItems").EnumerateArray())
            {
                if (i.GetProperty("sourceName").GetString() != source) continue;
                await obs.CallAsync("RemoveSceneItem", new { sceneName = scene, sceneItemId = i.GetProperty("sceneItemId").GetInt64() });
                break;
            }
            await Scene_RefreshOnly();
        }
        catch (Exception ex) { ControlStatus.Text = ex.Message; }
    }

    private async void SendAi_Click(object sender, RoutedEventArgs e) => await SendAiAsync();

    private async void Prompt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await SendAiAsync();
        }
    }

    private async Task SendAiAsync()
    {
        string text = PromptBox.Text.Trim();
        if (text.Length == 0) return;

        ai.AllowActions = AllowActionsBox.IsChecked == true;
        PromptBox.Clear();
        AppendChat("Sen", text);
        AiStatus.Text = "TB AI düşünüyor...";

        aiRequestCts?.Cancel();
        aiRequestCts?.Dispose();
        aiRequestCts = new CancellationTokenSource();

        try
        {
            string answer = await ai.AskAsync(text, aiRequestCts.Token);
            AppendChat("TB AI", answer);
            AiStatus.Text = "Tamamlandı.";
            if (obs.Connected) await RefreshObsAsync();
        }
        catch (OperationCanceledException)
        {
            AppendChat("TB AI", "İşlem iptal edildi.");
            AiStatus.Text = "İptal edildi.";
        }
        catch (Exception ex)
        {
            AppendChat("Hata", ex.Message);
            AiStatus.Text = "İşlem başarısız.";
        }
    }

    private async void CheckAi_Click(object sender, RoutedEventArgs e) => await CheckAiAsync();

    private void InstallAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://ollama.com/download/windows",
                UseShellExecute = true
            });
            AiStatus.Text = "Yerel AI kurulumu için Ollama indirme sayfası açıldı.";
        }
        catch (Exception ex)
        {
            AiStatus.Text = "Kurulum sayfası açılamadı: " + ex.Message;
        }
    }

    private async Task CheckAiAsync()
    {
        AiStatus.Text = "Yerel AI kontrol ediliyor...";
        AiEngineText.Text = "  Yerel AI kontrol ediliyor";
        AiDot.Fill = (Brush)FindResource("Danger");
        var result = await ai.CheckAsync();
        AiEngineText.Text = result.ok ? "  Yerel AI hazır" : "  Yerel AI hazır değil";
        AiDot.Fill = result.ok ? Brushes.LightGreen : (Brush)FindResource("Danger");
        AiStatus.Text = result.message;
    }

    private void CancelAi_Click(object sender, RoutedEventArgs e)
    {
        aiRequestCts?.Cancel();
        AiStatus.Text = "AI işlemi iptal ediliyor...";
    }

    private void FocusAi_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 0;
        PromptBox.Focus();
    }

    private void QuickScenes_Click(object sender, RoutedEventArgs e) => SetPrompt("OBS durumumu kontrol et ve bütün sahnelerimi, aktif sahneyi ve sahnelerdeki kaynakları özetle.");
    private void QuickCamera_Click(object sender, RoutedEventArgs e) => SetPrompt("Kamerayı sağ alta koy. Önce kamera kaynağının adını kontrol et.");
    private void QuickMic_Click(object sender, RoutedEventArgs e) => SetPrompt("Mikrofonumu temizle ve sesim için uygun filtreleri öner.");
    private void QuickPerformance_Click(object sender, RoutedEventArgs e) => SetPrompt("OBS performansını kontrol et. FPS, CPU, frame/drop ve encoder durumunu incele; sorun varsa nedenini ve çözümünü söyle.");

    private void SetPrompt(string text)
    {
        MainTabs.SelectedIndex = 0;
        PromptBox.Text = text;
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text.Length;
    }

    private void AppendChat(string who, string text)
    {
        var border = new Border
        {
            Background = who == "Sen" ? (Brush)FindResource("Surface2") : (Brush)FindResource("Surface"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 9)
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = who, FontWeight = FontWeights.SemiBold, Foreground = who == "Sen" ? (Brush)FindResource("Accent") : (Brush)FindResource("Purple") });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 5, 0, 0) });
        border.Child = panel;
        ChatPanel.Children.Add(border);
        ChatScroll.ScrollToEnd();
    }
}
