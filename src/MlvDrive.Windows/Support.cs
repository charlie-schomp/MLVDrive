// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
using System.Text.Json;
using Microsoft.Win32;

namespace MlvDrive.Windows;

/// <summary>Small rolling log in %LOCALAPPDATA%\MLV Drive.</summary>
static class Log
{
    static readonly object Lock = new();
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MLV Drive");
    public static string FilePath { get; } = Path.Combine(Folder, "mlvdrive.log");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                var fi = new System.IO.FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>User settings, stored as JSON next to the log.</summary>
sealed class Settings
{
    public char DriveLetter { get; set; } = 'M';
    /// <summary>Frames per second written into the DNGs instead of the recorded rate (null = as recorded).</summary>
    public double? FrameRateOverride { get; set; }
    /// <summary>Blend Dual ISO clips into one exposure.</summary>
    public bool MergeDualIso { get; set; } = true;
    /// <summary>Folders to show in addition to removable and non-system drives.</summary>
    public List<string> ExtraFolders { get; set; } = new();
    /// <summary>Also look on the Windows drive (slow to scan, off by default).</summary>
    public bool IncludeSystemDrive { get; set; }
    public int ScanDepth { get; set; } = 8;
    public bool ShowNotifications { get; set; } = true;

    static string PathOnDisk => Path.Combine(Log.Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOnDisk)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write("settings unreadable, using defaults: " + ex.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("could not save settings: " + ex.Message);
        }
    }
}

/// <summary>Start with Windows: a per-user Run entry (no administrator rights involved).</summary>
static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "MLV Drive";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string v && v.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue(Name, Command);
            else k.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}

/// <summary>Is the WinFsp driver installed (its user-mode DLL is what the drive needs)?</summary>
static class WinFspCheck
{
    public static string? InstallDir()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = hklm.OpenSubKey(@"SOFTWARE\WinFsp");
            if (k?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "bin", "winfsp-x64.dll")))
                return dir;
        }
        return null;
    }
}
