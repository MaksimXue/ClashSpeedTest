using System.Net;
using System.Net.Http;

namespace ClashSpeedTest;

/// <summary>
/// 节点测速核心：切换节点 → 测延迟 → 通过本地代理真实下载测速。
/// 下载前先"热身"1MB（Hysteria2/QUIC 连接提速），再正式计时。
/// </summary>
public sealed class NodeSpeedTestService
{
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly MihomoClient _mihomo;
    private readonly string _group;
    private readonly HttpClient _downloader;

    public NodeSpeedTestService(MihomoClient mihomo, string group, string proxyUrl)
    {
        _mihomo = mihomo;
        _group = group;
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxyUrl),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        _downloader = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,   // 超时由循环控制
        };
        _downloader.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeUa);
    }

    /// <summary>完整测试一个节点（切换 + 测延迟 + 下载测速）。</summary>
    public async Task<NodeResult> TestAsync(ProxyInfo node, TestOptions opts, CancellationToken ct)
    {
        var r = new NodeResult { Name = node.Name, Type = node.Type };
        try
        {
            await _mihomo.SelectProxyAsync(_group, node.Name, ct);
            await Task.Delay(400, ct);                    // 等切换生效

            var delay = await _mihomo.MeasureDelayAsync(node.Name, opts.DelayUrl, ct: ct);
            r.DelayMs = delay;
            r.DelayText = delay > 0 ? $"{delay} ms" : "error";

            if (opts.DelayOnly)
            {
                r.Ok = delay > 0;
                r.SpeedText = "-";
                r.StatusText = r.Ok ? "完成" : "error";
                r.ErrorOnly = !r.Ok;
                r.RetestVisibility = r.ErrorOnly ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                return r;
            }

            await RunDownloadPhaseAsync(r, opts, ct);
        }
        catch (OperationCanceledException)
        {
            r.StatusText = "已停止";
        }
        catch (Exception ex)
        {
            r.Ok = false;
            r.StatusText = $"失败 {ex.Message}";
        }
        return r;
    }

    /// <summary>仅下载测速（延迟已由阶段1提供，不再重复测）。</summary>
    public async Task<NodeResult> TestDownloadOnlyAsync(ProxyInfo node, int knownDelay,
        TestOptions opts, CancellationToken ct)
    {
        var r = new NodeResult { Name = node.Name, Type = node.Type };
        r.DelayMs = knownDelay;
        r.DelayText = knownDelay > 0 ? $"{knownDelay} ms" : "error";
        try
        {
            await _mihomo.SelectProxyAsync(_group, node.Name, ct);
            await Task.Delay(400, ct);
            await RunDownloadPhaseAsync(r, opts, ct);
        }
        catch (OperationCanceledException)
        {
            r.StatusText = "已停止";
        }
        catch (Exception ex)
        {
            r.Ok = false;
            r.StatusText = $"失败 {ex.Message}";
        }
        return r;
    }

    private async Task RunDownloadPhaseAsync(NodeResult r, TestOptions opts, CancellationToken ct)
    {
        var (mbs, mbps, err) = await DownloadSpeedAsync(opts.Url, opts.Seconds,
            (long)(opts.MaxMb * 1024 * 1024), ct);
        r.Mbs = mbs;
        if (mbs > 0)
        {
            r.Ok = true;
            r.SpeedText = $"{mbs:F2} MB/s  ({mbps:F0} Mbps)";
            r.StatusText = "完成";
        }
        else
        {
            r.Ok = false;
            r.SpeedText = "-";
            r.StatusText = $"失败 {err}";
        }
        r.ErrorOnly = false;
        r.RetestVisibility = System.Windows.Visibility.Collapsed;
    }

    private async Task<(double mbs, double mbps, string err)> DownloadSpeedAsync(
        string url, int maxSeconds, long maxBytes, CancellationToken ct)
    {
        try
        {
            using var resp = await _downloader.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[256 * 1024];

            // 热身：读到 1MB 或 15 秒，不计速
            long warm = 0;
            var t0 = DateTime.UtcNow;
            while (warm < 1024 * 1024 && (DateTime.UtcNow - t0).TotalSeconds < 15)
            {
                int n = await stream.ReadAsync(buf, ct);
                if (n == 0) break;
                warm += n;
            }

            // 正式计时
            long total = 0;
            var t1 = DateTime.UtcNow;
            while ((DateTime.UtcNow - t1).TotalSeconds < maxSeconds && total < maxBytes)
            {
                int n = await stream.ReadAsync(buf, ct);
                if (n == 0) break;
                total += n;
            }
            var elapsed = (DateTime.UtcNow - t1).TotalSeconds;
            if (total == 0) return (0, 0, "无数据");
            return (total / elapsed / 1e6, total * 8 / elapsed / 1e6, "");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (0, 0, ex.Message);
        }
    }
}
