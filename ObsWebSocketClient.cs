using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TB_OBS_AutoSetup;

/// <summary>
/// Minimal OBS WebSocket 5.x client. Calls are serialized because this client uses
/// one receive loop and correlates one outstanding request at a time.
/// </summary>
public sealed class ObsWebSocketClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private ClientWebSocket? ws;
    private int requestId;
    private readonly SemaphoreSlim callLock = new(1, 1);

    public bool Connected => ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("OBS adresi boş olamaz.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port 1 ile 65535 arasında olmalı.");

        await DropConnectionAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        try
        {
            string cleanHost = host.Trim();
            Uri address = new UriBuilder(Uri.UriSchemeWs, cleanHost, port).Uri;
            await ws.ConnectAsync(address, timeout.Token);

            using var hello = await ReceiveJsonAsync(timeout.Token);
            int op = GetOpCode(hello.RootElement);
            if (op != 0)
                throw new InvalidOperationException("OBS WebSocket el sıkışması beklenen Hello mesajını vermedi.");

            string? authentication = null;
            if (hello.RootElement.TryGetProperty("d", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("authentication", out var auth) &&
                auth.ValueKind == JsonValueKind.Object)
            {
                string salt = auth.TryGetProperty("salt", out var s) ? s.GetString() ?? "" : "";
                string challenge = auth.TryGetProperty("challenge", out var c) ? c.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(password))
                    throw new InvalidOperationException("OBS WebSocket şifre istiyor. OBS > Araçlar > WebSocket Sunucu Ayarları bölümündeki şifreyi gir.");
                if (salt.Length == 0 || challenge.Length == 0)
                    throw new InvalidOperationException("OBS kimlik doğrulama yanıtı eksik.");
                authentication = MakeAuth(password, salt, challenge);
            }

            await SendJsonAsync(new
            {
                op = 1,
                d = new { rpcVersion = 1, authentication }
            }, timeout.Token);

            using var identified = await ReceiveJsonAsync(timeout.Token);
            if (GetOpCode(identified.RootElement) != 2)
            {
                string comment = "OBS WebSocket kimlik doğrulaması başarısız.";
                if (identified.RootElement.TryGetProperty("d", out var idData) &&
                    idData.ValueKind == JsonValueKind.Object &&
                    idData.TryGetProperty("error", out var error))
                    comment = error.GetString() ?? comment;
                throw new InvalidOperationException(comment);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await DropConnectionAsync();
            throw new TimeoutException("OBS WebSocket bağlantısı 8 saniye içinde kurulamadı. OBS'de WebSocket'i, portu ve güvenlik duvarını kontrol et.");
        }
        catch
        {
            await DropConnectionAsync();
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(string requestType, object? requestData = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestType))
            throw new ArgumentException("OBS istek adı boş olamaz.", nameof(requestType));
        if (!Connected)
            throw new InvalidOperationException("OBS WebSocket bağlı değil. Önce OBS'ye bağlan.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        await callLock.WaitAsync(timeout.Token);
        try
        {
            if (!Connected)
                throw new InvalidOperationException("OBS bağlantısı istek gönderilmeden önce kapandı.");

            string id = Interlocked.Increment(ref requestId).ToString();
            await SendJsonAsync(new
            {
                op = 6,
                d = new { requestType, requestId = id, requestData }
            }, timeout.Token);

            while (true)
            {
                using var response = await ReceiveJsonAsync(timeout.Token);
                int op = GetOpCode(response.RootElement);
                // OBS may send events (op=5) between request and response. Ignore them.
                if (op != 7)
                    continue;

                if (!response.RootElement.TryGetProperty("d", out var data) || data.ValueKind != JsonValueKind.Object)
                    continue;
                if (!data.TryGetProperty("requestId", out var responseId) || responseId.GetString() != id)
                    continue;
                if (!data.TryGetProperty("requestStatus", out var status) || status.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"OBS {requestType} yanıtının requestStatus alanı eksik.");

                bool succeeded = status.TryGetProperty("result", out var result) &&
                                 result.ValueKind == JsonValueKind.True;
                if (!succeeded)
                {
                    string comment = status.TryGetProperty("comment", out var c)
                        ? c.GetString() ?? "OBS isteği başarısız."
                        : "OBS isteği başarısız.";
                    throw new InvalidOperationException($"{requestType}: {comment}");
                }

                return data.TryGetProperty("responseData", out var responseData)
                    ? responseData.Clone()
                    : JsonDocument.Parse("{}").RootElement.Clone();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await DropConnectionAsync();
            throw new TimeoutException($"OBS '{requestType}' isteğine 15 saniye içinde cevap vermedi. Bağlantı sıfırlandı; tekrar bağlanmayı dene.");
        }
        catch (WebSocketException)
        {
            await DropConnectionAsync();
            throw;
        }
        finally
        {
            callLock.Release();
        }
    }

    private static int GetOpCode(JsonElement message)
    {
        if (!message.TryGetProperty("op", out var element) || !element.TryGetInt32(out int op))
            throw new InvalidDataException("OBS WebSocket mesajında geçerli op alanı yok.");
        return op;
    }

    private static string MakeAuth(string password, string salt, string challenge)
    {
        using var sha = SHA256.Create();
        byte[] secret = sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt));
        string secretBase64 = Convert.ToBase64String(secret);
        byte[] auth = sha.ComputeHash(Encoding.UTF8.GetBytes(secretBase64 + challenge));
        return Convert.ToBase64String(auth);
    }

    private async Task SendJsonAsync(object value, CancellationToken ct)
    {
        ClientWebSocket socket = ws ?? throw new WebSocketException("OBS bağlantısı mevcut değil.");
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task<JsonDocument> ReceiveJsonAsync(CancellationToken ct)
    {
        ClientWebSocket socket = ws ?? throw new WebSocketException("OBS bağlantısı mevcut değil.");
        byte[] buffer = new byte[16 * 1024];
        using var memory = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("OBS WebSocket bağlantıyı kapattı.");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("OBS WebSocket beklenmeyen metin dışı mesaj gönderdi.");
            if (result.Count > 0)
                memory.Write(buffer, 0, result.Count);
            if (memory.Length > MaxMessageBytes)
                throw new InvalidDataException("OBS WebSocket mesajı izin verilen boyutu aştı.");
            if (result.EndOfMessage)
                break;
        }

        return JsonDocument.Parse(memory.ToArray());
    }

    private Task DropConnectionAsync()
    {
        ClientWebSocket? oldSocket = ws;
        ws = null;
        if (oldSocket is not null)
        {
            try { oldSocket.Abort(); } catch { }
            oldSocket.Dispose();
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await callLock.WaitAsync();
        try { await DropConnectionAsync(); }
        finally { callLock.Release(); }
    }
}
