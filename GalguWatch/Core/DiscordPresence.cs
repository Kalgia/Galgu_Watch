using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace GalguWatch.Core;

/// <summary>Discord Rich Presence — 로컬 IPC(named pipe)로 디스코드 프로필에
/// "작업 중 + 경과 시간"을 실시간 표시. 서버 불필요, 디스코드 데스크톱 앱이 켜져 있으면 동작.</summary>
public class DiscordPresence : IDisposable
{
    private readonly TimerEngine _engine;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _sync = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly object _lock = new();
    private NamedPipeClientStream? _pipe;
    private TimerState _lastState;
    private int _nonce;
    private bool _connecting;

    public DiscordPresence(TimerEngine engine, AppSettings settings)
    {
        _engine = engine;
        _settings = settings;
        _lastState = engine.State;
        _engine.Changed += OnEngineChanged;
        _sync.Tick += (s, e) => Refresh();   // 1분마다: 오늘 누적 갱신 + 끊겼으면 재연결
        _sync.Start();
        Refresh();
    }

    private bool Enabled =>
        _settings.Get("discord_presence_enabled") != "0" &&
        !string.IsNullOrWhiteSpace(_settings.DiscordClientId);

    private void OnEngineChanged()
    {
        if (_engine.State == _lastState) return;   // 1초 틱은 무시, 상태 전환만 반응
        _lastState = _engine.State;
        Refresh();
    }

    /// <summary>연결·활동 상태를 현재 타이머 상태에 맞춘다 (설정 변경 시에도 호출)</summary>
    public async void Refresh()
    {
        try
        {
            if (!Enabled)
            {
                SendActivity(null);
                return;
            }
            if (_pipe == null && !await ConnectAsync()) return;

            if (_engine.State == TimerState.Running && _engine.RunningSince != null)
            {
                var startUnix = ((DateTimeOffset)_engine.RunningSince.Value).ToUnixTimeSeconds();
                SendActivity(new
                {
                    details = "작업 중",
                    state = $"오늘 누적 {Fmt.Hm(_engine.TodayTotalSec)}",
                    timestamps = new { start = startUnix },
                });
            }
            else
            {
                SendActivity(null);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Discord Presence 갱신 실패", ex);
            DropPipe();
        }
    }

    private async Task<bool> ConnectAsync()
    {
        if (_connecting) return false;
        _connecting = true;
        try
        {
            var cid = _settings.DiscordClientId;
            if (string.IsNullOrWhiteSpace(cid)) return false;
            for (int i = 0; i < 10; i++)
            {
                NamedPipeClientStream? pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}",
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(300);
                    WriteFrame(pipe, 0, JsonSerializer.Serialize(new { v = 1, client_id = cid }));
                    var buf = new byte[8192];
                    using var cts = new CancellationTokenSource(2000);
                    var n = await pipe.ReadAsync(buf, cts.Token);   // READY 응답 확인
                    if (n <= 0) throw new IOException("READY 응답 없음");
                    lock (_lock) _pipe = pipe;
                    Log.Info($"Discord 연결됨 (pipe {i})");
                    _ = DrainLoop(pipe);
                    return true;
                }
                catch
                {
                    pipe?.Dispose();
                }
            }
            return false;   // 디스코드 미실행 — 1분 후 재시도
        }
        finally { _connecting = false; }
    }

    /// <summary>디스코드가 보내는 이벤트 프레임을 비워서 파이프 버퍼가 차지 않게 함</summary>
    private async Task DrainLoop(NamedPipeClientStream pipe)
    {
        var buf = new byte[8192];
        try
        {
            while (pipe.IsConnected)
            {
                var n = await pipe.ReadAsync(buf);
                if (n <= 0) break;
            }
        }
        catch { }
        lock (_lock)
        {
            if (ReferenceEquals(_pipe, pipe))
            {
                try { _pipe?.Dispose(); } catch { }
                _pipe = null;
            }
        }
    }

    private void SendActivity(object? activity)
    {
        var payload = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = (++_nonce).ToString(),
        });
        lock (_lock)
        {
            if (_pipe == null) return;
            try
            {
                WriteFrame(_pipe, 1, payload);
            }
            catch
            {
                try { _pipe.Dispose(); } catch { }
                _pipe = null;
            }
        }
    }

    private static void WriteFrame(NamedPipeClientStream pipe, int op, string json)
    {
        var data = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + data.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(data.Length).CopyTo(frame, 4);
        data.CopyTo(frame, 8);
        pipe.Write(frame, 0, frame.Length);
        pipe.Flush();
    }

    private void DropPipe()
    {
        lock (_lock)
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }
    }

    public void Dispose()
    {
        try { SendActivity(null); } catch { }
        _sync.Stop();
        DropPipe();
    }
}
