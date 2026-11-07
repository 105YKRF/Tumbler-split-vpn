using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tumbler;

/// <summary>
/// app-config.json службы ProxiFyre. «Тумблер» ведёт в нём по одному правилу на порт: список приложений → SOCKS5-порт.
/// Остальные поля файла (логин, TLS, logLevel и т.д.) сохраняются как были.
/// </summary>
internal sealed class PfConfig
{
    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    JsonObject _root;

    PfConfig(string filePath, JsonObject root)
    {
        FilePath = filePath;
        _root = root;
    }

    public string FilePath { get; }
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Порт первого правила в файле; им же заполняется правило-заглушка, когда приложений нет.</summary>
    public int DefaultPort { get; set; } = 3066;

    public bool Udp { get; set; } = true;

    /// <summary>Приложение (как записано в appNames) → порт.</summary>
    public Dictionary<string, int> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Excludes { get; } = new();

    public static PfConfig Load(string filePath)
    {
        JsonObject root = new();
        if (File.Exists(filePath))
        {
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            root = JsonNode.Parse(File.ReadAllText(filePath), documentOptions: options) as JsonObject ?? new();
        }

        var config = new PfConfig(filePath, root);
        var rules = (root["proxies"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();

        bool first = true;
        foreach (var rule in rules)
        {
            if (!TryParseEndpoint(rule["socks5ProxyEndpoint"], out var host, out int port)) continue;
            if (first)
            {
                first = false;
                config.Host = host;
                config.DefaultPort = port;

                // Пустой или отсутствующий список протоколов ProxiFyre трактует как «оба».
                var protocols = Strings(rule["supportedProtocols"]);
                config.Udp = protocols.Count == 0 || protocols.Contains("UDP", StringComparer.OrdinalIgnoreCase);
            }

            // У ProxiFyre побеждает первое подходящее правило — повторяем это при чтении.
            foreach (var name in Strings(rule["appNames"]))
                config.Rules.TryAdd(name, port);
        }

        config.Excludes.AddRange(Strings(root["excludes"]));
        return config;
    }

    public void SetRules(IReadOnlyDictionary<string, int> rules)
    {
        Rules.Clear();
        foreach (var (app, port) in rules) Rules[app] = port;
    }

    /// <returns>false, если файл уже содержит ровно это — службу можно не перезапускать.</returns>
    public bool Save()
    {
        var template = (_root["proxies"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? new JsonObject
        {
            ["username"] = "",
            ["password"] = "",
            ["socks5Transport"] = "TCP",
            ["supportedAddressFamilies"] = new JsonArray("IPv4"),
        };

        // ProxiFyre требует хотя бы одно правило; правило без приложений ни на что не срабатывает.
        var byPort = Rules.GroupBy(r => r.Value, r => r.Key).OrderBy(g => g.Key != DefaultPort).ThenBy(g => g.Key).ToList();
        var proxies = new JsonArray();
        if (byPort.Count == 0) proxies.Add(BuildRule(template, DefaultPort, []));
        foreach (var group in byPort) proxies.Add(BuildRule(template, group.Key, group));

        _root["proxies"] = proxies;
        _root["excludes"] = ToArray(Excludes);
        if (_root["bypassLan"] is null) _root["bypassLan"] = true;
        if (_root["logLevel"] is null) _root["logLevel"] = "Error";

        var json = _root.ToJsonString(WriteOptions);
        bool exists = File.Exists(FilePath);
        if (exists && File.ReadAllText(FilePath) == json) return false;

        var backup = FilePath + ".bak";
        if (exists && !File.Exists(backup)) File.Copy(FilePath, backup);

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
        return true;
    }

    JsonObject BuildRule(JsonObject template, int port, IEnumerable<string> apps)
    {
        var rule = (JsonObject)template.DeepClone();
        rule["appNames"] = ToArray(apps);
        rule["socks5ProxyEndpoint"] = $"{Host}:{port}";
        rule["supportedProtocols"] = Udp ? new JsonArray("TCP", "UDP") : new JsonArray("TCP");
        return rule;
    }

    static bool TryParseEndpoint(JsonNode? node, out string host, out int port)
    {
        host = "";
        port = 0;
        if (node is not JsonValue value || !value.TryGetValue(out string? endpoint) || endpoint is null) return false;

        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], out port)) return false;
        host = endpoint[..colon];
        return true;
    }

    // Пустая строка в appNames у ProxiFyre означает «все приложения подряд» — её никогда не читаем и не пишем.
    static List<string> Strings(JsonNode? node) =>
        (node as JsonArray)?
            .Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s?.Trim() : null)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList() ?? new();

    static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => (JsonNode)JsonValue.Create(v.Trim())!).ToArray());
}
