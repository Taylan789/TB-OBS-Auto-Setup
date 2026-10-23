using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TB_OBS_AutoSetup;

public sealed class ObsWebSocketClient : IAsyncDisposable
{
    private ClientWebSocket? ws;
    private int requestId;

    public bool Connected => ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        await DisposeAsync();
        ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://{host}:{port}"), ct);

        var hello = await ReceiveJsonAsync(ct);
        int op = hello.RootElement.GetProperty("op").GetInt32();

        if (op != 0)
            throw new InvalidOperationException("OBS WebSocket Hello alınamadı.");

        string? auth = null;

        if (hello.RootElement.TryGetProperty("d", out var d) &&
            d.TryGetProperty("authentication", out var a))
        {
            var salt = a.GetProperty("salt").GetString()!;
            var challenge = a.GetProperty("challenge").GetString()!;

            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException(
                    "OBS WebSocket şifre istiyor. OBS > Araçlar > WebSocket Sunucu Ayarları bölümündeki şifreyi gir.");

            auth = MakeAuth(password, salt, challenge);
        }

        var identify = new
        {
            op = 1,
            d = new
            {
                rpcVersion = 1,
                authentication = auth
            }
        };

        await SendJsonAsync(identify, ct);

        var identified = await ReceiveJsonAsync(ct);
        int resultOp = identified.RootElement.GetProperty("op").GetInt32();

        if (resultOp != 2)
        {
            string comment = "Kimlik doğrulama başarısız.";

            if (identified.RootElement.TryGetProperty("d", out var idd) &&
                idd.TryGetProperty("error", out var err))
                comment = err.GetString() ?? comment;

            throw new InvalidOperationException(comment);
        }
    }

    public async Task<JsonElement> CallAsync(
        string requestType,
        object? requestData = null,
        CancellationToken ct = default)
    {
        if (!Connected)
            throw new InvalidOperationException("OBS WebSocket bağlı değil.");

        string id = (++requestId).ToString();

        await SendJsonAsync(
            new
            {
                op = 6,
                d = new
                {
                    requestType,
                    requestId = id,
                    requestData
                }
            }, ct);

        while (true)
        {
            var response = await ReceiveJsonAsync(ct);
            int op = response.RootElement.GetProperty("op").GetInt32();

            if (op != 7)
                continue;

            var data = response.RootElement.GetProperty("d");

            if (data.GetProperty("requestId").GetString() != id)
                continue;

            var status = data.GetProperty("requestStatus");

            if (!status.GetProperty("result").GetBoolean())
            {
                string comment =
                    status.TryGetProperty("comment", out var c)
                        ? c.GetString() ?? "OBS isteği başarısız."
                        : "OBS isteği başarısız.";

                throw new InvalidOperationException(
                    $"{requestType}: {comment}");
            }

            return data.TryGetProperty("responseData", out var rd)
                ? rd
                : default;
        }
    }

    static string MakeAuth(string password, string salt, string challenge)
    {
        using var sha = SHA256.Create();

        byte[] secret =
            sha.ComputeHash(
                Encoding.UTF8.GetBytes(password + salt));

        string secretB64 =
            Convert.ToBase64String(secret);

        byte[] auth =
            sha.ComputeHash(
                Encoding.UTF8.GetBytes(secretB64 + challenge));

        return Convert.ToBase64String(auth);
    }

    async Task SendJsonAsync(object value, CancellationToken ct)
    {
        byte[] bytes =
            Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(value));

        await ws!.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            true,
            ct);
    }

    async Task<JsonDocument> ReceiveJsonAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];

        using var ms = new MemoryStream();

        while (true)
        {
            var r = await ws!.ReceiveAsync(buffer, ct);

            if (r.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(
                    "OBS WebSocket bağlantıyı kapattı.");

            ms.Write(buffer, 0, r.Count);

            if (r.EndOfMessage)
                break;
        }

        return JsonDocument.Parse(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "bye",
                        CancellationToken.None);
            }
            catch { }

            ws.Dispose();
            ws = null;
        }
    }
}
