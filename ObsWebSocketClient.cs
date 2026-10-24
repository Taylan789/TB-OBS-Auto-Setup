using System.IO;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TB_OBS_AutoSetup;

/// <summary>
/// OBS WebSocket 5.x client with one persistent receive loop.
/// Requests can run concurrently and are correlated by requestId; OBS events are
/// surfaced without forcing the UI/AI to poll OBS repeatedly.
/// </summary>
public sealed class ObsWebSocketClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private ClientWebSocket? ws;
    private CancellationTokenSource? receiveCts;
    private Task? receiveLoop;
    private int requestId;

    public bool Connected => ws?.State == WebSocketState.Open;
    public event Action<string>? EventReceived;
    public event Action? ConnectionLost;

    public async Task ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("OBS adresi boş olamaz.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Port 1 ile 65535 arasında olmalı.");

        await lifecycleLock.WaitAsync(ct);
        try
        {
            await DropConnectionAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);

            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            ws = socket;

            try
            {
                Uri address = new UriBuilder(Uri.UriSchemeWs, host.Trim(), port).Uri;
                await socket.ConnectAsync(address, timeout.Token);

                using var hello = await ReceiveJsonAsync(socket, timeout.Token);
                if (GetOpCode(hello.RootElement) != 0)
                    throw new InvalidOperationException("OBS WebSocket el sıkışması beklenen Hello mesajını vermedi.");

                string? authentication = null;
                if (hello.RootElement.TryGetProperty("d", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("authentication", out var auth) && auth.ValueKind == JsonValueKind.Object)
                {
                    string salt = auth.TryGetProperty("salt", out var s) ? s.GetString() ?? "" : "";
                    string challenge = auth.TryGetProperty("challenge", out var c) ? c.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(password))
                        throw new InvalidOperationException("OBS WebSocket şifre istiyor. OBS > Araçlar > WebSocket Sunucu Ayarları bölümündeki şifreyi gir.");
                    if (salt.Length == 0 || challenge.Length == 0)
                        throw new InvalidOperationException("OBS kimlik doğrulama yanıtı eksik.");
                    authentication = MakeAuth(password, salt, challenge);
                }

                await SendJsonAsync(socket, new { op = 1, d = new { rpcVersion = 1, authentication } }, timeout.Token);
                using var identified = await ReceiveJsonAsync(socket, timeout.Token);
                if (GetOpCode(identified.RootElement) != 2)
                {
                    string comment = "OBS WebSocket kimlik doğrulaması başarısız.";
                    if (identified.RootElement.TryGetProperty("d", out var idData) && idData.ValueKind == JsonValueKind.Object &&
                        idData.TryGetProperty("error", out var error)) comment = error.GetString() ?? comment;
                    throw new InvalidOperationException(comment);
                }

                receiveCts = new CancellationTokenSource();
                receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, receiveCts.Token));
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
        finally { lifecycleLock.Release(); }
    }

    public async Task<JsonElement> CallAsync(string requestType, object? requestData = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestType)) throw new ArgumentException("OBS istek adı boş olamaz.", nameof(requestType));
        ClientWebSocket socket = ws ?? throw new InvalidOperationException("OBS WebSocket bağlı değil. Önce OBS'ye bağlan.");
        if (socket.State != WebSocketState.Open) throw new InvalidOperationException("OBS WebSocket bağlı değil. Önce OBS'ye bağlan.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        string id = Interlocked.Increment(ref requestId).ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;

        try
        {
            await SendJsonAsync(socket, new { op = 6, d = new { requestType, requestId = id, requestData } }, timeout.Token);
            using var registration = timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token));
            return await tcs.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            pending.TryRemove(id, out _);
            throw new TimeoutException($"OBS '{requestType}' isteğine 15 saniye içinde cevap vermedi.");
        }
        catch (WebSocketException)
        {
            pending.TryRemove(id, out _);
            throw;
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using JsonDocument message = await ReceiveJsonAsync(socket, ct);
                JsonElement root = message.RootElement;
                int op = GetOpCode(root);
                if (op == 7)
                {
                    if (!root.TryGetProperty("d", out var data) || data.ValueKind != JsonValueKind.Object ||
                        !data.TryGetProperty("requestId", out var rid)) continue;
                    string id = rid.GetString() ?? "";
                    if (id.Length == 0 || !pending.TryRemove(id, out var waiter)) continue;
                    if (!data.TryGetProperty("requestStatus", out var status) || status.ValueKind != JsonValueKind.Object)
                    {
                        waiter.TrySetException(new InvalidDataException("OBS yanıtında requestStatus eksik."));
                        continue;
                    }
                    bool succeeded = status.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.True;
                    if (!succeeded)
                    {
                        string comment = status.TryGetProperty("comment", out var c) ? c.GetString() ?? "OBS isteği başarısız." : "OBS isteği başarısız.";
                        waiter.TrySetException(new InvalidOperationException(comment));
                    }
                    else waiter.TrySetResult(data.TryGetProperty("responseData", out var response) ? response.Clone() : JsonDocument.Parse("{}").RootElement.Clone());
                }
                else if (op == 5)
                {
                    try { EventReceived?.Invoke(root.GetRawText()); } catch { }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { }
        finally
        {
            if (ReferenceEquals(ws, socket))
            {
                ws = null;
                foreach (var pair in pending) pair.Value.TrySetException(new WebSocketException("OBS WebSocket bağlantısı kapandı."));
                pending.Clear();
                try { ConnectionLost?.Invoke(); } catch { }
            }
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

    private async Task SendJsonAsync(ClientWebSocket socket, object value, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        await sendLock.WaitAsync(ct);
        try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct); }
        finally { sendLock.Release(); }
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[16 * 1024];
        using var memory = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("OBS WebSocket bağlantıyı kapattı.");
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("OBS WebSocket metin dışı mesaj gönderdi.");
            if (result.Count > 0) memory.Write(buffer, 0, result.Count);
            if (memory.Length > MaxMessageBytes) throw new InvalidDataException("OBS WebSocket mesajı izin verilen boyutu aştı.");
            if (result.EndOfMessage) break;
        }
        return JsonDocument.Parse(memory.ToArray());
    }

    private async Task DropConnectionAsync()
    {
        receiveCts?.Cancel();
        ClientWebSocket? old = ws;
        ws = null;
        foreach (var pair in pending) pair.Value.TrySetException(new WebSocketException("OBS bağlantısı kapatıldı."));
        pending.Clear();
        if (old is not null)
        {
            try { old.Abort(); } catch { }
            old.Dispose();
        }
        if (receiveLoop is not null)
        {
            try { await receiveLoop; } catch { }
        }
        receiveLoop = null;
        receiveCts?.Dispose();
        receiveCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleLock.WaitAsync();
        try { await DropConnectionAsync(); }
        finally { lifecycleLock.Release(); lifecycleLock.Dispose(); sendLock.Dispose(); }
    }
}
