using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GalguWatch.Core;

namespace GalguWatch.Overlay;

public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static readonly Brush BgStopped = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x29, 0x3B));
    private static readonly Brush BgRunning = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x53, 0x2D));
    private static readonly Brush BgPaused = new SolidColorBrush(Color.FromArgb(0xE6, 0x78, 0x35, 0x0F));

    private readonly TimerEngine _engine;
    private readonly ScreenshotService _shots;
    private readonly AppSettings _settings;

    public OverlayWindow(TimerEngine engine, ScreenshotService shots, AppSettings settings)
    {
        _engine = engine;
        _shots = shots;
        _settings = settings;
        InitializeComponent();
        Opacity = Math.Clamp(_settings.OverlayOpacity, 0.3, 1.0);
        _engine.Changed += UpdateUi;
        Loaded += (s, e) => { PlaceWindow(); UpdateUi(); };
        // ⏹ 버튼 표시 등으로 폭이 변해도 화면 밖으로 밀리지 않게
        SizeChanged += (s, e) => { if (IsLoaded) ClampToScreen(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // NOACTIVATE: 클릭해도 ZBrush 등 작업 중인 앱의 포커스를 뺏지 않음. TOOLWINDOW: Alt-Tab에서 숨김
        var h = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    private void PlaceWindow()
    {
        double? x = _settings.OverlayX, y = _settings.OverlayY;
        if (x != null && y != null)
        {
            Left = x.Value;
            Top = y.Value;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 24;
            Top = wa.Top + 24;
        }
        ClampToScreen();
    }

    private void ClampToScreen()
    {
        var l = SystemParameters.VirtualScreenLeft;
        var t = SystemParameters.VirtualScreenTop;
        var r = l + SystemParameters.VirtualScreenWidth;
        var b = t + SystemParameters.VirtualScreenHeight;
        Left = Math.Max(l, Math.Min(Left, r - Math.Max(ActualWidth, 40)));
        Top = Math.Max(t, Math.Min(Top, b - Math.Max(ActualHeight, 20)));
    }

    private void UpdateUi()
    {
        bool running = _engine.State == TimerState.Running;
        bool paused = !running && _engine.WorkBlockOpen;
        BtnToggle.Content = running ? "⏸" : "▶";
        BtnStop.Visibility = _engine.WorkBlockOpen ? Visibility.Visible : Visibility.Collapsed;
        Root.Background = running ? BgRunning : paused ? BgPaused : BgStopped;
        TxtTime.Text = running
            ? Fmt.Hms(_engine.CurrentElapsed)
            : $"오늘 {Fmt.Hm(_engine.TodayTotalSec)}";
        Root.ToolTip = $"오늘 누적 {Fmt.Hm(_engine.TodayTotalSec)}"
            + (running ? " · 측정 중" : paused ? " · 일시정지" : "");
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { return; }
        ClampToScreen();
        _settings.SetOverlayPos(Left, Top);
    }

    /// <summary>설정 변경 시 투명도 즉시 반영</summary>
    public void RefreshFromSettings()
    {
        Opacity = Math.Clamp(_settings.OverlayOpacity, 0.3, 1.0);
    }

    private void BtnToggle_Click(object sender, RoutedEventArgs e) => _engine.Toggle();

    private void BtnStop_Click(object sender, RoutedEventArgs e) => _engine.Finish();

    private async void BtnShot_Click(object sender, RoutedEventArgs e)
    {
        var file = await _shots.CaptureAsync("manual");
        if (file != null)
            App.Tray.Balloon("📷 캡처 저장됨", System.IO.Path.GetFileName(file));
    }

    private async void BtnRegion_Click(object sender, RoutedEventArgs e)
        => await App.CaptureRegionWithUiAsync();

    private void OpenMain_Click(object sender, RoutedEventArgs e) => App.OpenMainWindow();

    private void HideOverlay_Click(object sender, RoutedEventArgs e) => App.Tray.HideOverlay();

    private void Exit_Click(object sender, RoutedEventArgs e) => App.ExitApp();
}
