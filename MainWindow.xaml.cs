using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripSeparator = System.Windows.Forms.ToolStripSeparator;

namespace BluetoothLock
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();

        private BluetoothDevice? _monitoredDevice;
        private NotifyIcon? _notifyIcon;
        private bool _isMonitoring = false;
        private BluetoothConnectionStatus _lastStatus; // 记录上次连接状态，避免重复锁屏

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
                try
                {
                    lastDeviceId = File.ReadAllText(DeviceIdFilePath).Trim();
                }
                catch { }
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
            _monitoredDevice = await BluetoothDevice.FromIdAsync(deviceInfo.Id);
            if (_monitoredDevice == null)
            {
                StatusText.Text = "无法连接到该设备，请检查设备是否开启蓝牙。";
                return;
            }

            // 记录当前状态，但不立即锁屏
            _lastStatus = _monitoredDevice.ConnectionStatus;
            _monitoredDevice.ConnectionStatusChanged += Device_ConnectionStatusChanged;

            try
            {
                File.WriteAllText(DeviceIdFilePath, deviceInfo.Id);
            }
            catch { }

            _isMonitoring = true;
            StatusText.Text = $"正在监控: {deviceInfo.Name} | 当前状态: {_lastStatus}";
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

        // 只在状态从 Connected 变为 Disconnected 时锁屏
        private void Device_ConnectionStatusChanged(BluetoothDevice sender, object args)
        {
            Dispatcher.Invoke(() =>
            {
                var currentStatus = sender.ConnectionStatus;
                if (_lastStatus == BluetoothConnectionStatus.Connected &&
                    currentStatus == BluetoothConnectionStatus.Disconnected)
                {
                    LockWorkStation();
                    StatusText.Text = $"设备已断开，屏幕已锁定 ({DateTime.Now:T})";
                }
                else
                {
                    StatusText.Text = $"状态变化: {_lastStatus} → {currentStatus} ({DateTime.Now:T})";
                }
                _lastStatus = currentStatus;
            });
        }

        // 测试连接按钮（XAML 中需要有对应的按钮）
        private void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_monitoredDevice != null)
            {
                var status = _monitoredDevice.ConnectionStatus;
                StatusText.Text = $"当前设备状态: {status}";
            }
            else
            {
                StatusText.Text = "尚未选择监控设备。";
            }
        }

        private void DeviceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }

        // ---------- 系统托盘初始化（使用自定义图标）----------
        private void InitializeTray()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),   // 加载自定义图标，失败时回退为默认图标
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

        // 从嵌入资源加载图标
        private Icon LoadTrayIcon()
        {
            try
            {
                // 嵌入资源名称格式：默认命名空间.文件名
                // 假设命名空间为 BluetoothLock，文件 app.ico 放在根目录
                var stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("BluetoothLock.app.ico");
                if (stream != null)
                    return new Icon(stream);
            }
            catch { }
            // 加载失败时使用系统默认应用程序图标
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
            _notifyIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
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
                shortcut.Description = "蓝牙锁屏监控";
                shortcut.Save();
            }
            catch (Exception ex) { StatusText.Text = $"开机自启设置失败: {ex.Message}"; }
        }
    }
}
