using System.IO;
using System.Windows;

namespace FH6GarageEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
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
        new MainWindow().Show();
    }
}
