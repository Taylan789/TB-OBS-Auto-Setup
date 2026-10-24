using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TB_OBS_AutoSetup;

/// <summary>
/// TB AI motoru. Kullanıcıya API anahtarı göstermez; yerel Ollama servisi üzerinden çalışır.
/// OBS ile konuşma yine yalnızca izin verilen tool'lar üzerinden yapılır.
/// </summary>
public sealed class AiObsService
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly ObsWebSocketClient obs;
    private readonly HardwareInfoService hardware = new();
    private readonly OllamaBootstrapper ollama = new();
    private readonly List<JsonElement> conversation = new();
    private readonly SemaphoreSlim aiLock = new(1, 1);
    private static readonly TimeSpan ReadCacheTtl = TimeSpan.FromSeconds(1.5);
    private readonly Dictionary<string, (DateTimeOffset expires, string json)> readCache = new();

    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; private set; } = "";
    public bool AllowActions { get; set; } = true;

    public AiObsService(ObsWebSocketClient obsClient)
    {
        obs = obsClient;
        obs.EventReceived += OnObsEvent;
        ollama.StatusChanged += message => { };
        obs.ConnectionLost += () => readCache.Clear();
    }

    private void OnObsEvent(string rawEvent)
    {
        // OBS state changed; stale read-cache entries are no longer trustworthy.
        readCache.Clear();
    }

    public async Task<(bool ok, string message, string model)> CheckAsync(CancellationToken ct = default)
    {
        var ready = await ollama.EnsureReadyAsync(ct);
        if (ready.ok) Model = ready.model;
        return ready;
    }

    public async Task<string> AskAsync(string userText, CancellationToken ct = default)
    {
        await aiLock.WaitAsync(ct);
        try
        {
        if (string.IsNullOrWhiteSpace(Model))
        {
            var check = await CheckAsync(ct);
            if (!check.ok)
                throw new InvalidOperationException(check.message);
        }

        conversation.Add(JsonSerializer.SerializeToElement(new { role = "user", content = userText }));

        for (int round = 0; round < 8; round++)
        {
            var payload = new
            {
                model = Model,
                messages = BuildMessages(),
                stream = false,
                tools = Tools,
                options = new { temperature = 0.15 }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/') + "/api/chat");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct);
            string json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Yerel AI hatası ({(int)response.StatusCode}): {json}");

            using var doc = JsonDocument.Parse(json);
            var message = doc.RootElement.GetProperty("message");
            bool hadTool = false;

            conversation.Add(message.Clone());

            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    hadTool = true;
                    var fn = call.GetProperty("function");
                    string name = fn.GetProperty("name").GetString() ?? "";
                    string args = fn.TryGetProperty("arguments", out var a) ? a.GetRawText() : "{}";
                    string result = await ExecuteToolAsync(name, args, ct);
                    conversation.Add(JsonSerializer.SerializeToElement(new
                    {
                        role = "tool",
                        content = result
                    }));
                }
            }

            if (!hadTool)
            {
                string text = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                return text.Trim();
            }
        }

        throw new InvalidOperationException("AI çok fazla işlem zinciri oluşturdu. Komutu biraz daha kısa yazmayı dene.");
        }
        finally
        {
            aiLock.Release();
        }
    }


    private object[] BuildMessages()
    {
        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt }
        };
        int start = Math.Max(0, conversation.Count - 16);
        for (int i = start; i < conversation.Count; i++)
            messages.Add(JsonSerializer.Deserialize<object>(conversation[i].GetRawText())!);
        return messages.ToArray();
    }

    private async Task<string> ExecuteToolAsync(string name, string argsJson, CancellationToken ct)
    {
        if (!AllowActions && IsMutating(name))
            return JsonSerializer.Serialize(new { ok = false, message = "Otomatik işlem modu kapalı. İşlemi kullanıcı kendisi uygulamalı." });

        try
        {
            if (IsMutating(name)) readCache.Clear();
            using JsonDocument argsDoc = JsonDocument.Parse(argsJson);
            JsonElement a = argsDoc.RootElement;
            JsonElement r;
            switch (name)
            {
                case "get_pc_info":
                    return JsonSerializer.Serialize(new { ok = true, summary = hardware.Summary() });
                case "get_obs_state":
                    r = await obs.CallAsync("GetVersion", null, ct);
                    var scenes = await obs.CallAsync("GetSceneList", null, ct);
                    var stream = await obs.CallAsync("GetStreamStatus", null, ct);
                    var record = await obs.CallAsync("GetRecordStatus", null, ct);
                    var stats = await obs.CallAsync("GetStats", null, ct);
                    return JsonSerializer.Serialize(new { ok = true, version = r, scenes, stream, record, stats });
                case "get_stats":
                    r = await obs.CallAsync("GetStats", null, ct); return r.GetRawText();
                case "list_scenes":
                    return await CachedObsCallAsync("scenes", "GetSceneList", null, ct);
                case "create_scene":
                    r = await obs.CallAsync("CreateScene", new { sceneName = Required(a, "sceneName") }, ct); return r.GetRawText();
                case "duplicate_scene":
                    r = await obs.CallAsync("DuplicateScene", new { sceneName = Required(a, "sceneName"), destinationSceneName = Required(a, "destinationSceneName") }, ct); return r.GetRawText();
                case "remove_scene":
                    await obs.CallAsync("RemoveScene", new { sceneName = Required(a, "sceneName") }, ct); return Ok("Sahne kaldırıldı.");
                case "set_scene":
                    await obs.CallAsync("SetCurrentProgramScene", new { sceneName = Required(a, "sceneName") }, ct); return Ok("Aktif sahne değiştirildi.");
                case "list_scene_items":
                    return await CachedObsCallAsync("items:" + Required(a, "sceneName"), "GetSceneItemList", new { sceneName = Required(a, "sceneName") }, ct);
                case "create_source":
                    r = await obs.CallAsync("CreateInput", new
                    {
                        sceneName = Required(a, "sceneName"),
                        inputName = Required(a, "inputName"),
                        inputKind = Required(a, "inputKind"),
                        inputSettings = a.TryGetProperty("inputSettings", out var settings) ? settings : JsonDocument.Parse("{}").RootElement,
                        sceneItemEnabled = true
                    }, ct); return r.GetRawText();
                case "remove_source":
                    await RemoveSceneSourceAsync(Required(a, "sceneName"), Required(a, "inputName"), ct); return Ok("Kaynak yalnızca bu sahneden kaldırıldı.");
                case "set_source_enabled":
                    await obs.CallAsync("SetSceneItemEnabled", new { sceneName = Required(a, "sceneName"), sceneItemId = a.GetProperty("sceneItemId").GetInt64(), sceneItemEnabled = a.GetProperty("enabled").GetBoolean() }, ct); return Ok("Kaynak görünürlüğü değiştirildi.");
                case "set_source_locked":
                    await obs.CallAsync("SetSceneItemLocked", new { sceneName = Required(a, "sceneName"), sceneItemId = a.GetProperty("sceneItemId").GetInt64(), sceneItemLocked = a.GetProperty("locked").GetBoolean() }, ct); return Ok("Kaynak kilit durumu değiştirildi.");
                case "set_source_index":
                    await obs.CallAsync("SetSceneItemIndex", new { sceneName = Required(a, "sceneName"), sceneItemId = a.GetProperty("sceneItemId").GetInt64(), sceneItemIndex = a.GetProperty("index").GetInt32() }, ct); return Ok("Kaynak sırası değiştirildi.");
                case "set_source_transform":
                    r = await obs.CallAsync("GetSceneItemList", new { sceneName = Required(a, "sceneName") }, ct);
                    long id = FindSceneItemId(r, Required(a, "inputName"));
                    var transform = new Dictionary<string, object>();
                    foreach (string p in new[] { "positionX", "positionY", "scaleX", "scaleY", "rotation", "cropLeft", "cropRight", "cropTop", "cropBottom", "boundsWidth", "boundsHeight" })
                        if (a.TryGetProperty(p, out var v)) transform[p] = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : v.GetRawText();
                    await obs.CallAsync("SetSceneItemTransform", new { sceneName = a.GetProperty("sceneName").GetString(), sceneItemId = id, sceneItemTransform = transform }, ct);
                    return Ok("Kaynak konumu, ölçeği veya kırpması değiştirildi.");
                case "get_source_settings":
                    r = await obs.CallAsync("GetInputSettings", new { inputName = Required(a, "inputName") }, ct); return r.GetRawText();
                case "set_source_settings":
                    r = await obs.CallAsync("SetInputSettings", new { inputName = Required(a, "inputName"), inputSettings = a.GetProperty("inputSettings"), overlay = a.TryGetProperty("overlay", out var ov) && ov.GetBoolean() }, ct); return r.GetRawText();
                case "list_inputs":
                    return await CachedObsCallAsync("inputs", "GetInputList", null, ct);
                case "list_input_kinds":
                    return await CachedObsCallAsync("inputKinds", "GetInputKindList", null, ct);
                case "get_input_defaults":
                    r = await obs.CallAsync("GetInputDefaultSettings", new { inputKind = Required(a, "inputKind") }, ct); return r.GetRawText();
                case "list_filters":
                    return await CachedObsCallAsync("filters:" + Required(a, "sourceName"), "GetSourceFilterList", new { sourceName = Required(a, "sourceName") }, ct);
                case "create_filter":
                    r = await obs.CallAsync("CreateSourceFilter", new { sourceName = Required(a, "sourceName"), filterName = Required(a, "filterName"), filterKind = Required(a, "filterKind"), filterSettings = a.TryGetProperty("filterSettings", out var fs) ? fs : JsonDocument.Parse("{}").RootElement }, ct); return r.GetRawText();
                case "remove_filter":
                    await obs.CallAsync("RemoveSourceFilter", new { sourceName = Required(a, "sourceName"), filterName = Required(a, "filterName") }, ct); return Ok("Filtre kaldırıldı.");
                case "set_filter_enabled":
                    await obs.CallAsync("SetSourceFilterEnabled", new { sourceName = Required(a, "sourceName"), filterName = Required(a, "filterName"), filterEnabled = a.GetProperty("enabled").GetBoolean() }, ct); return Ok("Filtre durumu değiştirildi.");
                case "set_filter_settings":
                    r = await obs.CallAsync("SetSourceFilterSettings", new { sourceName = Required(a, "sourceName"), filterName = Required(a, "filterName"), filterSettings = a.GetProperty("filterSettings"), overlay = a.TryGetProperty("overlay", out var fo) && fo.GetBoolean() }, ct); return r.GetRawText();
                case "list_transitions":
                    r = await obs.CallAsync("GetSceneTransitionList", null, ct); return r.GetRawText();
                case "get_transition":
                    r = await obs.CallAsync("GetCurrentSceneTransition", null, ct); return r.GetRawText();
                case "set_transition":
                    r = await obs.CallAsync("SetCurrentSceneTransition", new { transitionName = Required(a, "transitionName") }, ct); return r.GetRawText();
                case "set_transition_settings":
                    r = await obs.CallAsync("SetSceneTransitionSettings", new { transitionSettings = a.GetProperty("transitionSettings"), overlay = a.TryGetProperty("overlay", out var to) && to.GetBoolean() }, ct); return r.GetRawText();
                case "list_profiles":
                    r = await obs.CallAsync("GetProfileList", null, ct); return r.GetRawText();
                case "set_profile":
                    await obs.CallAsync("SetCurrentProfile", new { profileName = Required(a, "profileName") }, ct); return Ok("OBS profili değiştirildi.");
                case "list_scene_collections":
                    r = await obs.CallAsync("GetSceneCollectionList", null, ct); return r.GetRawText();
                case "set_scene_collection":
                    await obs.CallAsync("SetCurrentSceneCollection", new { sceneCollectionName = Required(a, "sceneCollectionName") }, ct); return Ok("Sahne koleksiyonu değiştirildi.");
                case "trigger_hotkey":
                    await obs.CallAsync("TriggerHotkeyByName", new { hotkeyName = Required(a, "hotkeyName") }, ct); return Ok("OBS kısayolu tetiklendi.");
                case "start_virtual_camera":
                    await obs.CallAsync("StartVirtualCam", null, ct); return Ok("Sanal kamera başlatıldı.");
                case "stop_virtual_camera":
                    await obs.CallAsync("StopVirtualCam", null, ct); return Ok("Sanal kamera durduruldu.");
                case "start_stream": await obs.CallAsync("StartStream", null, ct); return Ok("Yayın başlatıldı.");
                case "stop_stream": await obs.CallAsync("StopStream", null, ct); return Ok("Yayın durduruldu.");
                case "start_record": await obs.CallAsync("StartRecord", null, ct); return Ok("Kayıt başlatıldı.");
                case "stop_record": await obs.CallAsync("StopRecord", null, ct); return Ok("Kayıt durduruldu.");
                case "set_video_settings":
                    var vd = new Dictionary<string, object>();
                    foreach (string p in new[] { "baseWidth", "baseHeight", "outputWidth", "outputHeight", "fpsNumerator", "fpsDenominator" })
                        if (a.TryGetProperty(p, out var v)) vd[p] = v.GetInt32();
                    await obs.CallAsync("SetVideoSettings", vd, ct); return Ok("Video ayarları uygulandı.");
                default: return JsonSerializer.Serialize(new { ok = false, message = $"İzin verilen tool değil: {name}" });
            }
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
    }

    private async Task<string> CachedObsCallAsync(string key, string requestType, object? requestData, CancellationToken ct)
    {
        if (readCache.TryGetValue(key, out var hit) && hit.expires > DateTimeOffset.UtcNow)
            return hit.json;

        var result = await obs.CallAsync(requestType, requestData, ct);
        string json = result.GetRawText();
        readCache[key] = (DateTimeOffset.UtcNow.Add(ReadCacheTtl), json);
        return json;
    }

    private async Task RemoveSceneSourceAsync(string sceneName, string inputName, CancellationToken ct)
    {
        var r = await obs.CallAsync("GetSceneItemList", new { sceneName }, ct);
        long id = FindSceneItemId(r, inputName);
        await obs.CallAsync("RemoveSceneItem", new { sceneName, sceneItemId = id }, ct);
    }

    private static bool IsMutating(string n) => n is
        "create_scene" or "duplicate_scene" or "remove_scene" or "set_scene" or
        "create_source" or "remove_source" or "set_source_enabled" or "set_source_locked" or "set_source_index" or
        "set_source_transform" or "set_source_settings" or "create_filter" or "remove_filter" or
        "set_filter_enabled" or "set_filter_settings" or "set_transition" or "set_transition_settings" or
        "set_profile" or "set_scene_collection" or "trigger_hotkey" or "start_virtual_camera" or "stop_virtual_camera" or
        "start_stream" or "stop_stream" or "start_record" or "stop_record" or "set_video_settings";
    private static string Required(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : throw new InvalidOperationException($"{name} gerekli.");
    private static long FindSceneItemId(JsonElement r, string inputName)
    {
        foreach (var item in r.GetProperty("sceneItems").EnumerateArray()) if (item.GetProperty("sourceName").GetString() == inputName) return item.GetProperty("sceneItemId").GetInt64();
        throw new InvalidOperationException($"'{inputName}' adlı kaynak sahnede bulunamadı.");
    }
    private static string Ok(string message) => JsonSerializer.Serialize(new { ok = true, message });

    private const string SystemPrompt = "Sen TB OBS AI'sın. Türkçe konuş. OBS'deki sahne, kaynak, filtre, geçiş, profil, sahne koleksiyonu, kayıt, yayın, sanal kamera, video ve kısayol işlemlerini gerçekten yönetebilirsin. Kullanıcı OBS'yi bilmeyebilir; teknik terimleri gerektiğinde açıkla. Kullanıcı bir işlem istediğinde önce gerekli durumu oku; sahne veya kaynak adlarını asla uydurma; önce listele. Bir kaynak türünü bilmiyorsan list_input_kinds ve get_input_defaults kullan. Bir sahne kaynağını silerken yalnızca scene item'ı kaldır, global input'ı silme. Filtre eklerken önce mevcut filtreleri oku. İşlem bittikten sonra mümkünse durumu tekrar okuyarak doğrula. Stream key ve gizli bilgileri isteme veya yazma. Yayın/kayıt başlatma-durdurma gibi kritik işlemleri yalnızca kullanıcı açıkça istediğinde yap. Kullanıcının amacı PS5 ile sınırlı değildir; OBS'de istediği herhangi bir sahne ve kaynak düzenlemesini yönet. İşlem yapılamıyorsa nedenini ve sonraki adımı açık Türkçe anlat.";

    private static readonly object[] Tools =
    {
        Fn("get_pc_info", "Bilgisayarın CPU, RAM, işletim sistemi, ağ ve OBS kurulum durumunu oku.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("get_obs_state", "OBS sürümünü, sahneleri, yayın/kayıt durumunu ve istatistikleri oku.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("get_stats", "OBS CPU, FPS, frame/drop ve genel performans istatistiklerini oku.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("list_scenes", "OBS'deki bütün sahneleri listele.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("create_scene", "Yeni OBS sahnesi oluştur.", new { type = "object", properties = new { sceneName = new { type = "string" } }, required = new[] { "sceneName" }, additionalProperties = false }),
        Fn("duplicate_scene", "Bir sahneyi başka isimle kopyala.", new { type = "object", properties = new { sceneName = new { type = "string" }, destinationSceneName = new { type = "string" } }, required = new[] { "sceneName", "destinationSceneName" }, additionalProperties = false }),
        Fn("remove_scene", "Bir OBS sahnesini kaldır.", new { type = "object", properties = new { sceneName = new { type = "string" } }, required = new[] { "sceneName" }, additionalProperties = false }),
        Fn("set_scene", "Aktif program sahnesini değiştir.", new { type = "object", properties = new { sceneName = new { type = "string" } }, required = new[] { "sceneName" }, additionalProperties = false }),
        Fn("list_scene_items", "Bir sahnedeki bütün kaynakları ve scene item bilgilerini listele.", new { type = "object", properties = new { sceneName = new { type = "string" } }, required = new[] { "sceneName" }, additionalProperties = false }),
        Fn("create_source", "Bir sahneye OBS input/source ekle. Gerçek inputKind ve inputSettings kullan.", new { type = "object", properties = new { sceneName = new { type = "string" }, inputName = new { type = "string" }, inputKind = new { type = "string" }, inputSettings = new { type = "object" } }, required = new[] { "sceneName", "inputName", "inputKind", "inputSettings" }, additionalProperties = false }),
        Fn("remove_source", "Bir kaynağı yalnızca belirtilen sahneden kaldır.", new { type = "object", properties = new { sceneName = new { type = "string" }, inputName = new { type = "string" } }, required = new[] { "sceneName", "inputName" }, additionalProperties = false }),
        Fn("set_source_enabled", "Bir sahne kaynağını göster/gizle.", new { type = "object", properties = new { sceneName = new { type = "string" }, sceneItemId = new { type = "integer" }, enabled = new { type = "boolean" } }, required = new[] { "sceneName", "sceneItemId", "enabled" }, additionalProperties = false }),
        Fn("set_source_locked", "Bir sahne kaynağını kilitle/kilidini aç.", new { type = "object", properties = new { sceneName = new { type = "string" }, sceneItemId = new { type = "integer" }, locked = new { type = "boolean" } }, required = new[] { "sceneName", "sceneItemId", "locked" }, additionalProperties = false }),
        Fn("set_source_index", "Sahnedeki kaynak sırasını değiştir.", new { type = "object", properties = new { sceneName = new { type = "string" }, sceneItemId = new { type = "integer" }, index = new { type = "integer" } }, required = new[] { "sceneName", "sceneItemId", "index" }, additionalProperties = false }),
        Fn("set_source_transform", "Kaynağın konumunu, ölçeğini, dönüşünü veya kırpmasını değiştir.", new { type = "object", properties = new { sceneName = new { type = "string" }, inputName = new { type = "string" }, positionX = new { type = "number" }, positionY = new { type = "number" }, scaleX = new { type = "number" }, scaleY = new { type = "number" }, rotation = new { type = "number" }, cropLeft = new { type = "number" }, cropRight = new { type = "number" }, cropTop = new { type = "number" }, cropBottom = new { type = "number" }, boundsWidth = new { type = "number" }, boundsHeight = new { type = "number" } }, required = new[] { "sceneName", "inputName" }, additionalProperties = false }),
        Fn("get_source_settings", "Bir OBS input/source'un mevcut ayarlarını oku.", new { type = "object", properties = new { inputName = new { type = "string" } }, required = new[] { "inputName" }, additionalProperties = false }),
        Fn("set_source_settings", "Bir OBS input/source ayarlarını değiştir.", new { type = "object", properties = new { inputName = new { type = "string" }, inputSettings = new { type = "object" }, overlay = new { type = "boolean" } }, required = new[] { "inputName", "inputSettings" }, additionalProperties = false }),
        Fn("list_inputs", "OBS içindeki bütün input/source nesnelerini ve türlerini listele.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("list_input_kinds", "OBS'nin desteklediği gerçek input/source türlerini listele.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("get_input_defaults", "Belirli bir OBS input türünün varsayılan ayarlarını oku.", new { type = "object", properties = new { inputKind = new { type = "string" } }, required = new[] { "inputKind" }, additionalProperties = false }),
        Fn("list_filters", "Bir kaynağın mevcut filtrelerini listele.", new { type = "object", properties = new { sourceName = new { type = "string" } }, required = new[] { "sourceName" }, additionalProperties = false }),
        Fn("create_filter", "Bir kaynağa OBS filtresi ekle.", new { type = "object", properties = new { sourceName = new { type = "string" }, filterName = new { type = "string" }, filterKind = new { type = "string" }, filterSettings = new { type = "object" } }, required = new[] { "sourceName", "filterName", "filterKind", "filterSettings" }, additionalProperties = false }),
        Fn("remove_filter", "Bir kaynaktaki filtreyi kaldır.", new { type = "object", properties = new { sourceName = new { type = "string" }, filterName = new { type = "string" } }, required = new[] { "sourceName", "filterName" }, additionalProperties = false }),
        Fn("set_filter_enabled", "Bir kaynak filtresini aç/kapat.", new { type = "object", properties = new { sourceName = new { type = "string" }, filterName = new { type = "string" }, enabled = new { type = "boolean" } }, required = new[] { "sourceName", "filterName", "enabled" }, additionalProperties = false }),
        Fn("set_filter_settings", "Bir kaynak filtresinin ayarlarını değiştir.", new { type = "object", properties = new { sourceName = new { type = "string" }, filterName = new { type = "string" }, filterSettings = new { type = "object" }, overlay = new { type = "boolean" } }, required = new[] { "sourceName", "filterName", "filterSettings" }, additionalProperties = false }),
        Fn("list_transitions", "OBS geçişlerini listele.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("get_transition", "Aktif OBS sahne geçişini oku.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("set_transition", "Aktif OBS sahne geçişini değiştir.", new { type = "object", properties = new { transitionName = new { type = "string" } }, required = new[] { "transitionName" }, additionalProperties = false }),
        Fn("set_transition_settings", "Aktif sahne geçişinin ayarlarını değiştir.", new { type = "object", properties = new { transitionSettings = new { type = "object" }, overlay = new { type = "boolean" } }, required = new[] { "transitionSettings" }, additionalProperties = false }),
        Fn("list_profiles", "OBS profillerini listele.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("set_profile", "OBS aktif profilini değiştir.", new { type = "object", properties = new { profileName = new { type = "string" } }, required = new[] { "profileName" }, additionalProperties = false }),
        Fn("list_scene_collections", "OBS sahne koleksiyonlarını listele.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("set_scene_collection", "OBS aktif sahne koleksiyonunu değiştir.", new { type = "object", properties = new { sceneCollectionName = new { type = "string" } }, required = new[] { "sceneCollectionName" }, additionalProperties = false }),
        Fn("trigger_hotkey", "OBS'de kayıtlı bir hotkey'i adıyla tetikle.", new { type = "object", properties = new { hotkeyName = new { type = "string" } }, required = new[] { "hotkeyName" }, additionalProperties = false }),
        Fn("start_virtual_camera", "OBS sanal kamerayı başlat.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("stop_virtual_camera", "OBS sanal kamerayı durdur.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("start_stream", "OBS yayınını başlat.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("stop_stream", "OBS yayınını durdur.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("start_record", "OBS kaydını başlat.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("stop_record", "OBS kaydını durdur.", new { type = "object", properties = new { }, additionalProperties = false }),
        Fn("set_video_settings", "OBS çözünürlük ve FPS ayarlarını değiştir.", new { type = "object", properties = new { baseWidth = new { type = "integer" }, baseHeight = new { type = "integer" }, outputWidth = new { type = "integer" }, outputHeight = new { type = "integer" }, fpsNumerator = new { type = "integer" }, fpsDenominator = new { type = "integer" } }, additionalProperties = false })
    };

    public void ClearHistory()
    {
        conversation.Clear();
        readCache.Clear();
    }

    public void Dispose() => aiLock.Dispose();

    private static object Fn(string name, string description, object parameters) => new
    {
        type = "function",
        function = new { name, description, parameters }
    };
}
