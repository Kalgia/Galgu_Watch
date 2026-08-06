namespace GalguWatch.Core;

public static class Fmt
{
    /// <summary>1:23:45 — 10시간 넘어도 시간이 잘리지 않게</summary>
    public static string Hms(TimeSpan t)
        => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>3:42 (시:분)</summary>
    public static string Hm(int totalSec)
        => $"{totalSec / 3600}:{totalSec % 3600 / 60:00}";
}
