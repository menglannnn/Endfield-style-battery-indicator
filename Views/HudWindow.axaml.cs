using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using EndfieldCharge.Animations;
using EndfieldCharge.Services;
using EndfieldCharge.Settings;

namespace EndfieldCharge.Views;

/// <summary>完整三态动画的文案主题：充电（超充模式）或省电模式。</summary>
public enum HudPlayMode
{
    Charge,
    PowerSaver,
}

public partial class HudWindow : Window
{
    public event Action? ReShowRequested;

    private static readonly TimeSpan DismissDuration = TimeSpan.FromMilliseconds(160);
    private const double AnimationWindowWidth = 560d;
    private const double AnimationWindowHeight = 100d;
    private const double FinalPillWidth = 560d;
    private const double FinalPillHeight = 60d;

    private static readonly Color BadgeColorNormal = Color.Parse("#C6CA4C");
    private static readonly Color BadgeColorLow = Color.Parse("#FF4D4F");

    private const uint WmWindowPosChanging = 0x0046;
    private const uint SwpHideWindow = 0x0080;
    private const uint SwpNoZOrder = 0x0004;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _liveRefreshCts;
    private bool _wndHooked;
    private bool _playbackActive;
    private int _intentionalHide;
    private AppSettings _settings = new();

    public AppSettings CurrentSettings => _settings;
    private AnimationOptions _animOptions = AnimationOptions.Default;
    private int _fpsFrameCount;
    private DateTime _fpsLastMeasure = DateTime.UtcNow;
    private bool _fpsEnabled;

    public HudWindow()
    {
        InitializeComponent();

        TagLineText.Text = Localization.TagLine;
        TitleText.Text = Localization.TitleMode;

        Cursor = new Cursor(StandardCursorType.Hand);
        PointerPressed += (_, _) => _ = DismissAsync();

        _fpsEnabled = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--show-fps");
        if (_fpsEnabled)
        {
            FpsText.IsVisible = true;
            StartFpsCounter();
        }

        ResetToInitial();
        IsHitTestVisible = false;
        PropertyChanged += OnWindowPropertyChanged;
    }

    /// <summary>从设置更新 HUD 参数（缩放、动画微调、位置、显示器）。</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _animOptions = AnimationOptions.FromSettings(settings);

        // 全局缩放
        GlobalScale.RenderTransform = new ScaleTransform(settings.GlobalScale, settings.GlobalScale);

        // 更新本地化文本（可能语言变了）
        TagLineText.Text = Localization.TagLine;
        TitleText.Text = Localization.TitleMode;

        if (IsVisible)
        {
            SetAnimationWindowBounds();
            PositionTopCenter();
        }
    }

    // ---------------- 动画播放 ----------------

    public async Task ShowSimpleAsync(BatterySnapshot? battery, AnimationOptions? options = null)
    {
        ApplyBattery(battery, acOnline: false);
        StartLiveRefresh();

        var o = options ?? _animOptions;

        // 【关键修复】：确保 o 里携带最新的 KeepPersistent 设置，以防 options 是通过别处传入而丢失这个参数
        if (options == null && o.KeepPersistent != _settings.KeepHudPersistent)
        {
            o = o with { KeepPersistent = _settings.KeepHudPersistent };
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _playbackActive = true;

        ResetToInitial();
        SetSimpleCState();
        SetAnimationWindowBounds();

        IsHitTestVisible = true;
        ShowPositioned();

        try
        {
            await Task.WhenAll(
                HudAnimations.SimplePillAppear(o).RunAsync(Pill, ct),
                HudAnimations.SimpleFadeIn(o).RunAsync(BoltIcon, ct),
                HudAnimations.SimpleFadeIn(o).RunAsync(NumHost, ct),
                HudAnimations.SimpleScaleOut(o).RunAsync(ScaleHost, ct));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_settings.KeepHudPersistent)
        {
            // 常驻模式保持动画结束时的窗口尺寸，避免结束阶段再次调整窗口边界造成闪动。
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!ct.IsCancellationRequested && IsVisible)
        {
            IsHitTestVisible = false;
            StopLiveRefresh();
            HideFromApp();
        }
    }

    // ---------------- 完整充电动画 ----------------

    public async Task ShowAndPlayAsync(
        BatterySnapshot? battery,
        bool acOnline,
        HudPlayMode mode = HudPlayMode.Charge,
        AnimationOptions? options = null)
    {
        ApplyBattery(battery, acOnline);
        StartLiveRefresh();

        // 文案主题：充电 = 超充模式；省电 = 省电模式
        TagLineText.Text = mode == HudPlayMode.PowerSaver ? Localization.TagLineSaver : Localization.TagLine;
        TitleText.Text = mode == HudPlayMode.PowerSaver ? Localization.TitleSaver : Localization.TitleMode;

        var o = options ?? _animOptions;

        // 【关键修复】：确保 o 里携带最新的 KeepPersistent 设置，以防 options 是通过别处传入而丢失这个参数
        if (options == null && o.KeepPersistent != _settings.KeepHudPersistent)
        {
            o = o with { KeepPersistent = _settings.KeepHudPersistent };
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _playbackActive = true;

        bool debugStatic = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--debug-ring");

        ResetToInitial();
        SetAnimationWindowBounds();
        IsHitTestVisible = true;

        ShowPositioned();

        if (debugStatic)
        {
            ShowFullyExpandedStatic();
            try { await Task.Delay(1500, ct); }
            catch (OperationCanceledException) { return; }
            if (!ct.IsCancellationRequested && IsVisible)
            {
                IsHitTestVisible = false;
                StopLiveRefresh();
                HideFromApp();
            }
            return;
        }

        try
        {
            await Task.WhenAll(
                HudAnimations.PillCorner(o).RunAsync(Pill, ct),
                HudAnimations.PillAppear(o).RunAsync(Pill, ct),
                HudAnimations.PillHeight(o).RunAsync(Pill, ct),
                HudAnimations.PillHeight(o).RunAsync(RippleHost, ct),
                HudAnimations.ScaleOut(o).RunAsync(ScaleHost, ct),
                HudAnimations.BoltIcon(o).RunAsync(BoltIcon, ct),
                HudAnimations.RippleHost(o).RunAsync(RippleHost, ct),
                HudAnimations.CircleForm(o).RunAsync(CircleForm, ct),
                HudAnimations.SquareForm(o).RunAsync(SquareForm, ct),
                HudAnimations.TitleHost(o).RunAsync(TitleHost, ct),
                HudAnimations.NumHost(o).RunAsync(NumHost, ct),
                HudAnimations.Ripple(o, 1.5, 0.50).RunAsync(RippleInner, ct),
                HudAnimations.Ripple(o, 2.0, 0.50).RunAsync(RippleMid, ct),
                HudAnimations.Ripple(o, 2.5, 0.60).RunAsync(RippleOuter, ct),
                HudAnimations.RippleRise(o).RunAsync(RippleInnerHost, ct),
                HudAnimations.RippleRise(o).RunAsync(RippleMidHost, ct),
                HudAnimations.RippleRise(o).RunAsync(RippleOuterHost, ct));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_settings.KeepHudPersistent)
        {
            // 常驻模式保持动画结束时的窗口状态，避免结束阶段调整窗口边界造成闪动。
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!ct.IsCancellationRequested && IsVisible)
        {
            IsHitTestVisible = false;
            HideFromApp();
        }
    }

    private bool _isDismissing;

    private async Task DismissAsync()
    {
        if (_isDismissing) return;
        _isDismissing = true;

        try
        {
            _cts?.Cancel();

            var fade = new Animation
            {
                Duration = DismissDuration,
                FillMode = FillMode.Forward,
                Easing = new QuadraticEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 1d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 0d) } },
                },
            };

            await fade.RunAsync(Root);
            IsHitTestVisible = false;
            StopLiveRefresh();
            HideFromApp();

            if (_settings.KeepHudPersistent)
            {
                await Task.Delay(1000);
                ReShowRequested?.Invoke();
            }
        }
        finally
        {
            _isDismissing = false;
        }
    }

    private void StartLiveRefresh()
    {
        StopLiveRefresh();
        _liveRefreshCts = new CancellationTokenSource();
        _ = RefreshHardwareAsync(_liveRefreshCts.Token);
    }

    private void StopLiveRefresh()
    {
        _liveRefreshCts?.Cancel();
        _liveRefreshCts?.Dispose();
        _liveRefreshCts = null;
    }

    private async Task RefreshHardwareAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                if (ct.IsCancellationRequested || !IsVisible)
                    continue;

                var mode = _settings.DeviceMode;
                BatterySnapshot? snapshot;
                try
                {
                    snapshot = await Task.Run(() => BatteryService.GetSnapshot(mode), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warn($"HUD refresh failed: {ex.Message}");
                    continue;
                }

                if (ct.IsCancellationRequested || snapshot is null)
                    continue;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ct.IsCancellationRequested && IsVisible)
                        ApplyBattery(snapshot, snapshot.AcOnline);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---------------- 数据绑定 ----------------

    private const double RingDiameter = 46d;
    private const double RingThickness = 4.5d;

    private void ApplyBattery(BatterySnapshot? snap, bool acOnline)
    {
        if (_settings.DeviceMode == DeviceMode.Desktop)
            ApplyDesktopPower(snap);
        else
            ApplyLaptopBattery(snap);
    }

    /// <summary>笔记本：剩余 / 满充电量（mWh）和百分比，低电量变红。</summary>
    private void ApplyLaptopBattery(BatterySnapshot? snap)
    {
        double fraction = 0d;
        var badgeColor = BadgeColorNormal;

        if (snap is null || !snap.HasBattery)
        {
            WhValueText.Text = "--";
            WhMaxText.Text = string.Empty;
            PercentText.Text = "--";
            PercentUnitText.Text = "%";
        }
        else
        {
            WhValueText.Text = (snap.RemainingWh * 1000).ToString("F0");
            WhMaxText.Text = $"/{snap.FullWh * 1000:F0}";
            PercentText.Text = snap.Percent.ToString();
            PercentUnitText.Text = "%";
            fraction = Math.Clamp(snap.Percent / 100d, 0d, 1d);
            if (snap.Percent < 20)
                badgeColor = BadgeColorLow;
        }

        ApplyBadge(fraction, badgeColor);
    }

    /// <summary>台式机：整机功耗 / 额定功率。右侧圆环按功率占额定功率的比例绘制，超过额定功率变红。</summary>
    private void ApplyDesktopPower(BatterySnapshot? snap)
    {
        double ratedPower = Math.Clamp(_settings.RatedPowerWatts, 50, 1500);
        double? watts = snap?.PowerWatts;
        bool insufficient = watts is double power && power > ratedPower;
        WhValueText.Text = watts?.ToString("F1") ?? "--";
        WhMaxText.Text = $"W / {ratedPower:F0}W";
        PercentText.Text = insufficient
            ? Localization.PowerInsufficient
            : snap?.CpuUsagePercent?.ToString("F0") ?? "--";
        PercentUnitText.Text = insufficient ? string.Empty : "%";
        double fraction = watts is double drawn
            ? Math.Clamp(drawn / ratedPower, 0d, 1d)
            : 0d;
        ApplyBadge(fraction, insufficient ? BadgeColorLow : BadgeColorNormal);
    }

    /// <summary>立刻按当前设备模式重读一次数据并更新胶囊。供设置保存后调用。</summary>
    public void RefreshDisplayedData()
    {
        if (!IsVisible)
            return;

        var mode = _settings.DeviceMode;
        _ = RefreshOnceAsync(mode);
    }

    private async Task RefreshOnceAsync(DeviceMode mode)
    {
        try
        {
            var snapshot = await Task.Run(() => BatteryService.GetSnapshot(mode));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsVisible && _settings.DeviceMode == mode)
                    ApplyBattery(snapshot, snapshot?.AcOnline ?? false);
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"HUD refresh failed: {ex.Message}");
        }
    }

    private void ApplyBadge(double fraction, Color badgeColor)
    {
        BadgeArc.Data = BuildRingGeometry(fraction, RingDiameter, RingThickness);
        var brush = new SolidColorBrush(badgeColor);
        BadgeArc.Stroke = brush;
        LaptopScreen.BorderBrush = brush;
        LaptopBase.Background = brush;
        BadgeElectrode.Background = brush;
    }

    private static Geometry BuildRingGeometry(double fraction, double diameter, double thickness)
    {
        double radius = (diameter - thickness) / 2d;
        var center = new Point(diameter / 2d, diameter / 2d);

        double sweep = 360d * Math.Clamp(fraction, 0d, 1d);
        if (sweep < 0.5d) sweep = 0.5d;
        if (sweep > 359.5d) sweep = 359.5d;

        const double startAngle = -90d;
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweep);

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments = new PathSegments
        {
            new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                RotationAngle = 0d,
                IsLargeArc = sweep > 180d,
                SweepDirection = SweepDirection.Clockwise,
            },
        };

        return new PathGeometry { Figures = new PathFigures { figure } };
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    // ---------------- FPS 计数器 ----------------

    private void StartFpsCounter()
    {
        DispatcherTimer.Run(() =>
        {
            if (!IsVisible)
            {
                _fpsFrameCount = 0;
                _fpsLastMeasure = DateTime.UtcNow;
                return true;
            }

            _fpsFrameCount++;

            var now = DateTime.UtcNow;
            var elapsed = (now - _fpsLastMeasure).TotalSeconds;
            if (elapsed >= 1.0)
            {
                double fps = _fpsFrameCount / elapsed;
                FpsText.Text = $"{fps:F0} FPS";
                _fpsFrameCount = 0;
                _fpsLastMeasure = now;
            }

            return true;
        }, TimeSpan.FromMilliseconds(200));
    }

    // ---------------- 动画复位 ----------------

    private void ResetToInitial()
    {
        Root.Opacity = 1;

        ScaleHost.RenderTransform = new ScaleTransform(1d, 1d);

        Pill.Width = 560;
        Pill.Height = 60;
        Pill.CornerRadius = new CornerRadius(30d);
        Pill.Opacity = 0;
        Pill.RenderTransform = new ScaleTransform(0.6d, 0.6d);

        RippleHost.RenderTransform = new TranslateTransform(0d, 0d);

        BoltIcon.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(0.4d, 0.4d), new TranslateTransform(0d, 0d) },
        };
        BoltIcon.Opacity = 0;

        RippleInnerHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleMidHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleOuterHost.RenderTransform = new TranslateTransform(0d, 16d);

        RippleInner.RenderTransform = new ScaleTransform(0d, 0d);
        RippleInner.Opacity = 0;
        RippleMid.RenderTransform = new ScaleTransform(0d, 0d);
        RippleMid.Opacity = 0;
        RippleOuter.RenderTransform = new ScaleTransform(0d, 0d);
        RippleOuter.Opacity = 0;

        CircleForm.Opacity = 0;
        SquareForm.Opacity = 0;

        TitleHost.RenderTransform = new TranslateTransform(0d, 0d);
        TitleHost.Opacity = 0;

        NumHost.RenderTransform = new TranslateTransform(0d, 0d);
        NumHost.Opacity = 0;
    }

    private void ShowFullyExpandedStatic()
    {
        ScaleHost.RenderTransform = new ScaleTransform(1d, 1d);
        Pill.Width = 560;
        Pill.Height = 60;
        Pill.CornerRadius = new CornerRadius(30d);
        Pill.Opacity = 1;
        Pill.RenderTransform = new ScaleTransform(1d, 1d);

        RippleHost.RenderTransform = new TranslateTransform(-245d, 0d);

        BoltIcon.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(1d, 1d), new TranslateTransform(-245d, 0d) },
        };
        BoltIcon.Opacity = 1;
        CircleForm.Opacity = 0;
        SquareForm.Opacity = 1;

        TitleHost.Opacity = 0;

        NumHost.RenderTransform = new TranslateTransform(0d, 0d);
        NumHost.Opacity = 1;
    }

    private void SetSimpleCState()
    {
        Pill.Width = 560;
        Pill.Height = 60;
        Pill.CornerRadius = new CornerRadius(30d);
        Pill.Opacity = 0;
        Pill.RenderTransform = new ScaleTransform(0.6d, 0.6d);

        BoltIcon.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(1d, 1d), new TranslateTransform(-245d, 0d) },
        };
        BoltIcon.Opacity = 0;
        CircleForm.Opacity = 0;
        SquareForm.Opacity = 1;

        TitleHost.Opacity = 0;
        RippleHost.Height = 60;
        RippleHost.RenderTransform = new TranslateTransform(0d, 0d);
        RippleInnerHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleMidHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleOuterHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleInner.Opacity = 0;
        RippleMid.Opacity = 0;
        RippleOuter.Opacity = 0;

        NumHost.RenderTransform = new TranslateTransform(0d, 0d);
        NumHost.Opacity = 0;
    }

    // ---------------- 定位（多显示器 + 位置选择） ----------------

    private void PositionTopCenter()
    {
        var screen = ResolveScreen(_settings.MonitorIndex);
        if (screen is null) return;

        var area = screen.WorkingArea;
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        int pixelWidth = (int)Math.Round(Width * scaling);
        int pixelHeight = (int)Math.Round(Height * scaling);

        int x;
        int y;
        switch (_settings.HudPosition)
        {
            case HudPosition.TopLeft:
                x = area.X + 10;
                y = area.Y + 4;
                break;
            case HudPosition.TopRight:
                x = area.X + area.Width - pixelWidth - 10;
                y = area.Y + 4;
                break;
            case HudPosition.Custom:
                x = area.X + (int)Math.Round(area.Width * Math.Clamp(_settings.CustomPositionX, 0d, 1d) - pixelWidth / 2d);
                y = area.Y + (int)Math.Round(area.Height * Math.Clamp(_settings.CustomPositionY, 0d, 1d) - pixelHeight / 2d);
                break;
            default:
                x = area.X + (area.Width - pixelWidth) / 2;
                y = area.Y + 4;
                break;
        }

        Position = new PixelPoint(x, y);
    }

    /// <summary>
    /// 解析目标显示器：-1 = 主显示器（默认），0..N-1 = 显示器列表索引，越界退回主显示器。
    /// </summary>
    private Avalonia.Platform.Screen? ResolveScreen(int monitorIndex)
    {
        var screens = Screens.All;
        var primary = Screens.Primary;

        if (monitorIndex < 0)
            return primary ?? screens.FirstOrDefault();

        if (monitorIndex < screens.Count)
            return screens[monitorIndex];

        return primary ?? screens.FirstOrDefault();
    }

    private void SetAnimationWindowBounds()
    {
        double scale = _settings.GlobalScale;
        Width = AnimationWindowWidth * scale;
        Height = AnimationWindowHeight * scale;
        Root.Width = AnimationWindowWidth;
        Root.Height = AnimationWindowHeight;
    }

    private void SetFinalPillWindowBounds()
    {
        double scale = _settings.GlobalScale;
        double oldWidth = Width;
        double oldHeight = Height;
        double newWidth = FinalPillWidth * scale;
        double newHeight = FinalPillHeight * scale;
        double pixelScale = GetScreenScaling();

        // 保持窗口中心不变，只因尺寸收缩补偿半个差值，避免胶囊突然跳动。
        int xCorrection = (int)Math.Round((oldWidth - newWidth) * pixelScale / 2d);
        int yCorrection = (int)Math.Round((oldHeight - newHeight) * pixelScale / 2d);

        Width = newWidth;
        Height = newHeight;
        Root.Width = FinalPillWidth;
        Root.Height = FinalPillHeight;
        Position = new PixelPoint(Position.X + xCorrection, Position.Y + yCorrection);
    }

    private double GetScreenScaling()
    {
        var screen = Screens.ScreenFromPoint(Position);
        return screen?.Scaling > 0 ? screen.Scaling : 1d;
    }

    private void ShowPositioned()
    {
        PositionTopCenter();

        if (!IsVisible)
            Show();

        PositionTopCenter();
        EnsureWndHook();
    }

    /// <summary>
    /// 展开任务栏隐藏图标（倒三角）时，Windows 会给置顶分层窗口发 SWP_HIDEWINDOW，
    /// 或把它从 TOPMOST 降下去。播放中的 HUD 拦下这次变动；主动 Hide 时放行。
    /// </summary>
    private void EnsureWndHook()
    {
        if (_wndHooked)
            return;

        _wndHooked = true;
        Win32Properties.AddWndProcHookCallback(this, PreventOverflowHide);
    }

    private IntPtr PreventOverflowHide(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmWindowPosChanging || lParam == IntPtr.Zero || _intentionalHide != 0 || !IsVisible)
            return IntPtr.Zero;

        var pos = Marshal.PtrToStructure<NativeWindowPos>(lParam);
        var changed = false;
        if ((pos.Flags & SwpHideWindow) != 0)
        {
            pos.Flags &= ~SwpHideWindow;
            changed = true;
        }

        if (pos.HwndInsertAfter == new IntPtr(-2) && (pos.Flags & SwpNoZOrder) == 0)
        {
            pos.HwndInsertAfter = new IntPtr(-1);
            changed = true;
        }

        if (changed)
            Marshal.StructureToPtr(pos, lParam, false);

        return IntPtr.Zero;
    }

    private void HideFromApp()
    {
        _playbackActive = false;
        _intentionalHide++;
        try
        {
            Hide();
        }
        finally
        {
            _intentionalHide--;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || IsVisible || _intentionalHide != 0 || _isDismissing || !_playbackActive)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_intentionalHide != 0 || _isDismissing || !_playbackActive || IsVisible)
                return;

            IsHitTestVisible = true;
            ShowPositioned();
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }
}