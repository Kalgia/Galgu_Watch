using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using GalguWatch.Core;
using GalguWatch.Overlay;
using GalguWatch.Tray;
using Microsoft.Win32;

namespace GalguWatch;

public partial class App : Application
{
    private static Mutex? _mutex;
    private IdleWatcher? _idle;
    private bool _stoppedBySleep;

    public static string DataDir { get; private set; } = "";
    public static Db Db { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;
    public static TimerEngine Engine { get; private set; } = null!;
    public static ScreenshotService Shots { get; private set; } = null!;
    public static TrayIcon Tray { get; private set; } = null!;
    public static OverlayWindow Overlay { get; private set; } = null!;
    public static DiscordPresence Presence { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 관리용 CLI: GalguWatch.exe --set key=value ... → 설정만 쓰고 종료
        if (e.Args.Contains("--set"))
        {
            DataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalguWatch");
            Directory.CreateDirectory(DataDir);
            var db = new Db(Path.Combine(DataDir, "galgu.db"));
            var st = new AppSettings(db);
            for (int i = 0; i < e.Args.Length - 1; i++)
                if (e.Args[i] == "--set" && e.Args[i + 1].Contains('='))
                {
                    var kv = e.Args[i + 1].Split('=', 2);
                    st.Set(kv[0], kv[1]);
                }
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, "GalguWatch_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);

        DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalguWatch");
        Directory.CreateDirectory(DataDir);
        Log.Init(Path.Combine(DataDir, "log.txt"));
        DispatcherUnhandledException += (s, ev) =>
        {
            Log.Error("처리되지 않은 예외", ev.Exception);
            ev.Handled = true;
        };

        try
        {
            Log.Info("=== Galgu Watch 시작 ===");
            Db = new Db(Path.Combine(DataDir, "galgu.db"));
            Settings = new AppSettings(Db);
            Engine = new TimerEngine(Db, Settings);
            Engine.RecoverIfCrashed();
            Engine.WorkStarted += () => _cheerTask = PostCheerAsync(start: true);
            Engine.WorkFinished += () => _cheerTask = PostCheerAsync(start: false);
            Shots = new ScreenshotService(Db, Settings, Engine, DataDir);
            _idle = new IdleWatcher(Engine, Settings);
            _idle.IdleStopped += min => Tray.Balloon(
                "자리비움으로 측정을 멈췄어요",
                $"{min}분간 입력이 없어 마지막 입력 시점까지만 기록했습니다. 클릭하면 다시 시작해요.",
                clickRestartsTimer: true);
            Tray = new TrayIcon(Engine, Shots);
            Overlay = new OverlayWindow(Engine, Shots, Settings);
            Overlay.Show();
            Presence = new DiscordPresence(Engine, Settings);

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            if (Engine.RecoveryMessage != null)
                Tray.Balloon("기록 복구", Engine.RecoveryMessage);
            else
            {
                var dueToday = Db.Scalar<long>(
                    "SELECT COUNT(*) FROM goals WHERE done=0 AND due_date=$d",
                    ("$d", Engine.LogicalDate(DateTime.Now)));
                if (dueToday > 0)
                    Tray.Balloon("🎯 오늘까지인 목표", $"{dueToday}개의 목표가 오늘 마감이에요.");
            }
            _ = Shots.CleanupOldAsync();
            Log.Info("초기화 완료");
            if (e.Args.Contains("--open-main")) OpenMainWindow();
            ScheduleTrim(15);
        }
        catch (Exception ex)
        {
            Log.Error("초기화 실패", ex);
            MessageBox.Show($"Galgu Watch 시작 실패:\n{ex.Message}", "Galgu Watch",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && Engine.State == TimerState.Running)
        {
            Engine.Stop("화면 잠금");
            Tray.Balloon("화면 잠금으로 측정을 멈췄어요", "돌아와서 이 알림을 클릭하면 다시 시작해요.",
                clickRestartsTimer: true);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend && Engine.State == TimerState.Running)
        {
            Engine.Stop("절전");
            _stoppedBySleep = true;
        }
        else if (e.Mode == PowerModes.Resume && _stoppedBySleep)
        {
            _stoppedBySleep = false;
            Tray.Balloon("절전으로 측정을 멈췄었어요", "클릭하면 다시 시작해요.", clickRestartsTimer: true);
        }
    }

    private static MainUi.MainWindow? _main;

    public static void OpenMainWindow()
    {
        if (_main == null)
        {
            _main = new MainUi.MainWindow();
            _main.Closed += (s, e) =>
            {
                _main = null;
                ScheduleTrim(3);   // WebView2 해제 후 워킹셋 반납
            };
            _main.Show();
        }
        else
        {
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
        }
    }

    private static void ScheduleTrim(int seconds)
    {
        var t = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(seconds),
        };
        t.Tick += (s, e) =>
        {
            t.Stop();
            MemTrim.Trim();
        };
        t.Start();
    }

    /// <summary>영역 지정 캡처 — 오버레이를 잠시 숨기고 선택 창을 띄운 뒤 해당 영역만 저장</summary>
    public static async Task CaptureRegionWithUiAsync()
    {
        bool overlayVisible = Overlay.IsVisible;
        if (overlayVisible) Overlay.Hide();
        try
        {
            var region = RegionSelectWindow.PickRegion();
            if (region != null)
            {
                await Task.Delay(150);   // 선택 베일이 화면에서 사라진 뒤 캡처
                var f = await Shots.CaptureAsync("manual", region);
                if (f != null) Tray.Balloon("✂ 영역 캡처 저장됨", Path.GetFileName(f));
            }
        }
        finally
        {
            if (overlayVisible) Overlay.Show();
        }
    }

    private static readonly HttpClient CheerHttp = new();
    private static Task? _cheerTask;

    // {0}=표시 이름
    private static readonly string[] CheerStart =
    {
        "▶ **{0}**님이 작업을 시작하셨어요! 오늘 하루도 화이팅! 🔥",
        "🎨 **{0}**님, 오늘도 작업 시작! 멋진 결과물 기대할게요 ✨",
        "⚡ **{0}**님이 자리에 앉았습니다. 오늘도 갈구가 함께합니다 🗿",
        "🚀 **{0}**님 작업 출발! 어제의 나를 넘어봅시다 💪",
        "🌅 **{0}**님이 작업을 켰어요. 시작이 반이다, 화이팅!",
        "🔥 오늘의 갈굼 시작! **{0}**님, 집중 모드 돌입입니다",
        "🎯 **{0}**님이 작업대에 복귀! 목표를 향해 한 걸음 더",
        "💻 **{0}**님 작업 개시! 완성으로 가는 길, 오늘도 한 칸 전진",
    };

    // {0}=표시 이름, {1}=오늘 누적 시간
    private static readonly string[] CheerFinish =
    {
        "🏁 **{0}**님이 작업을 마무리하셨어요 — 오늘 {1} 작업. 오늘도 대단하세요! 👏",
        "🌙 **{0}**님, 오늘 {1} 작업 완료! 수고 많으셨어요. 푹 쉬세요 ☕",
        "✅ **{0}**님이 오늘의 작업을 끝냈습니다 — {1} 집중. 어제보다 성장했어요 📈",
        "🎉 오늘도 해냈다! **{0}**님 {1} 작업 마무리. 내일도 이 기세로!",
        "👏 **{0}**님, 오늘 {1} 작업하고 퇴근! 갈구 워치가 인정합니다 🗿",
        "🛌 **{0}**님이 오늘 작업 끝! {1} 몰입 — 충분히 자랑해도 됩니다 😎",
        "📦 오늘치 작업 완료! **{0}**님 {1} 기록. 꾸준함이 실력이 됩니다",
        "🌟 **{0}**님 수고하셨어요! 오늘 {1} — 쌓인 시간은 배신하지 않아요",
    };

    private static int _lastStartMsg = -1;
    private static int _lastFinishMsg = -1;

    /// <summary>직전에 쓴 문구를 제외하고 랜덤 선택 — 연속으로 같은 메시지가 나가지 않게</summary>
    private static int NextCheerIdx(int count, int last)
    {
        int idx = Random.Shared.Next(last >= 0 ? count - 1 : count);
        if (last >= 0 && idx >= last) idx++;
        return idx;
    }

    /// <summary>작업 시작·마무리 응원 메시지를 디스코드 채널에 전송</summary>
    private static async Task PostCheerAsync(bool start)
    {
        try
        {
            if (Settings.Get("discord_cheer_enabled") == "0") return;
            var url = Settings.Get("discord_webhook_url");
            if (string.IsNullOrWhiteSpace(url) ||
                !url.StartsWith("https://discord.com/api/webhooks/", StringComparison.Ordinal)) return;
            var name = Settings.Get("display_name");
            if (string.IsNullOrWhiteSpace(name)) name = Environment.UserName;
            string msg;
            if (start)
            {
                _lastStartMsg = NextCheerIdx(CheerStart.Length, _lastStartMsg);
                msg = string.Format(CheerStart[_lastStartMsg], name);
            }
            else
            {
                _lastFinishMsg = NextCheerIdx(CheerFinish.Length, _lastFinishMsg);
                msg = string.Format(CheerFinish[_lastFinishMsg], name, Fmt.Hm(Engine.TodayTotalSec));
            }
            using var body = new StringContent(
                JsonSerializer.Serialize(new { content = msg }), Encoding.UTF8, "application/json");
            // ConfigureAwait(false): 종료 시 UI 스레드가 이 작업을 기다려도 교착 없이 완료되도록
            var res = await CheerHttp.PostAsync(url, body).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                Log.Error($"응원 메시지 전송 실패 (HTTP {(int)res.StatusCode})");
        }
        catch (Exception ex)
        {
            Log.Error("응원 메시지 전송 실패", ex);
        }
    }

    public static void ExitApp()
    {
        try
        {
            Engine?.Finish();          // 하던 작업이 있으면 마무리 처리 (응원 메시지 포함)
            _cheerTask?.Wait(1500);    // 메시지 전송이 끝날 시간을 잠깐 준다
        }
        catch { }
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch { }
        try { Presence?.Dispose(); } catch { }
        try { Tray?.Dispose(); } catch { }
        Log.Info("=== 종료 ===");
        base.OnExit(e);
    }
}
