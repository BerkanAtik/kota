using System;
using System.Collections.Generic;
using System.Globalization;
using Kota.Core;

namespace Kota.App;

/// <summary>Turkish and English UI text.</summary>
static class L
{
    public static bool Turkish { get; private set; }

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Turkish ? "tr-TR" : "en-US");

    public static void Apply(string? language) =>
        Turkish = language is null
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr"
            : language == "tr";

    static readonly Dictionary<string, (string Tr, string En)> Text = new()
    {
        ["Open"] = ("Kota'yı aç", "Open Kota"),
        ["RefreshNow"] = ("Şimdi yenile", "Refresh now"),
        ["Settings"] = ("Ayarlar", "Settings"),
        ["StartWithWindows"] = ("Windows ile başlat", "Start with Windows"),
        ["Notifications"] = ("Limit bildirimleri (%80, %95)", "Limit notifications (80%, 95%)"),
        ["IconShows"] = ("Simgede göster", "Icon shows"),
        ["IconHighest"] = ("En yüksek kullanım", "Highest usage"),
        ["Language"] = ("Dil", "Language"),
        ["LanguageAuto"] = ("Windows ile aynı", "Same as Windows"),
        ["GitHub"] = ("GitHub sayfası", "GitHub page"),
        ["Exit"] = ("Çıkış", "Exit"),
        ["Updated"] = ("{0} itibarıyla", "as of {0}"),
        ["Loading"] = ("Yükleniyor…", "Loading…"),

        ["WinSession"] = ("5 saatlik oturum", "5-hour session"),
        ["WinWeekly"] = ("Haftalık", "Weekly"),
        ["Win5h"] = ("5 saatlik", "5-hour"),
        ["WinMonthly"] = ("Aylık", "Monthly"),
        ["WinHours"] = ("{0} saatlik", "{0}-hour"),
        ["WinDays"] = ("{0} günlük", "{0}-day"),
        ["WinGeneric"] = ("Limit", "Limit"),

        ["ResetsIn"] = ("{0} sonra sıfırlanır", "Resets in {0}"),
        ["WindowReset"] = ("Sıfırlandı", "Reset"),
        ["PaceHit"] = ("bu hızla ~{0} içinde dolar", "full in ~{0} at this pace"),
        ["PaceSafe"] = ("bu hızla ~{0} ile biter", "on pace for ~{0}"),
        ["PaceFull"] = ("limit doldu", "limit reached"),
        ["CodexAsOf"] = ("Codex'in son kaydı: {0} önce", "From Codex's last activity, {0} ago"),

        ["Last7Days"] = ("Son 7 gün", "Last 7 days"),
        ["ApiEquivalent"] = ("API karşılığı", "API equivalent"),
        ["ApiEquivalentHint"] = (
            "Bu kullanımın Anthropic API liste fiyatlarıyla tahmini maliyeti. Aboneliğinizden ayrıca ücret alınmaz.",
            "Estimated cost of this usage at Anthropic API list prices. Your subscription is not billed extra."),
        ["Scanning"] = ("Kayıtlar taranıyor…", "Scanning logs…"),
        ["Today"] = ("Bugün", "Today"),
        ["Week"] = ("7 gün", "7 days"),
        ["TokensUnit"] = ("{0} token", "{0} tokens"),
        ["Projects"] = ("Projeler · 7 gün", "Projects · 7 days"),
        ["Footer"] = ("Kota {0} · Veriler yalnızca bu bilgisayardan okunur", "Kota {0} · Reads data on this PC only"),

        ["AlertTitle"] = ("{0} · {1} {2}", "{0} · {1} at {2}"),
        ["AlertBody"] = ("{0}.", "{0}."),

        ["ErrNotInstalledClaude"] = ("Claude Code bulunamadı.", "Claude Code not found."),
        ["ErrNotInstalledCodex"] = ("Codex bulunamadı.", "Codex not found."),
        ["ErrNotLoggedIn"] = ("Giriş yapılmamış. Terminalde `claude` çalıştırıp giriş yapın.",
            "Not signed in. Run `claude` in a terminal and sign in."),
        ["ErrTokenExpired"] = ("Oturumun süresi dolmuş. Claude Code'u bir kez açınca yenilenir.",
            "Session expired. Opening Claude Code once refreshes it."),
        ["ErrRateLimited"] = ("Çok sık sorgulandı, birazdan tekrar denenecek.", "Rate limited, retrying shortly."),
        ["ErrNetwork"] = ("İnternet bağlantısı yok, son bilinen değerler gösteriliyor.", "Offline, showing last known values."),
        ["ErrNoDataClaude"] = ("Limit bilgisi gelmedi.", "No limit data returned."),
        ["ErrNoDataCodex"] = ("Henüz limit kaydı yok. Codex'i bir kez kullanınca görünür.",
            "No limit data yet. It appears after you use Codex once."),
        ["ErrUnknown"] = ("Okunamadı ({0}).", "Couldn't read ({0})."),
    };

    public static string T(string key) => Text.TryGetValue(key, out var v) ? (Turkish ? v.Tr : v.En) : key;

    public static string F(string key, params object[] args) => string.Format(Culture, T(key), args);

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) t = TimeSpan.FromMinutes(1);
        int d = (int)t.TotalDays, h = t.Hours, m = t.Minutes;
        if (Turkish) return d > 0 ? $"{d} g {h} sa" : h > 0 ? $"{h} sa {m} dk" : $"{m} dk";
        return d > 0 ? $"{d}d {h}h" : h > 0 ? $"{h}h {m}m" : $"{m}m";
    }

    public static string Number(long n)
    {
        string[] tr = ["", " bin", " mn", " mr"];
        string[] en = ["", "K", "M", "B"];
        double v = n;
        int i = 0;
        while (v >= 1000 && i < 3)
        {
            v /= 1000;
            i++;
        }
        var s = i == 0 ? v.ToString("0", Culture) : v.ToString(v < 10 ? "0.0" : "0", Culture);
        return s + (Turkish ? tr[i] : en[i]);
    }

    public static string Tokens(long n) => F("TokensUnit", Number(n));

    public static string Money(decimal d) => "$" + d.ToString(d >= 100 ? "N0" : "N2", Culture);

    public static string Percent(double p)
    {
        var n = Math.Round(p).ToString(Culture);
        return Turkish ? "%" + n : n + "%";
    }

    public static string ProviderName(Provider p) => p == Provider.Claude ? "Claude" : "Codex";

    public static string Plan(string plan) => plan.Length == 0 ? "" : char.ToUpperInvariant(plan[0]) + plan[1..];

    public static string WindowLabel(LimitWindow w) => w.Key switch
    {
        "five_hour" => T("WinSession"),
        "seven_day" => T("WinWeekly"),
        "seven_day_opus" => T("WinWeekly") + " · Opus",
        "seven_day_sonnet" => T("WinWeekly") + " · Sonnet",
        _ => LengthLabel(w.Length),
    };

    static string LengthLabel(TimeSpan length)
    {
        if (length == TimeSpan.FromHours(5)) return T("Win5h");
        if (length == TimeSpan.FromDays(7)) return T("WinWeekly");
        if (length.TotalDays is >= 28 and <= 31) return T("WinMonthly");
        if (length.TotalHours is >= 1 and < 24) return F("WinHours", (int)length.TotalHours);
        if (length.TotalDays >= 1) return F("WinDays", (int)length.TotalDays);
        return T("WinGeneric");
    }

    public static string Error(Provider p, LimitError e) => e.Kind switch
    {
        LimitErrorKind.NotInstalled => T(p == Provider.Claude ? "ErrNotInstalledClaude" : "ErrNotInstalledCodex"),
        LimitErrorKind.NotLoggedIn => T("ErrNotLoggedIn"),
        LimitErrorKind.TokenExpired => T("ErrTokenExpired"),
        LimitErrorKind.RateLimited => T("ErrRateLimited"),
        LimitErrorKind.Network => T("ErrNetwork"),
        LimitErrorKind.NoData => T(p == Provider.Claude ? "ErrNoDataClaude" : "ErrNoDataCodex"),
        _ => F("ErrUnknown", e.Detail ?? "?"),
    };
}
