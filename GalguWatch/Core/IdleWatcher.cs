using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GalguWatch.Core;

/// <summary>키보드·마우스 입력이 임계값 이상 없으면 측정을 자동 정지 (마지막 입력 시점까지만 기록)</summary>
public class IdleWatcher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly TimerEngine _engine;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>자동 정지됨 — 임계값(분) 전달</summary>
    public event Action<int>? IdleStopped;

    public IdleWatcher(TimerEngine engine, AppSettings settings)
    {
        _engine = engine;
        _settings = settings;
        _timer.Tick += Check;
        _timer.Start();
    }

    private void Check(object? s, EventArgs e)
    {
        if (_engine.State != TimerState.Running) return;
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return;
        // TickCount는 49.7일마다 래핑 — uint 뺄셈이면 모듈러 연산으로 정확
        uint idleMs = unchecked((uint)Environment.TickCount) - info.dwTime;
        int thresholdMin = Math.Max(1, _settings.IdleThresholdMin);
        if (idleMs >= (uint)(thresholdMin * 60_000))
        {
            var end = DateTime.Now.AddMilliseconds(-(double)idleMs);
            _engine.Stop("자리비움", end);
            IdleStopped?.Invoke(thresholdMin);
        }
    }
}
