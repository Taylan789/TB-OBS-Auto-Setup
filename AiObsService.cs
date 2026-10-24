using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TB_OBS_AutoSetup;

public sealed class AiObsService
{
    private readonly HttpClient http = new();
    private readonly ObsWebSocketClient obs;
    private readonly List<JsonElement> conversation = new();

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-6-astra";
    public bool AllowActions { get; set; } = true;

    public AiObsService(ObsWebSocketClient obsClient) => obs = obsClient;

    public async Task<string> AskAsync(string userText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Önce TB AI için OpenAI API anahtarını gir.");

        conversation.Add(JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content = userText
        }));

        for (int round = 0; round < 8; round++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = Model,
                instructions = SystemPrompt,
                input = conversation,
                tools = Tools,
                tool_choice = "auto"
            }), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI API hatası ({(int)response.StatusCode}): {json}");

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            JsonElement output = root.GetProperty("output");
            bool hadTool = false;
            string finalText = "";

            foreach (JsonElement item in output.EnumerateArray())
            {
                string type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type == "function_call")
                {
                    hadTool = true;
                    conversation.Add(item.Clone());
                    string name = item.GetProperty("name").GetString() ?? "";
                    string callId = item.GetProperty("call_id").GetString() ?? "";
                    string args = item.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                    string result = await ExecuteToolAsync(name, args, ct);
                    conversation.Add(JsonSerializer.SerializeToElement(new
                    {
                        type = "function_call_output",
                        call_id = callId,
                        output = result
                    }));
                }
                else if (type == "message")
                {
                    if (item.TryGetProperty("content", out var content))
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var text))
                                finalText += text.GetString();
                        }
                    }
                }
            }

            if (!hadTool)
            {
                if (string.IsNullOrWhiteSpace(finalText)) finalText = root.TryGetProperty("output_text", out var ot) ? ot.GetString() ?? "" : "";
                return finalText.Trim();
            }
        }

        throw new InvalidOperationException("AI çok fazla işlem zinciri oluşturdu. İsteği daha küçük bir komut halinde dene.");
    }

    private async Task<string> ExecuteToolAsync(string name, string argsJson, CancellationToken ct)
    {
        if (!AllowActions && IsMutating(name))
            return JsonSerializer.Serialize(new { ok = false, message = "Otomatik işlem modu kapalı. Kullanıcıdan onay alınmalı." });
        try
        {
            using JsonDocument argsDoc = JsonDocument.Parse(argsJson);
            JsonElement a = argsDoc.RootElement;
            JsonElement r;
            switch (name)
            {
                case "get_obs_state":
                    r = await obs.CallAsync("GetVersion", null, ct);
                    var scenes = await obs.CallAsync("GetSceneList", null, ct);
                    var stream = await obs.CallAsync("GetStreamStatus", null, ct);
                    var record = await obs.CallAsync("GetRecordStatus", null, ct);
                    return JsonSerializer.Serialize(new { version = r, scenes, stream, record });
                case "list_scenes":
                    r = await obs.CallAsync("GetSceneList", null, ct); return r.GetRawText();
                case "list_sources":
                    r = await obs.CallAsync("GetSceneItemList", new { sceneName = Required(a, "sceneName") }, ct); return r.GetRawText();
                case "create_scene":
                    r = await obs.CallAsync("CreateScene", new { sceneName = Required(a, "sceneName") }, ct); return r.GetRawText();
                case "set_scene":
                    await obs.CallAsync("SetCurrentProgramScene", new { sceneName = Required(a, "sceneName") }, ct); return Ok("Aktif sahne değiştirildi.");
                case "remove_source":
                    await obs.CallAsync("RemoveInput", new { inputName = Required(a, "inputName") }, ct); return Ok("Kaynak kaldırıldı.");
                case "set_source_enabled":
                    await obs.CallAsync("SetSceneItemEnabled", new { sceneName = Required(a, "sceneName"), sceneItemId = a.GetProperty("sceneItemId").GetInt64(), sceneItemEnabled = a.GetProperty("enabled").GetBoolean() }, ct); return Ok("Kaynak görünürlüğü değiştirildi.");
                case "create_source":
                    r = await obs.CallAsync("CreateInput", new
                    {
                        sceneName = Required(a, "sceneName"),
                        inputName = Required(a, "inputName"),
                        inputKind = Required(a, "inputKind"),
                        inputSettings = a.TryGetProperty("inputSettings", out var settings) ? settings : JsonDocument.Parse("{}").RootElement,
                        sceneItemEnabled = true
                    }, ct); return r.GetRawText();
                case "set_source_transform":
                    r = await obs.CallAsync("GetSceneItemList", new { sceneName = Required(a, "sceneName") }, ct);
                    long id = FindSceneItemId(r, Required(a, "inputName"));
                    var transform = new Dictionary<string, object>();
                    foreach (string p in new[] { "positionX", "positionY", "scaleX", "scaleY", "rotation" }) if (a.TryGetProperty(p, out var v)) transform[p] = v.GetDouble();
                    await obs.CallAsync("SetSceneItemTransform", new { sceneName = Required(a, "sceneName"), sceneItemId = id, sceneItemTransform = transform }, ct);
                    return Ok("Kaynak konumu/ölçeği değiştirildi.");
                case "start_stream": await obs.CallAsync("StartStream", null, ct); return Ok("Yayın başlatıldı.");
                case "stop_stream": await obs.CallAsync("StopStream", null, ct); return Ok("Yayın durduruldu.");
                case "start_record": await obs.CallAsync("StartRecord", null, ct); return Ok("Kayıt başlatıldı.");
                case "stop_record": await obs.CallAsync("StopRecord", null, ct); return Ok("Kayıt durduruldu.");
                case "set_video_settings":
                    var vd = new Dictionary<string, object>();
                    if (a.TryGetProperty("baseWidth", out var bw)) vd["baseWidth"] = bw.GetInt32();
                    if (a.TryGetProperty("baseHeight", out var bh)) vd["baseHeight"] = bh.GetInt32();
                    if (a.TryGetProperty("outputWidth", out var ow)) vd["outputWidth"] = ow.GetInt32();
                    if (a.TryGetProperty("outputHeight", out var oh)) vd["outputHeight"] = oh.GetInt32();
                    if (a.TryGetProperty("fpsNumerator", out var fn)) vd["fpsNumerator"] = fn.GetInt32();
                    if (a.TryGetProperty("fpsDenominator", out var fd)) vd["fpsDenominator"] = fd.GetInt32();
                    await obs.CallAsync("SetVideoSettings", vd, ct); return Ok("Video ayarları uygulandı.");
                default: return JsonSerializer.Serialize(new { ok = false, message = $"İzin verilen tool değil: {name}" });
            }
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
    }

    private static bool IsMutating(string n) => n is "create_scene" or "set_scene" or "remove_source" or "set_source_enabled" or "create_source" or "set_source_transform" or "start_stream" or "stop_stream" or "start_record" or "stop_record" or "set_video_settings";
    private static string Required(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : throw new InvalidOperationException($"{name} gerekli.");
    private static long FindSceneItemId(JsonElement r, string inputName)
    {
        foreach (var item in r.GetProperty("sceneItems").EnumerateArray()) if (item.GetProperty("sourceName").GetString() == inputName) return item.GetProperty("sceneItemId").GetInt64();
        throw new InvalidOperationException($"'{inputName}' adlı kaynak sahnede bulunamadı.");
    }
    private static string Ok(string message) => JsonSerializer.Serialize(new { ok = true, message });

    private const string SystemPrompt = "Sen TB OBS AI'sın. Türkçe konuş. OBS'yi hiç bilmeyen kullanıcıya anlaşılır davran. Kullanıcı bir işlem istiyorsa uygun OBS tool'unu çağır ve gerçekten uygula. İşlemden önce gerekiyorsa OBS durumunu oku. Bilmediğin isimleri uydurma. Bir işlem başarısız olursa hatayı açıkla ve alternatif öner. Stream key gibi gizli bilgileri isteme veya yazma. Yayın başlatma/durdurma gibi kritik işlemlerde kullanıcı açıkça komut verdiyse uygula. Kaynak eklerken OBS'nin gerçek inputKind değerlerini kullan; emin değilsen kullanıcıdan cihaz türünü sor. İşlem bittikten sonra kısa ve net sonuç ver.";

    private static readonly object[] Tools =
    {
        Tool("get_obs_state", "OBS sürümünü, sahneleri, yayın ve kayıt durumunu oku.", "{}", Array.Empty<string>()),
        Tool("list_scenes", "OBS sahnelerini listele.", "{}", Array.Empty<string>()),
        Tool("list_sources", "Bir sahnedeki kaynakları listele.", "{\"type\":\"object\",\"properties\":{\"sceneName\":{\"type\":\"string\"}},\"required\":[\"sceneName\"],\"additionalProperties\":false}", new[]{"sceneName"}),
        Tool("create_scene", "Yeni OBS sahnesi oluştur.", "{\"type\":\"object\",\"properties\":{\"sceneName\":{\"type\":\"string\"}},\"required\":[\"sceneName\"],\"additionalProperties\":false}", new[]{"sceneName"}),
        Tool("set_scene", "Aktif program sahnesini değiştir.", "{\"type\":\"object\",\"properties\":{\"sceneName\":{\"type\":\"string\"}},\"required\":[\"sceneName\"],\"additionalProperties\":false}", new[]{"sceneName"}),
        Tool("create_source", "Bir sahneye OBS input/source ekle.", "{\"type\":\"object\",\"properties\":{\"sceneName\":{\"type\":\"string\"},\"inputName\":{\"type\":\"string\"},\"inputKind\":{\"type\":\"string\"},\"inputSettings\":{\"type\":\"object\"}},\"required\":[\"sceneName\",\"inputName\",\"inputKind\",\"inputSettings\"],\"additionalProperties\":false}", new[]{"sceneName","inputName","inputKind","inputSettings"}),
        Tool("remove_source", "Bir OBS input kaynağını kaldır.", "{\"type\":\"object\",\"properties\":{\"inputName\":{\"type\":\"string\"}},\"required\":[\"inputName\"],\"additionalProperties\":false}", new[]{"inputName"}),
        Tool("set_source_enabled", "Bir sahne kaynağını göster/gizle.", "{\"type\":\"object\",\"properties\":{\"sceneName\":{\"type\":\"string\"},\"sceneItemId\":{\"type\":\"integer\"},\"enabled\":{\"type\":\"boolean\"}},\"required\":[\"sceneName\",\"sceneItemId\",\"enabled\"],\"additionalProperties\":false}", new[]{"sceneName","sceneItemId","enabled"}),
        Tool("set_source_transform", "Kaynağın konumunu, ölçeğini veya dönüşünü değiştir.", "{\"type\":\"object\",\"properties\":{\"sceneName\":{\"type\":\"string\"},\"inputName\":{\"type\":\"string\"},\"positionX\":{\"type\":\"number\"},\"positionY\":{\"type\":\"number\"},\"scaleX\":{\"type\":\"number\"},\"scaleY\":{\"type\":\"number\"},\"rotation\":{\"type\":\"number\"}},\"required\":[\"sceneName\",\"inputName\"],\"additionalProperties\":false}", new[]{"sceneName","inputName"}),
        Tool("start_stream", "OBS yayınını başlat.", "{}", Array.Empty<string>()), Tool("stop_stream", "OBS yayınını durdur.", "{}", Array.Empty<string>()),
        Tool("start_record", "OBS kaydını başlat.", "{}", Array.Empty<string>()), Tool("stop_record", "OBS kaydını durdur.", "{}", Array.Empty<string>()),
        Tool("set_video_settings", "OBS çözünürlük ve FPS ayarlarını değiştir.", "{\"type\":\"object\",\"properties\":{\"baseWidth\":{\"type\":\"integer\"},\"baseHeight\":{\"type\":\"integer\"},\"outputWidth\":{\"type\":\"integer\"},\"outputHeight\":{\"type\":\"integer\"},\"fpsNumerator\":{\"type\":\"integer\"},\"fpsDenominator\":{\"type\":\"integer\"}},\"additionalProperties\":false}", Array.Empty<string>())
    };

    private static object Tool(string name, string description, string parametersJson, string[] required)
    {
        using var doc = JsonDocument.Parse(parametersJson);
        return new { type = "function", name, description, parameters = doc.RootElement.Clone(), strict = false };
    }
}
