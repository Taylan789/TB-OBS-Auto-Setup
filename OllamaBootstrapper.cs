using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TB_OBS_AutoSetup;

/// <summary>
/// Ollama'yı kullanıcıya teknik kurulum göstermeden hazırlar.
/// Kurulum kullanıcı hesabına yapılır; yönetici yetkisi gerektirmez.
/// </summary>
public sealed class OllamaBootstrapper
{
    private const string ApiUrl = "http://127.0.0.1:11434";
    private const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";
    private const string DefaultModel = "qwen3:4b";
    private static readonly TimeSpan ServerWaitTimeout = TimeSpan.FromSeconds(45);

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim setupLock = new(1, 1);

    public event Action<string>? StatusChanged;

    public async Task<(bool ok, string message, string model)> EnsureReadyAsync(CancellationToken ct = default)
    {
        await setupLock.WaitAsync(ct);
        try
        {
            Report("TB AI hazırlanıyor...");

            if (!IsServerReady())
            {
                string? ollamaExe = FindOllamaExe();

                if (ollamaExe == null)
                {
                    Report("TB AI bileşeni hazırlanıyor...");
                    ollamaExe = await InstallOllamaAsync(ct);
                }

                Report("TB AI başlatılıyor...");
                StartOllama(ollamaExe);

                if (!await WaitForServerAsync(ct))
                    return (false, "TB AI başlatılamadı. Windows'ta Ollama'nın çalışmasına izin verildiğinden emin ol.", "");
            }

            Report("TB AI modeli kontrol ediliyor...");
            if (!await HasModelAsync(DefaultModel, ct))
            {
                Report("TB AI ilk kullanım için hazırlanıyor...");
                if (!await PullModelAsync(DefaultModel, ct))
                    return (false, "TB AI modeli indirilemedi. İlk kurulum için internet bağlantısı gerekiyor.", "");
            }

            Report("TB AI hazır ✓");
            return (true, "TB AI hazır.", DefaultModel);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, "TB AI hazırlanamadı: " + ex.Message, "");
        }
        finally
        {
            setupLock.Release();
        }
    }

    private static bool IsServerReady()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = client.GetAsync(ApiUrl + "/api/tags").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string? FindOllamaExe()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(local, "Programs", "Ollama", "ollama.exe"),
            Path.Combine(local, "Programs", "Ollama", "ollama app.exe"),
            Path.Combine(local, "Ollama", "ollama.exe")
        };

        foreach (string path in candidates)
            if (File.Exists(path)) return path;

        try
        {
            var psi = new ProcessStartInfo("where.exe", "ollama.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            string output = p?.StandardOutput.ReadToEnd().Trim() ?? "";
            p?.WaitForExit(3000);
            if (!string.IsNullOrWhiteSpace(output))
            {
                foreach (string line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                    if (File.Exists(line.Trim())) return line.Trim();
            }
        }
        catch { }

        return null;
    }

    private async Task<string?> InstallOllamaAsync(CancellationToken ct)
    {
        string temp = Path.Combine(Path.GetTempPath(), "TB-OBS", "OllamaSetup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);

        Report("TB AI bileşeni indiriliyor...");
        using (var response = await http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(temp);
            await source.CopyToAsync(target, ct);
        }

        Report("TB AI bileşeni kuruluyor...");
        var psi = new ProcessStartInfo
        {
            FileName = temp,
            Arguments = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("TB AI kurulum işlemi başlatılamadı.");
        await process.WaitForExitAsync(ct);

        try { File.Delete(temp); } catch { }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"TB AI bileşeni kurulamadı (kod {process.ExitCode}).");

        for (int i = 0; i < 20; i++)
        {
            string? exe = FindOllamaExe();
            if (exe != null) return exe;
            await Task.Delay(500, ct);
        }

        return FindOllamaExe();
    }

    private static void StartOllama(string ollamaExe)
    {
        try
        {
            string directory = Path.GetDirectoryName(ollamaExe) ?? "";
            string appExe = Path.Combine(directory, "ollama app.exe");
            if (File.Exists(appExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = appExe,
                    Arguments = "--hide --fast-startup",
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                return;
            }
        }
        catch { }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ollamaExe,
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    private async Task<bool> WaitForServerAsync(CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + ServerWaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (IsServerReady()) return true;
            await Task.Delay(1000, ct);
        }
        return false;
    }

    private async Task<bool> HasModelAsync(string model, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(ApiUrl + "/api/tags", ct);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in models.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), model, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private async Task<bool> PullModelAsync(string model, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { name = model, stream = false }),
                System.Text.Encoding.UTF8,
                "application/json");
            using var response = await http.PostAsync(ApiUrl + "/api/pull", content, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private void Report(string message) => StatusChanged?.Invoke(message);
}
