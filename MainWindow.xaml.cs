using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TB_OBS_AutoSetup;

public partial class MainWindow : Window
{
    private readonly ObsWebSocketClient obs = new();
    private readonly AiObsService ai;

    public MainWindow()
    {
        InitializeComponent();
        ai = new AiObsService(obs);
        Closed += async (_, _) => await obs.DisposeAsync();
        AppendChat("🤖 TB AI", "Hazırım. Bana normal bir cümleyle ne yapmak istediğini söyle. Örneğin: 'PS5 için yayın sahnesi oluştur, kamerayı sağ alta koy ve kaydı başlat.'");
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await obs.ConnectAsync("127.0.0.1", 4455, "");
            StatusText.Text = "Bağlı"; StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            Footer.Text = "OBS bağlantısı başarılı.";
            await RefreshObsAsync();
            AiStatus.Text = "OBS bağlı. AI artık OBS durumunu okuyup izin verilen işlemleri yapabilir.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "OBS bağlantısı", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task RefreshObsAsync()
    {
        if (!obs.Connected) return;
        var scenes = await obs.CallAsync("GetSceneList");
        ScenesList.Items.Clear();
        if (scenes.TryGetProperty("scenes", out var arr)) foreach (var s in arr.EnumerateArray()) if (s.TryGetProperty("sceneName", out var n)) ScenesList.Items.Add(n.GetString());
        var current = scenes.TryGetProperty("currentProgramSceneName", out var cp) ? cp.GetString() : "";
        ControlStatus.Text = $"Bağlı. Aktif sahne: {current}";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) { try { await RefreshObsAsync(); } catch (Exception ex) { ControlStatus.Text = ex.Message; } }
    private async void Scene_Changed(object sender, SelectionChangedEventArgs e)
    {
        SourcesList.Items.Clear();
        if (!obs.Connected || ScenesList.SelectedItem is not string scene) return;
        try { var r = await obs.CallAsync("GetSceneItemList", new { sceneName = scene }); if (r.TryGetProperty("sceneItems", out var a)) foreach (var i in a.EnumerateArray()) if (i.TryGetProperty("sourceName", out var n)) SourcesList.Items.Add(n.GetString()); } catch { }
    }
    private async void SetScene_Click(object sender, RoutedEventArgs e) { if (!obs.Connected || ScenesList.SelectedItem is not string scene) return; try { await obs.CallAsync("SetCurrentProgramScene", new { sceneName = scene }); await RefreshObsAsync(); } catch (Exception ex) { ControlStatus.Text = ex.Message; } }
    private async void ToggleSource_Click(object sender, RoutedEventArgs e) { if (!obs.Connected || ScenesList.SelectedItem is not string scene || SourcesList.SelectedItem is not string source) return; try { var r = await obs.CallAsync("GetSceneItemList", new { sceneName = scene }); foreach (var i in r.GetProperty("sceneItems").EnumerateArray()) if (i.GetProperty("sourceName").GetString() == source) { bool enabled = i.GetProperty("sceneItemEnabled").GetBoolean(); await obs.CallAsync("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = i.GetProperty("sceneItemId").GetInt64(), sceneItemEnabled = !enabled }); break; } await Scene_RefreshOnly(); } catch (Exception ex) { ControlStatus.Text = ex.Message; } }
    private async Task Scene_RefreshOnly() { if (ScenesList.SelectedItem is not string s) return; SourcesList.Items.Clear(); var r = await obs.CallAsync("GetSceneItemList", new { sceneName = s }); foreach (var i in r.GetProperty("sceneItems").EnumerateArray()) SourcesList.Items.Add(i.GetProperty("sourceName").GetString()); }
    private async void DeleteSource_Click(object sender, RoutedEventArgs e) { if (!obs.Connected || ScenesList.SelectedItem is not string scene || SourcesList.SelectedItem is not string source) return; if (MessageBox.Show($"'{source}' kaynağını bu sahneden kaldırmak istiyor musun?", "Kaynak", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; try { var r = await obs.CallAsync("GetSceneItemList", new { sceneName = scene }); foreach (var i in r.GetProperty("sceneItems").EnumerateArray()) if (i.GetProperty("sourceName").GetString() == source) { await obs.CallAsync("RemoveSceneItem", new { sceneName = scene, sceneItemId = i.GetProperty("sceneItemId").GetInt64() }); break; } await Scene_RefreshOnly(); } catch (Exception ex) { ControlStatus.Text = ex.Message; } }

    private async void SendAi_Click(object sender, RoutedEventArgs e) => await SendAiAsync();
    private async void Prompt_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await SendAiAsync(); } }
    private async Task SendAiAsync()
    {
        string text = PromptBox.Text.Trim();
        if (text.Length == 0) return;
        ai.ApiKey = ApiKeyBox.Password;
        ai.Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? "gpt-6-astra" : ModelBox.Text.Trim();
        ai.AllowActions = AllowActionsBox.IsChecked == true;
        PromptBox.Clear();
        AppendChat("👤 Sen", text);
        AiStatus.Text = "AI düşünüyor ve gerekiyorsa OBS durumunu kontrol edip işlemleri uyguluyor...";
        try
        {
            string answer = await ai.AskAsync(text);
            AppendChat("🤖 TB AI", answer);
            AiStatus.Text = "Tamamlandı.";
            if (obs.Connected) await RefreshObsAsync();
        }
        catch (Exception ex) { AppendChat("⚠️ Hata", ex.Message); AiStatus.Text = "İşlem başarısız."; }
    }
    private void FocusAi_Click(object sender, RoutedEventArgs e) { if (this.Content is not null) { /* UI zaten AI sekmesine kullanıcı geçebilir */ } }
    private void AppendChat(string who, string text) { ChatText.Text += $"\n{who}\n{text}\n"; ChatScroll.ScrollToEnd(); }
}
