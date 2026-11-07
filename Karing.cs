using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tumbler;

internal sealed record KaringStats(int Connections, string? Outbound);

/// <summary>Необязательная интеграция с Karing: подсказки по портам и счётчик того, что реально идёт через прокси.</summary>
internal static class Karing
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "karing", "karing");

    static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1.5) };

    static DateTime _coreStamp;
    static string? _controller, _secret;

    public static List<PortPreset> Presets()
    {
        var presets = new List<PortPreset>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "karing_setting.json")));
            var proxy = doc.RootElement.GetProperty("proxy");
            if (proxy.TryGetProperty("mixed_forword_port", out var all))
                presets.Add(new(all.GetInt32(), $"{all.GetInt32()} · всё через VPN", "Порт Karing, который отправляет в VPN весь трафик приложения"));
            if (proxy.TryGetProperty("mixed_port", out var rule))
                presets.Add(new(rule.GetInt32(), $"{rule.GetInt32()} · по правилам", "Порт Karing с правилами: например, российские сайты идут напрямую"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Karing не установлен или формат настроек другой — просто без подсказок.
        }
        return presets;
    }

    /// <summary>Порт Karing «всё напрямую»: SOCKS5 на нём отвечает, но в VPN он ничего не отправляет.</summary>
    public static int? DirectPort()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "karing_setting.json")));
            return doc.RootElement.GetProperty("proxy").TryGetProperty("mixed_direct_port", out var port) ? port.GetInt32() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Сколько соединений Karing сейчас получает от ProxiFyre и через какой сервер их отправляет.</summary>
    public static KaringStats? Stats()
    {
        try
        {
            if (!ReadController()) return null;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{_controller}/connections");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
            using var response = Http.Send(request);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(response.Content.ReadAsStream());
            if (!doc.RootElement.TryGetProperty("connections", out var list) || list.ValueKind != JsonValueKind.Array)
                return new KaringStats(0, null);

            int count = 0;
            var outbounds = new Dictionary<string, int>();
            foreach (var connection in list.EnumerateArray())
            {
                var process = connection.GetProperty("metadata").TryGetProperty("processPath", out var p) ? p.GetString() : null;
                if (process is null || !process.EndsWith("ProxiFyre.exe", StringComparison.OrdinalIgnoreCase)) continue;

                count++;
                if (connection.TryGetProperty("chains", out var chains) && chains.GetArrayLength() > 0 && chains[0].GetString() is { } name)
                    outbounds[name] = outbounds.GetValueOrDefault(name) + 1;
            }

            var top = outbounds.Count == 0 ? null : outbounds.MaxBy(kv => kv.Value).Key;
            return new KaringStats(count, top);
        }
        catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException or TaskCanceledException
                                       or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Адрес и ключ API лежат в service_core.json; ключ меняется при каждом перезапуске ядра.
    static bool ReadController()
    {
        var file = new FileInfo(Path.Combine(Dir, "service_core.json"));
        if (!file.Exists) return false;
        if (file.LastWriteTimeUtc == _coreStamp) return _controller != null;

        _coreStamp = file.LastWriteTimeUtc;
        _controller = _secret = null;

        using var doc = JsonDocument.Parse(File.ReadAllText(file.FullName));
        var api = doc.RootElement.GetProperty("experimental").GetProperty("clash_api");
        _controller = api.GetProperty("external_controller").GetString();
        _secret = api.TryGetProperty("secret", out var secret) ? secret.GetString() : "";
        return _controller != null;
    }
}
