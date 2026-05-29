using System.Windows;

namespace BluetoothLock
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            MainWindow mainWindow = new MainWindow();
            mainWindow.Hide();   // 启动后最小化到托盘
        }
    }
}
