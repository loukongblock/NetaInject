using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Resources;
using System.Windows.Threading;

namespace Neta
{
    public partial class MainWindow : Window
    {
        // 当前客户端版本号，与服务器第三行比较
        private const string CurrentVersion = "1.0.7";

        private double _originalWidth, _originalHeight, _originalLeft, _originalTop;
        private bool _isStretched = false;
        private bool _isProcessingSystemMax = false;
        private readonly int _cornerRadius = 12;
        private bool _isMaxButtonPressed = false;
        private bool _isInjecting = false;

        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_SIZE = 0xF000;
        private const int SIZE_LEFT = 1, SIZE_RIGHT = 2, SIZE_TOP = 3, SIZE_TOPLEFT = 4, SIZE_TOPRIGHT = 5, SIZE_BOTTOM = 6, SIZE_BOTTOMLEFT = 7, SIZE_BOTTOMRIGHT = 8;

        private DispatcherTimer _refreshTimer;

        public MainWindow()
        {
            InitializeComponent();
            ApplyAcrylic(this, 0xCC000000);
            SetWindowCorner(this, true);
            UpdateClipRegion(_cornerRadius);
            SaveOriginalWindowState();

            SizeChanged += (s, e) => { if (!_isStretched) UpdateClipRegion(_cornerRadius); };
            StateChanged += MainWindow_StateChanged;
            TitleBar.MouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
            TitleBar.MouseMove += TitleBar_MouseMove;

            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(2);
            _refreshTimer.Tick += (s, e) => { if (!_isInjecting) RefreshProcessList(); };

            Loaded += MainWindow_Loaded;
        }

        private DispatcherTimer _authDotTimer;
        private int _authDotCount = 0;
        private TextBlock _authTextBlock;

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (VersionText != null)
                VersionText.Text = "Version " + CurrentVersion;

            if (IsDisclaimerAccepted())
            {
                DisclaimerOverlay.Visibility = Visibility.Collapsed;
                if (IsAlreadyFollowed())
                {
                    // 进入授权流程，由 StartAuthCheckAsync 自己管理面板内容
                    await StartAuthCheckAsync();
                }
                else
                {
                    FollowOverlay.Visibility = Visibility.Visible;
                    InitProcessListPanel();
                }
            }
            else
            {
                DisclaimerOverlay.Visibility = Visibility.Visible;
                InitProcessListPanel();
            }
        }

        private void InitProcessListPanel()
        {
            ProcessListPanel.Children.Clear();
            ProcessListPanel.Visibility = Visibility.Visible;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
        private void AuthDotTimer_Tick(object sender, EventArgs e)
        {
            _authDotCount = (_authDotCount + 1) % 4;
            if (_authTextBlock != null)
                _authTextBlock.Text = "正在验证授权" + new string('.', _authDotCount);
        }

        private void Decline_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private bool _disclaimerAccepted = false;
        private bool _authPassed = false;
        private bool _authChecking = true;

        private async void Accept_Click(object sender, RoutedEventArgs e)
        {
            if (_disclaimerAccepted) return;
            _disclaimerAccepted = true;

            // 写入标记，下次启动不再显示使用须知
            SetDisclaimerAcceptedFlag();

            DisclaimerOverlay.Visibility = Visibility.Collapsed;

            if (IsAlreadyFollowed())
            {
                await StartAuthCheckAsync();
            }
            else
            {
                FollowOverlay.Visibility = Visibility.Visible;
            }
        }

        // ============ 标记文件相关 ============
        private static bool IsAlreadyFollowed()
        {
            try
            {
                string flagPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Neta", "followed.flag");
                return File.Exists(flagPath);
            }
            catch { return false; }
        }

        private static void SetFollowedFlag()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neta");
                Directory.CreateDirectory(dir);
                string flagPath = Path.Combine(dir, "followed.flag");
                File.WriteAllText(flagPath, "1");
            }
            catch { /* 忽略写入错误 */ }
        }

        private static bool IsDisclaimerAccepted()
        {
            try
            {
                string flagPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Neta", "disclaimer.accepted");
                return File.Exists(flagPath);
            }
            catch { return false; }
        }

        private static void SetDisclaimerAcceptedFlag()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neta");
                Directory.CreateDirectory(dir);
                string flagPath = Path.Combine(dir, "disclaimer.accepted");
                File.WriteAllText(flagPath, "1");
            }
            catch { /* 忽略 */ }
        }

        // “当然可以”按钮：记录标记并进入授权
        private async void FollowYes_Click(object sender, RoutedEventArgs e)
        {
            SetFollowedFlag();
            FollowOverlay.Visibility = Visibility.Collapsed;
            await StartAuthCheckAsync();
        }

        // “下次一定”按钮：直接进入授权，不记录标记
        private async void FollowLater_Click(object sender, RoutedEventArgs e)
        {
            FollowOverlay.Visibility = Visibility.Collapsed;
            await StartAuthCheckAsync();
        }

        private async Task StartAuthCheckAsync()
        {
            _authChecking = true;
            _authPassed = false;

            ProcessListPanel.Children.Clear();
            _authTextBlock = new TextBlock
            {
                Text = "正在验证授权",
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ProcessListPanel.Children.Add(_authTextBlock);

            _authDotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _authDotTimer.Tick += AuthDotTimer_Tick;
            _authDotTimer.Start();

            await RunAuthCheck();
        }

        private async Task RunAuthCheck()
        {
            var (serverOnline, forceUpdateFlag, latestVersion) = await CheckAuthAsync();

            if (_authDotTimer != null)
            {
                _authDotTimer.Stop();
                _authDotTimer.Tick -= AuthDotTimer_Tick;
                _authDotTimer = null;
            }

            _authChecking = false;

            if (!serverOnline)
            {
                _authPassed = false;
                ShowAuthFailedUI();
                return;
            }

            // 仅当第二行为true，且客户端版本低于服务器版本时才强制更新
            if (forceUpdateFlag && IsVersionLower(CurrentVersion, latestVersion))
            {
                _authPassed = false;
                ShowForceUpdateUI();
                return;
            }

            _authPassed = true;
            _refreshTimer.Start();
            RefreshProcessList();
        }

        // 简单版本比较：当前版本 < 目标版本返回true
        private bool IsVersionLower(string current, string target)
        {
            try
            {
                var v1 = new Version(current);
                var v2 = new Version(target);
                return v1 < v2;
            }
            catch
            {
                // 格式错误时，不强制更新
                return false;
            }
        }

        private void ShowForceUpdateUI()
        {
            ProcessListPanel.Children.Clear();

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var text = new TextBlock
            {
                Text = "此版本已经废弃！请前往官网下载最新版本",
                FontSize = 18,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };

            var btnText = new TextBlock
            {
                Text = "前往官网",
                FontSize = 14,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var btnBorder = new Border
            {
                Width = 140,
                Height = 40,
                Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(68, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = btnText
            };

            btnBorder.MouseLeftButtonDown += (s, e) =>
            {
                try
                {
                    Process.Start("https://loukongblock.github.io/Neta");
                }
                catch { }
            };

            stack.Children.Add(text);
            stack.Children.Add(btnBorder);
            ProcessListPanel.Children.Add(stack);
        }

        private void ShowAuthFailedUI()
        {
            ProcessListPanel.Children.Clear();

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var failText = new TextBlock
            {
                Text = "验证服务器无法连接！",
                FontSize = 18,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };

            var refreshBtn = new TextBlock
            {
                Text = "重试",
                FontSize = 14,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var refreshBorder = new Border
            {
                Width = 140,
                Height = 40,
                Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(68, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = refreshBtn
            };

            refreshBorder.MouseLeftButtonDown += OnRefreshClick;
            stack.Children.Add(failText);
            stack.Children.Add(refreshBorder);
            ProcessListPanel.Children.Add(stack);
        }

        private async void OnRefreshClick(object sender, MouseButtonEventArgs e)
        {
            var refreshBorder = sender as Border;
            if (refreshBorder == null) return;

            refreshBorder.MouseLeftButtonDown -= OnRefreshClick;
            refreshBorder.Cursor = Cursors.Arrow;
            refreshBorder.Opacity = 0.5;

            _authChecking = true;
            _authDotCount = 0;

            ProcessListPanel.Children.Clear();
            _authTextBlock = new TextBlock
            {
                Text = "正在验证授权",
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ProcessListPanel.Children.Add(_authTextBlock);

            _authDotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _authDotTimer.Tick += AuthDotTimer_Tick;
            _authDotTimer.Start();

            await RunAuthCheck();
        }

        #region 启动授权验证（异步）
        // 返回值：(服务器在线, 强制更新标志, 最新版本号)
        private async Task<(bool serverOnline, bool forceUpdateFlag, string latestVersion)> CheckAuthAsync()
        {
            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls12;

                var handler = new HttpClientHandler();
                using (var client = new HttpClient(handler))
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "NetaClient/1.0");
                    client.Timeout = TimeSpan.FromSeconds(15);

                    string result = await client.GetStringAsync(
                        "https://gh.llkk.cc/https://raw.githubusercontent.com/loukongblock/NetaTest2/main/Auth");

                    string[] lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    if (lines.Length >= 3)
                    {
                        string line1 = lines[0].Trim().TrimStart('\ufeff', '\ufffe').Trim('"', '\'');
                        string line2 = lines[1].Trim().TrimStart('\ufeff', '\ufffe').Trim('"', '\'');
                        string line3 = lines[2].Trim().TrimStart('\ufeff', '\ufffe').Trim('"', '\'');

                        bool serverOnline = line1.Equals("true", StringComparison.OrdinalIgnoreCase);
                        bool forceUpdateFlag = line2.Equals("true", StringComparison.OrdinalIgnoreCase);
                        string latestVersion = line3; // 版本号直接保留字符串

                        return (serverOnline, forceUpdateFlag, latestVersion);
                    }

                    // 格式不足3行，视为无法连接
                    return (false, false, "");
                }
            }
            catch
            {
                return (false, false, "");
            }
        }
        #endregion

        #region 自动刷新 Java 进程
        private void RefreshProcessList()
        {
            ProcessListPanel.Children.Clear();

            var processes = Process.GetProcessesByName("javaw")
                .Concat(Process.GetProcessesByName("java"))
                .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                .ToArray();

            if (processes.Length == 0)
            {
                TextBlock tip = new TextBlock
                {
                    Text = "未找到运行中的 Minecraft",
                    FontSize = 20,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                ProcessListPanel.Children.Add(tip);
                return;
            }

            foreach (var p in processes)
            {
                Border btn = CreateProcessButton(p);
                ProcessListPanel.Children.Add(btn);
            }
        }


        private Border CreateProcessButton(Process p)
        {
            string title = p.MainWindowTitle;
            if (string.IsNullOrWhiteSpace(title))
                title = "Minecraft Java";

            Border border = new Border
            {
                Width = 280,
                Height = 48,
                Margin = new Thickness(0, 8, 0, 8),
                Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(68, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Tag = p
            };

            // 主标题
            TextBlock titleText = new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260
            };

            // PID 标签
            TextBlock pidText = new TextBlock
            {
                Text = string.Format("PID: {0}", p.Id),
                Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            StackPanel stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(titleText);
            stack.Children.Add(pidText);

            border.Child = stack;
            border.MouseLeftButtonDown += OnProcessItemClick;
            return border;
        }

        #endregion

        #region 点击进程 → 进度动画
        private async void OnProcessItemClick(object sender, MouseButtonEventArgs e)
        {
            if (_isInjecting) return;

            // 获取选中的进程
            var border = sender as Border;
            var targetProcess = border?.Tag as Process;
            if (targetProcess == null) return;
            int targetPid = targetProcess.Id;

            _isInjecting = true;
            ProcessListPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;

            int totalDurationMs = 5000;
            int stepDelay = totalDurationMs / 100;

            for (int i = 0; i <= 100; i++)
            {
                InjectProgressBar.Value = i;
                ProgressText.Text = string.Format("{0}%", i);

                if (i == 70)
                {
                    DoRealInject(targetPid);   // ← 传入 PID
                }

                await Task.Delay(stepDelay);
            }

            ProgressText.Text = "注入完成！";
            await Task.Delay(1000);

            _isInjecting = false;
            InjectProgressBar.Value = 0;
            ProgressText.Text = "0%";
            ProgressPanel.Visibility = Visibility.Collapsed;
            ProcessListPanel.Visibility = Visibility.Visible;
        }
        #endregion

        #region 解压注入逻辑
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

        private void DoRealInject(int targetPid)   // ← 接收 PID
        {
            string tempDir = null;
            try
            {
                Uri zipUri = new Uri("/resources/lib.zip", UriKind.Relative);
                StreamResourceInfo zipInfo = Application.GetResourceStream(zipUri);
                if (zipInfo == null)
                    throw new FileNotFoundException("嵌入资源 lib.zip 未找到");

                tempDir = Path.Combine(Path.GetTempPath(), "Neta_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                File.SetAttributes(tempDir, FileAttributes.Hidden);

                using (Stream zipStream = zipInfo.Stream)
                {
                    string tempZipPath = Path.Combine(tempDir, "lib.zip");
                    using (FileStream fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write))
                    {
                        zipStream.CopyTo(fs);
                    }
                    ZipFile.ExtractToDirectory(tempZipPath, tempDir);
                    try { File.Delete(tempZipPath); } catch { }
                }

                // 字体复制逻辑
                try
                {
                    string fontsDir = Path.Combine("D:\\", "NetaLib", "fonts");
                    Directory.CreateDirectory(fontsDir);
                    string iconTtf = Path.Combine(tempDir, "icon.ttf");
                    string misansTtf = Path.Combine(tempDir, "misans.ttf");
                    if (File.Exists(iconTtf))
                        File.Copy(iconTtf, Path.Combine(fontsDir, "icon.ttf"), true);
                    if (File.Exists(misansTtf))
                        File.Copy(misansTtf, Path.Combine(fontsDir, "misans.ttf"), true);
                }
                catch { }

                string coreDllPath = Path.Combine(tempDir, "Core.dll");
                if (!File.Exists(coreDllPath))
                    throw new FileNotFoundException("Core.dll 未在压缩包中找到");

                //把 PID 作为参数传给 Run
                Process proc = Process.Start(new ProcessStartInfo("rundll32.exe",
                    string.Format("\"{0}\",Run {1}", coreDllPath, targetPid))
                {
                    WorkingDirectory = tempDir,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                });

                if (proc != null)
                    proc.WaitForExit();

                //清理逻辑
                string dirToClean = tempDir;
                Task.Run(() =>
                {
                    try
                    {
                        Thread.Sleep(5000);
                        while (true)
                        {
                            var javaProcesses = Process.GetProcessesByName("javaw")
                                                 .Concat(Process.GetProcessesByName("java"))
                                                 .ToArray();
                            if (javaProcesses.Length == 0) break;
                            Thread.Sleep(3000);
                        }
                        Thread.Sleep(2000);
                        Directory.Delete(dirToClean, true);
                    }
                    catch
                    {
                        try
                        {
                            if (Directory.Exists(dirToClean))
                            {
                                foreach (string file in Directory.GetFiles(dirToClean, "*", SearchOption.AllDirectories))
                                    MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                            }
                        }
                        catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("注入失败：{0}", ex.Message), "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                _isInjecting = false;
                ProgressPanel.Visibility = Visibility.Collapsed;
                ProcessListPanel.Visibility = Visibility.Visible;

                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try
                    {
                        foreach (string file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
                            MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    }
                    catch { }
                }
            }
        }
        #endregion

        #region 窗口控制
        private void SaveOriginalWindowState()
        {
            _originalWidth = Width;
            _originalHeight = Height;
            _originalLeft = Left;
            _originalTop = Top;
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized && !_isProcessingSystemMax && !_isStretched)
            {
                _isProcessingSystemMax = true;
                WindowState = WindowState.Normal;
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    StretchWindowToScreen();
                    MaxRestoreButton.Text = "\ue534";
                    _isProcessingSystemMax = false;
                }));
            }
        }

        private void UpdateClipRegion(double radius)
        {
            MainBorder.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
        }

        private void ResizeBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isStretched) return;
            var rect = sender as FrameworkElement;
            if (rect == null) return;

            ReleaseCapture();
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int cmd = 0;

            switch (rect.Name)
            {
                case "LeftResizeRect": cmd = SIZE_LEFT; break;
                case "RightResizeRect": cmd = SIZE_RIGHT; break;
                case "TopResizeRect": cmd = SIZE_TOP; break;
                case "BottomResizeRect": cmd = SIZE_BOTTOM; break;
                case "TopLeftResizeRect": cmd = SIZE_TOPLEFT; break;
                case "TopRightResizeRect": cmd = SIZE_TOPRIGHT; break;
                case "BottomLeftResizeRect": cmd = SIZE_BOTTOMLEFT; break;
                case "BottomRightResizeRect": cmd = SIZE_BOTTOMRIGHT; break;
            }

            if (cmd != 0) SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)(SC_SIZE + cmd), IntPtr.Zero);
            e.Handled = true;
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!e.LeftButton.HasFlag(MouseButtonState.Pressed) || _isMaxButtonPressed) return;
            if (_isStretched)
            {
                Point p = Mouse.GetPosition(null);
                RestoreWindowToMousePosition(p);
                MaxRestoreButton.Text = "\ue65b";
            }
            else DragMove();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isMaxButtonPressed) { e.Handled = true; return; }
        }

        private void MaxRestoreButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isMaxButtonPressed = true;
            e.Handled = true;
        }

        private void MaxRestoreButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isMaxButtonPressed) { e.Handled = true; return; }
            if (!_isStretched) { StretchWindowToScreen(); MaxRestoreButton.Text = "\ue534"; }
            else { RestoreWindowToOriginalPosition(); MaxRestoreButton.Text = "\ue65b"; }
            _isMaxButtonPressed = false;
            e.Handled = true;
        }

        private void close_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();
        private void hidewindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowState = WindowState.Minimized;

        private void StretchWindowToScreen()
        {
            _isStretched = true;
            var area = SystemParameters.WorkArea;
            Left = area.Left;
            Top = area.Top;
            Width = area.Width;
            Height = area.Height;
            UpdateClipRegion(0);
            SetWindowCorner(this, false);
        }

        private void RestoreWindowToMousePosition(Point p)
        {
            var area = SystemParameters.WorkArea;
            _isStretched = false;
            Left = Math.Max(area.Left, p.X - _originalWidth / 2);
            Top = Math.Max(area.Top, p.Y - 20);
            Width = _originalWidth;
            Height = _originalHeight;
            UpdateClipRegion(_cornerRadius);
            SetWindowCorner(this, true);
        }

        private void RestoreWindowToOriginalPosition()
        {
            _isStretched = false;
            Left = _originalLeft;
            Top = _originalTop;
            Width = _originalWidth;
            Height = _originalHeight;
            UpdateClipRegion(_cornerRadius);
            SetWindowCorner(this, true);
        }
        #endregion

        #region 亚克力 & 圆角
        public static void ApplyAcrylic(Window w, uint argb)
        {
            var h = new WindowInteropHelper(w).EnsureHandle();
            HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
            Margins m = new Margins { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
            DwmExtendFrameIntoClientArea(h, ref m);

            AccentPolicy accent = new AccentPolicy { AccentState = 4, GradientColor = (int)argb };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(accent, ptr, false);

            WindowCompositionAttributeData data = new WindowCompositionAttributeData
            { Attribute = 19, DataPointer = ptr, DataSize = (uint)size };
            SetWindowCompositionAttribute(h, ref data);
            Marshal.FreeHGlobal(ptr);
        }

        private void SetWindowCorner(Window window, bool round)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int pref = round ? 2 : 1;
            DwmSetWindowAttribute(hwnd, 33, ref pref, 4);
        }
        #endregion

        #region Win32
        [DllImport("user32")] private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
        [DllImport("dwmapi")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins pMarInset);
        [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("user32")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32")] private static extern bool ReleaseCapture();

        [StructLayout(LayoutKind.Sequential)]
        public struct Margins
        {
            public int LeftWidth;
            public int RightWidth;
            public int TopHeight;
            public int BottomHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr DataPointer;
            public uint DataSize;
        }
        #endregion
    }
}