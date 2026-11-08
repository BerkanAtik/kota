using System;
using System.IO;
using System.Text.Json;
using Kota.Core;
using Microsoft.Win32;

namespace Kota.App;

sealed class Settings
{
    public bool Notifications { get; set; } = true;
    public int[] Thresholds { get; set; } = [80, 95];

    /// <summary>"highest", "claude" or "codex".</summary>
    public string IconSource { get; set; } = "highest";

    public int ClaudeRefreshMinutes { get; set; } = 3;

    /// <summary>null follows Windows; otherwise "tr" or "en".</summary>
    public string? Language { get; set; }

    static string FilePath => Path.Combine(KnownPaths.KotaDataDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(KnownPaths.KotaDataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Kota";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If the user moved Kota.exe, point the startup entry at the new location.</summary>
    public static void RepairPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && current != Command) key.SetValue(ValueName, Command);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}
