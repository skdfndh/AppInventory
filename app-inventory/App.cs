using System.Windows;

namespace AppInventory;

internal sealed class App : Application
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--lab-uninstall") return UninstallLab.RunWorker(args[1], args[2], args[3]);
        if (args.Length == 2 && args[0] == "--self-test") return SelfTest.Run(args[1]);
        if (args.Length == 2 && args[0] == "--smoke-read") return SelfTest.ReadInstalled(args[1]);
        if (args.Length != 0) return 2;

        var application = new App();
        application.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show("界面遇到错误：" + e.Exception.Message, "应用清单", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        return application.Run(new MainWindow());
    }
}
