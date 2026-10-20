using System.Windows;
namespace BikeGame;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(); MainWindow = window; window.Show();
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            try { await window.SmokeTest(e.Args[1]); Shutdown(0); }
            catch (Exception ex) { System.IO.File.WriteAllText(e.Args[1] + ".error.txt", ex.ToString()); Shutdown(1); }
        }
    }
}
