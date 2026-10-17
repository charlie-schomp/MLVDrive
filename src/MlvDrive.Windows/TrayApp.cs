using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using Fsp;
using MlvDrive.Core.Dng;
using MlvDrive.Core.VirtualFs;

namespace MlvDrive.Windows;

/// <summary>
/// The whole app: a tray icon, the virtual drive, and the watcher that keeps it current.
/// Nothing to open or configure for the normal case - clips appear on the drive when the
/// disk holding them is connected.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    readonly Settings _settings;
    readonly ClipCache _cache;
    readonly NotifyIcon _icon;
    readonly SourceWatcher _watcher;
    readonly System.Windows.Forms.Timer _releaseTimer;
    readonly SynchronizationContext _ui;
    WinFspDrive? _drive;
    FileSystemHost? _host;
    string _status = "Looking for clips...";
    HashSet<string> _shownSources = new(StringComparer.OrdinalIgnoreCase);

    string MountPoint => _settings.DriveLetter + ":";

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = Settings.Load();
        _cache = new ClipCache(new DngOptions(_settings.FrameRateOverride));

        _icon = new NotifyIcon { Icon = MakeIcon(), Text = "MLV Drive", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu(_icon.ContextMenuStrip);
        _icon.DoubleClick += (_, _) => OpenDrive();
        BuildMenu(_icon.ContextMenuStrip);

        Mount(new StaticDirectory("", DateTime.UtcNow));
        _watcher = new SourceWatcher(() => _settings, _cache, (tree, sources) => _ui.Post(_ => OnTree(tree, sources), null));
        _watcher.RequestScan(0);

        // Release clips nobody has read for a while, so their drives can be ejected.
        _releaseTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _releaseTimer.Tick += (_, _) => _cache.ReleaseIdle(TimeSpan.FromSeconds(10));
        _releaseTimer.Start();
    }

    void Mount(VDirectory root)
    {
        Unmount();
        _drive = new WinFspDrive(root, "MLV Drive");
        _host = new FileSystemHost(_drive) { Prefix = null };
        int status = _host.Mount(MountPoint, null, false, 0);
        if (status != 0)
        {
            Log.Write($"mount {MountPoint} failed: 0x{status:X8}");
            _status = $"Could not use {MountPoint} (in use?). Pick another letter.";
            _host.Dispose();
            _host = null;
            _drive = null;
            ShowBalloon("MLV Drive", $"Drive {MountPoint} is in use. Right-click the tray icon to choose another letter.", ToolTipIcon.Warning);
            return;
        }
        Log.Write($"mounted {MountPoint}");
    }

    void Unmount()
    {
        if (_host is null) return;
        try { _host.Unmount(); } catch (Exception ex) { Log.Write("unmount: " + ex.Message); }
        _host.Dispose();
        _host = null;
        _drive = null;
    }

    void OnTree(StaticDirectory tree, IReadOnlyList<ClipSource> sources)
    {
        _drive?.SetRoot(tree);
        int clips = CountClips(tree);
        var withClips = tree.Children().Select(c => c.Name).ToList();
        _status = clips == 0 ? "No MLV clips on any connected drive"
                             : $"{clips} clip{(clips == 1 ? "" : "s")} on {string.Join(", ", withClips)}";
        _icon.Text = Truncate($"MLV Drive ({MountPoint}) - {_status}", 63);

        var fresh = withClips.Where(n => !_shownSources.Contains(n)).ToList();
        if (fresh.Count > 0 && _host is not null && _settings.ShowNotifications)
            ShowBalloon("MLV Drive", $"Clips from {string.Join(", ", fresh)} are on {MountPoint}\\ - drag them into Resolve from there.", ToolTipIcon.Info);
        _shownSources = withClips.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    static int CountClips(VDirectory d) =>
        d.Children().Sum(c => c is ClipDirectory ? 1 : c is StaticDirectory s ? CountClips(s) : 0);

    void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem($"MLV Drive  -  {MountPoint}\\") { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem(_status) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open drive", null, (_, _) => OpenDrive());
        menu.Items.Add("Look for clips again", null, (_, _) => _watcher.RequestScan(0));

        var fps = new ToolStripMenuItem("Frame rate in DNGs");
        void FpsItem(string text, double? value) =>
            fps.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => SetFrameRate(value)) { Checked = _settings.FrameRateOverride == value });
        FpsItem("As recorded", null);
        foreach (var v in new[] { 23.976, 24, 25, 29.97, 30, 48, 50, 59.94, 60 })
            FpsItem(v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), v);
        menu.Items.Add(fps);

        var letters = new ToolStripMenuItem("Drive letter");
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        for (char c = 'M'; c <= 'Z'; c++)
        {
            char letter = c;
            bool mine = letter == _settings.DriveLetter;
            if (used.Contains(letter) && !mine) continue;
            letters.DropDownItems.Add(new ToolStripMenuItem(letter + ":", null, (_, _) => SetLetter(letter)) { Checked = mine });
        }
        menu.Items.Add(letters);

        var folders = new ToolStripMenuItem("Also show a folder...", null, (_, _) => AddFolder());
        menu.Items.Add(folders);
        if (_settings.ExtraFolders.Count > 0)
        {
            var remove = new ToolStripMenuItem("Stop showing");
            foreach (var f in _settings.ExtraFolders.ToList())
                remove.DropDownItems.Add(f, null, (_, _) => { _settings.ExtraFolders.Remove(f); _settings.Save(); _watcher.RequestScan(0); });
            menu.Items.Add(remove);
        }

        bool autostart = SafeAutostart();
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Checked = autostart });
        menu.Items.Add(new ToolStripMenuItem("Notifications", null, (_, _) => { _settings.ShowNotifications ^= true; _settings.Save(); }) { Checked = _settings.ShowNotifications });
        menu.Items.Add("Open log", null, (_, _) => Shell(Log.FilePath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
    }

    void SetFrameRate(double? fps)
    {
        _settings.FrameRateOverride = fps;
        _settings.Save();
        _cache.Options = new DngOptions(fps);
        _cache.Clear();
        _watcher.RequestScan(0);
    }

    void SetLetter(char letter)
    {
        if (letter == _settings.DriveLetter && _host is not null) return;
        _settings.DriveLetter = letter;
        _settings.Save();
        Mount(new StaticDirectory("", DateTime.UtcNow));
        _shownSources.Clear();
        _watcher.RequestScan(0);
    }

    void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Show the MLV clips in this folder on the virtual drive", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        if (!_settings.ExtraFolders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            _settings.ExtraFolders.Add(dlg.SelectedPath);
            _settings.Save();
        }
        _watcher.RequestScan(0);
    }

    static bool SafeAutostart()
    {
        try { return Autostart.Enabled; } catch (Exception) { return false; }
    }

    void ToggleAutostart()
    {
        try { Autostart.Enabled = !Autostart.Enabled; }
        catch (Exception ex) { Log.Write("autostart: " + ex.Message); }
    }

    void OpenDrive()
    {
        if (_host is null) { ShowBalloon("MLV Drive", "The drive is not mounted. Pick a drive letter from the tray menu.", ToolTipIcon.Warning); return; }
        Shell(MountPoint + "\\");
    }

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"open {target}: {ex.Message}"); }
    }

    void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(5000);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    /// <summary>A simple drive-with-film icon drawn at runtime (no binary assets in the repo).</summary>
    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var body = new SolidBrush(Color.FromArgb(0x2B, 0x6C, 0xB0));
            g.FillRectangle(body, 2, 6, 28, 20);
            using var holes = new SolidBrush(Color.White);
            for (int x = 4; x < 30; x += 5)
            {
                g.FillRectangle(holes, x, 8, 3, 3);
                g.FillRectangle(holes, x, 21, 3, 3);
            }
            using var font = new Font("Segoe UI", 8, FontStyle.Bold);
            g.DrawString("MLV", font, holes, 3, 10);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        _releaseTimer.Stop();
        _watcher.Dispose();
        Unmount();
        _cache.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        Log.Write("exit");
        base.ExitThreadCore();
    }
}
