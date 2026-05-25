using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using NotifyIcon = System.Windows.Forms.NotifyIcon;

namespace BluetoothLock
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();

        private BluetoothDevice? _monitoredDevice;
        private NotifyIcon? _notifyIcon;
        private bool _isMonitoring = false;
        private string? _lastDeviceId;
        private const string DeviceIdSettingName = "LastMonitoredDeviceId";

        public MainWindow()
        {
            InitializeComponent();
            _lastDeviceId = Properties.Settings.Default[DeviceIdSettingName] as string;
            InitializeTray();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadPairedDevices();
            if (!string.IsNullOrEmpty(_lastDeviceId))
            {
                var savedDevice = DeviceComboBox.Items.Cast<DeviceInformation>()
                    .FirstOrDefault(d => d.Id == _lastDeviceId);
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
            _monitoredDevice.ConnectionStatusChanged += Device_ConnectionStatusChanged;
            Properties.Settings.Default[DeviceIdSettingName] = deviceInfo.Id;
            Properties.Settings.Default.Save();
            _isMonitoring = true;
            StatusText.Text = $"正在监控: {deviceInfo.Name}";
            if (_monitoredDevice.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                LockWorkStation();
                StatusText.Text = "监控开始时设备已断开，已锁屏。";
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
            _isMonitoring = false;
            StatusText.Text = "监控已停止。";
        }

        private void Device_ConnectionStatusChanged(BluetoothDevice sender, object args)
        {
            Dispatcher.Invoke(() =>
            {
                if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                {
                    LockWorkStation();
                    StatusText.Text = $"设备已断开，屏幕已锁定 ({DateTime.Now:T})";
                }
                else
                    StatusText.Text = $"设备已连接 ({DateTime.Now:T})";
            });
        }

        private void DeviceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }

        private void InitializeTray()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "蓝牙锁屏监控"
            };
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (s, e) => ShowWindow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出程序", null, (s, e) => ExitApplication());
            _notifyIcon.ContextMenuStrip = menu;
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
            Application.Current.Shutdown();
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
                shortcut.TargetPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                shortcut.WorkingDirectory = Path.GetDirectoryName(shortcut.TargetPath);
                shortcut.Description = "蓝牙锁屏监控";
                shortcut.Save();
            }
            catch (Exception ex) { StatusText.Text = $"开机自启设置失败: {ex.Message}"; }
        }
    }
}
