using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClashSpeedTest;

/// <summary>
/// FlClash（github.com/chen08209/FlClash）对接支持。
///
/// 数据目录：%APPDATA%\com.follow\clash
///   - shared_preferences.json : {"flutter.config": "&lt;json&gt;"}
///       内层含 currentProfileId 与 patchClashConfig{ external-controller, secret }
///   - database.sqlite         : profiles 表（id / type / label），type=url 即远程订阅
///   - profiles\{id}.yaml      : 各订阅的实际配置
///
/// 与另外两个客户端的差异：
///   - FlClash 默认 **关闭** 外部控制器（external-controller 为空），此时内核只通过
///     gRPC 命名管道 \\.\pipe\FlClashCore_&lt;hash&gt; 与界面通信，第三方无法调用 REST API。
///   - 在 FlClash「设置 → 外部控制器」开启后，固定监听 127.0.0.1:9090，
///     并自动生成 32 位随机密钥（secret），可通过本类自动读取。
/// </summary>
public sealed class FlClashSupport
{
    public string DataDir { get; }
    public bool Installed => Directory.Exists(DataDir);

    /// <summary>是否已在 FlClash 中开启「外部控制器」。</summary>
    public bool ControllerEnabled { get; private set; }

    /// <summary>外部控制器地址，如 http://127.0.0.1:9090（未开启时为空）。</summary>
    public string ControllerUrl { get; private set; } = "";

    /// <summary>外部控制器密钥（未设置时为空）。</summary>
    public string Secret { get; private set; } = "";

    /// <summary>订阅（机场）名称列表。</summary>
    public List<string> Airlines { get; } = new();

    /// <summary>当前激活的订阅名称。</summary>
    public string CurrentName { get; private set; } = "";

    public string? DatabasePath { get; private set; }

    private string _currentProfileId = "";

    private FlClashSupport(string dataDir) => DataDir = dataDir;

    /// <summary>定位并读取 FlClash 数据；未安装时返回一个 Installed=false 的实例（不抛异常）。</summary>
    public static FlClashSupport Load()
    {
        var apd = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var result = new FlClashSupport(Path.Combine(apd, "com.follow", "clash"));
        if (!result.Installed) return result;

        result.ReadPreferences();
        result.ReadProfiles();
        return result;
    }

    /// <summary>仅探测是否安装了 FlClash（不看是否运行）。</summary>
    public static bool IsInstalled()
    {
        var apd = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Directory.Exists(Path.Combine(apd, "com.follow", "clash"));
    }

    /// <summary>解析 shared_preferences.json，取出当前订阅 id 与外部控制器配置。</summary>
    private void ReadPreferences()
    {
        try
        {
            var path = Path.Combine(DataDir, "shared_preferences.json");
            if (!File.Exists(path)) return;

            using var outer = JsonDocument.Parse(File.ReadAllText(path));
            if (!outer.RootElement.TryGetProperty("flutter.config", out var cfgEl)) return;
            var cfgText = cfgEl.ValueKind == JsonValueKind.String ? cfgEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(cfgText)) return;

            using var cfg = JsonDocument.Parse(cfgText);

            // 当前订阅 id（数字或字符串）
            if (cfg.RootElement.TryGetProperty("currentProfileId", out var cid))
            {
                _currentProfileId = cid.ValueKind switch
                {
                    JsonValueKind.Number => cid.TryGetInt64(out var n) ? n.ToString() : "",
                    JsonValueKind.String => cid.GetString() ?? "",
                    _ => ""
                };
            }

            if (!cfg.RootElement.TryGetProperty("patchClashConfig", out var patch) ||
                patch.ValueKind != JsonValueKind.Object)
                return;

            // FlClash 把开关序列化成地址字符串：关闭时 ""，开启时 "127.0.0.1:9090"
            var addr = ReadString(patch, "external-controller").Trim();
            Secret = ReadString(patch, "secret").Trim();
            ControllerEnabled = !string.IsNullOrEmpty(addr);
            if (ControllerEnabled)
            {
                ControllerUrl = addr.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? addr.TrimEnd('/')
                    : "http://" + addr;
            }
        }
        catch
        {
            // 配置读取失败不影响内核连接（管道/端口仍有兜底）
        }
    }

    /// <summary>从 database.sqlite 的 profiles 表读取订阅名称与当前项。</summary>
    private void ReadProfiles()
    {
        var db = Path.Combine(DataDir, "database.sqlite");
        if (!File.Exists(db)) return;
        DatabasePath = db;

        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = SqliteOpenMode.ReadOnly
            };
            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select id, type, label from profiles order by \"order\"";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? "" : reader.GetInt64(0).ToString();
                var type = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var label = reader.IsDBNull(2) ? "" : reader.GetString(2);

                // type=url 为远程订阅（机场）；file 为本地配置，不计入机场
                if (!type.Equals("url", StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("remote", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(label)) label = id;
                Airlines.Add(label);
                if (id == _currentProfileId) CurrentName = label;
            }

            if (string.IsNullOrEmpty(CurrentName) && Airlines.Count > 0)
                CurrentName = Airlines[0];
        }
        catch
        {
            // 数据库被占用/版本不识别时降级：仅显示客户端名，不影响测速
        }
    }

    private static string ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";
}
