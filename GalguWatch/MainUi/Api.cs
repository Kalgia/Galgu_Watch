using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalguWatch.Core;

namespace GalguWatch.MainUi;

/// <summary>웹 UI(JS)가 postMessage로 호출하는 로컬 API</summary>
public static class Api
{
    public static object? Handle(string method, JsonElement p) => method switch
    {
        "getConfig" => GetConfig(),
        "getMonth" => GetMonth(p.GetProperty("year").GetInt32(), p.GetProperty("month").GetInt32()),
        "getDay" => GetDay(ReqDate(p)),
        "saveNote" => SaveNote(ReqDate(p), p.GetProperty("content").GetString() ?? ""),
        "deleteShot" => DeleteShot(p.GetProperty("id").GetInt64()),
        "setTheme" => SetTheme(p.GetProperty("theme").GetString() ?? "light"),
        "getSettings" => GetSettings(),
        "saveSettings" => SaveSettings(p),
        "exportDay" => ExportDay(ReqDate(p), p.GetProperty("noteHtml").GetString() ?? "",
            p.GetProperty("shotIds")),
        "openDataFolder" => OpenDataFolder(),
        "ready" => LogReady(p),
        _ => throw new InvalidOperationException($"알 수 없는 API: {method}"),
    };

    private static string ReqDate(JsonElement p)
    {
        var d = p.GetProperty("date").GetString() ?? "";
        if (!Regex.IsMatch(d, @"^\d{4}-\d{2}-\d{2}$")) throw new ArgumentException("잘못된 날짜 형식");
        return d;
    }

    private static object GetConfig() => new
    {
        today = App.Engine.LogicalDate(DateTime.Now),
        dayStartHour = App.Settings.DayStartHour,
        goalMinutes = App.Settings.GoalMinutes,
        captureIntervalMin = App.Settings.CaptureIntervalMin,
        retentionDays = App.Settings.RetentionDays,
        theme = App.Settings.Get("theme") ?? "light",
    };

    private static object SetTheme(string theme)
    {
        if (theme != "dark" && theme != "light") throw new ArgumentException("잘못된 테마");
        App.Settings.Set("theme", theme);
        foreach (System.Windows.Window w in System.Windows.Application.Current.Windows)
            if (w is MainWindow mw) mw.ApplyTheme(theme);
        return true;
    }

    private static object GetMonth(int year, int month)
    {
        var first = new DateTime(year, month, 1);
        var a = first.ToString("yyyy-MM-dd");
        var b = first.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
        var days = new Dictionary<string, long>();
        using (var c = App.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText =
                "SELECT date, SUM(duration_sec) FROM sessions WHERE date >= $a AND date <= $b GROUP BY date";
            cmd.Parameters.AddWithValue("$a", a);
            cmd.Parameters.AddWithValue("$b", b);
            using var r = cmd.ExecuteReader();
            while (r.Read()) days[r.GetString(0)] = r.GetInt64(1);
        }
        // 오늘은 측정 중인 시간까지 반영
        var today = App.Engine.LogicalDate(DateTime.Now);
        if (string.CompareOrdinal(today, a) >= 0 && string.CompareOrdinal(today, b) <= 0)
        {
            var live = App.Engine.TodayTotalSec;
            if (live > 0) days[today] = live;
        }
        return new
        {
            today,
            streak = CalcStreak(),
            days = days.OrderBy(kv => kv.Key)
                       .Select(kv => new { date = kv.Key, totalSec = kv.Value })
                       .ToList(),
        };
    }

    /// <summary>오늘부터 거슬러 올라가며 일일 목표를 달성한 연속 일수 (오늘 미달성이면 어제부터 카운트)</summary>
    private static int CalcStreak()
    {
        int goalMin = App.Settings.GoalMinutes;
        if (goalMin <= 0) return 0;
        long goalSec = goalMin * 60L;
        var today = App.Engine.LogicalDate(DateTime.Now);
        var todayDt = DateTime.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var from = todayDt.AddDays(-400).ToString("yyyy-MM-dd");

        var totals = new Dictionary<string, long>();
        using (var c = App.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT date, SUM(duration_sec) FROM sessions WHERE date >= $a GROUP BY date";
            cmd.Parameters.AddWithValue("$a", from);
            using var r = cmd.ExecuteReader();
            while (r.Read()) totals[r.GetString(0)] = r.GetInt64(1);
        }
        var live = App.Engine.TodayTotalSec;
        if (live > 0) totals[today] = live;

        int streak = 0;
        if (totals.TryGetValue(today, out var t) && t >= goalSec) streak++;
        for (var day = todayDt.AddDays(-1); ; day = day.AddDays(-1))
        {
            if (totals.TryGetValue(day.ToString("yyyy-MM-dd"), out var v) && v >= goalSec) streak++;
            else break;
        }
        return streak;
    }

    private static object GetSettings() => new
    {
        captureIntervalMin = App.Settings.CaptureIntervalMin,
        retentionDays = App.Settings.RetentionDays,
        idleThresholdMin = App.Settings.IdleThresholdMin,
        dayStartHour = App.Settings.DayStartHour,
        overlayOpacity = App.Settings.OverlayOpacity,
        goalMinutes = App.Settings.GoalMinutes,
        captureMonitor = App.Settings.CaptureMonitor,
        discordPresence = App.Settings.Get("discord_presence_enabled") != "0",
        discordWebhookUrl = App.Settings.Get("discord_webhook_url") ?? "",
    };

    private static object SaveSettings(JsonElement p)
    {
        int interval = Math.Clamp(p.GetProperty("captureIntervalMin").GetInt32(), 1, 120);
        int retention = Math.Clamp(p.GetProperty("retentionDays").GetInt32(), 0, 3650);
        int idle = Math.Clamp(p.GetProperty("idleThresholdMin").GetInt32(), 1, 120);
        int dayStart = Math.Clamp(p.GetProperty("dayStartHour").GetInt32(), 0, 12);
        double opacity = Math.Clamp(p.GetProperty("overlayOpacity").GetDouble(), 0.3, 1.0);
        int goal = Math.Clamp(p.GetProperty("goalMinutes").GetInt32(), 0, 1440);
        var monitor = p.GetProperty("captureMonitor").GetString() == "all" ? "all" : "primary";

        var s = App.Settings;
        s.Set("capture_interval_min", interval.ToString());
        s.Set("retention_days", retention.ToString());
        s.Set("idle_threshold_min", idle.ToString());
        s.Set("day_start_hour", dayStart.ToString());
        s.Set("overlay_opacity", opacity.ToString(CultureInfo.InvariantCulture));
        s.Set("goal_minutes", goal.ToString());
        s.Set("capture_monitor", monitor);

        s.Set("discord_presence_enabled", p.GetProperty("discordPresence").GetBoolean() ? "1" : "0");
        var wh = (p.GetProperty("discordWebhookUrl").GetString() ?? "").Trim();
        if (wh.Length > 0 && !wh.StartsWith("https://discord.com/api/webhooks/", StringComparison.Ordinal))
            throw new ArgumentException("웹훅 URL 형식이 아니에요 (https://discord.com/api/webhooks/… 이어야 함)");
        s.Set("discord_webhook_url", wh);

        App.Shots.RefreshInterval();
        App.Overlay.RefreshFromSettings();
        App.Presence.Refresh();
        _ = App.Shots.CleanupOldAsync();
        Log.Info("설정 저장됨");
        return true;
    }

    private static readonly System.Net.Http.HttpClient Http = new();

    /// <summary>공유 카드 PNG를 디스코드 웹훅으로 채널에 업로드</summary>
    public static async Task PostCardToDiscordAsync(string filePath, string message, string asciiName)
    {
        var url = App.Settings.Get("discord_webhook_url");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("웹훅 URL이 없어요 — ⚙️ 설정의 디스코드 항목에 넣어주세요");
        if (!url.StartsWith("https://discord.com/api/webhooks/", StringComparison.Ordinal))
            throw new InvalidOperationException("웹훅 URL 형식이 이상해요");
        using var form = new System.Net.Http.MultipartFormDataContent();
        form.Add(new System.Net.Http.StringContent(
            JsonSerializer.Serialize(new { content = message }), Encoding.UTF8, "application/json"),
            "payload_json");
        var img = new System.Net.Http.ByteArrayContent(File.ReadAllBytes(filePath));
        img.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(img, "files[0]", asciiName);
        var res = await Http.PostAsync(url, form);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"디스코드 업로드 실패 (HTTP {(int)res.StatusCode})");
        Log.Info("디스코드 채널에 공유 카드 업로드됨");
    }

    private static object GetDay(string date)
    {
        var sessions = new List<object>();
        var shots = new List<object>();
        long sum = 0;
        using (var c = App.Db.Open())
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT started_at, ended_at, duration_sec FROM sessions WHERE date=$d ORDER BY started_at";
                cmd.Parameters.AddWithValue("$d", date);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    sum += r.GetInt64(2);
                    sessions.Add(new
                    {
                        startedAt = r.GetString(0),
                        endedAt = r.GetString(1),
                        durationSec = r.GetInt64(2),
                    });
                }
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, taken_at, kind, file_path, memo FROM screenshots WHERE date=$d ORDER BY taken_at";
                cmd.Parameters.AddWithValue("$d", date);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    shots.Add(new
                    {
                        id = r.GetInt64(0),
                        takenAt = r.GetString(1),
                        kind = r.GetString(2),
                        url = $"https://shots.galgu/{date}/{Path.GetFileName(r.GetString(3))}",
                        memo = r.IsDBNull(4) ? null : r.GetString(4),
                    });
                }
            }
        }
        var note = App.Db.Scalar<string>("SELECT content_md FROM notes WHERE date=$d", ("$d", date));
        bool isToday = date == App.Engine.LogicalDate(DateTime.Now);
        return new
        {
            sessions,
            shots,
            note,
            totalSec = isToday ? App.Engine.TodayTotalSec : sum,
            running = isToday && App.Engine.State == TimerState.Running,
        };
    }

    private static object SaveNote(string date, string content)
    {
        App.Db.NonQuery("""
            INSERT INTO notes(date, content_md, updated_at) VALUES($d, $c, $u)
            ON CONFLICT(date) DO UPDATE SET content_md=$c, updated_at=$u
            """,
            ("$d", date),
            ("$c", content),
            ("$u", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        return true;
    }

    private static object DeleteShot(long id)
    {
        var path = App.Db.Scalar<string>("SELECT file_path FROM screenshots WHERE id=$i", ("$i", id));
        if (path != null && File.Exists(path)) File.Delete(path);
        App.Db.NonQuery("DELETE FROM screenshots WHERE id=$i", ("$i", id));
        Log.Info($"스크린샷 삭제: id={id}");
        return true;
    }

    private static object OpenDataFolder()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", App.DataDir) { UseShellExecute = true });
        return true;
    }

    /// <summary>공유 파일 저장 위치: 바탕화면\GalguWatch 공유\갈구워치_날짜.확장자</summary>
    public static string ShareFilePath(string date, string ext)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GalguWatch 공유");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"갈구워치_{date}.{ext}");
    }

    public static void RevealInExplorer(string path)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    /// <summary>이미지 포함 단일 HTML로 하루 일지 내보내기 — 받는 사람은 브라우저만 있으면 됨</summary>
    private static object ExportDay(string date, string noteHtml, JsonElement shotIdsEl)
    {
        var ids = new HashSet<long>();
        if (shotIdsEl.ValueKind == JsonValueKind.Array)
            foreach (var e in shotIdsEl.EnumerateArray()) ids.Add(e.GetInt64());

        var sessions = new List<(string Start, string End, long Dur)>();
        long total = 0;
        using (var c = App.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText =
                "SELECT started_at, ended_at, duration_sec FROM sessions WHERE date=$d ORDER BY started_at";
            cmd.Parameters.AddWithValue("$d", date);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                sessions.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));
                total += r.GetInt64(2);
            }
        }
        if (date == App.Engine.LogicalDate(DateTime.Now)) total = App.Engine.TodayTotalSec;

        var imgs = new List<string>();
        using (var c = App.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, file_path FROM screenshots WHERE date=$d ORDER BY taken_at";
            cmd.Parameters.AddWithValue("$d", date);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!ids.Contains(r.GetInt64(0))) continue;
                var fp = r.GetString(1);
                if (File.Exists(fp)) imgs.Add(Convert.ToBase64String(File.ReadAllBytes(fp)));
            }
        }

        var dt = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var title = dt.ToString("yyyy년 M월 d일 (ddd)", new CultureInfo("ko-KR")) + " 작업일지";

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(title).Append("</title><style>");
        sb.Append("body{font-family:'Segoe UI','Malgun Gothic',sans-serif;max-width:820px;margin:26px auto;padding:0 18px;color:#1f2937;line-height:1.65;background:#fff}");
        sb.Append("h1{font-size:23px;border-bottom:2px solid #16a34a;padding-bottom:10px}");
        sb.Append(".stats{font-size:16px;margin:12px 0}.sess{color:#64748b;font-size:13.5px;margin:3px 0}");
        sb.Append("img{max-width:100%;border-radius:8px;border:1px solid #dbe3ee;margin:8px 0}");
        sb.Append("hr{border:none;border-top:1px solid #dbe3ee;margin:18px 0}");
        sb.Append("code,pre{background:#eef2f7;border-radius:5px;font-family:Consolas,monospace}code{padding:1px 5px}pre{padding:10px 12px;overflow-x:auto}");
        sb.Append("blockquote{border-left:3px solid #dbe3ee;padding-left:10px;color:#64748b}");
        sb.Append("footer{margin:26px 0 10px;color:#94a3b8;font-size:12px;text-align:center}");
        sb.Append("</style></head><body>");
        sb.Append("<h1>").Append(title).Append("</h1>");
        sb.Append("<div class=\"stats\">⏱ 총 <b>").Append(Fmt.Hms(TimeSpan.FromSeconds(total)))
          .Append("</b> · 세션 ").Append(sessions.Count).Append("개</div>");
        foreach (var s in sessions)
            sb.Append("<div class=\"sess\">").Append(s.Start[11..16]).Append("–").Append(s.End[11..16])
              .Append(" (").Append(Fmt.Hms(TimeSpan.FromSeconds(s.Dur))).Append(")</div>");
        foreach (var b64 in imgs)
            sb.Append("<img src=\"data:image/webp;base64,").Append(b64).Append("\">");
        sb.Append("<hr>").Append(noteHtml);
        sb.Append("<footer>Galgu Watch</footer></body></html>");

        var path = ShareFilePath(date, "html");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Log.Info($"공유 HTML 저장: {path}");
        RevealInExplorer(path);
        return path;
    }

    private static object LogReady(JsonElement p)
    {
        Log.Info($"웹 UI 준비 완료: {p}");
        return true;
    }
}
