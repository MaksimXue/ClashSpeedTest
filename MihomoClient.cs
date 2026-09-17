using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClashSpeedTest;

/// <summary>
/// Clash / Mihomo 控制接口客户端。
/// 支持通过命名管道连接 Clash Party（pipe://MihomoParty/mihomo*）
/// 或 Clash Verge Rev（pipe://verge-mihomo*）的内核，也可回退到 TCP。
/// 管道名带动态后缀（如 MihomoParty\mihomo-user-Console-47944），故需先枚举发现。
/// </summary>
public sealed class MihomoClient : IDisposable
{
    private readonly HttpClient _http;
    public string Endpoint { get; }

    /// <summary>
    /// 枚举 \\.\pipe\ 目录，发现 Clash 内核的命名管道。
    /// 优先 Clash Party（MihomoParty\mihomo*），其次 Clash Verge Rev（verge-mihomo*）。
    /// 找不到返回 null。
    /// </summary>
    public static string? DiscoverPipeEndpoint()
    {
        try
        {
            var pipes = Directory.GetFiles(@"\\.\pipe\");
            // Clash Party：\\.\pipe\MihomoParty\mihomo-user-Console-47944
            var party = pipes.FirstOrDefault(p =>
                p.Contains(@"MihomoParty\", StringComparison.OrdinalIgnoreCase));
            if (party != null) return "pipe://" + party.Replace(@"\\.\pipe\", "");
            // Clash Verge Rev：\\.\pipe\verge-mihomo（可能带 -xxx 后缀）
            var verge = pipes.FirstOrDefault(p =>
                p.EndsWith(@"\verge-mihomo", StringComparison.OrdinalIgnoreCase) ||
                p.Contains(@"\verge-mihomo-", StringComparison.OrdinalIgnoreCase));
            if (verge != null) return "pipe://" + verge.Replace(@"\\.\pipe\", "");
        }
        catch
        {
            // 枚举失败则回退固定名
        }
        return null;
    }

    public MihomoClient(string endpoint = "")
    {
        Endpoint = NormalizeEndpoint(endpoint);
        HttpMessageHandler handler;
        Uri baseAddress;

        if (Endpoint.StartsWith("pipe://", StringComparison.OrdinalIgnoreCase))
        {
            var pipeName = NormalizePipeName(Endpoint["pipe://".Length..]);
            handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ConnectCallback = async (_, ct) =>
                {
                    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(ct);
                        return pipe;
                    }
                    catch
                    {
                        pipe.Dispose();
                        throw;
                    }
                }
            };
            baseAddress = new Uri("http://localhost/");
        }
        else
        {
            handler = new HttpClientHandler { UseProxy = false };
            baseAddress = new Uri(Endpoint);
        }

        _http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>拉取节点列表 + 配置(mixed-port)，并挑出主选择组。</summary>
    public async Task<ClashState> GetStateAsync(CancellationToken ct = default)
    {
        using var proxiesDoc = await GetJsonAsync("proxies", ct);
        using var configDoc = await GetJsonAsync("configs", ct);

        var proxies = new Dictionary<string, ProxyInfo>(StringComparer.Ordinal);
        if (proxiesDoc.RootElement.TryGetProperty("proxies", out var pe))
        {
            foreach (var p in pe.EnumerateObject())
            {
                var all = new List<string>();
                if (p.Value.TryGetProperty("all", out var allEl) &&
                    allEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in allEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                            all.Add(s);
                    }
                }
                proxies[p.Name] = new ProxyInfo
                {
                    Name = p.Name,
                    Type = ReadString(p.Value, "type"),
                    Now = ReadString(p.Value, "now"),
                    All = all
                };
            }
        }

        var cfg = configDoc.RootElement;
        var mixed = ReadInt(cfg, "mixed-port");
        var httpPort = ReadInt(cfg, "port");
        var socks = ReadInt(cfg, "socks-port");
        string proxyUrl = "";
        int effective = 0;
        if (mixed > 0) { effective = mixed; proxyUrl = $"http://127.0.0.1:{mixed}"; }
        else if (httpPort > 0) { effective = httpPort; proxyUrl = $"http://127.0.0.1:{httpPort}"; }
        else if (socks > 0) { effective = socks; proxyUrl = $"socks5://127.0.0.1:{socks}"; }

        var group = PickGroup(proxies);
        return new ClashState
        {
            Proxies = proxies,
            MixedPort = effective,
            ProxyUrl = proxyUrl,
            Group = group,
            Mode = ReadString(cfg, "mode")
        };
    }

    /// <summary>测单个节点延迟，返回毫秒；失败返回 0。</summary>
    public async Task<int> MeasureDelayAsync(string node, string testUrl, int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        try
        {
            var path = $"proxies/{Uri.EscapeDataString(node)}/delay?url={Uri.EscapeDataString(testUrl)}" +
                       $"&timeout={Math.Clamp(timeoutMs, 500, 15000)}" +
                       $"&fresh={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            using var doc = await GetJsonAsync(path, ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Number &&
                doc.RootElement.TryGetInt32(out var direct))
                return direct;
            var delay = ReadInt(doc.RootElement, "delay");
            if (delay <= 0) delay = ReadInt(doc.RootElement, "meanDelay");
            return delay > 0 && delay < timeoutMs ? delay : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>切换选择组到指定节点。</summary>
    public async Task SelectProxyAsync(string group, string node, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync($"proxies/{Uri.EscapeDataString(group)}",
            new { name = node }, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>机场（用户自定义 Selector 组）识别：排除内置元组。</summary>
    public static bool IsMetaGroup(string name)
    {
        if (string.Equals(name, "GLOBAL", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "DIRECT", StringComparison.OrdinalIgnoreCase)) return true;
        string[] keywords = { "自动选择", "故障转移", "负载均衡", "负载平衡", "测速",
                              "url-test", "fallback", "loadbalance", "auto" };
        foreach (var k in keywords)
            if (name.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>列出用户自定义机场组（Selector 且非元组）。</summary>
    public List<string> GetAirlineGroups(Dictionary<string, ProxyInfo> proxies)
    {
        return proxies.Values
            .Where(p => p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Where(n => !IsMetaGroup(n))
            .ToList();
    }

    /// <summary>批量测一组节点的延迟（内核并发），返回 节点名→延迟ms（失败节点缺失或为0）。</summary>
    public async Task<Dictionary<string, int>> GetGroupDelaysAsync(
        string group, string testUrl, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            var path = $"group/{Uri.EscapeDataString(group)}/delay?url={Uri.EscapeDataString(testUrl)}" +
                       $"&timeout={Math.Clamp(timeoutMs, 500, 15000)}" +
                       $"&fresh={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            using var doc = await GetJsonAsync(path, ct);
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var d = ReadDelay(p.Value);
                if (d > 0 && d < timeoutMs) result[p.Name] = d;
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    /// <summary>并发测多个单节点延迟（批量接口失败时的回退方案）。</summary>
    public async Task<Dictionary<string, int>> MeasureDelaysConcurrentAsync(
        IEnumerable<string> nodes, string testUrl, int timeoutMs, CancellationToken ct = default)
    {
        var results = new Dictionary<string, int>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(10);
        var sync = new object();
        var tasks = nodes.Select(async name =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var d = await MeasureDelayAsync(name, testUrl, timeoutMs, ct);
                lock (sync) results[name] = d;
            }
            catch
            {
                lock (sync) results[name] = 0;
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    private static string PickGroup(Dictionary<string, ProxyInfo> proxies)
    {
        string[] hints = { "proxy", "节点选择", "选择代理", "🔰", "🚀", "代理" };
        foreach (var hint in hints)
        {
            var hit = proxies.Values.FirstOrDefault(p =>
                p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase) &&
                p.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Name;
        }
        var first = proxies.Values.FirstOrDefault(p =>
            p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase) && p.Name != "GLOBAL");
        return first?.Name ?? "GLOBAL";
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var resp = await _http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var v = endpoint.Trim();
        if (string.IsNullOrEmpty(v)) v = "pipe://verge-mihomo";
        if (v.StartsWith("pipe://", StringComparison.OrdinalIgnoreCase))
            return "pipe://" + NormalizePipeName(v["pipe://".Length..]);
        if (!v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            v = "http://" + v;
        return v.TrimEnd('/') + "/";
    }

    /// <summary>
    /// 把命名管道规范化为纯管道名（去掉 \\.\pipe\ 前缀与首尾分隔符，
    /// "/" 转 "\"），供 NamedPipeClientStream(".", name) 使用。
    /// 兼容 "MihomoParty/mihomo"、"MihomoParty\mihomo-user-Console-47944"、
    /// "\\.\pipe\MihomoParty\mihomo-xxx" 等写法。
    /// </summary>
    private static string NormalizePipeName(string name)
    {
        var n = name.Replace('/', '\\').Trim('\\');
        // 去掉可选的 "\\.\pipe\" 前缀（可能因 Trim 后开头变成 ".\\pipe\\"）
        if (n.StartsWith(@"pipe\", StringComparison.OrdinalIgnoreCase))
            n = n[5..];
        else if (n.StartsWith(@".\pipe\", StringComparison.OrdinalIgnoreCase))
            n = n[7..];
        else if (n.StartsWith(@"\pipe\", StringComparison.OrdinalIgnoreCase))
            n = n[6..];
        return n.Trim('\\');
    }

    private static string ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static int ReadInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : 0;

    private static int ReadDelay(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v)) return v;
        if (e.ValueKind == JsonValueKind.Object)
        {
            var d = ReadInt(e, "delay");
            if (d <= 0) d = ReadInt(e, "meanDelay");
            return d;
        }
        return 0;
    }

    public void Dispose() => _http.Dispose();
}
