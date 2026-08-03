using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClashSpeedTest;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> GroupTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Selector", "URLTest", "Fallback", "LoadBalance", "Direct", "Reject",
        "RejectDrop", "Compatible", "Pass", "PassRule"
    };

    private MihomoClient? _mihomo;
    private NodeSpeedTestService? _service;
    private string _group = "";
    private string _proxyUrl = "";
    private string _originalNode = "";
    private CancellationTokenSource? _cts;
    private List<NodeResult> _results = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            TxtMeta.Text = "正在连接 Clash 内核…";
            _mihomo = await ConnectMihomoAsync();
            var state = await _mihomo.GetStateAsync();
            _proxyUrl = state.ProxyUrl;
            SetBadge("就绪", "#656D76");
            await RefreshAllAsync(state);
        }
        catch (Exception ex)
        {
            TxtMeta.Text = "无法连接 Clash：请确认已启动并载入订阅";
            TxtFooter.Text = "连接失败：" + ex.Message;
            SetBadge("连接失败", "#CF222E");
            MessageBox.Show("无法连接 Clash 内核。\n\n" + ex.Message +
                "\n\n请确认：1) Clash Verge Rev 正在运行 2) 已载入订阅 3) 系统代理或 TUN 已开启。",
                "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>重新检测：读 Verge 订阅(机场) + 拉取当前配置的节点列表。</summary>
    private async Task RefreshAllAsync(ClashState? state = null)
    {
        if (_mihomo == null) return;
        state ??= await _mihomo.GetStateAsync();
        _group = state.Group;
        _proxyUrl = state.ProxyUrl;
        _service = new NodeSpeedTestService(_mihomo, _group, _proxyUrl);

        // 机场 = Verge profiles.yaml 里的 remote 订阅
        var profiles = VergeProfiles.Load();
        string airlineText = string.IsNullOrEmpty(profiles.CurrentName) ? "未知" : profiles.CurrentName;
        if (profiles.Airlines.Count > 0)
            airlineText += $"（共 {profiles.Airlines.Count} 个订阅：{string.Join("、", profiles.Airlines)}）";
        else if (profiles.FilePath == null)
            airlineText += "（未找到 Verge profiles.yaml）";

        TxtAirline.Text = airlineText;
        TxtGroup.Text = airlineText;
        TxtPort.Text = state.MixedPort > 0 ? state.MixedPort.ToString() : "—";
        TxtMeta.Text = $"Clash 已连接 · 模式 {state.Mode}";

        // 节点 = 当前配置下的所有真实节点（去重）
        var nodes = state.Proxies.Values
            .Where(p => !GroupTypes.Contains(p.Type) && !MihomoClient.IsMetaGroup(p.Name))
            .ToList();
        _results = nodes.Select((n, i) => MakeNode(n.Name, n.Type, i + 1)).ToList();
        RebindGrid();

        TxtFooter.Text = nodes.Count > 0
            ? $"检测到 {nodes.Count} 个节点 · 当前机场：{airlineText}"
            : "未检测到节点，请检查 Clash 订阅";
    }

    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnRescan.IsEnabled = false;
            TxtFooter.Text = "正在重新检测…";
            await RefreshAllAsync();
            TxtFooter.Text = "重新检测完成";
        }
        catch (Exception ex)
        {
            TxtFooter.Text = "重新检测失败：" + ex.Message;
        }
        finally
        {
            BtnRescan.IsEnabled = true;
        }
    }

    private static NodeResult MakeNode(string name, string type, int rank)
    {
        var r = new NodeResult { Name = name, Type = type, RankText = rank.ToString() };
        SetState(r, "等待", "#EAEEF2", "#656D76", "#D0D7DE");
        return r;
    }

    private static void SetState(NodeResult r, string text, string bg, string fg, string leftBar)
    {
        r.StatusText = text;
        r.StatusBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        r.StatusFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        r.LeftBar = new SolidColorBrush((Color)ColorConverter.ConvertFromString(leftBar));
        r.NotifyAll();
    }

    private static async Task<MihomoClient> ConnectMihomoAsync()
    {
        string[] endpoints = { "", "http://127.0.0.1:9097", "http://127.0.0.1:9090" };
        Exception? last = null;
        foreach (var ep in endpoints)
        {
            var client = new MihomoClient(ep);
            try
            {
                await client.GetStateAsync();
                return client;
            }
            catch (Exception ex)
            {
                last = ex;
                client.Dispose();
            }
        }
        throw new InvalidOperationException("所有连接方式均失败：" + last?.Message);
    }

    private TestOptions CurrentOptions()
    {
        return new TestOptions
        {
            Seconds = int.TryParse(TxtSeconds.Text, out var s) ? Math.Clamp(s, 3, 120) : 10,
            MaxMb = double.TryParse(TxtMaxMb.Text, out var m) ? Math.Clamp(m, 2, 500) : 30,
            Filter = TxtFilter.Text.Trim(),
            Url = string.IsNullOrWhiteSpace(TxtUrl.Text) ? "https://speed.cloudflare.com/__down?bytes=52428800" : TxtUrl.Text.Trim(),
            DelayOnly = ChkDelayOnly.IsChecked == true,
        };
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_mihomo == null || _service == null) return;
        if (string.IsNullOrEmpty(_group))
        {
            MessageBox.Show("未检测到机场/选择组，请先点重新检测按钮。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var opts = CurrentOptions();
        var toTest = _results.Where(r =>
            string.IsNullOrEmpty(opts.Filter) ||
            r.Name.Contains(opts.Filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (toTest.Count == 0)
        {
            MessageBox.Show("没有可测节点（关键词过滤后为空）。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 重置状态
        foreach (var r in _results)
        {
            r.DelayText = "—"; r.SpeedText = "—"; r.Mbs = 0; r.Ok = false; r.DelayMs = 0;
            r.ErrorOnly = false; r.RetestVisibility = Visibility.Collapsed;
            SetState(r, "等待", "#EAEEF2", "#656D76", "#D0D7DE");
        }
        RebindGrid();
        RecoBox.Visibility = Visibility.Collapsed;
        Progress.Value = 0;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = true;
        SetBadge("测速中…", "#0969DA");

        // 记住当前节点，测完恢复
        _originalNode = "";
        try { _originalNode = (await _mihomo.GetStateAsync()).Proxies.TryGetValue(_group, out var g) ? g.Now ?? "" : ""; }
        catch { }

        int total = toTest.Count;
        try
        {
            // ===== 阶段1：批量延迟检测 =====
            var nodeNames = toTest.Select(r => r.Name).ToList();
            Dispatcher.Invoke(() => TxtFooter.Text = $"阶段1/2：批量检测 {total} 个节点延迟…");
            var delays = await _mihomo.GetGroupDelaysAsync(_group, opts.DelayUrl, 5000, ct);
            if (delays.Count == 0)
                delays = await _mihomo.MeasureDelaysConcurrentAsync(nodeNames, opts.DelayUrl, 5000, ct);

            Dispatcher.Invoke(() =>
            {
                foreach (var row in toTest)
                {
                    var d = delays.TryGetValue(row.Name, out var v) ? v : 0;
                    row.DelayMs = d;
                    row.DelayText = d > 0 ? $"{d} ms" : "error";
                    if (d <= 0)
                    {
                        row.ErrorOnly = true;
                        row.RetestVisibility = Visibility.Visible;
                        SetState(row, "error", "#FFEBE9", "#CF222E", "#CF222E");
                    }
                }
                RebindGrid();
            });

            // 只测延迟模式：阶段1 即完成
            if (opts.DelayOnly)
            {
                Dispatcher.Invoke(() =>
                {
                    var ranked = toTest.OrderBy(r => r.DelayMs <= 0 ? int.MaxValue : r.DelayMs).ToList();
                    for (int i = 0; i < ranked.Count; i++) ranked[i].RankText = (i + 1).ToString();
                    foreach (var r in ranked) r.NotifyAll();
                    RebindGrid();
                    SetBadge("已完成", "#1A7F37");
                    TxtFooter.Text = $"延迟检测完成 · {toTest.Count(r => r.DelayMs > 0)}/{total} 可用";
                });
                return;
            }

            // ===== 阶段2：按延迟从低到高逐个下载测速 =====
            var alive = toTest.Where(r => !r.ErrorOnly).OrderBy(r => r.DelayMs).ToList();
            int done = 0;
            Dispatcher.Invoke(() =>
            {
                TxtProgress.Text = $"阶段2/2：下载测速 {alive.Count} 个可用节点";
                UpdateCounts(done, alive.Count);
            });

            await Task.Run(async () =>
            {
                foreach (var row in alive)
                {
                    if (ct.IsCancellationRequested) break;
                    var node = new ProxyInfo { Name = row.Name, Type = row.Type };
                    Dispatcher.Invoke(() =>
                    {
                        SetState(row, "测速中", "#DDEBF6", "#0969DA", "#0969DA");
                        TxtCurrent.Text = row.Name;
                        TxtSideCurrent.Text = row.Name;
                        TxtFooter.Text = $"正在下载测速：{row.Name}";
                    });
                    var result = await _service.TestDownloadOnlyAsync(node, row.DelayMs, opts, ct);
                    Dispatcher.Invoke(() =>
                    {
                        row.DelayText = result.DelayText;
                        row.SpeedText = result.SpeedText;
                        row.Ok = result.Ok;
                        row.Mbs = result.Mbs;
                        if (result.Ok)
                            SetState(row, "完成", "#DAFBE1", "#1A7F37", "#1A7F37");
                        else
                            SetState(row, result.StatusText, "#FFEBE9", "#CF222E", "#CF222E");
                        done++;
                        TxtProgress.Text = $"阶段2/2：下载测速 {done}/{alive.Count}";
                        UpdateCounts(done, alive.Count);
                    });
                }
            }, ct);

            // ===== 汇总：按下载速度排序 =====
            Dispatcher.Invoke(() =>
            {
                var ok = _results.Where(r => r.Ok && r.Mbs > 0)
                    .OrderByDescending(r => r.Mbs).ToList();
                var rest = _results.Where(r => !ok.Contains(r)).ToList();
                var all = ok.Concat(rest).ToList();
                for (int i = 0; i < all.Count; i++) all[i].RankText = (i + 1).ToString();
                foreach (var r in all) r.NotifyAll();
                _results = all;
                RebindGrid();
                if (ok.Count > 0)
                {
                    var best = ok[0];
                    TxtBestName.Text = best.Name;
                    TxtBestMeta.Text = $"{best.Mbs:F2} MB/s  ({best.Mbs * 8:F0} Mbps)  ·  延迟 {best.DelayText}";
                    RecoBox.Visibility = Visibility.Visible;
                }
                SetBadge("已完成", "#1A7F37");
                TxtProgress.Text = $"完成 · {ok.Count}/{total} 成功";
                TxtFooter.Text = "测速完成，已恢复原节点";
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Invoke(() =>
            {
                SetBadge("已停止", "#9A6700");
                TxtFooter.Text = "已停止，正在恢复原节点…";
            });
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(_originalNode) && _mihomo != null)
                    await _mihomo.SelectProxyAsync(_group, _originalNode);
            }
            catch { }
            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            Dispatcher.Invoke(() => { TxtCurrent.Text = ""; TxtSideCurrent.Text = "—"; });
        }
    }

    /// <summary>手动重测单个 error 节点（完整流程：切换+延迟+下载）。</summary>
    private async void OnRetest(object sender, RoutedEventArgs e)
    {
        if (_mihomo == null || _service == null) return;
        if (sender is not Button btn || btn.Tag is not string name) return;
        if (_cts is { } cts && !cts.IsCancellationRequested && BtnStart.IsEnabled == false)
        {
            TxtFooter.Text = "测速进行中，请先停止再重测。";
            return;
        }
        if (string.IsNullOrEmpty(_group)) return;

        var row = _results.FirstOrDefault(r => r.Name == name);
        if (row == null) return;

        var opts = CurrentOptions();
        _service = new NodeSpeedTestService(_mihomo, _group, _proxyUrl);
        Dispatcher.Invoke(() =>
        {
            row.RetestVisibility = Visibility.Collapsed;
            SetState(row, "测速中", "#DDEBF6", "#0969DA", "#0969DA");
            TxtFooter.Text = $"正在重测：{name}";
        });

        var node = new ProxyInfo { Name = name, Type = row.Type };
        var result = await _service.TestAsync(node, opts, CancellationToken.None);
        Dispatcher.Invoke(() =>
        {
            row.DelayText = result.DelayText;
            row.SpeedText = result.SpeedText;
            row.Ok = result.Ok;
            row.Mbs = result.Mbs;
            row.DelayMs = result.DelayMs;
            row.ErrorOnly = !result.Ok;
            row.RetestVisibility = !result.Ok ? Visibility.Visible : Visibility.Collapsed;
            if (result.Ok)
                SetState(row, "完成", "#DAFBE1", "#1A7F37", "#1A7F37");
            else
                SetState(row, result.StatusText, "#FFEBE9", "#CF222E", "#CF222E");
            TxtFooter.Text = $"重测完成：{name} → {result.SpeedText}";
        });
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        TxtFooter.Text = "正在停止…";
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"clash_speedtest_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        using var writer = new StreamWriter(dlg.FileName, false,
            new System.Text.UTF8Encoding(true));
        writer.WriteLine("节点名,类型,延迟(ms),下载速度(MB/s),状态");
        foreach (var r in _results)
        {
            writer.WriteLine($"{r.Name},{r.Type},{r.DelayMs},{r.Mbs:F2},\"{r.StatusText}\"");
        }
        MessageBox.Show("已导出：" + dlg.FileName, "导出完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCopyBest(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtBestName.Text))
        {
            Clipboard.SetText(TxtBestName.Text);
            TxtBestMeta.Text += "  （已复制）";
        }
    }

    private void UpdateCounts(int done, int total)
    {
        TxtDone.Text = $"{done} / {total}";
        var ok = _results.Count(r => r.Ok);
        var pct = _results.Count == 0 ? 0 : (double)ok / _results.Count * 100;
        TxtOkRate.Text = $"{ok}/{_results.Count}  ({pct:F0}%)";
    }

    private void RebindGrid()
    {
        LstNodes.ItemsSource = null;
        LstNodes.ItemsSource = _results;
        TxtListCount.Text = $"共 {_results.Count} 项";
    }

    private void SetBadge(string text, string color)
    {
        TxtBadge.Text = text;
        DotStatus.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}
