using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClashSpeedTest;

/// <summary>缓存中的单个节点结果。</summary>
public sealed class CachedNodeResult
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int DelayMs { get; set; }
    public string DelayText { get; set; } = "—";
    public string SpeedText { get; set; } = "—";
    public double Mbs { get; set; }
    public bool Ok { get; set; }
    public bool ErrorOnly { get; set; }
    public string StatusText { get; set; } = "等待";
    public bool IsSelected { get; set; } = true;
}

/// <summary>一次完整测速的快照，按机场分别保存。</summary>
public sealed class CachedTestRun
{
    public string AirportName { get; set; } = "";
    public string ClientKind { get; set; } = "";
    public string Group { get; set; } = "";
    /// <summary>是否为「只测延迟」模式（决定重载时的状态文案与成功口径）。</summary>
    public bool DelayOnly { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public List<CachedNodeResult> Nodes { get; set; } = new();
}

/// <summary>
/// 测速结果本地缓存。存放在 %APPDATA%\ClashSpeedTest\results.json，
/// 按「客户端 + 机场名」分组，切换机场后原结果保留，切回时优先加载最近一次。
/// 写失败静默忽略，不影响测速主流程。
/// </summary>
public static class TestResultStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static Dictionary<string, CachedTestRun>? _cache;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClashSpeedTest", "results.json");

    private static Dictionary<string, CachedTestRun> LoadAll()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, CachedTestRun>>(json, Options);
                if (parsed != null)
                {
                    // 反序列化后的字典丢失比较器，重建为 Ordinal
                    _cache = new Dictionary<string, CachedTestRun>(parsed, StringComparer.Ordinal);
                    return _cache;
                }
            }
        }
        catch
        {
            // 缓存文件损坏：当作空缓存，下次保存会覆盖
        }
        _cache = new Dictionary<string, CachedTestRun>(StringComparer.Ordinal);
        return _cache;
    }

    public static CachedTestRun? Load(string key) =>
        string.IsNullOrEmpty(key) ? null :
        LoadAll().TryGetValue(key, out var run) ? run : null;

    public static void Save(string key, CachedTestRun run)
    {
        if (string.IsNullOrEmpty(key)) return;
        LoadAll()[key] = run;
        WriteAll();
    }

    public static void Remove(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (LoadAll().Remove(key)) WriteAll();
    }

    /// <summary>机场缓存键：客户端 + 机场名，避免不同客户端的同名订阅互相串。</summary>
    public static string MakeKey(string clientKind, string airportName) =>
        $"{clientKind}|{airportName}";

    private static void WriteAll()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cache, Options);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);   // 原子替换，避免写一半损坏
        }
        catch
        {
            // 磁盘不可写时静默失败
        }
    }
}
