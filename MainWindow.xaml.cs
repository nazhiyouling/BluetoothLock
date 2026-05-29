using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Windows.Devices.Bluetooth.Advertisement;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripSeparator = System.Windows.Forms.ToolStripSeparator;

namespace BluetoothLock
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();

        // 目标 UUID（必须与手机广播的服务 UUID 完全一致）
        private static readonly Guid TargetUuid = Guid.Parse("0000AAAA-0000-1000-8000-00805F9B34FB");

        private BluetoothLEAdvertisementWatcher? _watcher;
        private NotifyIcon? _notifyIcon;
        private bool _isListening = false;

        // RSSI 数据处理
        private short? _lastRssi = null;
        private short _rssiThreshold = -70;          // 默认阈值 (dBm)
        private int _lowRssiCount = 0;
        private const int LowRssiThresholdCount = 2;  // 连续2次低于阈值触发锁屏
        private DispatcherTimer? _rssiTimer;

        // 配置文件路径
        private static string ConfigFilePath =>
            Path.Combine(AppContext.BaseDirectory, "config.txt");

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            InitializeTray();
            this.Closing += MainWindow_Closing;
        }

        // ---------- 设置加载与保存 ----------
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
                        if (parts[0] == "Threshold" && short.TryParse(parts[1], out short t))
                            _rssiThreshold = t;
                        else if (parts[0] == "AutoStart" && bool.TryParse(parts[1], out bool auto))
                            AutoStartCheckBox.IsChecked = auto;
                    }
                }
            }
            catch { }
            ThresholdTextBox.Text = _rssiThreshold.ToString();
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(ConfigFilePath, new[]
                {
                    $"Threshold={_rssiThreshold}",
                    $"AutoStart={AutoStartCheckBox.IsChecked ?? false}"
                });
            }
            catch { }
        }

        private void SaveThreshold_Click(object sender, RoutedEventArgs e)
        {
            if (short.TryParse(ThresholdTextBox.Text, out short t))
            {
                _rssiThreshold = t;
                StatusText.Text = $"阈值已更新为 {_rssiThreshold} dBm";
                SaveSettings();
            }
            else
                StatusText.Text = "阈值格式错误，请输入整数（例如 -70）";
        }

        // ---------- 开始/停止监听 ----------
        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isListening) StopListening();
            else StartListening();
        }

        private void StartListening()
        {
            try
            {
                _watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };
                _watcher.Received += OnAdvertisementReceived;
                _watcher.Start();
                _isListening = true;

                // 每 1.5 秒检查一次 RSSI 状态（2次连续低于阈值即锁屏，约3秒）
                _rssiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                _rssiTimer.Tick += CheckRssiAndLock;
                _rssiTimer.Start();

                StatusText.Text = "监听已启动，正在搜索目标设备...";
                (sender as System.Windows.Controls.Button)!.Content = "停止监听";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"启动失败: {ex.Message}";
            }
        }

        private void StopListening()
        {
            if (_watcher != null)
            {
                _watcher.Stop();
                _watcher = null;
            }
            _isListening = false;
            _rssiTimer?.Stop();
            _lastRssi = null;
            _lowRssiCount = 0;
            RssiValueText.Text = "-- dBm";
            StatusText.Text = "监听已停止";
            // 按钮文本在 StartStopButton_Click 中已改为“停止监听”，这里改为“开始监听”
            // 但由于该按钮被传递给多个地方，这里安全起见不直接修改按钮内容，
            // 会在调用方重新设置。
        }

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            // 检查广播包中是否包含目标 UUID
            if (args.Advertisement.ServiceUuids.Any(uuid => uuid == TargetUuid))
            {
                _lastRssi = args.RawSignalStrengthInDBm;
                Dispatcher.Invoke(() =>
                {
                    RssiValueText.Text = $"{_lastRssi} dBm";
                    if (_lastRssi < _rssiThreshold)
                        RssiValueText.Foreground = System.Windows.Media.Brushes.Red;
                    else
                        RssiValueText.Foreground = System.Windows.Media.Brushes.Green;
                });
            }
        }

        private void CheckRssiAndLock(object? sender, EventArgs e)
        {
            if (!_lastRssi.HasValue)
            {
                Dispatcher.Invoke(() => StatusText.Text = "未检测到目标设备信号");
                _lowRssiCount = 0;
                return;
            }

            if (_lastRssi.Value < _rssiThreshold)
            {
                _lowRssiCount++;
                Dispatcher.Invoke(() =>
                    StatusText.Text = $"信号弱: {_lastRssi} dBm (阈值 {_rssiThreshold})，计数 {_lowRssiCount}/{LowRssiThresholdCount}");
                if (_lowRssiCount >= LowRssiThresholdCount)
                {
                    LockWorkStation();
                    Dispatcher.Invoke(() =>
                        StatusText.Text = $"信号持续低于阈值，屏幕已锁定 ({DateTime.Now:T})");
                    _lowRssiCount = 0; // 锁屏后重置计数器
                }
            }
            else
            {
                _lowRssiCount = 0;
                Dispatcher.Invoke(() => StatusText.Text = "信号正常");
            }
        }

        // ---------- 系统托盘 ----------
        private void InitializeTray()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Visible = true,
                Text = "蓝牙锁屏监听"
            };
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (s, e) => ShowWindow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出程序", null, (s, e) => ExitApplication());
            _notifyIcon.ContextMenuStrip = menu;
        }

        // 加载嵌入的 app.ico，失败时回退到系统默认图标
        private Icon LoadTrayIcon()
        {
            try
            {
                var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("BluetoothLock.app.ico");
                if (stream != null)
                    return new Icon(stream);
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
            // 停止监听
            if (_isListening)
            {
                _watcher?.Stop();
                _rssiTimer?.Stop();
            }
            _notifyIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            // 关闭窗口时隐藏到托盘
            e.Cancel = true;
            this.Hide();
        }

        // ---------- 开机自启 ----------
        private void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, "蓝牙锁屏监听.lnk");
            if (AutoStartCheckBox.IsChecked == true)
            {
                CreateShortcut(shortcutPath);
                StatusText.Text = "已设置开机自启";
            }
            else
            {
                if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                StatusText.Text = "已取消开机自启";
            }
            SaveSettings();
        }

        private void CreateShortcut(string shortcutPath)
        {
            try
            {
                var shell = new IWshRuntimeLibrary.WshShell();
                var shortcut = (IWshRuntimeLibrary.IWshShortcut)shell.CreateShortcut(shortcutPath);
                string exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = "蓝牙锁屏监听";
                shortcut.Save();
            }
            catch { }
        }
    }
}
