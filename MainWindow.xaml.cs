using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeFeature;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripSeparator = System.Windows.Forms.ToolStripSeparator;

namespace BluetoothLock
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();

        private BluetoothLEDevice? _monitoredDevice;
        private NotifyIcon? _notifyIcon;
        private bool _isMonitoring = false;
        private BluetoothConnectionStatus _lastStatus;

        // RSSI 记录列表
        private List<(DateTime Time, short Rssi)> _rssiLogs = new List<(DateTime, short)>();

        private static string DeviceIdFilePath =>
            Path.Combine(AppContext.BaseDirectory, "lastdevice.txt");

        public MainWindow()
        {
            InitializeComponent();
            InitializeTray();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadPairedDevices();
            string? lastDeviceId = null;
            if (File.Exists(DeviceIdFilePath))
            {
                try { lastDeviceId = File.ReadAllText(DeviceIdFilePath).Trim(); } catch { }
            }
            if (!string.IsNullOrEmpty(lastDeviceId))
            {
                var savedDevice = DeviceComboBox.Items.Cast<DeviceInformation>()
                    .FirstOrDefault(d => d.Id == lastDeviceId);
                if (savedDevice != null)
                {
                    DeviceComboBox.SelectedItem = savedDevice;
                    await StartMonitoring(savedDevice);
                    StatusText.Text = $"已自动恢复监控: {savedDevice.Name}";
                }
            }
        }

        private async System.Threading.Tasks.Task LoadPairedDevices()
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(
                    BluetoothDevice.GetDeviceSelectorFromPairingState(true));
                DeviceComboBox.ItemsSource = devices;
                if (devices.Count == 0)
                    StatusText.Text = "未找到已配对的蓝牙设备，请先在系统蓝牙设置中配对手机。";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"加载设备失败: {ex.Message}";
            }
        }

        private async void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isMonitoring)
            {
                StopMonitoring();
                (sender as System.Windows.Controls.Button)!.Content = "开始监控";
            }
            else
            {
                if (DeviceComboBox.SelectedItem is DeviceInformation device)
                {
                    await StartMonitoring(device);
                    (sender as System.Windows.Controls.Button)!.Content = "停止监控";
                }
                else StatusText.Text = "请先选择一个设备。";
            }
        }

        private async System.Threading.Tasks.Task StartMonitoring(DeviceInformation deviceInfo)
        {
            StopMonitoring();
            try
            {
                _monitoredDevice = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"无法创建 BLE 设备: {ex.Message}";
                return;
            }
            if (_monitoredDevice == null)
            {
                StatusText.Text = "该设备不支持 BLE 或无法连接。";
                return;
            }
            _lastStatus = _monitoredDevice.ConnectionStatus;
            _monitoredDevice.ConnectionStatusChanged += Device_ConnectionStatusChanged;
            try { File.WriteAllText(DeviceIdFilePath, deviceInfo.Id); } catch { }
            _isMonitoring = true;
            StatusText.Text = $"正在监控 (BLE): {deviceInfo.Name} | 状态: {_lastStatus}";
        }

        private void StopMonitoring()
        {
            if (_monitoredDevice != null)
            {
                _monitoredDevice.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
                _monitoredDevice.Dispose();
                _monitoredDevice = null;
            }
            _isMonitoring = false;
            StatusText.Text = "监控已停止。";
        }

        private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            Dispatcher.Invoke(() =>
            {
                var currentStatus = sender.ConnectionStatus;
                if (_lastStatus == BluetoothConnectionStatus.Connected &&
                    currentStatus == BluetoothConnectionStatus.Disconnected)
                {
                    LockWorkStation();
                    StatusText.Text = $"设备断开，屏幕已锁定 ({DateTime.Now:T})";
                }
                else
                {
                    StatusText.Text = $"状态变化: {_lastStatus} → {currentStatus} ({DateTime.Now:T})";
                }
                _lastStatus = currentStatus;
            });
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_monitoredDevice != null)
            {
                var status = _monitoredDevice.ConnectionStatus;
                short? rssi = await ReadRssiAsync(_monitoredDevice);
                StatusText.Text = $"状态: {status}, RSSI: {(rssi.HasValue ? rssi.Value.ToString() : "无法读取")}";
            }
            else if (DeviceComboBox.SelectedItem is DeviceInformation device)
            {
                try
                {
                    using var tempDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
                    if (tempDevice != null)
                    {
                        short? rssi = await ReadRssiAsync(tempDevice);
                        StatusText.Text = $"状态: {tempDevice.ConnectionStatus}, RSSI: {(rssi.HasValue ? rssi.Value.ToString() : "无法读取")}";
                    }
                    else StatusText.Text = "无法访问该设备。";
                }
                catch (Exception ex) { StatusText.Text = $"测试失败: {ex.Message}"; }
            }
            else StatusText.Text = "请先选择设备。";
        }

        // 新增：记录 RSSI 按钮事件
        private async void RecordRssi_Click(object sender, RoutedEventArgs e)
        {
            BluetoothLEDevice? device = _monitoredDevice;
            if (device == null && DeviceComboBox.SelectedItem is DeviceInformation devInfo)
            {
                try { device = await BluetoothLEDevice.FromIdAsync(devInfo.Id); } catch { }
            }
            if (device == null)
            {
                StatusText.Text = "无法获取设备对象，请先开始监控或选择设备。";
                return;
            }

            short? rssi = await ReadRssiAsync(device);
            if (rssi.HasValue)
            {
                _rssiLogs.Add((DateTime.Now, rssi.Value));
                UpdateLogCount();
                StatusText.Text = $"已记录 RSSI: {rssi.Value} dBm (时间: {DateTime.Now:T})";
            }
            else
            {
                StatusText.Text = "RSSI 读取失败，请确认设备支持 BLE 且在范围内。";
            }

            // 如果临时创建了设备对象，需要释放（但 _monitoredDevice 不能释放）
            if (device != _monitoredDevice)
                device.Dispose();
        }

        // 读取 RSSI 的通用方法
        private async System.Threading.Tasks.Task<short?> ReadRssiAsync(BluetoothLEDevice device)
        {
            try
            {
                // 获取 TX Power 服务 (0x1804)
                var serviceResult = await GattDeviceService.FromIdAsync(device.DeviceId);
                if (serviceResult == null) return null;
                var characteristic = serviceResult.GetCharacteristics(new Guid("00002a07-0000-1000-8000-00805f9b34fb")).FirstOrDefault();
                if (characteristic == null) return null;
                var readResult = await characteristic.ReadValueAsync();
                if (readResult.Status == GattCommunicationStatus.Success)
                {
                    using var reader = DataReader.FromBuffer(readResult.Value);
                    byte[] data = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(data);
                    if (data.Length > 0)
                        return (sbyte)data[0]; // RSSI 是有符号字节
                }
            }
            catch { }
            return null;
        }

        private void UpdateLogCount()
        {
            LogCountText.Text = $"已记录: {_rssiLogs.Count} 次";
        }

        private void DeviceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }

        // ---------- 托盘 ----------
        private void InitializeTray()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Visible = true,
                Text = "蓝牙锁屏 RSSI 测试"
            };
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (s, e) => ShowWindow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出程序", null, (s, e) => ExitApplication());
            _notifyIcon.ContextMenuStrip = menu;
        }

        private Icon LoadTrayIcon()
        {
            try
            {
                var stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("BluetoothLock.app.ico");
                if (stream != null) return new Icon(stream);
            }
            catch { }
            return SystemIcons.Application;
        }

        private void ShowWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ExitApplication()
        {
            StopMonitoring();
            // 退出前写入 RSSI 日志
            WriteRssiLogToFile();
            _notifyIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        // 将 RSSI 记录写入文件（保存到桌面或程序目录，便于查找）
        private void WriteRssiLogToFile()
        {
            if (_rssiLogs.Count == 0) return;
            try
            {
                // 为了更容易找到，输出到桌面上的 rssi_log.txt
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string filePath = Path.Combine(desktopPath, "rssi_log.txt");
                using var writer = new StreamWriter(filePath, false); // 覆盖写入
                writer.WriteLine("时间,RSSI(dBm)");
                foreach (var log in _rssiLogs)
                {
                    writer.WriteLine($"{log.Time:yyyy-MM-dd HH:mm:ss},{log.Rssi}");
                }
                // 也可以输出到程序目录作为备份
                string appDir = AppContext.BaseDirectory;
                string appFilePath = Path.Combine(appDir, "rssi_log.txt");
                File.WriteAllLines(appFilePath,
                    new[] { "时间,RSSI(dBm)" }.Concat(
                        _rssiLogs.Select(l => $"{l.Time:yyyy-MM-dd HH:mm:ss},{l.Rssi}")));
            }
            catch (Exception ex)
            {
                // 静默失败，或者写入临时目录
                try
                {
                    string fallback = Path.Combine(Path.GetTempPath(), "rssi_log.txt");
                    File.WriteAllText(fallback, "日志写入失败: " + ex.Message);
                }
                catch { }
            }
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized) this.Hide();
            base.OnStateChanged(e);
        }

        private void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, "蓝牙锁屏监控.lnk");
            if (AutoStartCheckBox.IsChecked == true)
            {
                CreateShortcut(shortcutPath);
                StatusText.Text = "已设置开机自启。";
            }
            else
            {
                if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                StatusText.Text = "已取消开机自启。";
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
                shortcut.Description = "蓝牙锁屏 RSSI 测试版";
                shortcut.Save();
            }
            catch (Exception ex) { StatusText.Text = $"开机自启设置失败: {ex.Message}"; }
        }
    }
}
