using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using GalguWatch.Core;
using Microsoft.Web.WebView2.Core;

namespace GalguWatch.MainUi;

/// <summary>캘린더·작업일지 창 — 열 때만 WebView2를 만들고 닫으면 완전히 해제한다</summary>
public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) Title = $"Galgu Watch v{v.ToString(2)}";
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTheme(App.Settings.Get("theme") ?? "light");
    }

    /// <summary>스크린샷 저장 폴더가 바뀌면 갤러리 이미지 매핑을 새 폴더로 교체</summary>
    public void RemapShots(string dir)
    {
        try
        {
            Wv.CoreWebView2?.SetVirtualHostNameToFolderMapping(
                "shots.galgu", dir, CoreWebView2HostResourceAccessKind.Allow);
        }
        catch { }
    }

    /// <summary>창 배경·제목줄·테두리를 테마에 맞춰 통일 (흰/검 라인이 생기지 않게)</summary>
    public void ApplyTheme(string theme)
    {
        bool dark = theme == "dark";
        try
        {
            Background = new System.Windows.Media.SolidColorBrush(dark
                ? System.Windows.Media.Color.FromRgb(15, 23, 42)
                : System.Windows.Media.Color.FromRgb(244, 246, 250));
            Wv.DefaultBackgroundColor = dark
                ? System.Drawing.Color.FromArgb(15, 23, 42)
                : System.Drawing.Color.FromArgb(244, 246, 250);

            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            int darkFlag = dark ? 1 : 0;
            DwmSetWindowAttribute(h, 20, ref darkFlag, sizeof(int));  // 다크 제목줄 모드
            int bg = dark ? 0x2A170F : 0xFAF6F4;                       // COLORREF는 BGR 순서
            DwmSetWindowAttribute(h, 35, ref bg, sizeof(int));         // 제목줄 색
            DwmSetWindowAttribute(h, 34, ref bg, sizeof(int));         // 테두리 색
            int fg = dark ? 0xF0E8E2 : 0x37291F;                       // 제목 글자
            DwmSetWindowAttribute(h, 36, ref fg, sizeof(int));
        }
        catch { }
    }

    private CoreWebView2Environment? _env;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _env = await CoreWebView2Environment.CreateAsync(
                null, Path.Combine(App.DataDir, "webview2"));
            await Wv.EnsureCoreWebView2Async(_env);
            var core = Wv.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;

            // 웹 UI는 실행파일 내장 리소스에서 응답 (app.galgu), 스크린샷은 실제 파일 매핑 (shots.galgu)
            core.AddWebResourceRequestedFilter("https://app.galgu/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResource;
            core.SetVirtualHostNameToFolderMapping(
                "shots.galgu", App.Shots.ShotsDir,
                CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessage;
            core.Navigate("https://app.galgu/index.html");
            Log.Info("메인 창 열림 — WebView2 로딩");
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 초기화 실패", ex);
            MessageBox.Show($"화면을 열 수 없습니다:\n{ex.Message}", "Galgu Watch",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void OnWebResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var path = new Uri(e.Request.Uri).AbsolutePath;              // 예: /index.html
            var resName = "GalguWatch.web" + path.Replace('/', '.');     // GalguWatch.web.index.html
            var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resName);
            if (stream == null)
            {
                e.Response = _env!.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            var mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                _ => "application/octet-stream",
            };
            e.Response = _env!.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {mime}");
        }
        catch (Exception ex)
        {
            Log.Error("내장 웹 리소스 응답 실패", ex);
        }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        long id = 0;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt64();
            var method = root.GetProperty("method").GetString() ?? "";
            var p = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;
            object? result = method == "captureCard"
                ? await CaptureCardAsync(p)
                : Api.Handle(method, p);
            Post(new { id, result });
        }
        catch (Exception ex)
        {
            Log.Error("웹 API 처리 실패", ex);
            if (id != 0) Post(new { id, error = ex.Message });
        }
    }

    /// <summary>웹에서 만든 공유 카드 DOM 영역을 DevTools 프로토콜로 PNG 캡처해 저장</summary>
    private async Task<object?> CaptureCardAsync(JsonElement p)
    {
        var date = p.GetProperty("date").GetString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$"))
            throw new ArgumentException("잘못된 날짜");
        var clipJson = JsonSerializer.Serialize(new
        {
            format = "png",
            captureBeyondViewport = true,
            clip = new
            {
                x = p.GetProperty("x").GetDouble(),
                y = p.GetProperty("y").GetDouble(),
                width = p.GetProperty("w").GetDouble(),
                height = p.GetProperty("h").GetDouble(),
                scale = 2.0,
            },
        });
        var res = await Wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", clipJson);
        using var doc = JsonDocument.Parse(res);
        var bytes = Convert.FromBase64String(doc.RootElement.GetProperty("data").GetString() ?? "");
        var path = Api.ShareFilePath(date, "png");
        File.WriteAllBytes(path, bytes);
        Log.Info($"공유 카드 저장: {path}");

        bool upload = p.TryGetProperty("upload", out var up) && up.GetBoolean();
        if (upload)
        {
            await Api.PostCardToDiscordAsync(path, $"📔 {date} 작업일지", $"galgu_{date}.png");
            return new { uploaded = true };
        }
        Api.RevealInExplorer(path);
        return path;
    }

    private void Post(object o)
        => Wv.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(o));

    protected override void OnClosed(EventArgs e)
    {
        try { Wv.Dispose(); } catch { }
        base.OnClosed(e);
        Log.Info("메인 창 닫힘 — WebView2 해제");
    }
}
