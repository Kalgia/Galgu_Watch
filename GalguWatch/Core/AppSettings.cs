using System.Globalization;

namespace GalguWatch.Core;

public class AppSettings
{
    private readonly Db _db;
    private readonly Dictionary<string, string> _cache = new();

    public AppSettings(Db db)
    {
        _db = db;
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings";
        using var r = cmd.ExecuteReader();
        while (r.Read()) _cache[r.GetString(0)] = r.GetString(1);
    }

    public string? Get(string key) => _cache.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string value)
    {
        _cache[key] = value;
        _db.NonQuery("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v",
            ("$k", key), ("$v", value));
    }

    public void Delete(string key)
    {
        _cache.Remove(key);
        _db.NonQuery("DELETE FROM settings WHERE key=$k", ("$k", key));
    }

    private int GetInt(string key, int def)
        => int.TryParse(Get(key), out var v) ? v : def;

    private double GetDouble(string key, double def)
        => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    public int CaptureIntervalMin => GetInt("capture_interval_min", 10);
    public int RetentionDays => GetInt("retention_days", 90);
    public int DayStartHour => GetInt("day_start_hour", 5);
    public int IdleThresholdMin => GetInt("idle_threshold_min", 5);
    public int GoalMinutes => GetInt("goal_minutes", 0);
    public double OverlayOpacity => GetDouble("overlay_opacity", 1.0);
    public string CaptureMonitor => Get("capture_monitor") ?? "primary";

    /// <summary>Rich Presence 앱 ID — 공개 값이라 기본 내장 (배포본에서도 동일한 "Galgu Watch" 이름으로 표시)</summary>
    public string DiscordClientId => Get("discord_client_id") ?? "1534779963771719760";

    /// <summary>응원 메시지·일지 카드를 올릴 웹훅 — 배포본에서도 설정 없이 바로 동작하게 기본 내장.
    /// 설정에서 다른 URL을 넣으면 그 값이 우선.</summary>
    public string DiscordWebhookUrl
    {
        get
        {
            var v = Get("discord_webhook_url");
            return string.IsNullOrWhiteSpace(v)
                ? "https://discord.com/api/webhooks/1534781516750524488/3YrdAPiQ-G6_Jls5DzbVDz2AibYFfAkbA0PZjKOk6YC_FaR7QioWiM6TXAAAaG0CXnYw"
                : v;
        }
    }

    public double? OverlayX => double.TryParse(Get("overlay_x"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    public double? OverlayY => double.TryParse(Get("overlay_y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void SetOverlayPos(double x, double y)
    {
        Set("overlay_x", x.ToString(CultureInfo.InvariantCulture));
        Set("overlay_y", y.ToString(CultureInfo.InvariantCulture));
    }
}
