using System.IO;
using System.Text;

namespace GalguWatch.Core;

public static class Log
{
    private static string _path = "";
    private static readonly object _lock = new();

    public static void Init(string path)
    {
        _path = path;
        try
        {
            // 2MB 넘으면 이전 로그로 밀어내기
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 2_000_000)
            {
                var old = path + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
        }
        catch { }
    }

    public static void Info(string msg) => Write("INFO ", msg);

    public static void Error(string msg, Exception? ex = null)
        => Write("ERROR", ex == null ? msg : $"{msg} — {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        if (_path.Length == 0) return;
        try
        {
            lock (_lock)
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}\r\n", Encoding.UTF8);
        }
        catch { }
    }
}
