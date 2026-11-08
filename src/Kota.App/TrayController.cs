using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kota.Core;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Kota.App;

sealed record AppState(
    ProviderLimits? Claude,
    ProviderLimits? Codex,
    IReadOnlyDictionary<BucketKey, TokenTotals>? Usage,
    DateTimeOffset? LastUpdated,
    DateTimeOffset Now);

/// <summary>Owns the tray icon, polling and notifications.</summary>
sealed class TrayController : IDisposable
{
    public const string RepoUrl = "https://github.com/BerkanAtik/kota";

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MinForcedClaudeGap = TimeSpan.FromSeconds(30);

    readonly Settings _settings;
    readonly Forms.NotifyIcon _icon = new();
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly ClaudeLimitsClient _claude;
    readonly CodexLimitsReader _codex = new(KnownPaths.CodexDir);
    readonly UsageHistory _history = new(KnownPaths.ClaudeDir, KnownPaths.CodexDir,
        Path.Combine(KnownPaths.KotaDataDir, "usage-cache.bin"));
    readonly AlertTracker _alerts = new();
    readonly DispatcherTimer _timer = new() { Interval = PollInterval };

    FlyoutWindow? _flyout;
    ProviderLimits? _claudeLimits, _codexLimits;
    IReadOnlyDictionary<BucketKey, TokenTotals>? _usage;
    DateTimeOffset? _lastUpdated;
    DateTimeOffset _nextClaudeFetch = DateTimeOffset.MinValue, _lastClaudeFetch = DateTimeOffset.MinValue;
    TimeSpan _rateLimitBackoff = TimeSpan.FromMinutes(5);
    bool _limitsBusy, _historyBusy, _historyLoaded;
    Drawing.Icon? _currentIcon;
    string? _iconText;

    readonly bool _demo;

    public TrayController(Settings settings, bool demo = false)
    {
        _settings = settings;
        _demo = demo;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Kota/{Version} (+{RepoUrl})");
        _claude = new ClaudeLimitsClient(_http, KnownPaths.ClaudeDir);
        _timer.Tick += (_, _) => Refresh(forceClaude: false);
    }

    public static string Version => typeof(TrayController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public void Start()
    {
        SetIcon(null);
        _icon.Text = "Kota";
        _icon.ContextMenuStrip = BuildMenu();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleFlyout();
        };
        _icon.BalloonTipClicked += (_, _) => ShowFlyout();
        _icon.Visible = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _timer.Start();
        Refresh(forceClaude: true);
    }

    void Refresh(bool forceClaude)
    {
        if (_demo)
        {
            var now = DateTimeOffset.UtcNow;
            _claudeLimits = DemoData.Claude(now);
            _codexLimits = DemoData.Codex(now);
            _usage = DemoData.Usage(now);
            _lastUpdated = now;
            UpdateUi();
            return;
        }
        _ = RefreshLimitsAsync(forceClaude);
        _ = RefreshHistoryAsync();
    }

    async Task RefreshLimitsAsync(bool forceClaude)
    {
        if (_limitsBusy) return;
        _limitsBusy = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            _codexLimits = await Task.Run(() => _codex.Read(now));

            bool forcedAllowed = forceClaude && now - _lastClaudeFetch >= MinForcedClaudeGap;
            if (forcedAllowed || now >= _nextClaudeFetch)
            {
                var result = await _claude.FetchAsync(CancellationToken.None);
                _lastClaudeFetch = now;
                _nextClaudeFetch = now + NextClaudeDelay(result);
                // On a temporary error keep showing the last good numbers, with the error underneath.
                _claudeLimits = result.Error is not null && _claudeLimits is { Windows.Count: > 0 } previous
                    ? previous with { Error = result.Error }
                    : result;
            }
            _lastUpdated = now;

            foreach (var limits in new[] { _claudeLimits, _codexLimits })
            {
                if (limits is null) continue;
                var alerts = _alerts.Check(limits, _settings.Thresholds);
                if (_settings.Notifications) foreach (var alert in alerts) Notify(alert, now);
            }
            UpdateUi();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _limitsBusy = false;
        }
    }

    async Task RefreshHistoryAsync()
    {
        if (_historyBusy) return;
        _historyBusy = true;
        try
        {
            _usage = await Task.Run(() =>
            {
                if (!_historyLoaded)
                {
                    _history.Load();
                    _historyLoaded = true;
                }
                return _history.Refresh();
            });
            UpdateUi();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _historyBusy = false;
        }
    }

    TimeSpan NextClaudeDelay(ProviderLimits result)
    {
        switch (result.Error?.Kind)
        {
            case null:
                _rateLimitBackoff = TimeSpan.FromMinutes(5);
                return TimeSpan.FromMinutes(Math.Clamp(_settings.ClaudeRefreshMinutes, 1, 60));
            case LimitErrorKind.RateLimited:
                var delay = _rateLimitBackoff;
                _rateLimitBackoff = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(30).Ticks));
                return delay;
            case LimitErrorKind.Network:
            case LimitErrorKind.TokenExpired:
            case LimitErrorKind.NotLoggedIn:
                // Cheap: an expired token is detected locally, without a network call.
                return TimeSpan.FromMinutes(1);
            default:
                return TimeSpan.FromMinutes(5);
        }
    }

    AppState State(DateTimeOffset now) => new(_claudeLimits, _codexLimits, _usage, _lastUpdated, now);

    void UpdateUi()
    {
        var now = DateTimeOffset.UtcNow;
        double? claude = _claudeLimits?.MaxPercent(now), codex = _codexLimits?.MaxPercent(now);
        double? percent = _settings.IconSource switch
        {
            "claude" => claude,
            "codex" => codex,
            _ => claude is null ? codex : codex is null ? claude : Math.Max(claude.Value, codex.Value),
        };
        SetIcon(percent);
        _icon.Text = Tooltip(now);
        if (_flyout is { IsVisible: true }) _flyout.Render(State(now));
    }

    string Tooltip(DateTimeOffset now)
    {
        var lines = new List<string> { "Kota" };
        foreach (var limits in new[] { _claudeLimits, _codexLimits })
        {
            if (limits is not { Windows.Count: > 0 }) continue;
            var parts = limits.Windows.Select(w => w.Normalize(now))
                .Select(w => $"{L.WindowLabel(w)} {L.Percent(w.Percent)}");
            lines.Add($"{L.ProviderName(limits.Provider)}: {string.Join(", ", parts)}");
        }
        var text = string.Join("\n", lines);
        return text.Length <= 127 ? text : text[..126] + "…";
    }

    void SetIcon(double? percent)
    {
        string text = percent is { } p ? Math.Round(p).ToString() : "-";
        if (text == _iconText) return;
        _iconText = text;
        var old = _currentIcon;
        _currentIcon = TrayIconRenderer.Render(percent);
        _icon.Icon = _currentIcon;
        old?.Dispose();
    }

    void Notify(LimitAlert alert, DateTimeOffset now)
    {
        var window = alert.Window;
        var title = L.F("AlertTitle", L.ProviderName(alert.Provider), L.WindowLabel(window), L.Percent(window.Percent));
        var details = new List<string>();
        if (window.ResetsAt is { } reset) details.Add(L.F("ResetsIn", L.Duration(reset - now)));
        var pace = Pace.Estimate(window, now);
        if (pace is { Kind: PaceKind.WillHitLimit, TimeToFull: { } ttf }) details.Add(L.F("PaceHit", L.Duration(ttf)));
        _icon.ShowBalloonTip(10_000, title, L.F("AlertBody", string.Join(" · ", details)), Forms.ToolTipIcon.Warning);
    }

    // ---------------- flyout ----------------

    void ToggleFlyout()
    {
        if (_flyout is { IsVisible: true })
        {
            _flyout.Hide();
            return;
        }
        // Clicking the tray icon first deactivates (and hides) the open flyout; don't reopen it.
        if (_flyout is not null && (DateTime.UtcNow - _flyout.LastHidden).TotalMilliseconds < 350) return;
        ShowFlyout();
    }

    public void ShowFlyout()
    {
        if (_flyout is null)
        {
            _flyout = new FlyoutWindow();
            _flyout.RefreshRequested += () => Refresh(forceClaude: true);
            _flyout.MenuRequested += () => _icon.ContextMenuStrip?.Show(Forms.Cursor.Position);
        }
        _flyout.Render(State(DateTimeOffset.UtcNow));
        _flyout.ShowNearTray();
        Refresh(forceClaude: false);
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle)) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Theme.Apply(Application.Current.Resources);
            if (_flyout is { IsVisible: true }) _flyout.Render(State(DateTimeOffset.UtcNow));
        });
    }

    // ---------------- menu ----------------

    Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();

        var open = new Forms.ToolStripMenuItem(L.T("Open"), null, (_, _) => ShowFlyout());
        open.Font = new Drawing.Font(open.Font, Drawing.FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(L.T("RefreshNow"), null, (_, _) => Refresh(forceClaude: true));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var startup = new Forms.ToolStripMenuItem(L.T("StartWithWindows")) { CheckOnClick = true };
        try { startup.Checked = StartupRegistration.IsEnabled; } catch (Exception ex) { App.Log(ex); }
        startup.CheckedChanged += (_, _) =>
        {
            try { StartupRegistration.Set(startup.Checked); } catch (Exception ex) { App.Log(ex); }
        };
        menu.Items.Add(startup);

        var notifications = new Forms.ToolStripMenuItem(L.T("Notifications"))
            { CheckOnClick = true, Checked = _settings.Notifications };
        notifications.CheckedChanged += (_, _) =>
        {
            _settings.Notifications = notifications.Checked;
            _settings.Save();
        };
        menu.Items.Add(notifications);

        menu.Items.Add(RadioSubmenu(L.T("IconShows"),
            [("highest", L.T("IconHighest")), ("claude", "Claude"), ("codex", "Codex")],
            () => _settings.IconSource,
            key =>
            {
                _settings.IconSource = key;
                _iconText = null;
                UpdateUi();
            }));

        menu.Items.Add(RadioSubmenu(L.T("Language"),
            [("", L.T("LanguageAuto")), ("tr", "Türkçe"), ("en", "English")],
            () => _settings.Language ?? "",
            key =>
            {
                _settings.Language = key.Length == 0 ? null : key;
                L.Apply(_settings.Language);
                var old = _icon.ContextMenuStrip;
                _icon.ContextMenuStrip = BuildMenu();
                old?.Dispose();
                UpdateUi();
            }));

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("GitHub"), null, (_, _) => OpenUrl(RepoUrl));
        menu.Items.Add(L.T("Exit"), null, (_, _) => Application.Current.Shutdown());
        return menu;
    }

    Forms.ToolStripMenuItem RadioSubmenu(string title, (string Key, string Label)[] options, Func<string> current,
        Action<string> select)
    {
        var parent = new Forms.ToolStripMenuItem(title);
        foreach (var (key, label) in options)
        {
            var item = new Forms.ToolStripMenuItem(label) { Checked = current() == key };
            item.Click += (_, _) =>
            {
                select(key);
                _settings.Save();
                foreach (Forms.ToolStripItem other in parent.DropDownItems)
                    if (other is Forms.ToolStripMenuItem mi) mi.Checked = ReferenceEquals(mi, item);
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _timer.Stop();
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _currentIcon?.Dispose();
        _http.Dispose();
        _flyout?.CloseForReal();
    }
}
