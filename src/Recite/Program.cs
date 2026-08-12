namespace Recite;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WaitForTakeover(args);

        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirstInstance)
        {
            SingleInstance.SignalExisting();
            return;
        }

        SelfTidy.Run();

        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);

        using var context = new TrayContext(instance);
        Application.Run(context);
    }

    /// <summary>"Restart as administrator" hands us the old instance's pid: grabbing
    /// the single-instance mutex before it dies used to end the relaunch as
    /// "already running", leaving nothing running at all.</summary>
    private static void WaitForTakeover(string[] args)
    {
        if (args is ["--takeover", var pidText] && int.TryParse(pidText, out int pid))
        {
            try
            {
                System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000);
            }
            catch
            {
                // Already gone — exactly what we were waiting for.
            }
        }
    }

    private static void Report(Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        MessageBox.Show(
            ex.Message, $"{AppInfo.Name} error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
