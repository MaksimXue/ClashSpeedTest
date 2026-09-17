using System.IO;
using System.Text.RegularExpressions;

namespace ClashSpeedTest;

/// <summary>
/// 读取 Clash 客户端（Clash Party / Clash Verge Rev）的订阅配置，识别"机场"（remote 类型订阅）
/// 与当前激活的 profile。兼容两种文件与两种结构：
///   - Clash Party（Electron / mihomo-party）:
///       数据目录 &lt;userData&gt;/profile.yaml，结构 current + items[]{ id, type, name }
///   - Clash Verge Rev（Tauri）:
///       数据目录 &lt;appData&gt;/io.github.clash-verge-rev.clash-verge-rev/profiles.yaml，
///       结构 current + chain[]{ uid, type, name }
/// 两种文件里"机场"的可靠来源都是 type=remote 的订阅项。
/// </summary>
public sealed class VergeProfiles
{
    public List<string> Airlines { get; } = new();
    public string CurrentName { get; private set; } = "";
    public string? FilePath { get; }

    /// <summary>识别到的客户端名称（用于 UI 提示），如 "Clash Party" / "Clash Verge Rev"。</summary>
    public string ClientName { get; }

    private VergeProfiles(string? filePath, string clientName)
    {
        FilePath = filePath;
        ClientName = clientName;
    }

    /// <summary>自动定位并解析订阅配置；失败时返回一个空对象（不抛异常）。</summary>
    public static VergeProfiles Load()
    {
        var apd = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<(string Client, string Path)>();

        // Clash Party：Electron 的 userData 目录名依安装方式/版本而异，逐一探测
        foreach (var dir in new[] { "mihomo-party", "Mihomo Party", "clash-party" })
            candidates.Add(("Clash Party", Path.Combine(apd, dir, "profile.yaml")));

        // Clash Verge Rev：Tauri 固定的 appId 目录
        candidates.Add(("Clash Verge Rev",
            Path.Combine(apd, "io.github.clash-verge-rev.clash-verge-rev", "profiles.yaml")));

        foreach (var (client, path) in candidates)
        {
            if (!File.Exists(path)) continue;
            var result = new VergeProfiles(path, client);
            try
            {
                result.Parse(File.ReadAllText(path));
            }
            catch
            {
                // 解析失败保持空；UI 层会提示读取失败
            }
            return result;
        }

        return new VergeProfiles(null, "");
    }

    private void Parse(string text)
    {
        // 当前激活的 profile id（两个客户端字段名都是 current，但项键名不同：id / uid）
        var cur = Regex.Match(text, @"(?m)^\s*current:\s*""?([^""\n]+)");
        string? currentId = cur.Success ? cur.Groups[1].Value.Trim() : null;

        // 每个订阅条目块：Clash Party 为 "- id:"，Verge 为 "- uid:"
        var blocks = Regex.Matches(text,
            @"(?m)^\s*-\s*(?:id|uid)\s*:\s*([^\n]+)(.*?)(?=^\s*-\s*(?:id|uid)\s*:|\z)",
            RegexOptions.Singleline);

        foreach (Match block in blocks)
        {
            var id = block.Groups[1].Value.Trim();
            var body = block.Groups[2].Value;
            var type = MatchValue(body, @"(?m)^\s*type:\s*""?([^""\n]+)");
            var name = MatchValue(body, @"(?m)^\s*name:\s*""?([^""\n]+)");
            if (!type.Equals("remote", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(name)) name = id;
            Airlines.Add(name);
            if (id == currentId) CurrentName = name;
        }

        // current 没匹配到名字（可能指向本地/空配置），回退用第一个机场
        if (string.IsNullOrEmpty(CurrentName) && Airlines.Count > 0)
            CurrentName = Airlines[0];
    }

    private static string MatchValue(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
}
