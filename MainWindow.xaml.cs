using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
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

        // RSSI 相关
        private List<(DateTime Time, short Rssi)> _rssiLogs = new();
        private DispatcherTimer? _rssiTimer;
        private int _lowRssiCount = 0;                // 连续低于阈值的次数
        private const int LowRssiThresholdCount = 2;  // 连续2次低于阈值才锁屏
        private short? _rssiThreshold;                // RSSI 阈值，从设置读取
        private bool _useRssiMode = false;            // 是否启用 RSSI 模式

        private static string DeviceIdFilePath =>
            Path.Combine(AppContext.BaseDirectory, "lastdevice.txt");
        private static string ConfigFilePath =>
            Path.Combine(AppContext.BaseDirectory, "config.txt");

        public MainWindow()
        {
            InitializeComponent();
            LoadVersionInfo();
            InitializeTray();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        private void LoadVersionInfo()
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            string verStr = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
            TitleText.Text = $"蓝牙锁屏监控 {verStr}";
            this.Title = $"蓝牙锁屏监控 {verStr}";
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
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

        // ---------- 设备列表（仅 BLE） ----------
        private async System.Threading.Tasks.Task LoadPairedDevices()
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(
                    BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
                DeviceComboBox.ItemsSource = devices;
                if (devices.Count == 0)
                    StatusText.Text = "未找到已配对的 BLE 设备，请先在系统蓝牙设置中与手机配对。";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"加载设备失败: {ex.Message}";
            }
        }

        // ---------- 开始/停止监控 ----------
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

            // 如果启用 RSSI 模式，启动定时器
            if (_useRssiMode && _rssiThreshold.HasValue)
            {
                StartRssiTimer();
                StatusText.Text = $"RSSI 监控已启动 (阈值: {_rssiThreshold} dBm)";
            }
            else
            {
                StatusText.Text = $"连接监控已启动 (当前: {_lastStatus})";
            }
        }

        private void StopMonitoring()
        {
            if (_monitoredDevice != null)
            {
                _monitoredDevice.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
                _monitoredDevice.Dispose();
                _monitoredDevice = null;
            }
            StopRssiTimer();
            _isMonitoring = false;
            StatusText.Text = "监控已停止。";
        }

        // ---------- 连接状态变化（作为保底） ----------
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
                    StatusText.Text = $"连接状态: {currentStatus}";
                }
                _lastStatus = currentStatus;
            });
        }

        // ---------- RSSI 定时检测 ----------
        private void StartRssiTimer()
        {
            _rssiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _rssiTimer.Tick += async (s, e) => await CheckRssiAndLock();
            _rssiTimer.Start();
        }

        private void StopRssiTimer()
        {
            if (_rssiTimer != null)
            {
                _rssiTimer.Stop();
                _rssiTimer = null;
            }
            _lowRssiCount = 0;
        }

        private async Task CheckRssiAndLock()
        {
            if (_monitoredDevice == null || !_rssiThreshold.HasValue) return;

            short? rssi = await ReadRssiAsync(_monitoredDevice);
            if (!rssi.HasValue) return;

            Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"RSSI: {rssi.Value} dBm (阈值: {_rssiThreshold})";
            });

            if (rssi.Value < _rssiThreshold.Value)
            {
                _lowRssiCount++;
                if (_lowRssiCount >= LowRssiThresholdCount)
                {
                    Dispatcher.Invoke(() =>
                    {
                        LockWorkStation();
                        StatusText.Text = $"RSSI 持续低于阈值，屏幕已锁定 ({DateTime.Now:T})";
                    });
                    _lowRssiCount = 0; // 锁屏后重置
                }
            }
            else
            {
                _lowRssiCount = 0;
            }
        }

        // ---------- 读取 RSSI ----------
        private async Task<short?> ReadRssiAsync(BluetoothLEDevice device)
        {
            try
            {
                var servicesResult = await device.GetGattServicesAsync();
                if (servicesResult.Status != GattCommunicationStatus.Success)
                    return null;

                var txPowerService = servicesResult.Services
                    .FirstOrDefault(s => s.Uuid == new Guid("00001804-0000-1000-8000-00805f9b34fb"));
                if (txPowerService == null)
                    return null;

                var characteristicsResult = await txPowerService.GetCharacteristicsAsync();
                if (characteristicsResult.Status != GattCommunicationStatus.Success)
                    return null;

                var rssiChar = characteristicsResult.Characteristics
                    .FirstOrDefault(c => c.Uuid == new Guid("00002a07-0000-1000-8000-00805f9b34fb"));
                if (rssiChar == null)
                    return null;

                var readResult = await rssiChar.ReadValueAsync();
                if (readResult.Status == GattCommunicationStatus.Success)
                {
                    using var reader = DataReader.FromBuffer(readResult.Value);
                    byte[] data = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(data);
                    if (data.Length > 0)
                        return (sbyte)data[0];
                }
            }
            catch { }
            return null;
        }

        // ---------- 设置窗口相关 ----------
        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWin = new SettingsWindow(this);
            settingsWin.Owner = this;
            settingsWin.ShowDialog();
            // 重新加载设置，可能改变了阈值或模式
            LoadSettings();
            // 如果正在监控，重启 RSSI 定时器以应用新阈值
            if (_isMonitoring && _useRssiMode && _rssiThreshold.HasValue)
            {
                StopRssiTimer();
                StartRssiTimer();
                StatusText.Text = $"RSSI 阈值已更新: {_rssiThreshold} dBm";
            }
            else if (_isMonitoring && !_useRssiMode)
            {
                StopRssiTimer();
                StatusText.Text = "已切换至连接断开锁屏模式";
            }
        }

        // ---------- 设置存取 ----------
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    var lines = File.ReadAllLines(ConfigFilePath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('=');
                        if (parts.Length != 2) continue;
                        if (parts[0] == "RssiThreshold")
                        {
                            if (short.TryParse(parts[1], out short t))
                            {
                                _rssiThreshold = t;
                                _useRssiMode = true;
                            }
                        }
                        else if (parts[0] == "UseRssiMode")
                        {
                            bool.TryParse(parts[1], out _useRssiMode);
                        }
                    }
                }
            }
            catch { }
        }

        public void SaveSettings(short? threshold, bool useRssi, bool autoStart)
        {
            try
            {
                List<string> lines = new List<string>();
                if (threshold.HasValue)
                    lines.Add($"RssiThreshold={threshold.Value}");
                lines.Add($"UseRssiMode={useRssi}");
                lines.Add($"AutoStart={autoStart}");
                File.WriteAllLines(ConfigFilePath, lines);
            }
            catch { }
        }

        // 供设置窗口调用的公共方法
        public BluetoothLEDevice? GetMonitoredDevice() => _monitoredDevice;
        public async Task<short?> ReadRssiForDevice() =>
            _monitoredDevice != null ? await ReadRssiAsync(_monitoredDevice) : null;
        public void AddRssiLog(short rssi)
        {
            _rssiLogs.Add((DateTime.Now, rssi));
            LogCountText.Text = $"已记录: {_rssiLogs.Count} 次";
        }

        // ---------- 托盘 ----------
        private void InitializeTray()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Visible = true,
                Text = "蓝牙锁屏监控"
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
                var stream = Assembly.GetExecutingAssembly()
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
            Application.Current.Shutdown();
        }

        private void WriteRssiLogToFile()
        {
            if (_rssiLogs.Count == 0) return;
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string filePath = Path.Combine(desktopPath, "rssi_log.txt");
                using var writer = new StreamWriter(filePath, false);
                writer.WriteLine("时间,RSSI(dBm)");
                foreach (var log in _rssiLogs)
                    writer.WriteLine($"{log.Time:yyyy-MM-dd HH:mm:ss},{log.Rssi}");

                string appDir = AppContext.BaseDirectory;
                string appFilePath = Path.Combine(appDir, "rssi_log.txt");
                File.WriteAllLines(appFilePath,
                    new[] { "时间,RSSI(dBm)" }.Concat(
                        _rssiLogs.Select(l => $"{l.Time:yyyy-MM-dd HH:mm:ss},{l.Rssi}")));
            }
            catch { }
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
    }
}
