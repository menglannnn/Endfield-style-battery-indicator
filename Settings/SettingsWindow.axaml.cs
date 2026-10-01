using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EndfieldCharge.Animations;
using EndfieldCharge.Services;
using EndfieldCharge.Views;

namespace EndfieldCharge.Settings;

public partial class SettingsWindow : Window
{
    private readonly HudWindow _hud;
    private readonly StackPanel _customPositionPanel;
    private readonly TextBlock _labelCustomX;
    private readonly TextBlock _labelCustomY;
    private readonly TextBlock _customXValue;
    private readonly TextBlock _customYValue;
    private readonly Slider _customXSlider;
    private readonly Slider _customYSlider;
    private readonly TextBox _ratedPowerTextBox;
    private readonly ComboBox _deviceModeCombo;
    private readonly StackPanel _ratedPowerPanel;
    private readonly StackPanel _laptopAlertsPanel;
    private readonly TextBlock _desktopNotifyHint;
    private readonly TextBlock _deviceModeDescText;
    private AppSettings _clean = new();
    private bool _trackDirty;
    private static readonly IBrush SaveDirtyBackground = new SolidColorBrush(Color.Parse("#C6CA4C"));
    private static readonly IBrush SaveDirtyForeground = new SolidColorBrush(Color.Parse("#1A1A1C"));
    private static readonly IBrush SaveCleanBackground = new SolidColorBrush(Color.Parse("#2A2A2D"));
    private static readonly IBrush SaveCleanForeground = new SolidColorBrush(Color.Parse("#6A6A6E"));

    public SettingsWindow(AppSettings settings, HudWindow hud, string initialTab = "General")
    {
        InitializeComponent();

        _hud = hud;
        _customPositionPanel = this.FindControl<StackPanel>("CustomPositionPanel")!;
        _labelCustomX = this.FindControl<TextBlock>("LabelCustomX")!;
        _labelCustomY = this.FindControl<TextBlock>("LabelCustomY")!;
        _customXValue = this.FindControl<TextBlock>("CustomXValue")!;
        _customYValue = this.FindControl<TextBlock>("CustomYValue")!;
        _customXSlider = this.FindControl<Slider>("CustomXSlider")!;
        _customYSlider = this.FindControl<Slider>("CustomYSlider")!;
        _ratedPowerTextBox = this.FindControl<TextBox>("RatedPowerTextBox")!;
        _deviceModeCombo = this.FindControl<ComboBox>("DeviceModeCombo")!;
        _ratedPowerPanel = this.FindControl<StackPanel>("RatedPowerPanel")!;
        _laptopAlertsPanel = this.FindControl<StackPanel>("LaptopAlertsPanel")!;
        _desktopNotifyHint = this.FindControl<TextBlock>("DesktopNotifyHint")!;
        _deviceModeDescText = this.FindControl<TextBlock>("DeviceModeDescText")!;

        // 窗口图标
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://EndfieldCharge/Assets/tray_bolt.png"));
            Icon = new WindowIcon(new Bitmap(stream));
        }
        catch { }

        Title = Localization.SettingsTitle;
        InitLanguageCombo();
        InitPositionCombo();
        InitPreviewModeCombo();
        InitDeviceModeCombo();
        ApplyLocalization();

        PopulateMonitors();
        LoadSettings(settings);

        // Tab 切换
        TabGeneralBtn.PointerPressed += (_, _) => SwitchTab(TabGeneralBtn, GeneralPanel);
        TabAnimationBtn.PointerPressed += (_, _) => SwitchTab(TabAnimationBtn, AnimationPanel);
        TabNotificationsBtn.PointerPressed += (_, _) => SwitchTab(TabNotificationsBtn, NotificationsPanel);
        TabAboutBtn.PointerPressed += (_, _) => SwitchTab(TabAboutBtn, AboutPanel);

        // 滑块值同步
        ScaleSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                ScaleValue.Text = ScaleSlider.Value.ToString("F2");
        };
        DurationSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                DurationValue.Text = $"{DurationSlider.Value:F1}s";
        };
        BounceSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                BounceValue.Text = BounceSlider.Value.ToString("F3");
        };
        RippleIntensitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                RippleIntensityValue.Text = RippleIntensitySlider.Value.ToString("F2");
        };
        RippleSpreadSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                RippleSpreadValue.Text = RippleSpreadSlider.Value.ToString("F2");
        };
        LowBatterySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                LowBatteryValue.Text = $"{LowBatterySlider.Value:F0}%";
        };
        _customXSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                _customXValue.Text = $"{_customXSlider.Value:F0}%";
        };
        _customYSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                _customYValue.Text = $"{_customYSlider.Value:F0}%";
        };
        PositionCombo.SelectionChanged += (_, _) => UpdateCustomPositionVisibility();
        _deviceModeCombo.SelectionChanged += (_, _) => UpdateDeviceModeVisibility();

        // 低电量开关联动
        LowBatterySwitch.IsCheckedChanged += (_, _) =>
        {
            LowBatterySlider.IsEnabled = LowBatterySwitch.IsChecked == true;
        };

        // 事件
        SaveBtn.Click += OnSave;
        CheckUpdateBtn.Click += OnCheckUpdate;
        PreviewPlayBtn.Click += OnPlayPreview;
        FontInstallBtn.Click += OnInstallFont;

        // 默认 Tab
        var (tab, panel) = initialTab switch
        {
            "Animation" => (TabAnimationBtn, AnimationPanel),
            "Notifications" => (TabNotificationsBtn, NotificationsPanel),
            "About" => (TabAboutBtn, AboutPanel),
            _ => (TabGeneralBtn, GeneralPanel),
        };
        SwitchTab(tab, panel);
        HookDirtyTracking();
        BeginDirtyTracking();
    }

    private void HookDirtyTracking()
    {
        void OnSlider(object? _, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == RangeBase.ValueProperty)
                UpdateSaveButton();
        }

        ScaleSlider.PropertyChanged += OnSlider;
        DurationSlider.PropertyChanged += OnSlider;
        BounceSlider.PropertyChanged += OnSlider;
        RippleIntensitySlider.PropertyChanged += OnSlider;
        RippleSpreadSlider.PropertyChanged += OnSlider;
        LowBatterySlider.PropertyChanged += OnSlider;
        _customXSlider.PropertyChanged += OnSlider;
        _customYSlider.PropertyChanged += OnSlider;

        PositionCombo.SelectionChanged += (_, _) => UpdateSaveButton();
        MonitorCombo.SelectionChanged += (_, _) => UpdateSaveButton();
        LanguageCombo.SelectionChanged += (_, _) => UpdateSaveButton();
        _deviceModeCombo.SelectionChanged += (_, _) => UpdateSaveButton();

        _ratedPowerTextBox.TextChanged += (_, _) => UpdateSaveButton();
        PowerSaverSwitch.IsCheckedChanged += (_, _) => UpdateSaveButton();
        LowBatterySwitch.IsCheckedChanged += (_, _) => UpdateSaveButton();
        FullChargeSwitch.IsCheckedChanged += (_, _) => UpdateSaveButton();
        AutoStartSwitch.IsCheckedChanged += (_, _) => UpdateSaveButton();
        KeepPersistentSwitch.IsCheckedChanged += (_, _) => UpdateSaveButton();
    }

    private void BeginDirtyTracking()
    {
        _clean = CollectSettings();
        _trackDirty = true;
        UpdateSaveButton();
    }

    private void UpdateSaveButton()
    {
        if (!_trackDirty)
            return;

        bool dirty = CollectSettings() != _clean;
        SaveBtn.IsEnabled = dirty;
        SaveBtn.Background = dirty ? SaveDirtyBackground : SaveCleanBackground;
        SaveBtn.Foreground = dirty ? SaveDirtyForeground : SaveCleanForeground;
    }

    // ---------------- 初始化 ComboBox 项 ----------------

    private void InitLanguageCombo()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Tag = "auto" });
        LanguageCombo.Items.Add(new ComboBoxItem { Tag = "zh" });
        LanguageCombo.Items.Add(new ComboBoxItem { Tag = "en" });
    }

    private void InitPositionCombo()
    {
        PositionCombo.Items.Clear();
        PositionCombo.Items.Add(new ComboBoxItem { Tag = "TopCenter" });
        PositionCombo.Items.Add(new ComboBoxItem { Tag = "TopRight" });
        PositionCombo.Items.Add(new ComboBoxItem { Tag = "TopLeft" });
        PositionCombo.Items.Add(new ComboBoxItem { Tag = "Custom" });
    }

    private void InitPreviewModeCombo()
    {
        PreviewModeCombo.Items.Clear();
        PreviewModeCombo.Items.Add(new ComboBoxItem { Tag = "plug" });
        PreviewModeCombo.Items.Add(new ComboBoxItem { Tag = "saver" });
        PreviewModeCombo.Items.Add(new ComboBoxItem { Tag = "unplug" });
    }

    private void InitDeviceModeCombo()
    {
        _deviceModeCombo.Items.Clear();
        _deviceModeCombo.Items.Add(new ComboBoxItem { Tag = DeviceMode.Laptop });
        _deviceModeCombo.Items.Add(new ComboBoxItem { Tag = DeviceMode.Desktop });
    }

    // ---------------- 本地化 ----------------

    private void ApplyLocalization()
    {
        WinTitle.Text = Localization.SettingsTitle;
        TabGeneralText.Text = Localization.TabGeneral;
        TabAnimationText.Text = Localization.TabAnimation;
        TabNotificationsText.Text = Localization.TabNotifications;
        TabAboutText.Text = Localization.TabAbout;
        LabelScale.Text = Localization.LabelScale;
        LabelDeviceMode.Text = Localization.LabelDeviceMode;
        _deviceModeDescText.Text = Localization.DeviceModeDesc;
        _desktopNotifyHint.Text = Localization.DesktopNotifyHint;
        LabelRatedPower.Text = Localization.LabelRatedPower;
        _ratedPowerTextBox.Watermark = Localization.RatedPowerWatermark;
        LabelDuration.Text = Localization.LabelDuration;
        LabelPosition.Text = Localization.LabelPosition;
        _labelCustomX.Text = Localization.LabelCustomX;
        _labelCustomY.Text = Localization.LabelCustomY;
        LabelMonitor.Text = Localization.LabelMonitor;
        LabelLanguage.Text = Localization.LabelLanguage;
        LabelAutoStart.Text = Localization.LabelAutoStart;
        DescAutoStartText.Text = Localization.DescAutoStart;
        LabelKeepPersistent.Text = Localization.LabelKeepPersistent;
        DescKeepPersistentText.Text = Localization.DescKeepPersistent;
        LabelLowBatteryEnable.Text = Localization.LabelLowBatteryEnable;
        DescLowBatteryAlertText.Text = Localization.DescLowBatteryAlert;
        LabelLowBattery.Text = Localization.LabelLowBattery;
        LabelFullChargeEnable.Text = Localization.LabelFullChargeEnable;
        DescFullChargeAlertText.Text = Localization.DescFullChargeAlert;
        LabelPowerSaverNotify.Text = Localization.LabelPowerSaverNotify;
        PowerSaverNotifyDesc.Text = Localization.PowerSaverNotifyDesc;
        LabelVersion.Text = Localization.LabelVersion;
        LabelAuthor.Text = Localization.LabelAuthor;
        SaveBtn.Content = Localization.BtnSave;
        CheckUpdateBtn.Content = Localization.BtnCheckUpdate;
        AboutSubtitleText.Text = Localization.AboutSubtitle;
        FontSectionTitle.Text = Localization.FontSectionTitle;
        FontDescText.Text = Localization.FontDesc;
        FontInstallBtn.Content = Localization.BtnInstallFont;

        SectionDisplayText.Text = Localization.SectionDisplay;
        SectionPositionText.Text = Localization.SectionPosition;
        SectionStartupText.Text = Localization.SectionStartup;
        SectionAlertSettingsText.Text = Localization.SectionAlertSettings;
        SectionAnimParams.Text = Localization.SectionAnimParams;
        SectionPreview.Text = Localization.SectionPreview;
        LabelBounce.Text = Localization.LabelBounce;
        LabelRippleIntensity.Text = Localization.LabelRippleIntensity;
        LabelRippleSpread.Text = Localization.LabelRippleSpread;
        LabelPlayMode.Text = Localization.LabelPlayMode;
        PreviewPlayBtn.Content = Localization.BtnPlay;

        if (LanguageCombo.Items.Count >= 3)
        {
            if (LanguageCombo.Items[0] is ComboBoxItem ci0) ci0.Content = Localization.ValueAuto;
            if (LanguageCombo.Items[1] is ComboBoxItem ci1) ci1.Content = Localization.ValueChinese;
            if (LanguageCombo.Items[2] is ComboBoxItem ci2) ci2.Content = Localization.ValueEnglish;
        }

        if (PositionCombo.Items.Count >= 4)
        {
            if (PositionCombo.Items[0] is ComboBoxItem pi0) pi0.Content = Localization.PosTopCenter;
            if (PositionCombo.Items[1] is ComboBoxItem pi1) pi1.Content = Localization.PosTopRight;
            if (PositionCombo.Items[2] is ComboBoxItem pi2) pi2.Content = Localization.PosTopLeft;
            if (PositionCombo.Items[3] is ComboBoxItem pi3) pi3.Content = Localization.PosCustom;
        }

        if (PreviewModeCombo.Items.Count >= 3)
        {
            if (PreviewModeCombo.Items[0] is ComboBoxItem mi0) mi0.Content = Localization.ModePlug;
            if (PreviewModeCombo.Items[1] is ComboBoxItem mi1) mi1.Content = Localization.ModeSaver;
            if (PreviewModeCombo.Items[2] is ComboBoxItem mi2) mi2.Content = Localization.ModeUnplug;
        }

        if (_deviceModeCombo.Items.Count >= 2)
        {
            if (_deviceModeCombo.Items[0] is ComboBoxItem di0) di0.Content = Localization.DeviceLaptop;
            if (_deviceModeCombo.Items[1] is ComboBoxItem di1) di1.Content = Localization.DeviceDesktop;
        }
    }

    // ---------------- 显示器 ----------------

    private void PopulateMonitors()
    {
        var screens = Screens.All;
        MonitorCombo.Items.Clear();

        // 第一项：主显示器（默认），MonitorIndex 存 -1
        MonitorCombo.Items.Add(new ComboBoxItem
        {
            Content = Localization.MonitorPrimaryDefault,
            Tag = -1,
        });

        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = Localization.MonitorName(i, s.IsPrimary),
                Tag = i,
            });
        }
    }

    // ---------------- 加载 / 收集 ----------------

    private void LoadSettings(AppSettings s)
    {
        ScaleSlider.Value = s.GlobalScale;
        _deviceModeCombo.SelectedIndex = s.DeviceMode == DeviceMode.Laptop ? 0 : 1;
        _ratedPowerTextBox.Text = NormalizeRatedPower(s.RatedPowerWatts).ToString();
        UpdateDeviceModeVisibility();
        DurationSlider.Value = s.DisplayDurationSeconds;
        BounceSlider.Value = s.BounceStrength;
        RippleIntensitySlider.Value = s.RippleIntensity;
        RippleSpreadSlider.Value = s.RippleSpread;
        PositionCombo.SelectedIndex = (int)s.HudPosition;
        _customXSlider.Value = Math.Clamp(s.CustomPositionX, 0d, 1d) * 100d;
        _customYSlider.Value = Math.Clamp(s.CustomPositionY, 0d, 1d) * 100d;
        // 下拉第一项是「主显示器（默认）」(-1)，物理显示器 i 对应下拉第 i+1 项
        int monitorSel = s.MonitorIndex < 0 ? 0 : s.MonitorIndex + 1;
        MonitorCombo.SelectedIndex = monitorSel is >= 0 && monitorSel < MonitorCombo.Items.Count
            ? monitorSel
            : 0;

        LanguageCombo.SelectedIndex = s.Language switch
        {
            "zh" => 1,
            "en" => 2,
            _ => 0,
        };

        PowerSaverSwitch.IsChecked = s.EnablePowerSaverNotify;
        LowBatterySwitch.IsChecked = s.EnableLowBatteryAlert;
        LowBatterySlider.Value = s.LowBatteryThreshold;
        LowBatterySlider.IsEnabled = s.EnableLowBatteryAlert;
        FullChargeSwitch.IsChecked = s.EnableFullChargeAlert;
        AutoStartSwitch.IsChecked = s.EnableAutoStart;
        KeepPersistentSwitch.IsChecked = s.KeepHudPersistent;

        VersionText.Text = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        FontStatusText.Text = string.Empty;
    }

    private AppSettings CollectSettings() => new()
    {
        GlobalScale = Math.Round(ScaleSlider.Value, 2),
        DeviceMode = SelectedDeviceMode(),
        RatedPowerWatts = ParseRatedPower(),
        DisplayDurationSeconds = Math.Round(DurationSlider.Value, 1),
        BounceStrength = Math.Round(BounceSlider.Value, 3),
        RippleIntensity = Math.Round(RippleIntensitySlider.Value, 2),
        RippleSpread = Math.Round(RippleSpreadSlider.Value, 2),
        HudPosition = (HudPosition)Math.Clamp(PositionCombo.SelectedIndex, 0, 3),
        CustomPositionX = Math.Clamp(_customXSlider.Value / 100d, 0d, 1d),
        CustomPositionY = Math.Clamp(_customYSlider.Value / 100d, 0d, 1d),
        MonitorIndex = MonitorCombo.SelectedItem is ComboBoxItem item && item.Tag is int idx
            ? idx
            : 0,
        Language = LanguageCombo.SelectedIndex switch
        {
            1 => "zh",
            2 => "en",
            _ => "auto",
        },
        EnableLowBatteryAlert = LowBatterySwitch.IsChecked == true,
        LowBatteryThreshold = (int)LowBatterySlider.Value,
        EnableFullChargeAlert = FullChargeSwitch.IsChecked == true,
        EnablePowerSaverNotify = PowerSaverSwitch.IsChecked == true,
        EnableAutoStart = AutoStartSwitch.IsChecked == true,
        KeepHudPersistent = KeepPersistentSwitch.IsChecked == true,
    };

    private int ParseRatedPower()
    {
        if (!int.TryParse(_ratedPowerTextBox.Text, out var value))
            value = 650;

        return NormalizeRatedPower(value);
    }

    private static int NormalizeRatedPower(int value)
    {
        value = Math.Clamp(value, 50, 1500);
        return Math.Clamp((int)Math.Round(value / 50d) * 50, 50, 1500);
    }

    private DeviceMode SelectedDeviceMode() =>
        _deviceModeCombo.SelectedIndex == 0 ? DeviceMode.Laptop : DeviceMode.Desktop;

    private void UpdateDeviceModeVisibility()
    {
        bool laptop = SelectedDeviceMode() == DeviceMode.Laptop;
        _ratedPowerPanel.IsVisible = !laptop;
        _laptopAlertsPanel.IsVisible = laptop;
        _desktopNotifyHint.IsVisible = !laptop;
    }

    private void UpdateCustomPositionVisibility()
    {
        _customPositionPanel.IsVisible = PositionCombo.SelectedIndex == (int)HudPosition.Custom;
    }

    // ---------------- Tab 切换 ----------------

    private void SwitchTab(Border tabBtn, StackPanel panel)
    {
        TabGeneralBtn.Background = Brushes.Transparent;
        TabAnimationBtn.Background = Brushes.Transparent;
        TabNotificationsBtn.Background = Brushes.Transparent;
        TabAboutBtn.Background = Brushes.Transparent;

        tabBtn.Background = new SolidColorBrush(Color.Parse("#2A2A2D"));

        GeneralPanel.IsVisible = panel == GeneralPanel;
        AnimationPanel.IsVisible = panel == AnimationPanel;
        NotificationsPanel.IsVisible = panel == NotificationsPanel;
        AboutPanel.IsVisible = panel == AboutPanel;
    }

    // ---------------- 动画预览 ----------------

    /// <summary>用当前滑块值（未保存也生效）实时预览动画。</summary>
    private async void OnPlayPreview(object? sender, RoutedEventArgs e)
    {
        PreviewPlayBtn.IsEnabled = false;

        // 用当前滑块值构造参数，无需保存即可预览效果
        var options = new AnimationOptions
        {
            DurationSeconds = Math.Clamp(DurationSlider.Value, 3d, 10d),
            BounceStrength = Math.Clamp(BounceSlider.Value, 0d, 0.5d),
            RippleIntensity = Math.Clamp(RippleIntensitySlider.Value, 0d, 2d),
            RippleSpread = Math.Clamp(RippleSpreadSlider.Value, 0.5d, 1.5d),
            KeepPersistent = KeepPersistentSwitch.IsChecked == true,
        };

        var draft = CollectSettings();
        var sample = draft.DeviceMode == DeviceMode.Desktop
            ? new BatterySnapshot(
                RemainingWh: 0, FullWh: 0,
                Percent: 0, AcOnline: true, Charging: false)
            {
                PowerWatts = 186.4,
                CpuUsagePercent = 37,
            }
            : new BatterySnapshot(
                RemainingWh: 62.4, FullWh: 90.0,
                Percent: 69, AcOnline: true, Charging: true);
        var previous = _hud.CurrentSettings;

        try
        {
            _hud.ApplySettings(draft);
            switch (PreviewModeCombo.SelectedIndex)
            {
                case 1:
                    await _hud.ShowAndPlayAsync(sample, acOnline: true,
                        HudPlayMode.PowerSaver, options);
                    break;
                case 2:
                    await _hud.ShowSimpleAsync(sample, options);
                    break;
                default:
                    await _hud.ShowAndPlayAsync(sample, acOnline: true,
                        HudPlayMode.Charge, options);
                    break;
            }
        }
        catch
        {
        }
        finally
        {
            _hud.ApplySettings(previous);
            PreviewPlayBtn.IsEnabled = true;
        }
    }

    // ---------------- 保存 ----------------

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var settings = CollectSettings();
        SettingsManager.Save(settings);

        // 处理开机自启
        if (settings.EnableAutoStart)
            Services.AutoStart.Enable(Services.AutoStart.CurrentExePath);
        else
            Services.AutoStart.Disable();

        _clean = settings;
        UpdateSaveButton();

        if (Application.Current is App app)
            app.OnSettingsChanged(settings);

        SavedHint.Text = Localization.SavedToast;
        SavedHint.Opacity = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            await System.Threading.Tasks.Task.Delay(2000);
            SavedHint.Opacity = 0;
        });
    }

    // ---------------- 检查更新 ----------------

    private async void OnCheckUpdate(object? sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = "...";

        try
        {
            var (hasUpdate, version, url) = await Services.UpdateChecker.CheckAsync();
            if (hasUpdate && url is not null)
            {
                var result = await MessageBox.Show(
                    this,
                    Localization.UpdateMsg(version ?? "?"),
                    Localization.UpdateTitle,
                    MessageBoxButton.OkCancel);

                if (result == MessageBoxResult.Ok)
                    Platform.Start(url);
            }
            else
            {
                UpdateStatusText.Text = Localization.UpToDate;
            }
        }
        catch
        {
            UpdateStatusText.Text = Localization.UpdateCheckFailed;
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    // ---------------- 字体安装 ----------------

    private async void OnInstallFont(object? sender, RoutedEventArgs e)
    {
        FontInstallBtn.IsEnabled = false;
        FontStatusText.Text = Localization.FontInstalling;

        // Inter 字体 GitHub Releases 下载页
        const string fontUrl = "https://github.com/rsms/inter/releases/latest";

        try
        {
            Platform.Start(fontUrl);
            await Task.Delay(500);
            FontStatusText.Text = Localization.FontInstalled;
        }
        catch
        {
            FontStatusText.Text = Localization.UpdateCheckFailed;
        }
        finally
        {
            FontInstallBtn.IsEnabled = true;
        }
    }
}

// 极简消息框辅助
public enum MessageBoxButton { Ok, OkCancel }
public enum MessageBoxResult { Ok, Cancel }

public static class MessageBox
{
    public static async Task<MessageBoxResult> Show(
        Window owner, string message, string title,
        MessageBoxButton button = MessageBoxButton.Ok)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
            Foreground = Brushes.White,
            CanResize = false,
            SystemDecorations = SystemDecorations.None,
            FontFamily = new FontFamily("HarmonyOS Sans SC, HarmonyOS Sans, Inter, Microsoft YaHei UI, sans-serif"),
        };

        var result = MessageBoxResult.Ok;
        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        stack.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        var btnPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };

        var okBtn = new Button
        {
            Content = Localization.BtnDownload,
            Width = 80, Height = 32,
            Background = new SolidColorBrush(Color.Parse("#C6CA4C")),
            Foreground = new SolidColorBrush(Color.Parse("#1E1E1E")),
            FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        okBtn.Click += (_, _) => { result = MessageBoxResult.Ok; dialog.Close(); };
        btnPanel.Children.Add(okBtn);

        if (button == MessageBoxButton.OkCancel)
        {
            var cancelBtn = new Button
            {
                Content = Localization.BtnCancel,
                Width = 80, Height = 32,
                CornerRadius = new CornerRadius(6),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            cancelBtn.Click += (_, _) => { result = MessageBoxResult.Cancel; dialog.Close(); };
            btnPanel.Children.Insert(0, cancelBtn);
        }

        stack.Children.Add(btnPanel);
        dialog.Content = stack;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        await dialog.ShowDialog(owner);
        return result;
    }
}

internal static class Platform
{
    public static void Start(string url)
    {
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            },
        };
        p.Start();
    }
}