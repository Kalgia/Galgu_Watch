using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WF = System.Windows.Forms;

namespace GalguWatch.Overlay;

/// <summary>드래그로 화면 영역을 고르는 전체 화면 선택 창.
/// 좌표는 GetCursorPos의 물리 픽셀로 계산해 DPI와 무관하게 캡처가 정확하다.</summary>
public class RegionSelectWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private readonly RectangleGeometry _full = new();
    private readonly RectangleGeometry _hole = new(new Rect(0, 0, 0, 0));
    private readonly TextBlock _sizeLabel;
    private System.Drawing.Point _startPx;
    private bool _dragging;
    private double _scale = 1.0;
    private readonly System.Drawing.Rectangle _virtualPx = WF.SystemInformation.VirtualScreen;

    public System.Drawing.Rectangle? Result { get; private set; }

    /// <summary>선택된 영역(물리 픽셀)을 돌려준다. 취소하면 null.</summary>
    public static System.Drawing.Rectangle? PickRegion()
    {
        var w = new RegionSelectWindow();
        w.ShowDialog();
        return w.Result;
    }

    private RegionSelectWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // 어두운 베일에 선택 영역만 구멍을 뚫는다 (EvenOdd)
        var veilData = new GeometryGroup { FillRule = FillRule.EvenOdd };
        veilData.Children.Add(_full);
        veilData.Children.Add(_hole);
        var veil = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x5A, 0x00, 0x00, 0x00)),
            Data = veilData,
        };
        var frame = new Path
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Data = _hole,
            IsHitTestVisible = false,
        };
        _sizeLabel = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x11, 0x18, 0x27)),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 12,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        var labelCanvas = new Canvas { IsHitTestVisible = false };
        labelCanvas.Children.Add(_sizeLabel);
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x11, 0x18, 0x27)),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 7, 16, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 48, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "드래그해서 캡처할 영역을 선택하세요 — Esc 취소",
                Foreground = Brushes.White,
                FontSize = 13.5,
            },
        };
        var root = new Grid();
        root.Children.Add(veil);
        root.Children.Add(frame);
        root.Children.Add(labelCanvas);
        root.Children.Add(hint);
        Content = root;

        Loaded += (s, e) =>
        {
            _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _full.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
            Focus();
        };
    }

    private static System.Drawing.Point CursorPx()
    {
        GetCursorPos(out var p);
        return new System.Drawing.Point(p.X, p.Y);
    }

    private static System.Drawing.Rectangle Normalize(System.Drawing.Point a, System.Drawing.Point b)
        => System.Drawing.Rectangle.FromLTRB(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _startPx = CursorPx();
        _dragging = true;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var r = Normalize(_startPx, CursorPx());
        // 모니터마다 배율이 다르면 표시가 약간 어긋날 수 있으나, 캡처 좌표(물리 픽셀)는 정확함
        var dip = new Rect(
            (r.X - _virtualPx.X) / _scale,
            (r.Y - _virtualPx.Y) / _scale,
            r.Width / _scale,
            r.Height / _scale);
        _hole.Rect = dip;
        _sizeLabel.Visibility = Visibility.Visible;
        _sizeLabel.Text = $"{r.Width} × {r.Height}";
        Canvas.SetLeft(_sizeLabel, dip.X + 2);
        Canvas.SetTop(_sizeLabel, dip.Y > 28 ? dip.Y - 26 : dip.Y + dip.Height + 8);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var r = Normalize(_startPx, CursorPx());
        if (r.Width >= 5 && r.Height >= 5) Result = r;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
}
