namespace MlvDrive.Windows;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\MLVDrive-single-instance", out bool first);
        if (!first) return 0;                                  // already running: nothing to do

        ApplicationConfiguration.Initialize();

        if (WinFspCheck.InstallDir() is null)
        {
            MessageBox.Show(
                "MLV Drive needs WinFsp, the free virtual-drive driver it is built on.\n\n" +
                "Run INSTALL.cmd from the MLV Drive download again: it installs WinFsp from its official\n" +
                "release (signature checked), or install it yourself from https://winfsp.dev.",
                "MLV Drive", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        if (args.Contains("--enable-autostart")) { try { Autostart.Enabled = true; } catch (Exception ex) { Log.Write("autostart: " + ex.Message); } }

        Log.Write("start " + Application.ProductVersion);
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);
        Application.Run(new TrayApp());
        return 0;
    }
}
