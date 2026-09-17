using System.Windows;
using System.Windows.Media;

namespace ClashSpeedTest;

public sealed class ProxyInfo
{
    public required string Name { get; init; }
    public string Type { get; init; } = "";
    public string Now { get; init; } = "";
    public List<string> All { get; init; } = new();
}

public sealed class ClashState
{
    public Dictionary<string, ProxyInfo> Proxies { get; init; } = new(StringComparer.Ordinal);
    public int MixedPort { get; init; }
    public string ProxyUrl { get; init; } = "";
    public string Group { get; init; } = "";
    public string Mode { get; init; } = "";
}

public sealed class NodeResult : System.ComponentModel.INotifyPropertyChanged
{
    public required string Name { get; init; }
    public string Type { get; init; } = "";
    public string RankText { get; set; } = "—";
    public string DelayText { get; set; } = "—";
    public string SpeedText { get; set; } = "—";
    public string StatusText { get; set; } = "等待";
    public Brush StatusBg { get; set; } = Brush("#EAEEF2");
    public Brush StatusFg { get; set; } = Brush("#656D76");

    /// <summary>是否纳入本次检测（点击条目切换）。</summary>
    public bool IsSelected { get; set; } = true;

    /// <summary>左侧色条：绿色=要检测，灰色=不检测。</summary>
    public Brush SelectionBar { get; set; } = Brush("#1A7F37");

    /// <summary>根据 IsSelected 刷新左侧色条。</summary>
    public void RefreshSelectionBar()
    {
        SelectionBar = Brush(IsSelected ? "#1A7F37" : "#D0D7DE");
        Raise("SelectionBar");
    }
    public bool Ok { get; set; }
    public double Mbs { get; set; }
    public int DelayMs { get; set; }
    /// <summary>延迟 error（连不上），跳过下载，显示"重测"按钮。</summary>
    public bool ErrorOnly { get; set; }
    public Visibility RetestVisibility { get; set; } = Visibility.Collapsed;
    public Visibility UseVisibility { get; set; } = Visibility.Collapsed;
    public string UseButtonText { get; set; } = "使用";
    public bool IsUseEnabled { get; set; } = true;
    public bool IsCurrent { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    public void NotifyAll()
    {
        foreach (var p in new[] { "RankText", "DelayText", "SpeedText", "StatusText",
                                  "StatusBg", "StatusFg", "SelectionBar", "RetestVisibility",
                                  "UseVisibility", "UseButtonText", "IsUseEnabled", "IsCurrent" })
            Raise(p);
    }

    private static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}

public sealed class TestOptions
{
    public int Seconds { get; set; } = 10;
    public double MaxMb { get; set; } = 30;
    public string Filter { get; set; } = "";
    public string Url { get; set; } = "https://speed.cloudflare.com/__down?bytes=52428800";
    public bool DelayOnly { get; set; }
    public string DelayUrl { get; set; } = "http://www.gstatic.com/generate_204";
}
