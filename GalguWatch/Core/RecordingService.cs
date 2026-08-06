using System.IO;
using ScreenRecorderLib;

namespace GalguWatch.Core;

/// <summary>수동 화면 녹화 — 오버레이·트레이에서 시작/정지 토글.
/// mp4는 스크린샷과 같은 날짜 폴더에 저장되고 screenshots 테이블(kind='video')로 등록되어
/// 일별 상세 갤러리에 함께 표시된다. 보관 기한 정리도 스크린샷 정책을 그대로 따른다.</summary>
public class RecordingService
{
    private readonly Db _db;
    private readonly AppSettings _settings;
    private readonly TimerEngine _engine;
    private readonly ScreenshotService _shots;

    private Recorder? _rec;
    private string? _file;
    private DateTime _startedAt;
    private readonly ManualResetEventSlim _done = new(true);

    public bool IsRecording { get; private set; }

    /// <summary>녹화 시작/정지 시 UI(오버레이 버튼·트레이 메뉴) 갱신용</summary>
    public event Action? Changed;

    public RecordingService(Db db, AppSettings settings, TimerEngine engine, ScreenshotService shots)
    {
        _db = db;
        _settings = settings;
        _engine = engine;
        _shots = shots;
    }

    public void Toggle()
    {
        if (IsRecording) Stop();
        else Start();
    }

    public void Start()
    {
        if (IsRecording) return;
        try
        {
            _startedAt = DateTime.Now;
            var date = _engine.LogicalDate(_startedAt);
            var dir = Path.Combine(_shots.ShotsDir, date);
            Directory.CreateDirectory(dir);
            _file = Path.Combine(dir, $"{_startedAt:HHmmss}_rec.mp4");

            // 캡처 대상 모니터 설정을 그대로 따른다 — "all"이면 모든 디스플레이를 한 캔버스에
            var sources = new List<RecordingSourceBase>();
            if (_settings.CaptureMonitor == "all")
                foreach (var d in Recorder.GetDisplays())
                    sources.Add(d);
            if (sources.Count == 0)
                sources.Add(DisplayRecordingSource.MainMonitor);

            var opts = new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = sources },
                OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Encoder = new H264VideoEncoder
                    {
                        BitrateMode = H264BitrateControlMode.Quality,
                        EncoderProfile = H264Profile.Main,
                    },
                    Quality = 60,
                    Framerate = 30,
                    // 정지 화면이 많은 작업 녹화 특성상 가변 프레임으로 용량 절약
                    IsFixedFramerate = false,
                },
                AudioOptions = new AudioOptions { IsAudioEnabled = false },
                MouseOptions = new MouseOptions { IsMousePointerEnabled = true },
            };

            _rec = Recorder.CreateRecorder(opts);
            _rec.OnRecordingComplete += OnComplete;
            _rec.OnRecordingFailed += OnFailed;
            _done.Reset();
            _rec.Record(_file);
            IsRecording = true;
            Changed?.Invoke();
            Log.Info($"녹화 시작: {Path.GetFileName(_file)}");
        }
        catch (Exception ex)
        {
            Log.Error("녹화 시작 실패", ex);
            _done.Set();
            DisposeRecorder();
            App.Tray.Balloon("🎥 녹화를 시작하지 못했어요", ex.Message);
        }
    }

    public void Stop()
    {
        if (!IsRecording || _rec == null) return;
        IsRecording = false;
        Changed?.Invoke();
        try { _rec.Stop(); }   // 파일 마무리는 OnComplete에서
        catch (Exception ex)
        {
            Log.Error("녹화 정지 실패", ex);
            _done.Set();
            DisposeRecorder();
        }
    }

    /// <summary>앱 종료 시 — 녹화 중이면 정지하고 mp4 마무리(OnComplete)까지 잠깐 기다린다</summary>
    public void StopAndWait(int timeoutMs)
    {
        if (IsRecording) Stop();
        _done.Wait(timeoutMs);
    }

    private void OnComplete(object? sender, RecordingCompleteEventArgs e)
    {
        try
        {
            var date = _engine.LogicalDate(_startedAt);
            var dur = DateTime.Now - _startedAt;
            _db.NonQuery(
                "INSERT INTO screenshots(date, taken_at, kind, file_path) VALUES($d,$t,$k,$p)",
                ("$d", date),
                ("$t", _startedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                ("$k", "video"),
                ("$p", e.FilePath));
            Log.Info($"녹화 저장: {Path.GetFileName(e.FilePath)} ({dur:mm\\:ss})");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                App.Tray.Balloon("🎥 녹화 저장됨", $"{Path.GetFileName(e.FilePath)} ({dur:mm\\:ss})"));
        }
        catch (Exception ex) { Log.Error("녹화 저장 처리 실패", ex); }
        finally
        {
            DisposeRecorder();
            _done.Set();
        }
    }

    private void OnFailed(object? sender, RecordingFailedEventArgs e)
    {
        Log.Error($"녹화 실패: {e.Error}");
        IsRecording = false;
        try
        {
            // 실패한 조각 파일은 지운다
            if (_file != null && File.Exists(_file)) File.Delete(_file);
        }
        catch { }
        DisposeRecorder();
        _done.Set();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Changed?.Invoke();
            App.Tray.Balloon("🎥 녹화 실패", e.Error);
        });
    }

    private void DisposeRecorder()
    {
        var r = _rec;
        _rec = null;
        if (r == null) return;
        r.OnRecordingComplete -= OnComplete;
        r.OnRecordingFailed -= OnFailed;
        try { r.Dispose(); } catch { }
    }
}
