using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using WF = System.Windows.Forms;

namespace GalguWatch.Core;

public class ScreenshotService
{
    private readonly Db _db;
    private readonly AppSettings _settings;
    private readonly TimerEngine _engine;
    private readonly string _defaultShotsDir;
    private readonly DispatcherTimer _auto = new();
    private bool _capturing;

    /// <summary>스크린샷 저장 루트 — 설정에서 바꾸면 즉시 반영</summary>
    public string ShotsDir
    {
        get
        {
            var s = _settings.Get("screenshots_dir");
            return string.IsNullOrWhiteSpace(s) ? _defaultShotsDir : s;
        }
    }

    public string DefaultShotsDir => _defaultShotsDir;

    public ScreenshotService(Db db, AppSettings settings, TimerEngine engine, string dataDir)
    {
        _db = db;
        _settings = settings;
        _engine = engine;
        _defaultShotsDir = Path.Combine(dataDir, "screenshots");
        Directory.CreateDirectory(ShotsDir);
        _auto.Tick += async (s, e) => await CaptureAsync("auto");
        _engine.Changed += SyncAutoTimer;
        SyncAutoTimer();
    }

    /// <summary>주기 캡처는 측정 중에만 돈다</summary>
    private void SyncAutoTimer()
    {
        bool running = _engine.State == TimerState.Running;
        if (running && !_auto.IsEnabled)
        {
            _auto.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.CaptureIntervalMin));
            _auto.Start();
        }
        else if (!running && _auto.IsEnabled)
        {
            _auto.Stop();
        }
    }

    public async Task<string?> CaptureAsync(string kind)
    {
        if (_capturing) return null;
        _capturing = true;
        try
        {
            var takenAt = DateTime.Now;
            var date = _engine.LogicalDate(takenAt);
            System.Drawing.Rectangle b;
            if (_settings.CaptureMonitor == "all")
            {
                b = WF.SystemInformation.VirtualScreen;
            }
            else
            {
                var screen = WF.Screen.PrimaryScreen;
                if (screen == null) return null;
                b = screen.Bounds;
            }
            int w = b.Width, h = b.Height;

            // 화면 복사는 UI 스레드에서 수십 ms — 인코딩·저장은 백그라운드로 넘김
            var pixels = new byte[w * h * 4];
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(b.X, b.Y, 0, 0, new System.Drawing.Size(w, h));
                var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), pixels, y * w * 4, w * 4);
                }
                finally { bmp.UnlockBits(d); }
            }

            var dir = Path.Combine(ShotsDir, date);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{takenAt:HHmmss}_{kind}.webp");

            await Task.Run(() =>
            {
                using var img = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(pixels, w, h);
                img.Save(file, new WebpEncoder { Quality = 75, FileFormat = WebpFileFormatType.Lossy });
                _db.NonQuery(
                    "INSERT INTO screenshots(date, taken_at, kind, file_path) VALUES($d,$t,$k,$p)",
                    ("$d", date),
                    ("$t", takenAt.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("$k", kind),
                    ("$p", file));
            });

            Log.Info($"캡처 저장 ({kind}): {Path.GetFileName(file)}");
            return file;
        }
        catch (Exception ex)
        {
            Log.Error("캡처 실패", ex);
            return null;
        }
        finally { _capturing = false; }
    }

    /// <summary>설정 변경 시 실행 중인 주기 캡처 타이머에 새 간격 적용</summary>
    public void RefreshInterval()
    {
        if (_auto.IsEnabled)
        {
            _auto.Stop();
            _auto.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.CaptureIntervalMin));
            _auto.Start();
        }
    }

    /// <summary>보관 기한 지난 날짜 폴더와 DB 기록 정리</summary>
    public Task CleanupOldAsync() => Task.Run(() =>
    {
        try
        {
            int days = _settings.RetentionDays;
            if (days <= 0) return;
            var cutoff = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd");
            int n = 0;
            if (!Directory.Exists(ShotsDir)) return;
            foreach (var dir in Directory.GetDirectories(ShotsDir))
            {
                var name = Path.GetFileName(dir);
                if (Regex.IsMatch(name, @"^\d{4}-\d{2}-\d{2}$") && string.CompareOrdinal(name, cutoff) < 0)
                {
                    Directory.Delete(dir, true);
                    n++;
                }
            }
            _db.NonQuery("DELETE FROM screenshots WHERE date < $c", ("$c", cutoff));
            if (n > 0) Log.Info($"오래된 스크린샷 폴더 {n}개 정리 (기준일 {cutoff})");
        }
        catch (Exception ex) { Log.Error("스크린샷 정리 실패", ex); }
    });

    /// <summary>저장 폴더 변경 시 기존 스크린샷을 새 폴더로 이동하고 DB 경로를 일괄 갱신.
    /// 드라이브가 달라도 되도록 복사 후 삭제 방식.</summary>
    public Task MigrateAsync(string oldRoot, string newRoot) => Task.Run(() =>
    {
        int moved = 0, failed = 0;
        try
        {
            if (Directory.Exists(oldRoot))
            {
                Directory.CreateDirectory(newRoot);
                foreach (var srcDir in Directory.GetDirectories(oldRoot))
                {
                    var name = Path.GetFileName(srcDir);
                    if (!Regex.IsMatch(name, @"^\d{4}-\d{2}-\d{2}$")) continue;
                    var dstDir = Path.Combine(newRoot, name);
                    Directory.CreateDirectory(dstDir);
                    foreach (var f in Directory.GetFiles(srcDir))
                    {
                        try
                        {
                            File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)), overwrite: true);
                            File.Delete(f);
                            moved++;
                        }
                        catch { failed++; }
                    }
                    try
                    {
                        if (Directory.GetFileSystemEntries(srcDir).Length == 0) Directory.Delete(srcDir);
                    }
                    catch { }
                }
            }
            // DB의 절대 경로를 새 루트로 치환 (정확한 접두사 비교 — LIKE 와일드카드 회피)
            _db.NonQuery(
                "UPDATE screenshots SET file_path = $new || substr(file_path, length($old) + 1) " +
                "WHERE substr(file_path, 1, length($old)) = $old",
                ("$new", newRoot), ("$old", oldRoot));
            Log.Info($"스크린샷 폴더 이동: {moved}개 이동, {failed}개 실패 → {newRoot}");
        }
        catch (Exception ex) { Log.Error("스크린샷 폴더 이동 실패", ex); }
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
            App.Tray.Balloon("스크린샷 폴더 변경 완료",
                failed == 0 ? $"{moved}개 파일을 새 폴더로 옮겼어요." : $"{moved}개 이동, {failed}개 실패 — 로그를 확인해주세요."));
    });
}
