using System.Drawing;
using GalguWatch.Core;
using WF = System.Windows.Forms;

namespace GalguWatch.Tray;

public class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly Icon _iconRunning;
    private readonly Icon _iconStopped;
    private readonly TimerEngine _engine;
    private readonly ScreenshotService _shots;
    private readonly WF.ToolStripMenuItem _miToggle;
    private readonly WF.ToolStripMenuItem _miOverlay;
    private TimerState _lastState;
    private string _lastText = "";
    private bool _balloonRestartsTimer;

    public TrayIcon(TimerEngine engine, ScreenshotService shots)
    {
        _engine = engine;
        _shots = shots;
        _iconRunning = MakeDotIcon(Color.FromArgb(34, 197, 94));
        _iconStopped = MakeDotIcon(Color.FromArgb(148, 163, 184));

        var menu = new WF.ContextMenuStrip();
        _miToggle = new WF.ToolStripMenuItem("측정 시작", null, (s, e) => _engine.Toggle());
        var miShot = new WF.ToolStripMenuItem("지금 캡처", null, async (s, e) =>
        {
            var f = await _shots.CaptureAsync("manual");
            if (f != null) Balloon("📷 캡처 저장됨", System.IO.Path.GetFileName(f));
        });
        var miOpen = new WF.ToolStripMenuItem("캘린더 열기", null, (s, e) => App.OpenMainWindow());
        _miOverlay = new WF.ToolStripMenuItem("오버레이 숨기기", null, (s, e) => ToggleOverlay());
        var miExit = new WF.ToolStripMenuItem("종료", null, (s, e) => App.ExitApp());
        menu.Items.Add(_miToggle);
        menu.Items.Add(miShot);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(miOpen);
        menu.Items.Add(_miOverlay);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(miExit);

        _icon = new WF.NotifyIcon
        {
            Icon = _iconStopped,
            Text = "Galgu Watch",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (s, e) => _engine.Toggle();
        _icon.BalloonTipClicked += (s, e) =>
        {
            if (_balloonRestartsTimer && _engine.State != TimerState.Running) _engine.Start();
            _balloonRestartsTimer = false;
        };
        _icon.BalloonTipClosed += (s, e) => _balloonRestartsTimer = false;

        _lastState = _engine.State;
        _engine.Changed += Refresh;
        Refresh();
    }

    private void ToggleOverlay()
    {
        if (App.Overlay.IsVisible) HideOverlay();
        else
        {
            App.Overlay.Show();
            _miOverlay.Text = "오버레이 숨기기";
        }
    }

    public void HideOverlay()
    {
        App.Overlay.Hide();
        _miOverlay.Text = "오버레이 표시";
    }

    private void Refresh()
    {
        bool running = _engine.State == TimerState.Running;
        if (_engine.State != _lastState)
        {
            _lastState = _engine.State;
            _icon.Icon = running ? _iconRunning : _iconStopped;
            _miToggle.Text = running ? "측정 정지" : "측정 시작";
        }
        var text = $"Galgu Watch — 오늘 {Fmt.Hm(_engine.TodayTotalSec)}" + (running ? " · 측정 중" : "");
        if (text != _lastText)
        {
            _lastText = text;
            _icon.Text = text.Length <= 63 ? text : text[..63];
        }
    }

    /// <summary>clickRestartsTimer: 알림 클릭 시 측정 재시작 (자리비움·잠금·절전 정지 후 복귀용)</summary>
    public void Balloon(string title, string body, bool clickRestartsTimer = false)
    {
        _balloonRestartsTimer = clickRestartsTimer;
        _icon.ShowBalloonTip(5000, title, body, WF.ToolTipIcon.None);
    }

    private static Icon MakeDotIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 2, 2, 12, 12);
            using var pen = new Pen(Color.FromArgb(90, 0, 0, 0));
            g.DrawEllipse(pen, 2, 2, 12, 12);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
