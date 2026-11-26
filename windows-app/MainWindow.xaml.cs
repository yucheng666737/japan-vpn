using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JapanVPN.Models;
using JapanVPN.Services;
using Brush = System.Windows.Media.Brush;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfApp = System.Windows.Application;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfMessageBox = System.Windows.MessageBox;

namespace JapanVPN;

public partial class MainWindow : Window
{
    private readonly VpnConnectionManager _vpnManager;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayStatusItem;
    private bool _isDarkTheme;
    private bool _suppressOptionEvents;
    private bool _isExiting;
    private bool _shownTrayTip;

    public MainWindow()
    {
        InitializeComponent();

        _vpnManager = ((App)WpfApp.Current).VpnManager;

        // Build Windows System Tray icon & context menu
        _trayStatusItem = new Forms.ToolStripMenuItem("狀態：未連線") { Enabled = false };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("開啟主視窗", null, (_, _) => RestoreFromTray());
        menu.Items.Add("換下一個日本節點", null, (_, _) => _vpnManager.SwitchToNextServer());
        menu.Items.Add("中斷連線", null, async (_, _) => await _vpnManager.DisconnectAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("結束程式", null, (_, _) => ExitApplication());

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Japan VPN",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        _vpnManager.StateChanged += OnVpnStateChanged;
        ApplyTheme(false);
        RenderState(_vpnManager.CurrentState);
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var streamInfo = WpfApp.GetResourceStream(new Uri("pack://application:,,,/Assets/japanvpn.ico"));
            if (streamInfo is not null)
            {
                return new Drawing.Icon(streamInfo.Stream);
            }
        }
        catch { }
        return Drawing.SystemIcons.Shield;
    }

    private void RestoreFromTray()
    {
        Dispatcher.BeginInvoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
        });
    }

    private async void ExitApplication()
    {
        _isExiting = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        await _vpnManager.DisconnectAsync();
        WpfApp.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            if (!_shownTrayTip)
            {
                _shownTrayTip = true;
                _trayIcon.ShowBalloonTip(
                    2500,
                    "Japan VPN",
                    "已縮小至右下角系統匣繼續保護連線，右鍵可一鍵換節點或結束程式。",
                    Forms.ToolTipIcon.Info);
            }
            return;
        }
        base.OnClosing(e);
    }

    private void OnVpnStateChanged(VpnUiState state)
    {
        Dispatcher.BeginInvoke(() => RenderState(state));
    }

    private void RenderState(VpnUiState state)
    {
        BannerOpenVpnMissing.Visibility = string.IsNullOrEmpty(state.OpenVpnPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool isConnected = state.Phase == ConnectionPhase.Connected;
        bool isBusy = state.Phase is ConnectionPhase.Fetching
            or ConnectionPhase.Probing
            or ConnectionPhase.Connecting
            or ConnectionPhase.Switching;

        var accentHex = isConnected ? "#16A34A" : isBusy ? "#EA580C" : "#C62828";
        var accentBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(accentHex));

        BtnPower.Background = accentBrush;
        TxtStatusTitle.Foreground = accentBrush;
        PowerHalo.Fill = (Brush)FindResource(isConnected ? "GreenSoftBrush" : isBusy ? "AmberSoftBrush" : "CrimsonSoftBrush");

        TxtTimer.Text = FormatDuration(state.ConnectedSeconds);
        TxtStatusTitle.Text = state.StatusTitle;
        TxtStatusDetail.Text = string.IsNullOrEmpty(state.AttemptProgress)
            ? state.StatusDetail
            : $"{state.StatusDetail}  ({state.AttemptProgress})";

        TxtDownloadSpeed.Text = FormatSpeed(state.DownloadBps);
        TxtUploadSpeed.Text = FormatSpeed(state.UploadBps);

        var displayServer = state.ActiveServer ?? state.SelectedServer;
        TxtSelectedServerMeta.Text = displayServer is not null
            ? $"{displayServer.Ip} • {displayServer.FormattedPing()} • {displayServer.FormattedSpeed()}"
            : "點擊選擇或重新抓取日本節點";

        TxtFilterSummary.Text = $"已自動排除 {state.Excluded219Count} 個 219 開頭連線 • 可用 {state.Servers.Count} 個日本節點";
        TxtRefreshTime.Text = state.LastFetchedAtMillis > 0
            ? $"最後更新：{DateTimeOffset.FromUnixTimeMilliseconds(state.LastFetchedAtMillis).LocalDateTime:HH:mm:ss}"
            : "最後更新：尚未更新";

        _suppressOptionEvents = true;
        ChkFailover.IsChecked = state.AutoFailoverEnabled;
        ChkAutoRefresh.IsChecked = state.AutoRefreshEnabled;
        ChkHealth.IsChecked = state.HealthCheckEnabled;
        _suppressOptionEvents = false;

        BtnSwitchNext.Visibility = (isConnected || isBusy) ? Visibility.Visible : Visibility.Collapsed;

        _trayStatusItem.Text = isConnected && state.ActiveServer is not null
            ? $"已連線：{state.ActiveServer.Ip}"
            : $"狀態：{state.StatusTitle}";
        var trayText = isConnected && state.ActiveServer is not null
            ? $"Japan VPN - {state.ActiveServer.Ip}"
            : $"Japan VPN - {state.StatusTitle}";
        _trayIcon.Text = trayText.Length > 63 ? trayText[..63] : trayText;

        RenderServerList(state);
        TxtLogs.Text = state.Logs.Count == 0
            ? "尚無連線紀錄"
            : string.Join(Environment.NewLine, state.Logs.Reverse());
    }

    private void RenderServerList(VpnUiState state)
    {
        var query = TxtSearch.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? state.Servers
            : state.Servers.Where(s =>
                s.Ip.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.HostName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Protocol.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        TxtListAvail.Text = $"日本免費節點 ({filtered.Count})";
        TxtListExcluded.Text = $"已排除 219 開頭 ({state.Excluded219Count})";

        ServerListPanel.Children.Clear();
        if (filtered.Count == 0)
        {
            ServerListPanel.Children.Add(new TextBlock
            {
                Text = state.IsRefreshing ? "正在抓取日本 VPN 節點..." : "找不到符合的日本節點，請點擊右上角重新整理",
                Foreground = (Brush)FindResource("TextMutedBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 36, 0, 0)
            });
            return;
        }

        foreach (var server in filtered)
        {
            bool isCurrent = state.ActiveServer?.Ip == server.Ip && state.Phase != ConnectionPhase.Disconnected;
            bool isConnected = isCurrent && state.Phase == ConnectionPhase.Connected;
            bool isFailed = state.FailedIps.Contains(server.Ip);

            var card = new Border
            {
                Background = (Brush)FindResource("SurfaceBrush"),
                BorderBrush = isConnected
                    ? new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#16A34A"))
                    : isCurrent
                        ? new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#C62828"))
                        : (Brush)FindResource("BorderBrushColor"),
                BorderThickness = new Thickness(isCurrent ? 1.5 : 1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var topRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            topRow.Children.Add(new TextBlock
            {
                Text = "Japan",
                FontSize = 14.5,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            topRow.Children.Add(CreateBadge($"{server.Protocol.ToUpperInvariant()}:{server.Port}",
                (Brush)FindResource("SurfaceAltBrush"),
                (Brush)FindResource("TextMutedBrush")));

            if (isConnected)
            {
                topRow.Children.Add(CreateBadge("已連線",
                    new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#16A34A")),
                    System.Windows.Media.Brushes.White));
            }
            else if (isFailed)
            {
                topRow.Children.Add(CreateBadge("近期失敗",
                    (Brush)FindResource("CrimsonSoftBrush"),
                    new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#C62828"))));
            }

            left.Children.Add(topRow);
            left.Children.Add(new TextBlock
            {
                Text = server.Ip,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 3, 0, 2)
            });
            left.Children.Add(new TextBlock
            {
                Text = $"速度 {server.FormattedSpeed()} • 延遲 {server.FormattedPing()} • 連線人數 {server.Sessions}",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("TextMutedBrush")
            });

            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var signal = BuildSignalBars(server.SignalBars);
            Grid.SetColumn(signal, 1);
            grid.Children.Add(signal);

            card.Child = grid;
            card.MouseLeftButtonUp += (_, _) =>
            {
                ServersView.Visibility = Visibility.Collapsed;
                MainView.Visibility = Visibility.Visible;
                _vpnManager.ConnectToServer(server);
            };
            ServerListPanel.Children.Add(card);
        }
    }

    private static Border CreateBadge(string text, Brush bg, Brush fg) => new()
    {
        Background = bg,
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(6, 2, 6, 2),
        Margin = new Thickness(6, 0, 0, 0),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg
        }
    };

    private StackPanel BuildSignalBars(int bars)
    {
        var colorHex = bars >= 4 ? "#22C55E" : bars >= 2 ? "#F59E0B" : "#EF4444";
        var activeBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(colorHex));
        var inactiveBrush = (Brush)FindResource("BorderBrushColor");

        var panel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        for (int i = 0; i < 5; i++)
        {
            panel.Children.Add(new Border
            {
                Width = 4,
                Height = 6 + i * 3.5,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(i == 0 ? 0 : 2.5, 0, 0, 0),
                Background = i < bars ? activeBrush : inactiveBrush
            });
        }
        return panel;
    }

    private void ApplyTheme(bool dark)
    {
        _isDarkTheme = dark;
        if (dark)
        {
            SetBrush("BgBrush", "#0F1115");
            SetBrush("SurfaceBrush", "#1A1D24");
            SetBrush("SurfaceAltBrush", "#252A34");
            SetBrush("TextBrush", "#F3F4F6");
            SetBrush("TextMutedBrush", "#9CA3AF");
            SetBrush("BorderBrushColor", "#2E3440");
            SetBrush("CrimsonSoftBrush", "#3A1D20");
            SetBrush("GreenSoftBrush", "#153222");
            SetBrush("AmberSoftBrush", "#3B2214");
            TxtThemeGlyph.Text = "\uE706";
        }
        else
        {
            SetBrush("BgBrush", "#F8F9FA");
            SetBrush("SurfaceBrush", "#FFFFFF");
            SetBrush("SurfaceAltBrush", "#F1F3F5");
            SetBrush("TextBrush", "#111827");
            SetBrush("TextMutedBrush", "#6B7280");
            SetBrush("BorderBrushColor", "#E5E7EB");
            SetBrush("CrimsonSoftBrush", "#FFE4E6");
            SetBrush("GreenSoftBrush", "#DCFCE7");
            SetBrush("AmberSoftBrush", "#FFEDD5");
            TxtThemeGlyph.Text = "\uE708";
        }
    }

    private void SetBrush(string key, string hex)
    {
        Resources[key] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(hex));
    }

    private static string FormatDuration(long totalSeconds)
    {
        var h = totalSeconds / 3600;
        var m = (totalSeconds % 3600) / 60;
        var s = totalSeconds % 60;
        return $"{h:00}:{m:00}:{s:00}";
    }

    private static string FormatSpeed(long bps) => bps switch
    {
        >= 1_048_576 => $"{bps / 1_048_576.0:0.0} MB/s",
        >= 1_024 => $"{bps / 1_024.0:0.0} KB/s",
        _ => $"{bps} B/s"
    };

    private async void BtnPower_Click(object sender, RoutedEventArgs e)
    {
        if (_vpnManager.CurrentState.Phase == ConnectionPhase.Disconnected)
        {
            _vpnManager.ConnectAuto();
        }
        else
        {
            await _vpnManager.DisconnectAsync();
        }
    }

    private void BtnSwitchNext_Click(object sender, RoutedEventArgs e) => _vpnManager.SwitchToNextServer();

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await _vpnManager.RefreshServersAsync(silent: false);

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(!_isDarkTheme);
        RenderState(_vpnManager.CurrentState);
    }

    private void BtnOpenServers_Click(object sender, RoutedEventArgs e)
    {
        MainView.Visibility = Visibility.Collapsed;
        ServersView.Visibility = Visibility.Visible;
    }

    private void BtnBackToMain_Click(object sender, RoutedEventArgs e)
    {
        ServersView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => RenderServerList(_vpnManager.CurrentState);

    private void ChkOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressOptionEvents) return;
        if (sender == ChkFailover) _vpnManager.SetAutoFailover(ChkFailover.IsChecked == true);
        else if (sender == ChkAutoRefresh) _vpnManager.SetAutoRefresh(ChkAutoRefresh.IsChecked == true);
        else if (sender == ChkHealth) _vpnManager.SetHealthCheckEnabled(ChkHealth.IsChecked == true);
    }

    private async void BtnInstallOpenVpn_Click(object sender, RoutedEventArgs e)
    {
        BtnInstallOpenVpn.IsEnabled = false;
        BtnInstallOpenVpn.Content = "正在安裝中...";
        var (ok, msg) = await OpenVpnLocator.InstallOpenVpnViaWingetAsync();
        BtnInstallOpenVpn.IsEnabled = true;
        BtnInstallOpenVpn.Content = "一鍵安裝 OpenVPN";
        _vpnManager.RecheckOpenVpnPath();
        WpfMessageBox.Show(this, msg, ok ? "安裝成功" : "安裝提示", MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void BtnManualOpenVpn_Click(object sender, RoutedEventArgs e) => OpenVpnLocator.OpenManualDownloadPage();

    private void BtnRecheckOpenVpn_Click(object sender, RoutedEventArgs e) => _vpnManager.RecheckOpenVpnPath();

    private void BtnLogs_Click(object sender, RoutedEventArgs e) => LogsOverlay.Visibility = Visibility.Visible;

    private void BtnCloseLogs_Click(object sender, RoutedEventArgs e) => LogsOverlay.Visibility = Visibility.Collapsed;

    private void BtnClearLogs_Click(object sender, RoutedEventArgs e) => _vpnManager.ClearLogs();
}
