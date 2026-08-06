using System.Windows.Threading;

namespace GalguWatch.Core;

public enum TimerState { Stopped, Running }

public class TimerEngine
{
    private readonly Db _db;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastHeartbeat;
    private string _todayCacheDate = "";
    private int _todayCacheSec;

    public TimerState State { get; private set; } = TimerState.Stopped;
    public DateTime? RunningSince { get; private set; }
    public string? RecoveryMessage { get; private set; }

    /// <summary>상태 변화 + 측정 중 1초 틱마다 (UI 갱신용)</summary>
    public event Action? Changed;

    /// <summary>측정 시작됨</summary>
    public event Action? Started;

    /// <summary>세션 종료됨 — (지속 초, 사유)</summary>
    public event Action<int, string>? SessionEnded;

    /// <summary>작업 블록(시작~마무리, 일시정지 포함)이 열려 있는지</summary>
    public bool WorkBlockOpen { get; private set; }

    /// <summary>작업 블록 시작 — 첫 ▶ (응원 메시지 트리거)</summary>
    public event Action? WorkStarted;

    /// <summary>작업 마무리 — ⏹ (응원 메시지 트리거)</summary>
    public event Action? WorkFinished;

    public TimerEngine(Db db, AppSettings settings)
    {
        _db = db;
        _settings = settings;
        _tick.Tick += OnTick;
    }

    /// <summary>"하루 시작 시각"(기본 05:00) 기준의 논리적 날짜 — 새벽 작업은 전날로 집계</summary>
    public string LogicalDate(DateTime t) => t.AddHours(-_settings.DayStartHour).ToString("yyyy-MM-dd");

    public TimeSpan CurrentElapsed => State == TimerState.Running && RunningSince != null
        ? DateTime.Now - RunningSince.Value
        : TimeSpan.Zero;

    public int TodayTotalSec
    {
        get
        {
            var d = LogicalDate(DateTime.Now);
            if (_todayCacheDate != d)
            {
                _todayCacheDate = d;
                _todayCacheSec = _db.Scalar<int>(
                    "SELECT COALESCE(SUM(duration_sec),0) FROM sessions WHERE date=$d", ("$d", d));
            }
            return _todayCacheSec + (int)CurrentElapsed.TotalSeconds;
        }
    }

    public void Toggle()
    {
        if (State == TimerState.Running) Stop("수동 정지");
        else Start();
    }

    public void Start()
    {
        if (State == TimerState.Running) return;
        RunningSince = DateTime.Now;
        State = TimerState.Running;
        _settings.Set("running_since", RunningSince.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        WriteHeartbeat();
        _tick.Start();
        Log.Info("측정 시작");
        Changed?.Invoke();
        Started?.Invoke();
        if (!WorkBlockOpen)
        {
            WorkBlockOpen = true;
            WorkStarted?.Invoke();
        }
    }

    /// <summary>작업 마무리 — 측정 중이면 세션을 끝내고, 열려 있던 작업 블록을 닫는다</summary>
    public void Finish()
    {
        if (State == TimerState.Running) Stop("마무리");
        if (WorkBlockOpen)
        {
            WorkBlockOpen = false;
            Log.Info("작업 마무리");
            Changed?.Invoke();
            WorkFinished?.Invoke();
        }
    }

    /// <summary>endAt: 자리비움 등으로 실제 종료 시점을 되돌려야 할 때</summary>
    public void Stop(string reason, DateTime? endAt = null)
    {
        if (State != TimerState.Running || RunningSince == null) return;
        var start = RunningSince.Value;
        var end = endAt ?? DateTime.Now;
        if (end < start) end = start;

        State = TimerState.Stopped;
        RunningSince = null;
        _tick.Stop();
        _settings.Delete("running_since");
        _settings.Delete("last_heartbeat");

        var durSec = (int)(end - start).TotalSeconds;
        if (durSec >= 5) InsertSession(start, end, durSec);
        _todayCacheDate = "";
        Log.Info($"측정 정지 ({reason}) — {Fmt.Hms(TimeSpan.FromSeconds(durSec))}");
        Changed?.Invoke();
        SessionEnded?.Invoke(durSec, reason);
    }

    private void InsertSession(DateTime start, DateTime end, int durSec)
    {
        _db.NonQuery(
            "INSERT INTO sessions(date, started_at, ended_at, duration_sec) VALUES($d,$s,$e,$u)",
            ("$d", LogicalDate(start)),
            ("$s", start.ToString("yyyy-MM-dd HH:mm:ss")),
            ("$e", end.ToString("yyyy-MM-dd HH:mm:ss")),
            ("$u", durSec));
    }

    /// <summary>지난 실행이 측정 중에 죽었으면 마지막 하트비트 시점까지를 세션으로 복구</summary>
    public void RecoverIfCrashed()
    {
        var sinceRaw = _settings.Get("running_since");
        if (sinceRaw == null) return;
        var hbRaw = _settings.Get("last_heartbeat") ?? sinceRaw;
        _settings.Delete("running_since");
        _settings.Delete("last_heartbeat");
        if (!DateTime.TryParse(sinceRaw, out var since) || !DateTime.TryParse(hbRaw, out var hb)) return;
        if (hb < since) hb = since;
        var durSec = (int)(hb - since).TotalSeconds;
        if (durSec >= 5)
        {
            InsertSession(since, hb, durSec);
            RecoveryMessage = $"지난번 비정상 종료를 복구했어요 — {Fmt.Hms(TimeSpan.FromSeconds(durSec))} 기록됨";
            Log.Info(RecoveryMessage);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (State == TimerState.Running && (DateTime.Now - _lastHeartbeat).TotalSeconds >= 30)
            WriteHeartbeat();
        Changed?.Invoke();
    }

    private void WriteHeartbeat()
    {
        _lastHeartbeat = DateTime.Now;
        _settings.Set("last_heartbeat", _lastHeartbeat.ToString("yyyy-MM-dd HH:mm:ss"));
    }
}
