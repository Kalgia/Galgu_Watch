using System.IO;
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

    public static void ExitApp()
    {
        try
        {
            if (Engine != null! && Engine.State == TimerState.Running) Engine.Stop("앱 종료");
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
