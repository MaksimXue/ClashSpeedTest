using System.IO;
using System.Text.RegularExpressions;

namespace ClashSpeedTest;

/// <summary>
/// 读取 Clash Verge Rev 的 profiles.yaml，识别"机场"（remote 类型订阅）
/// 和当前激活的 profile 名。这是"机场"概念的可靠来源（Merge 模式下 Selector 组 ≠ 机场）。
/// </summary>
public sealed class VergeProfiles
{
    public List<string> Airlines { get; } = new();
    public string CurrentName { get; private set; } = "";
    public string? FilePath { get; }

    private VergeProfiles(string? filePath) { FilePath = filePath; }

    /// <summary>自动定位并解析 profiles.yaml；失败时返回一个空对象（不抛异常）。</summary>
    public static VergeProfiles Load()
    {
        var apd = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = Path.Combine(apd,
            "io.github.clash-verge-rev.clash-verge-rev", "profiles.yaml");
        var result = new VergeProfiles(File.Exists(path) ? path : null);
        if (result.FilePath == null) return result;
        try
        {
            var text = File.ReadAllText(path);
            result.Parse(text);
        }
        catch
        {
            // 解析失败保持空；UI 层会提示读取失败
        }
        return result;
    }

    private void Parse(string text)
    {
        // 当前激活的 profile uid
        var cur = Regex.Match(text, @"(?m)^\s*current:\s*""?([^""\n]+)");
        string? currentUid = cur.Success ? cur.Groups[1].Value.Trim() : null;

        // 每个条目块
        var blocks = Regex.Matches(text, @"- uid:\s*([^\n]+)(.*?)(?=\n- uid:|\z)", RegexOptions.Singleline);
        foreach (Match block in blocks)
        {
            var uid = block.Groups[1].Value.Trim();
            var body = block.Groups[2].Value;
            var type = MatchValue(body, @"(?m)^\s*type:\s*""?([^""\n]+)");
            var name = MatchValue(body, @"(?m)^\s*name:\s*""?([^""\n]+)");
            if (!type.Equals("remote", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(name)) name = uid;
            Airlines.Add(name);
            if (uid == currentUid) CurrentName = name;
        }
        // current 没匹配到名字（可能指向本地配置），回退用 airlines 第一个
        if (string.IsNullOrEmpty(CurrentName) && Airlines.Count > 0)
            CurrentName = Airlines[0];
    }

    private static string MatchValue(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
}
