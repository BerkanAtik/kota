using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Kota.Core;

namespace Kota.App;

public partial class FlyoutWindow : Window
{
    const double ShadowMargin = 12;
    bool _closing;

    public FlyoutWindow() => InitializeComponent();

    public event Action? RefreshRequested;
    public event Action? MenuRequested;

    public DateTime LastHidden { get; private set; }

    // ---------------- window behavior ----------------

    public void ShowNearTray()
    {
        Show();
        Reposition();
        Activate();
    }

    void Reposition()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth + ShadowMargin / 2;
        Top = area.Bottom - ActualHeight + ShadowMargin / 2;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!IsVisible) return;
        Hide();
        LastHidden = DateTime.UtcNow;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 should only hide the flyout; the app lives in the tray.
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void CloseForReal()
    {
        _closing = true;
        Close();
    }

    void OnRefresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    void OnMenu(object sender, RoutedEventArgs e) => MenuRequested?.Invoke();

    // ---------------- rendering ----------------

    internal void Render(AppState s)
    {
        UpdatedText.Text = s.LastUpdated is { } u ? L.F("Updated", u.ToLocalTime().ToString("HH:mm", L.Culture)) : "";
        RefreshButton.ToolTip = L.T("RefreshNow");
        MenuButton.ToolTip = L.T("Settings");

        ProvidersPanel.Children.Clear();
        bool claudeMissing = s.Claude?.Error?.Kind == LimitErrorKind.NotInstalled;
        bool codexMissing = s.Codex?.Error?.Kind == LimitErrorKind.NotInstalled;
        if (!claudeMissing || codexMissing) ProvidersPanel.Children.Add(ProviderCard(Provider.Claude, s.Claude, s.Now));
        if (!codexMissing) ProvidersPanel.Children.Add(ProviderCard(Provider.Codex, s.Codex, s.Now));

        RenderHistory(s);
        FooterText.Text = L.F("Footer", TrayController.Version);
        if (IsVisible) Reposition();
    }

    UIElement ProviderCard(Provider provider, ProviderLimits? limits, DateTimeOffset now)
    {
        var stack = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var plan = Label(limits?.Plan is { } p ? L.Plan(p) : "", 11.5, sub: true);
        DockPanel.SetDock(plan, Dock.Right);
        header.Children.Add(plan);
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, provider == Provider.Claude ? "ClaudeAccent" : "CodexAccent");
        header.Children.Add(dot);
        header.Children.Add(Label(L.ProviderName(provider), 13.5, weight: FontWeights.SemiBold));
        stack.Children.Add(header);

        if (limits is null)
        {
            stack.Children.Add(Label(L.T("Loading"), 11.5, sub: true));
            return Card(stack);
        }

        foreach (var window in limits.Windows) stack.Children.Add(WindowRow(window.Normalize(now), now));

        if (limits.Error is { } error)
        {
            var text = Label(L.Error(provider, error), 11.5, sub: true);
            text.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(text);
        }
        else if (provider == Provider.Codex && limits.DataAsOf is { } asOf && now - asOf > TimeSpan.FromMinutes(10))
        {
            var text = Label(L.F("CodexAsOf", L.Duration(now - asOf)), 11, sub: true);
            text.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(text);
        }
        return Card(stack);
    }

    static UIElement WindowRow(LimitWindow w, DateTimeOffset now)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 5, 0, 3) };

        var top = new DockPanel();
        var percent = Label(L.Percent(w.Percent), 13, weight: FontWeights.SemiBold);
        DockPanel.SetDock(percent, Dock.Right);
        top.Children.Add(percent);
        top.Children.Add(Label(L.WindowLabel(w), 12.5));
        panel.Children.Add(top);

        double value = Math.Clamp(w.Percent, 0, 100);
        var bar = new Grid { Height = 6, Margin = new Thickness(0, 5, 0, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(value, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - value, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "Track");
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        if (value > 0)
            bar.Children.Add(new Border
            {
                // Fills the first star column; MinWidth keeps 1-2% visible as a rounded dot.
                CornerRadius = new CornerRadius(3), MinWidth = 6, Background = Theme.SeverityBrush(w.Percent),
            });
        panel.Children.Add(bar);

        var detail = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Sub");
        detail.Inlines.Add(w.ResetsAt is { } reset ? L.F("ResetsIn", L.Duration(reset - now)) : L.T("WindowReset"));
        var pace = Pace.Estimate(w, now);
        string? paceText = pace.Kind switch
        {
            PaceKind.WillHitLimit => L.F("PaceHit", L.Duration(pace.TimeToFull!.Value)),
            PaceKind.Safe => L.F("PaceSafe", L.Percent(pace.ProjectedPercent!.Value)),
            PaceKind.Full => L.T("PaceFull"),
            _ => null,
        };
        if (paceText is not null)
        {
            detail.Inlines.Add(" · ");
            var run = new Run(paceText);
            if (pace.Kind is PaceKind.WillHitLimit or PaceKind.Full)
            {
                run.Foreground = Theme.SeverityBrush(100);
                run.FontWeight = FontWeights.SemiBold;
            }
            detail.Inlines.Add(run);
        }
        panel.Children.Add(detail);
        return panel;
    }

    void RenderHistory(AppState s)
    {
        HistoryTitle.Text = L.T("Last7Days");
        HistoryHint.Text = L.T("ApiEquivalent") + " ⓘ";
        HistoryHint.ToolTip = new ToolTip { Content = L.T("ApiEquivalentHint"), MaxWidth = 280 };
        ProjectsTitle.Text = L.T("Projects");
        StatsGrid.Children.Clear();
        ChartGrid.Children.Clear();
        ChartGrid.ColumnDefinitions.Clear();
        ChartLabels.Children.Clear();
        ChartLabels.ColumnDefinitions.Clear();
        ProjectsPanel.Children.Clear();

        if (s.Usage is null)
        {
            var scanning = Label(L.T("Scanning"), 11.5, sub: true);
            Grid.SetColumnSpan(scanning, 2);
            StatsGrid.Children.Add(scanning);
            ChartGrid.Visibility = ChartLabels.Visibility = ProjectsCard.Visibility = Visibility.Collapsed;
            return;
        }
        ChartGrid.Visibility = ChartLabels.Visibility = Visibility.Visible;

        var days = UsageStats.LastDays(s.Usage, 7, s.Now, TimeZoneInfo.Local);
        var today = days[^1];
        var week = (Claude: new TokenTotals(), Codex: new TokenTotals());
        foreach (var d in days)
        {
            week.Claude.Add(d.Claude);
            week.Codex.Add(d.Codex);
        }
        AddStat(0, L.T("Today"), today.Claude.Cost, today.Claude.Total + today.Codex.Total);
        AddStat(1, L.T("Week"), week.Claude.Cost, week.Claude.Total + week.Codex.Total);

        // Bar chart: one column per day, Claude stacked under Codex.
        long max = Math.Max(1, days.Max(d => d.Claude.Total + d.Codex.Total));
        const double chartHeight = 60;
        for (int i = 0; i < days.Count; i++)
        {
            var day = days[i];
            ChartGrid.ColumnDefinitions.Add(new ColumnDefinition());
            ChartLabels.ColumnDefinitions.Add(new ColumnDefinition());

            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 4, 0), Background = Brushes.Transparent };
            column.Children.Add(Bar(day.Codex.Total * chartHeight / max, "CodexAccent", top: true));
            column.Children.Add(Bar(day.Claude.Total * chartHeight / max, "ClaudeAccent", top: day.Codex.Total == 0));
            if (day.Claude.Total + day.Codex.Total == 0) column.Children.Add(Bar(2, "Track", top: true));
            var date = day.Day.ToDateTime(TimeOnly.MinValue);
            column.ToolTip = $"{date.ToString("d MMMM, dddd", L.Culture)}\n"
                             + $"Claude: {L.Tokens(day.Claude.Total)} · {L.Money(day.Claude.Cost)}\n"
                             + $"Codex: {L.Tokens(day.Codex.Total)}";
            Grid.SetColumn(column, i);
            ChartGrid.Children.Add(column);

            var label = Label(date.ToString("ddd", L.Culture), 11, sub: i != days.Count - 1,
                weight: i == days.Count - 1 ? FontWeights.SemiBold : FontWeights.Normal);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(label, i);
            ChartLabels.Children.Add(label);
        }

        var since = new DateTimeOffset(days[0].Day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
        var projects = UsageStats.TopProjects(s.Usage, since, 5);
        ProjectsCard.Visibility = projects.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var p in projects) ProjectsPanel.Children.Add(ProjectRow(p));
    }

    void AddStat(int column, string title, decimal cost, long tokens)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(title, 11.5, sub: true));
        panel.Children.Add(Label(L.Money(cost), 20, weight: FontWeights.SemiBold));
        panel.Children.Add(Label(L.Tokens(tokens), 11.5, sub: true));
        Grid.SetColumn(panel, column);
        StatsGrid.Children.Add(panel);
    }

    static Border Bar(double height, string brushKey, bool top)
    {
        var bar = new Border
        {
            Height = height <= 0 ? 0 : Math.Max(2, height),
            CornerRadius = top ? new CornerRadius(3, 3, 0, 0) : new CornerRadius(0),
        };
        bar.SetResourceReference(Border.BackgroundProperty, brushKey);
        return bar;
    }

    static UIElement ProjectRow(ProjectTotal p)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        var name = Label(p.Project, 12.5);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = p.Project;
        grid.Children.Add(name);

        var tokens = Label(L.Tokens(p.ClaudeTokens + p.CodexTokens), 11.5, sub: true);
        tokens.Margin = new Thickness(8, 0, 8, 0);
        tokens.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(tokens, 1);
        grid.Children.Add(tokens);

        var cost = Label(p.Cost > 0 ? L.Money(p.Cost) : "–", 12.5, weight: FontWeights.SemiBold);
        cost.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(cost, 2);
        grid.Children.Add(cost);
        return grid;
    }

    Border Card(UIElement child) => new() { Style = (Style)FindResource("CardStyle"), Child = child };

    static TextBlock Label(string text, double size, bool sub = false, FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        if (sub) block.SetResourceReference(TextBlock.ForegroundProperty, "Sub");
        return block;
    }
}
