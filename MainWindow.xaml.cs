using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly ObservableCollection<NodeResult> _results = new();

    /// <summary>当前连接上的客户端类型（FlClash / Clash Party / Clash Verge Rev / TCP）。</summary>
    private string _clientKind = "";

    /// <summary>当前机场（订阅）名称。</summary>
    private string _airlineName = "";

    /// <summary>结果缓存的键（客户端 + 机场名）。</summary>
    private string _airportKey = "";

    /// <summary>本次（或上次）检测的开始 / 结束时间。</summary>
    private DateTimeOffset? _testStartedAt;
    private DateTimeOffset? _testFinishedAt;

    public MainWindow()
    {
        InitializeComponent();
        LstNodes.ItemsSource = _results;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            TxtMeta.Text = "正在连接 Clash 内核…";
            var (client, kind) = await ConnectMihomoAsync();
            _mihomo = client;
            _clientKind = kind;
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
                "\n\n请确认：1) Clash Party / Clash Verge Rev / FlClash 正在运行 2) 已载入订阅 3) 系统代理或 TUN 已开启。" +
                "\n\n若使用 FlClash：需先在「设置 → 外部控制器」中开启（固定端口 9090），否则内核不开放 REST 接口。",
                "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 刷新：读客户端订阅(机场) + 拉取当前配置的节点列表。
    /// invalidateCache = true 表示用户主动「重新检测」：同一机场下丢弃历史结果；
    /// 若机场已切换，则仍加载新机场的历史结果。
    /// </summary>
    private async Task RefreshAllAsync(ClashState? state = null, bool invalidateCache = false)
    {
        if (_mihomo == null) return;
        state ??= await _mihomo.GetStateAsync();
        _group = state.Group;
        _proxyUrl = state.ProxyUrl;
        _service = new NodeSpeedTestService(_mihomo, _group, _proxyUrl);

        // 订阅（机场）信息：按当前实际连接的客户端选择数据源
        string airlineName, airlineSummary, airlineDetails, clientName;
        if (_clientKind == "FlClash")
        {
            // FlClash：订阅在 database.sqlite(profiles 表)，当前项在 shared_preferences.json
            var fl = FlClashSupport.Load();
            airlineName = string.IsNullOrEmpty(fl.CurrentName) ? "未知" : fl.CurrentName;
            airlineSummary = fl.Airlines.Count > 0
                ? $"{airlineName} · 共 {fl.Airlines.Count} 个订阅"
                : $"{airlineName} · 未读取到 FlClash 订阅";
            airlineDetails = fl.Airlines.Count > 0
                ? $"当前：{airlineName}\n全部订阅：{string.Join("、", fl.Airlines)}"
                : airlineSummary;
            clientName = "FlClash";
        }
        else
        {
            // 机场 = 客户端订阅配置里的 remote 订阅（Clash Party 的 profile.yaml / Verge 的 profiles.yaml）
            var profiles = VergeProfiles.Load();
            airlineName = string.IsNullOrEmpty(profiles.CurrentName) ? "未知" : profiles.CurrentName;
            airlineSummary = profiles.Airlines.Count > 0
                ? $"{airlineName} · 共 {profiles.Airlines.Count} 个订阅"
                : profiles.FilePath == null ? $"{airlineName} · 未找到订阅配置" : airlineName;
            airlineDetails = profiles.Airlines.Count > 0
                ? $"当前：{airlineName}\n全部订阅：{string.Join("、", profiles.Airlines)}"
                : airlineSummary;
            clientName = profiles.ClientName;
        }

        _airlineName = airlineName;
        TxtAirline.Text = airlineSummary;
        TxtAirline.ToolTip = airlineDetails;
        TxtGroup.Text = airlineName;
        TxtGroup.ToolTip = airlineDetails;
        TxtPort.Text = state.MixedPort > 0 ? state.MixedPort.ToString() : "—";
        TxtMeta.Text = string.IsNullOrEmpty(clientName)
            ? $"Clash 已连接 · 模式 {state.Mode}"
            : $"{clientName} 已连接 · 模式 {state.Mode}";

        // 只列出当前选择组中能直接切换的真实节点。
        var groupNodeNames = state.Proxies.TryGetValue(_group, out var selectedGroup)
            ? selectedGroup.All
            : new List<string>();
        var nodes = groupNodeNames
            .Distinct(StringComparer.Ordinal)
            .Select(name => state.Proxies.TryGetValue(name, out var proxy) ? proxy : null)
            .Where(p => p != null && !GroupTypes.Contains(p.Type) && !MihomoClient.IsMetaGroup(p.Name))
            .Select(p => p!)
            .ToList();
        ReplaceResults(nodes.Select((n, i) => MakeNode(
            n.Name, n.Type, i + 1, string.Equals(n.Name, selectedGroup?.Now, StringComparison.Ordinal))));
        RebindGrid();

        TxtFooter.Text = nodes.Count > 0
            ? $"检测到 {nodes.Count} 个节点 · 当前机场：{airlineName}"
            : "未检测到节点，请检查 Clash 订阅";

        // ===== 结果缓存：按机场加载上次结果 =====
        // 机场切换 → 加载该机场最近一次结果（原结果不丢弃）；
        // 同一机场点「重新检测」→ 视为重新开始，丢弃该机场缓存。
        var airportKey = TestResultStore.MakeKey(clientName, airlineName);
        bool airportChanged = !string.Equals(airportKey, _airportKey, StringComparison.Ordinal);
        _airportKey = airportKey;

        if (invalidateCache && !airportChanged)
        {
            TestResultStore.Remove(_airportKey);
            ClearTestTime();
            ResetProgressUi();
            SetBadge("空闲", "#656D76");
        }
        else
        {
            ApplyCachedResults(_airportKey);
        }
    }

    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnRescan.IsEnabled = false;
            TxtFooter.Text = "正在重新检测…";
            await RefreshAllAsync(invalidateCache: true);
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

    private static NodeResult MakeNode(string name, string type, int rank, bool isCurrent)
    {
        var r = new NodeResult
        {
            Name = name,
            Type = type,
            RankText = rank.ToString(),
            IsCurrent = isCurrent,
            IsSelected = true,          // 默认全部纳入检测
            UseButtonText = isCurrent ? "当前" : "使用",
            IsUseEnabled = !isCurrent
        };
        r.RefreshSelectionBar();
        SetState(r, "等待", "#EAEEF2", "#656D76");
        return r;
    }

    /// <summary>
    /// 设置条目的测速状态（文字 + 徽章配色）。
    /// 注意：左侧色条只表示"是否检测"，不随测速状态变化，故此处不设置。
    /// </summary>
    private static void SetState(NodeResult r, string text, string bg, string fg)
    {
        r.StatusText = text;
        r.StatusBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        r.StatusFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        r.NotifyAll();
    }

    private static async Task<(MihomoClient Client, string Kind)> ConnectMihomoAsync()
    {
        // 端点列表：(地址, 密钥, 客户端类型)
        var endpoints = new List<(string Url, string Secret, string Kind)>();

        // 1) FlClash：默认不开外部控制器；用户开启后固定 127.0.0.1:9090，密钥自动生成
        var flclash = FlClashSupport.Load();
        if (flclash.ControllerEnabled && !string.IsNullOrEmpty(flclash.ControllerUrl))
            endpoints.Add((flclash.ControllerUrl, flclash.Secret, "FlClash"));

        // 2) 动态发现 Clash 内核命名管道（Clash Party / Verge Rev 管道名带动态后缀）
        var pipeEndpoint = MihomoClient.DiscoverPipeEndpoint();
        if (pipeEndpoint != null)
        {
            var pipeKind = pipeEndpoint.Contains("MihomoParty", StringComparison.OrdinalIgnoreCase)
                ? "Clash Party"
                : "Clash Verge Rev";
            endpoints.Add((pipeEndpoint, "", pipeKind));
        }

        // 3) 固定管道名兜底 + TCP 回退
        endpoints.Add(("pipe://MihomoParty/mihomo", "", "Clash Party"));
        endpoints.Add(("pipe://verge-mihomo", "", "Clash Verge Rev"));
        endpoints.Add(("http://127.0.0.1:9097", "", "TCP"));
        endpoints.Add(("http://127.0.0.1:9090", "", "TCP"));

        Exception? last = null;
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (url, secret, kind) in endpoints)
        {
            if (!tried.Add(url)) continue;
            var client = new MihomoClient(url, secret);
            try
            {
                await client.GetStateAsync();
                return (client, kind);
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
            Filter = "",   // 关键词过滤已移除，改用列表勾选
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

        // 只测列表中勾选（左侧色条为绿色）的节点
        var opts = CurrentOptions();
        var toTest = _results.Where(r => r.IsSelected).ToList();
        if (toTest.Count == 0)
        {
            MessageBox.Show("没有勾选要检测的节点。\n\n请在节点列表中点击条目，左侧色条变绿色即表示纳入检测。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 重置状态
        foreach (var r in _results)
        {
            r.DelayText = "—"; r.SpeedText = "—"; r.Mbs = 0; r.Ok = false; r.DelayMs = 0;
            r.ErrorOnly = false; r.RetestVisibility = Visibility.Collapsed;
            r.UseVisibility = Visibility.Collapsed;
            SetState(r, "等待", "#EAEEF2", "#656D76");
        }
        RebindGrid();
        RecoBox.Visibility = Visibility.Collapsed;
        Progress.Value = 0;

        // 记录本次检测开始时间（结束时间在完成/停止时写入）
        _testStartedAt = DateTimeOffset.Now;
        _testFinishedAt = null;
        UpdateTimeLabels();

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
        bool testCompleted = false;
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
                        SetState(row, "error", "#FFEBE9", "#CF222E");
                    }
                }
                var selected = toTest.ToHashSet();
                var ordered = toTest.Where(r => !r.ErrorOnly)
                    .OrderBy(r => r.DelayMs)
                    .ThenBy(r => r.Name, StringComparer.CurrentCulture)
                    .Concat(toTest.Where(r => r.ErrorOnly)
                        .OrderBy(r => r.Name, StringComparer.CurrentCulture))
                    .Concat(_results.Where(r => !selected.Contains(r)))
                    .ToList();
                ReplaceResults(ordered, updateRanks: true);
                RebindGrid();
            });

            // 只测延迟模式：阶段1 即完成
            if (opts.DelayOnly)
            {
                Dispatcher.Invoke(() =>
                {
                    // 延迟模式没有下载测速，"可用"节点统一标记为已检测
                    foreach (var row in toTest.Where(r => r.DelayMs > 0))
                        SetState(row, "已检测", "#DAFBE1", "#1A7F37");

                    int usable = toTest.Count(r => r.DelayMs > 0);
                    Progress.Maximum = Math.Max(1, total);
                    Progress.Value = usable;
                    TxtProgress.Text = $"延迟检测完成 · {usable}/{total} 可用";
                    TxtDone.Text = $"{usable} / {total}";
                    TxtOkRate.Text = $"{usable}/{total}  ({(total == 0 ? 0 : usable * 100.0 / total):F0}%)";

                    MarkFinished();
                    RebindGrid();
                    SetBadge("已完成", "#1A7F37");
                    TxtFooter.Text = $"延迟检测完成 · {usable}/{total} 可用";
                });
                return;
            }

            // ===== 阶段2：按延迟从低到高逐个下载测速 =====
            var selectedForTest = toTest.ToHashSet();
            var alive = _results.Where(r => selectedForTest.Contains(r) && !r.ErrorOnly).ToList();
            int done = 0;
            Dispatcher.Invoke(() =>
            {
                TxtProgress.Text = $"阶段2/2：下载测速 {alive.Count} 个可用节点";
                Progress.Maximum = Math.Max(1, alive.Count);
                Progress.Value = 0;
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
                        SetState(row, "测速中", "#DDEBF6", "#0969DA");
                        LstNodes.ScrollIntoView(row);
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
                            SetState(row, "完成", "#DAFBE1", "#1A7F37");
                        else
                            SetState(row, result.StatusText, "#FFEBE9", "#CF222E");
                        done++;
                        Progress.Value = done;
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
                foreach (var r in ok)
                {
                    r.UseVisibility = Visibility.Visible;
                    r.RetestVisibility = Visibility.Collapsed;
                    r.NotifyAll();
                }
                ReplaceResults(all, updateRanks: true);
                RebindGrid();
                SetRecoBox();
                MarkFinished();
                SetBadge("已完成", "#1A7F37");
                TxtProgress.Text = $"完成 · {ok.Count}/{total} 成功";
                TxtFooter.Text = "测速完成，正在恢复原节点…";
            });
            testCompleted = true;
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Invoke(() =>
            {
                MarkFinished();   // 已测完的部分照样保存
                SetBadge("已停止", "#9A6700");
                TxtFooter.Text = "已停止，正在恢复原节点…";
            });
        }
        finally
        {
            bool restored = true;
            try
            {
                if (!string.IsNullOrEmpty(_originalNode) && _mihomo != null)
                    await _mihomo.SelectProxyAsync(_group, _originalNode);
            }
            catch { restored = false; }
            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            Dispatcher.Invoke(() =>
            {
                TxtSideCurrent.Text = "—";
                if (testCompleted)
                    TxtFooter.Text = restored ? "测速完成，已恢复原节点" : "测速完成，但恢复原节点失败";
            });
        }
    }

    /// <summary>手动重测单个 error 节点（完整流程：切换+延迟+下载）。</summary>
    private async void OnRetest(object sender, RoutedEventArgs e)
    {
        if (_mihomo == null || _service == null) return;
        if (sender is not Button btn || btn.Tag is not string name) return;
        if (!BtnStart.IsEnabled)
        {
            TxtFooter.Text = "测速进行中，请先停止再重测。";
            return;
        }
        if (string.IsNullOrEmpty(_group)) return;

        var row = _results.FirstOrDefault(r => r.Name == name);
        if (row == null) return;

        var opts = CurrentOptions();
        _service = new NodeSpeedTestService(_mihomo, _group, _proxyUrl);
        BtnStart.IsEnabled = false;
        BtnRescan.IsEnabled = false;
        row.RetestVisibility = Visibility.Collapsed;
        SetState(row, "测速中", "#DDEBF6", "#0969DA");
        TxtFooter.Text = $"正在重测：{name}";

        string nodeBeforeRetest = "";
        try
        {
            var state = await _mihomo.GetStateAsync();
            nodeBeforeRetest = state.Proxies.TryGetValue(_group, out var group) ? group.Now : "";
        }
        catch { }

        try
        {
            var node = new ProxyInfo { Name = name, Type = row.Type };
            var result = await _service.TestAsync(node, opts, CancellationToken.None);
            row.DelayText = result.DelayText;
            row.SpeedText = result.SpeedText;
            row.Ok = result.Ok;
            row.Mbs = result.Mbs;
            row.DelayMs = result.DelayMs;
            row.ErrorOnly = !result.Ok;
            row.RetestVisibility = !result.Ok ? Visibility.Visible : Visibility.Collapsed;
            row.UseVisibility = result.Ok ? Visibility.Visible : Visibility.Collapsed;
            if (result.Ok)
                SetState(row, "完成", "#DAFBE1", "#1A7F37");
            else
                SetState(row, result.StatusText, "#FFEBE9", "#CF222E");

            // 单节点重测也写回缓存（结束时间取本次重测时间）
            _testFinishedAt = DateTimeOffset.Now;
            _testStartedAt ??= _testFinishedAt;
            UpdateTimeLabels();
            SaveResults();

            TxtFooter.Text = $"重测完成：{name} → {result.SpeedText}";
        }
        catch (Exception ex)
        {
            row.RetestVisibility = Visibility.Visible;
            row.UseVisibility = Visibility.Collapsed;
            SetState(row, "重测失败", "#FFEBE9", "#CF222E");
            TxtFooter.Text = $"重测失败：{ex.Message}";
        }
        finally
        {
            if (!string.IsNullOrEmpty(nodeBeforeRetest))
            {
                try { await _mihomo.SelectProxyAsync(_group, nodeBeforeRetest); }
                catch { TxtFooter.Text += " · 恢复原节点失败"; }
            }
            BtnStart.IsEnabled = true;
            BtnRescan.IsEnabled = true;
        }
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
            TxtFooter.Text = $"已复制节点名：{TxtBestName.Text}";
        }
    }

    private async void OnUseNode(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
            await SwitchToNodeAsync(name);
    }

    private async void OnUseBest(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TxtBestName.Text))
            await SwitchToNodeAsync(TxtBestName.Text);
    }

    private async Task SwitchToNodeAsync(string name)
    {
        if (_mihomo == null || string.IsNullOrEmpty(_group)) return;
        if (!BtnStart.IsEnabled)
        {
            TxtFooter.Text = "测速进行中，完成或停止后才能切换节点。";
            return;
        }

        SetUseButtonsEnabled(false);
        BtnUseBest.IsEnabled = false;
        TxtFooter.Text = $"正在切换到：{name}…";
        try
        {
            await _mihomo.SelectProxyAsync(_group, name);
            var state = await _mihomo.GetStateAsync();
            bool selected = state.Proxies.TryGetValue(_group, out var group) &&
                string.Equals(group.Now, name, StringComparison.Ordinal);
            if (!selected)
                throw new InvalidOperationException("Clash 未确认该节点已生效");

            foreach (var row in _results)
            {
                row.IsCurrent = string.Equals(row.Name, name, StringComparison.Ordinal);
                row.UseButtonText = row.IsCurrent ? "当前" : "使用";
                row.IsUseEnabled = !row.IsCurrent;
                row.NotifyAll();
            }
            TxtFooter.Text = $"已切换到：{name}";
        }
        catch (Exception ex)
        {
            SetUseButtonsEnabled(true);
            TxtFooter.Text = $"切换失败：{ex.Message}";
            MessageBox.Show($"无法切换到节点“{name}”。\n\n{ex.Message}", "切换失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnUseBest.IsEnabled = true;
        }
    }

    private void SetUseButtonsEnabled(bool enabled)
    {
        foreach (var row in _results)
        {
            row.IsUseEnabled = enabled && !row.IsCurrent;
            row.NotifyAll();
        }
    }

    private void UpdateCounts(int done, int total)
    {
        TxtDone.Text = $"{done} / {total}";
        var ok = _results.Count(r => r.Ok);
        var pct = _results.Count == 0 ? 0 : (double)ok / _results.Count * 100;
        TxtOkRate.Text = $"{ok}/{_results.Count}  ({pct:F0}%)";
    }

    /// <summary>刷新列表标题右侧的计数：要检测数 / 总数。</summary>
    private void RebindGrid()
    {
        var selected = _results.Count(r => r.IsSelected);
        TxtListCount.Text = $"要检测 {selected} / 共 {_results.Count}";
    }

    /// <summary>点击节点条目：切换"是否纳入检测"（绿色=检测，灰色=跳过）。</summary>
    private void OnNodeRowClick(object sender, MouseButtonEventArgs e)
    {
        // 条目内的按钮（重测/使用）有自己的点击逻辑，不触发切换
        DependencyObject? dep = e.OriginalSource as DependencyObject;
        while (dep != null)
        {
            if (dep is Button) return;
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (!BtnStart.IsEnabled)
        {
            TxtFooter.Text = "测速进行中，完成后才能调整检测项。";
            return;
        }

        if (LstNodes.SelectedItem is not NodeResult row) return;
        row.IsSelected = !row.IsSelected;
        row.RefreshSelectionBar();
        RebindGrid();
    }

    private void ReplaceResults(IEnumerable<NodeResult> ordered, bool updateRanks = false)
    {
        var snapshot = ordered.ToList();
        if (updateRanks)
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                snapshot[i].RankText = (i + 1).ToString();
                snapshot[i].NotifyAll();
            }
        }

        _results.Clear();
        foreach (var row in snapshot)
            _results.Add(row);
    }

    // ===== 检测时间 =====

    private void UpdateTimeLabels()
    {
        TxtStartTime.Text = _testStartedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
        TxtEndTime.Text = _testFinishedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    }

    private void ClearTestTime()
    {
        _testStartedAt = null;
        _testFinishedAt = null;
        UpdateTimeLabels();
    }

    /// <summary>清空进度区（切换机场/重新检测且无历史结果时）。</summary>
    private void ResetProgressUi()
    {
        Progress.Maximum = 1;
        Progress.Value = 0;
        TxtProgress.Text = "就绪 · 等待开始";
        TxtDone.Text = "0 / 0";
        TxtOkRate.Text = "—";
        RecoBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>标记本次检测结束，并把结果写入当前机场的缓存。</summary>
    private void MarkFinished()
    {
        _testFinishedAt = DateTimeOffset.Now;
        _testStartedAt ??= _testFinishedAt;
        UpdateTimeLabels();
        SaveResults();
    }

    private void SaveResults()
    {
        if (string.IsNullOrEmpty(_airportKey) || _testStartedAt == null) return;
        try
        {
            var run = new CachedTestRun
            {
                AirportName = _airlineName,
                ClientKind = _clientKind,
                Group = _group,
                DelayOnly = ChkDelayOnly.IsChecked == true,
                StartedAt = _testStartedAt.Value,
                FinishedAt = _testFinishedAt ?? DateTimeOffset.Now,
                Nodes = _results.Select(r => new CachedNodeResult
                {
                    Name = r.Name,
                    Type = r.Type,
                    DelayMs = r.DelayMs,
                    DelayText = r.DelayText,
                    SpeedText = r.SpeedText,
                    Mbs = r.Mbs,
                    Ok = r.Ok,
                    ErrorOnly = r.ErrorOnly,
                    StatusText = r.StatusText,
                    IsSelected = r.IsSelected
                }).ToList()
            };
            TestResultStore.Save(_airportKey, run);
        }
        catch
        {
            // 缓存写入失败不影响界面
        }
    }

    /// <summary>把该机场最近一次结果套用到当前节点列表（按节点名匹配）。</summary>
    private void ApplyCachedResults(string key)
    {
        var run = TestResultStore.Load(key);
        if (run == null || run.Nodes.Count == 0 || _results.Count == 0)
        {
            ClearTestTime();
            ResetProgressUi();
            return;
        }

        var byName = run.Nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
        int applied = 0;
        foreach (var row in _results)
        {
            if (!byName.TryGetValue(row.Name, out var c)) continue;

            row.DelayMs = c.DelayMs;
            row.DelayText = string.IsNullOrWhiteSpace(c.DelayText) ? "—" : c.DelayText;
            row.SpeedText = string.IsNullOrWhiteSpace(c.SpeedText) ? "—" : c.SpeedText;
            row.Mbs = c.Mbs;
            row.Ok = c.Ok;
            row.ErrorOnly = c.ErrorOnly;
            row.IsSelected = c.IsSelected;
            row.RefreshSelectionBar();
            row.RetestVisibility = c.ErrorOnly ? Visibility.Visible : Visibility.Collapsed;
            row.UseVisibility = c.Ok ? Visibility.Visible : Visibility.Collapsed;

            if (c.Ok) SetState(row, "完成", "#DAFBE1", "#1A7F37");
            else if (c.ErrorOnly) SetState(row, "error", "#FFEBE9", "#CF222E");
            else if (c.DelayMs > 0) SetState(row, "已检测", "#DAFBE1", "#1A7F37"); // 只测延迟的结果
            else SetState(row, string.IsNullOrWhiteSpace(c.StatusText) ? "等待" : c.StatusText,
                "#EAEEF2", "#656D76");
            applied++;
        }

        // 缓存里的节点在当前订阅中一个都匹配不上 → 说明机场配置已变，不复用
        if (applied == 0)
        {
            ClearTestTime();
            ResetProgressUi();
            return;
        }

        // 按缓存中保存的顺序恢复（那正是上次测完后的最终排序）；
        // 缓存中不存在的节点（订阅新增）保持原相对顺序排在后面。
        var orderMap = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < run.Nodes.Count; i++) orderMap[run.Nodes[i].Name] = i;
        var ordered = _results
            .OrderBy(r => orderMap.TryGetValue(r.Name, out var idx) ? idx : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
        ReplaceResults(ordered, updateRanks: true);
        RebindGrid();

        _testStartedAt = run.StartedAt;
        _testFinishedAt = run.FinishedAt;
        UpdateTimeLabels();

        // 兼容旧缓存（没有 DelayOnly 字段）：全部无下载成功但有延迟数据 → 视为延迟检测
        bool delayOnlyRun = run.DelayOnly ||
            (run.Nodes.All(n => !n.Ok) && run.Nodes.Any(n => n.DelayMs > 0));

        int total = run.Nodes.Count;
        int good = delayOnlyRun
            ? run.Nodes.Count(n => n.DelayMs > 0)
            : run.Nodes.Count(n => n.Ok);
        Progress.Maximum = Math.Max(1, total);
        Progress.Value = applied;
        TxtProgress.Text = delayOnlyRun
            ? $"上次延迟检测 · {applied} 个节点"
            : $"上次结果 · {applied} 个节点";
        TxtDone.Text = $"{good} / {total}";
        TxtOkRate.Text = $"{good}/{total}  ({(total == 0 ? 0 : good * 100.0 / total):F0}%)";

        SetRecoBox();
        SetBadge("上次结果", "#656D76");
        TxtFooter.Text = $"已加载上次检测结果（{run.FinishedAt:MM-dd HH:mm}）· 机场：{run.AirportName}";
    }

    /// <summary>根据当前结果刷新「最快节点」卡片。</summary>
    private void SetRecoBox()
    {
        var best = _results.Where(r => r.Ok && r.Mbs > 0)
            .OrderByDescending(r => r.Mbs).FirstOrDefault();
        if (best == null)
        {
            RecoBox.Visibility = Visibility.Collapsed;
            return;
        }
        TxtBestName.Text = best.Name;
        TxtBestMeta.Text = $"{best.Mbs:F2} MB/s  ({best.Mbs * 8:F0} Mbps)  ·  延迟 {best.DelayText}";
        RecoBox.Visibility = Visibility.Visible;
    }

    private void SetBadge(string text, string color)
    {
        TxtBadge.Text = text;
        DotStatus.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}
