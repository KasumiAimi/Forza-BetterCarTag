using System.IO;
using System.Windows;

namespace FH6GarageEditor;

public partial class App : Application
{
    private Mutex? instance;

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
        var gate = new Mutex(false, @"Local\FH6GarageEditor.Instance");
        try
        {
            if (!gate.WaitOne(0))
            {
                gate.Dispose();
                ExistingEditorWindow.Activate();
                Shutdown(0);
                return;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous editor exited without releasing the mutex. This thread now owns it.
        }
        instance = gate;

        var window = new MainWindow();
        window.SourceInitialized += (_, _) => ExistingEditorWindow.Mark(window);
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

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
