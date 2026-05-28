using System;
using System.IO;
using System.Windows;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;  
using Windows.Devices.Enumeration;

namespace BluetoothLock
{
    public partial class SettingsWindow : Window
    {
        private MainWindow _mainWindow;

        public SettingsWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            // 从配置文件读取
            string configPath = System.IO.Path.Combine(AppContext.BaseDirectory, "config.txt");
            if (File.Exists(configPath))
            {
                var lines = File.ReadAllLines(configPath);
                foreach (var line in lines)
                {
                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    if (parts[0] == "RssiThreshold")
                    {
                        RssiThresholdBox.Text = parts[1];
                    }
                    else if (parts[0] == "UseRssiMode")
                    {
                        if (bool.TryParse(parts[1], out bool use))
                            UseRssiCheckBox.IsChecked = use;
                    }
                    else if (parts[0] == "AutoStart")
                    {
                        if (bool.TryParse(parts[1], out bool auto))
                            AutoStartCheckBox.IsChecked = auto;
                    }
                }
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            var device = _mainWindow.GetMonitoredDevice();
            if (device == null)
            {
                RssiResultText.Text = "未监控设备，请先返回主界面开始监控。";
                return;
            }
            var status = device.ConnectionStatus;
            short? rssi = await _mainWindow.ReadRssiForDevice();
            RssiResultText.Text = $"状态: {status}, RSSI: {(rssi.HasValue ? rssi.Value.ToString() : "无")}";
        }

        private async void RecordRssi_Click(object sender, RoutedEventArgs e)
        {
            var device = _mainWindow.GetMonitoredDevice();
            if (device == null)
            {
                RssiResultText.Text = "未监控设备";
                return;
            }
            short? rssi = await _mainWindow.ReadRssiForDevice();
            if (rssi.HasValue)
            {
                _mainWindow.AddRssiLog(rssi.Value);
                RssiResultText.Text = $"已记录 RSSI: {rssi.Value} dBm";
            }
            else
            {
                RssiResultText.Text = "RSSI 读取失败";
            }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            short? threshold = null;
            if (!string.IsNullOrWhiteSpace(RssiThresholdBox.Text))
            {
                if (short.TryParse(RssiThresholdBox.Text, out short t))
                    threshold = t;
                else
                {
                    SaveStatusText.Text = "阈值格式错误";
                    return;
                }
            }
            bool useRssi = UseRssiCheckBox.IsChecked ?? false;
            bool autoStart = AutoStartCheckBox.IsChecked ?? false;

            _mainWindow.SaveSettings(threshold, useRssi, autoStart);
            SaveStatusText.Text = "设置已保存";
        }

        private void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // 保存由 SaveSettings 统一执行，此处可忽略，也可立即保存
            // 为了体验，立即更新快捷方式
            bool autoStart = AutoStartCheckBox.IsChecked ?? false;
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, "蓝牙锁屏监控.lnk");
            if (autoStart)
            {
                CreateShortcut(shortcutPath);
            }
            else
            {
                if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
            }
        }

        private void CreateShortcut(string shortcutPath)
        {
            try
            {
                var shell = new IWshRuntimeLibrary.WshShell();
                var shortcut = (IWshRuntimeLibrary.IWshShortcut)shell.CreateShortcut(shortcutPath);
                string exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = "蓝牙锁屏监控";
                shortcut.Save();
            }
            catch { }
        }
    }
}
