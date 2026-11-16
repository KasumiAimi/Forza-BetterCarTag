using System.IO;
using System.Windows;

namespace FH6GarageEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        if (args.Args is [ReleaseUpdates.FinishArgument, var exe, var token, var parent, var version, var hash])
        {
            Shutdown(ReleaseUpdates.Finish(exe, token, parent, version, hash) ? 0 : 1);
            return;
        }
        if (args.Args is ["--verify", var report])
        {
            try
            {
                Verification.Run(report);
                Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(report, error.ToString());
                Shutdown(1);
            }
            return;
        }
        var window = new MainWindow();
        if (args.Args is [ReleaseUpdates.ReadyArgument, var readyToken])
        {
            window.ContentRendered += (_, _) =>
            {
                try { ReleaseUpdates.SignalReady(readyToken); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            };
        }
        window.Show();
        _ = Task.Run(ReleaseUpdates.CleanOldHelpers);
    }
}
