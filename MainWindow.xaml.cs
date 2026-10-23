
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AMM.App
{
    public partial class MainWindow : Window
    {
        private static readonly System.Net.Http.HttpClient RecommendedCatalogHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        private const string RecommendedCatalogUrl =
            "https://twm-catalog-worker.tridvarazt.workers.dev/catalog.json";
        private static readonly System.Net.Http.HttpClient RecommendedBuildDownloadClient = new()
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 2)
            {
                ToggleWindowState();
                return;
            }
            try { DragMove(); } catch (InvalidOperationException) { }
        }



        private bool _launcherUpdateCheckInProgress;

        private async Task CheckForLauncherUpdatesAsync(bool manual = false)
        {
            if (_launcherUpdateCheckInProgress)
                return;

            _launcherUpdateCheckInProgress = true;
            PostLauncherUpdateState("checking", "Проверяем обновления",
                "Подключаемся к серверу обновлений TWM.", 0);

            try
            {
                var updateService = new UpdateService();
                var result = await updateService.CheckForUpdatesAsync();

                if (!result.UpdateAvailable || result.Manifest is null)
                {
                    PostLauncherUpdateState("upToDate", "У вас актуальная версия",
                        $"Установлена версия {result.CurrentVersion}. Обновлений нет.", 100,
                        result.CurrentVersion.ToString(), result.LatestVersion.ToString());

                    if (manual)
                        ShowNotificationMessage(
                            $"Установлена актуальная версия {result.CurrentVersion}.",
                            "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                PostLauncherUpdateState("downloading", "Найдено обновление",
                    $"Версия {result.LatestVersion}. Скачиваем пакет и проверяем его целостность…",
                    0, result.CurrentVersion.ToString(), result.LatestVersion.ToString());

                var progress = new Progress<UpdateService.DownloadProgress>(p =>
                {
                    var percent = (int)Math.Round(p.Percent);
                    var received = FormatUpdateBytes(p.BytesReceived);
                    var total = FormatUpdateBytes(p.TotalBytes);
                    PostLauncherUpdateState("downloading", "Загружаем обновление",
                        $"{received} из {total}", percent,
                        result.CurrentVersion.ToString(), result.LatestVersion.ToString());
                });

                var packagePath = await updateService.DownloadAndVerifyPackageAsync(
                    result.Manifest, progress);

                PostLauncherUpdateState("installing", "Подготавливаем установку",
                    $"Пакет версии {result.LatestVersion} проверен. Запускаем безопасную установку…",
                    100, result.CurrentVersion.ToString(), result.LatestVersion.ToString());

                StartUpdaterAndShutdown(packagePath);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления: {ex}");
                PostLauncherUpdateState("error", "Не удалось обновить лаунчер",
                    ex.Message + " Лаунчер продолжит работу с текущей версией.", 0);
                if (manual)
                {
                    ShowNotificationMessage(
                        "Не удалось проверить или скачать обновление.\n\n" + ex.Message,
                        "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                _launcherUpdateCheckInProgress = false;
            }
        }

        private void StartUpdaterAndShutdown(string packagePath)
        {
            string installDirectory = AppContext.BaseDirectory;

            string launcherPath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess()
                    .MainModule?.FileName
                ?? throw new InvalidOperationException(
                    "Не удалось определить путь к лаунчеру.");

            string installedUpdaterPath = Path.Combine(
                installDirectory,
                "TWM Launcher.Updater.exe");

            if (!File.Exists(packagePath))
                throw new FileNotFoundException(
                    "Файл обновления не найден.",
                    packagePath);

            if (!File.Exists(installedUpdaterPath))
                throw new FileNotFoundException(
                    "Не найден файл программы обновления. " +
                    "Убедитесь, что TWM Launcher.Updater.exe " +
                    "находится в папке лаунчера.",
                    installedUpdaterPath);

            // Создаём отдельную временную папку для обновлятора.
            string updatesDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "TWM Launcher",
                "Updates");

            string updaterTempDirectory = Path.Combine(
                updatesDirectory,
                "Updater-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(updaterTempDirectory);

            string temporaryUpdaterPath = Path.Combine(
                updaterTempDirectory,
                "TWM Launcher.Updater.exe");

            try
            {
                // Копируем обновлятор, чтобы он не запускался
                // непосредственно из папки установки.
                File.Copy(
                    installedUpdaterPath,
                    temporaryUpdaterPath,
                    overwrite: false);

                int launcherPid =
                    System.Diagnostics.Process.GetCurrentProcess().Id;

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = temporaryUpdaterPath,
                    WorkingDirectory = installDirectory,
                    UseShellExecute = false
                };

                // 1. Путь к ZIP-пакету.
                startInfo.ArgumentList.Add(packagePath);

                // 2. Папка установки лаунчера.
                startInfo.ArgumentList.Add(installDirectory);

                // 3. PID текущего процесса.
                startInfo.ArgumentList.Add(
                    launcherPid.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));

                // 4. Полный путь к EXE лаунчера.
                startInfo.ArgumentList.Add(launcherPath);

                var updaterProcess =
                    System.Diagnostics.Process.Start(startInfo);

                if (updaterProcess is null)
                    throw new InvalidOperationException(
                        "Не удалось запустить программу обновления.");

                // Обновлятор самостоятельно дождётся завершения лаунчера.
                Application.Current.Shutdown();
            }
            catch
            {
                // Удаляем временную папку, если запуск не состоялся.
                // Если процесс уже стартовал, не трогаем его файлы.
                if (!System.Diagnostics.Process
                        .GetProcesses()
                        .Any(p =>
                        {
                            try
                            {
                                return p.Id != 0 &&
                                    string.Equals(
                                        p.MainModule?.FileName,
                                        temporaryUpdaterPath,
                                        StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                                return false;
                            }
                        }))
                {
                    try
                    {
                        Directory.Delete(
                            updaterTempDirectory,
                            recursive: true);
                    }
                    catch
                    {
                        // Очистка временных файлов необязательна
                        // для продолжения работы лаунчера.
                    }
                }

                throw;
            }
        }


        private static string FormatUpdateBytes(long bytes)
        {
            string[] units = { "Б", "КБ", "МБ", "ГБ" };
            double size = Math.Max(0, bytes);
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.0} {units[unit]}";
        }

        private void PostLauncherUpdateState(
            string state,
            string title,
            string message,
            int percent,
            string? currentVersion = null,
            string? latestVersion = null)
        {
            void Send()
            {
                try
                {
                    if (LauncherWebView.CoreWebView2 is null)
                        return;

                    var payload = new
                    {
                        type = "launcherUpdate",
                        state,
                        title,
                        message,
                        percent = Math.Clamp(percent, 0, 100),
                        currentVersion = currentVersion ?? "",
                        latestVersion = latestVersion ?? ""
                    };

                    LauncherWebView.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(payload));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "Не удалось обновить экран обновления: " + ex.Message);
                }
            }

            if (Dispatcher.CheckAccess())
                Send();
            else
                Dispatcher.BeginInvoke((Action)Send);
        }



        private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeWindow_Click(object sender, RoutedEventArgs e) => ToggleWindowState();

        private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
        private void BeginNativeWindowDrag()
        {
            if (WindowState == WindowState.Maximized) return;
            ReleaseCapture();
            SendMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x00A1, (nint)2, nint.Zero);
        }

        private void ToggleWindowState()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            if (FindName("MaximizeWindowButton") is Button maximizeButton)
                maximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }

        private readonly string _settingsDirectory;
        private readonly string _settingsFile;

        private bool _animationsInitialized;
        private bool _wasMinimized;
        private bool _restoreAnimationRunning;
        private readonly DispatcherTimer _gameStatusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private bool _launchInProgress;
        private bool _lastGameRunning;
        private CancellationTokenSource? _webImportCancellation;

        private AppSettings _settings = new();

        private readonly Dictionary<string, ServerInfo> _servers =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["RED"] = new("RED", "80.66.82.192", 7777, "#EF4444"),
                ["YELLOW"] = new("YELLOW", "80.66.82.193", 7777, "#EAB308"),
                ["GREEN"] = new("GREEN", "80.66.82.194", 7777, "#22C55E"),
                ["AZURE"] = new("AZURE", "80.66.82.195", 7777, "#3B82F6"),
                ["SILVER"] = new("SILVER", "80.66.82.196", 7777, "#94A3B8"),
                ["ROSE"] = new("ROSE", "80.66.71.6", 7777, "#A7ADB6"),
                ["BLACK"] = new("BLACK", "80.66.82.40", 7777, "#64748B"),
                ["SKY"] = new("SKY", "80.66.82.41", 7777, "#38BDF8"),
                ["TITAN"] = new("TITAN", "80.66.82.165", 7777, "#A7ADB6"),
                ["X"] = new("X", "80.66.82.185", 7777, "#F8FAFC"),
                ["FIRE"] = new("FIRE", "80.66.82.163", 7777, "#F97316"),
                ["LIME"] = new("LIME", "80.66.82.189", 7777, "#A3E635"),
                ["NIGHT"] = new("NIGHT", "80.66.82.143", 7777, "#A7ADB6")
            };

        private string _selectedServer = "NIGHT";
        private readonly string? _updateConfirmationPath;

        public MainWindow(string? updateConfirmationPath = null)
        {
            _updateConfirmationPath = updateConfirmationPath;

            InitializeComponent();

            // Server browser is now a dedicated screen instead of a section on the home page.
            ServersSectionHeader.Visibility = Visibility.Collapsed;
            ServersGrid.Visibility = Visibility.Collapsed;

            _settingsDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "TWM Launcher");

            _settingsFile = Path.Combine(
                _settingsDirectory,
                "settings.json");

            LoadSettings();
            ApplySettingsToInterface();
            UpdateSelectedServerInterface();

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            StateChanged += MainWindow_StateChanged;
            _gameStatusTimer.Tick += (_, _) => RefreshGameStatus();
            Closed += (_, _) =>
            {
                _gameStatusTimer.Stop();
                PersistNicknameOnClose();
            };
            RefreshGameStatus();
            _gameStatusTimer.Start();
        }

        // Ensure Windows treats this borderless window as a normal taskbar window.
        // WindowStyle=None otherwise omits the native minimize/maximize system styles,
        // which can prevent the taskbar button from toggling minimize/restore reliably.
        private const int GWL_STYLE = -16;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style |= WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
            SetWindowLongPtr(hwnd, GWL_STYLE, new nint(style));
            SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            // Keep the custom title-bar maximize glyph in sync when Windows restores
            // the borderless window from its taskbar button.
            if (FindName("MaximizeWindowButton") is Button maximizeButton)
                maximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";

            if (WindowState == WindowState.Minimized)
            {
                _wasMinimized = true;
                return;
            }

            // Windows restores the taskbar window instantly by default. Add a short,
            // subtle fade-and-rise transition after a taskbar restore without changing
            // sizing, window styles, or the existing minimize/restore behavior.
            if (!IsVisible) Show();
            if (_wasMinimized && IsLoaded && !_restoreAnimationRunning)
                AnimateTaskbarRestore();
            _wasMinimized = false;
        }

        private void AnimateTaskbarRestore()
        {
            _restoreAnimationRunning = true;
            var startOpacity = Opacity;
            var transform = RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                RenderTransform = transform;
            }

            // Keep the motion restrained; the window itself fades in while moving
            // upward by only a few pixels. Input remains available during the effect.
            Opacity = Math.Min(startOpacity, 0.72);
            transform.Y = 8;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(Opacity, startOpacity, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = easing
            };
            fade.Completed += (_, _) => _restoreAnimationRunning = false;
            BeginAnimation(Window.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = easing
                }, HandoffBehavior.SnapshotAndReplace);
        }

        // =========================================================
        // UI ANIMATIONS
        // =========================================================


        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_animationsInitialized)
                return;

            _animationsInitialized = true;
            RefreshGameStatus();

            Root.Opacity = 0;
            if (Root.RenderTransform is not TranslateTransform rootTranslate)
            {
                rootTranslate = new TranslateTransform(0, 12);
                Root.RenderTransform = rootTranslate;
            }

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            Root.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(430))
                {
                    EasingFunction = easing
                });
            rootTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(520))
                {
                    EasingFunction = easing
                });

            AnimateEntrance(TopBar, 55);
            AnimateEntrance(HeroCard, 110);
            AnimateEntrance(ServersSectionHeader, 185);
            AnimateEntrance(ServersGrid, 235);
            AnimateEntrance(QuickLinksPanel, 320);
            AnimateEntrance(ActiveServerPanel, 365);

            foreach (Button button in FindVisualChildren<Button>(this))
            {
                button.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                button.MouseEnter += AnimatedButton_MouseEnter;
                button.MouseLeave += AnimatedButton_MouseLeave;
            }

            UpdateServerSelectionVisuals();

            bool webViewReady = await InitializeWebViewAsync();

            if (webViewReady)
                WriteUpdateConfirmation();

            await CheckForLauncherUpdatesAsync();
        }

        private async Task<bool> InitializeWebViewAsync()
        {
            try
            {
                var webFolder = Path.Combine(AppContext.BaseDirectory, "Web");
                var htmlPath = Path.Combine(webFolder, "index.html");

                if (!File.Exists(htmlPath))
                    throw new FileNotFoundException(
                        "Не найдены файлы интерфейса WebView2.", htmlPath);

                await LauncherWebView.EnsureCoreWebView2Async();

                LauncherWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                LauncherWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                LauncherWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                LauncherWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                LauncherWebView.AllowExternalDrop = false;
                LauncherWebView.CoreWebView2.WebMessageReceived +=
                    LauncherWebView_WebMessageReceived;

                var navigationCompleted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                void OnNavigationCompleted(
                    object? sender,
                    CoreWebView2NavigationCompletedEventArgs args)
                {
                    navigationCompleted.TrySetResult(args.IsSuccess);
                }

                LauncherWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                try
                {
                    LauncherWebView.Source = new Uri(htmlPath);

                    bool succeeded = await navigationCompleted.Task.WaitAsync(
                        TimeSpan.FromSeconds(20));

                    if (!succeeded)
                        throw new InvalidOperationException(
                            "WebView2 не смог загрузить основной интерфейс.");
                }
                finally
                {
                    LauncherWebView.CoreWebView2.NavigationCompleted -=
                        OnNavigationCompleted;
                }

                return true;
            }
            catch (Exception ex)
            {
                LauncherWebView.Visibility = Visibility.Collapsed;
                Root.Visibility = Visibility.Visible;

                SilverMessageBox.Show(
                    "Не удалось загрузить новый интерфейс TWM Launcher. " +
                    "Будет открыт резервный интерфейс.\n\n" + ex.Message,
                    "TWM Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return false;
            }
        }

        private void WriteUpdateConfirmation()
        {
            if (string.IsNullOrWhiteSpace(_updateConfirmationPath))
                return;

            try
            {
                string? directory = Path.GetDirectoryName(_updateConfirmationPath);
                if (string.IsNullOrWhiteSpace(directory))
                    return;

                Directory.CreateDirectory(directory);

                using var stream = new FileStream(
                    _updateConfirmationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);

                using var writer = new StreamWriter(stream);
                writer.WriteLine(DateTime.UtcNow.ToString("O"));
            }
            catch
            {
                // If confirmation cannot be written, the updater will time out
                // and preserve the rollback backup.
            }
        }


        private string _selectedSkinDffPath = string.Empty;
        private string _selectedSkinTxdPath = string.Empty;
        private readonly Dictionary<string, string> _selectedAudioReplacementPaths = new(StringComparer.Ordinal);
        private MediaPlayer? _audioPreviewPlayer;
        private string _audioPreviewTempPath = string.Empty;

        private static readonly (string Id, string Label)[] ReplaceableAudioFiles =
        {
            ("4039958235", "Звук попадания №1"), ("3895281620", "Звук попадания №2"),
            ("3314631289", "Звук попадания №3"), ("2987269210", "Звук попадания №4"),
            ("3126722945", "Звук попадания №5"), ("2778267589", "Звук попадания №6"),
            ("2633590974", "Звук попадания №7"), ("2443923692", "Звук попадания №8"),
            ("2387758424", "Звук попадания №9"), ("1941241313", "Звук попадания №10"),
            ("926482119", "Звук попадания №11"), ("338203391", "Звук попадания №12"),
            ("244921696", "Звук попадания №13"), ("2226068343", "Звук нокдауна"),
            ("606571409", "Звук убийства")
        };

        private void LauncherWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // The bridge accepts commands only from the local HTML page bundled with this application.
            if (LauncherWebView.Source?.IsFile != true)
                return;

            try
            {
                using var document = JsonDocument.Parse(e.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("action", out var actionElement)) return;
                string action = actionElement.GetString() ?? string.Empty;

                switch (action)
                {
                    case "ready":
                        SendWebState();
                        break;
                    case "checkLauncherUpdates":
                        _ = CheckForLauncherUpdatesAsync(manual: true);
                        break;
                    case "getBuilds":
                        SendLocalBuilds();
                        break;
                    case "getRecommendedBuilds":
                        _ = SendRecommendedBuildsAsync();
                        break;
                    case "openRecommendedBuild":
                        if (root.TryGetProperty("url", out var recommendedUrlElement))
                            OpenRecommendedBuildUrl(recommendedUrlElement.GetString() ?? string.Empty);
                        break;
                    case "downloadRecommendedBuild":
                        if (root.TryGetProperty("url", out var downloadUrlElement))
                        {
                            string downloadName = root.TryGetProperty("name", out var downloadNameElement)
                                ? downloadNameElement.GetString() ?? "TWM-сборка" : "TWM-сборка";
                            _ = DownloadAndImportRecommendedBuildAsync(
                                downloadUrlElement.GetString() ?? string.Empty, downloadName);
                        }
                        break;
                    case "importBuild":
                        ImportLocalBuildFromWeb(folderImport: false);
                        break;
                    case "importBuildFolder":
                        ImportLocalBuildFromWeb(folderImport: true);
                        break;
                    case "cancelImport":
                        _webImportCancellation?.Cancel();
                        SendBuildImportStatus("busy", "Отменяем импорт…", 0);
                        break;
                    case "buildAction":
                        if (root.TryGetProperty("id", out var buildIdElement) &&
                            root.TryGetProperty("operation", out var buildOperationElement))
                        {
                            HandleWebBuildAction(buildIdElement.GetString() ?? string.Empty,
                                buildOperationElement.GetString() ?? string.Empty);
                        }
                        break;
                    case "selectServer":
                        if (root.TryGetProperty("server", out var serverElement))
                        {
                            string serverName = serverElement.GetString() ?? string.Empty;
                            if (_servers.ContainsKey(serverName))
                            {
                                _selectedServer = serverName;
                                _settings.SelectedServer = serverName;
                                ApplyActiveNicknameForServer(serverName);
                                UpdateSelectedServerInterface();
                                SaveSettings();
                                SendWebState();
                            }
                        }
                        break;
                    case "nickname":
                        if (root.TryGetProperty("nickname", out var nickElement))
                        {
                            NicknameTextBox.Text = nickElement.GetString() ?? string.Empty;
                            SaveNickname();
                        }
                        break;
                    case "saveNicknameProfile": SaveNicknameProfile(root); break;
                    case "selectNicknameProfile": SelectNicknameProfile(root); break;
                    case "deleteNicknameProfile": DeleteNicknameProfile(root); break;
                    case "launch":
                        if (root.TryGetProperty("nickname", out var launchNick))
                            NicknameTextBox.Text = launchNick.GetString() ?? string.Empty;
                        if (root.TryGetProperty("server", out var launchServer))
                        {
                            string serverName = launchServer.GetString() ?? string.Empty;
                            if (_servers.ContainsKey(serverName))
                            {
                                _selectedServer = serverName;
                                ApplyActiveNicknameForServer(serverName);
                            }
                        }
                        UpdateSelectedServerInterface();
                        _ = SaveNickname();
                        LaunchGame_Click(this, new RoutedEventArgs());
                        break;
                    case "openServerBrowser": OpenServerBrowser_Click(this, new RoutedEventArgs()); break;
                    case "openMods": SendSettingsToWeb(); LauncherWebView.CoreWebView2?.ExecuteScriptAsync("window.twmSelectSettingsTab && window.twmSelectSettingsTab('mods');"); break;
                    case "settings": SendSettingsToWeb(); break;
                    case "closeSettings":
                        LauncherWebView.CoreWebView2?.ExecuteScriptAsync("(function(){const o=document.getElementById(\"settingsOverlay\");if(o){o.classList.add(\"settings-closed\");o.hidden=true;o.style.setProperty(\"display\",\"none\",\"important\");o.setAttribute(\"aria-hidden\",\"true\");o.inert=true;}document.body.classList.remove(\"settings-open\");document.documentElement.classList.remove(\"settings-open\");})()");
                        break;
                    case "getSettings": SendSettingsToWeb(); break;
                    case "getModLibrary": SendModLibraryToWeb(); break;
                    case "toggleMod": ToggleModFromWeb(root); break;
                    case "deleteMod": DeleteModFromWeb(root); break;
                    case "importDll":
                        if (NormalizeTargetProcess(_settings.TargetProcess) == "amazing.exe")
                            LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "modActionResult", success = false, message = "Добавление DLL / ASI отключено для amazing.exe. Выбери gta_sa.exe в настройках." }));
                        else { ImportDll_Click(this, new RoutedEventArgs()); SendModLibraryToWeb(); }
                        break;
                    case "chooseGameDirectory": ChooseGameDirectoryFromWeb(); break;
                    case "openGameDirectory": OpenDirectoryFromWeb(_settings.GameDirectory, "Папка игры"); break;
                    case "openDataDirectory": OpenDirectoryFromWeb(_settingsDirectory, "Папка данных"); break;
                    case "saveWebSettings": SaveWebSettings(root); break;
                    case "chooseF1Image": ChooseF1ImageFromWeb(); break;
                    case "chooseSkinFile": ChooseSkinFileFromWeb(root); break;
                    case "applySkinReplacement": ApplySkinReplacementFromWeb(root); break;
                    case "restoreSkinReplacement": RestoreSkinReplacementFromWeb(root); break;
                    case "chooseAudioReplacement": ChooseAudioReplacementFromWeb(root); break;
                    case "previewAudio": PreviewAudioFromWeb(root); break;
                    case "applyAudioReplacement": ApplyAudioReplacementFromWeb(root); break;
                    case "restoreAudioReplacement": RestoreAudioReplacementFromWeb(root); break;
                    case "applyFoliage": ApplyFoliageFromWeb(root); break;
                    case "runDiagnostics": SendLaunchDiagnosticsToWeb(); break;
                    case "dragWindow": BeginNativeWindowDrag(); break;
                    case "minimize": MinimizeWindow_Click(this, new RoutedEventArgs()); break;
                    case "maximize": MaximizeWindow_Click(this, new RoutedEventArgs()); break;
                    case "close": CloseWindow_Click(this, new RoutedEventArgs()); break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("WebView2 bridge error: " + ex);
            }
        }

        private static string GetLauncherVersionString()
        {
            var assembly = Assembly.GetEntryAssembly();
            var value = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly?.GetName().Version?.ToString()
                ?? "1.0.0";
            return value.Split('+')[0];
        }

        private void SendWebState()
        {
            if (LauncherWebView.CoreWebView2 == null) return;

            bool running = IsGameClientRunning();
            bool launching = _launchInProgress && !running;
            var state = new
            {
                server = _selectedServer,
                nickname = _settings.Nickname ?? string.Empty,
                savedNicknames = GetNicknamesForServer(_selectedServer),
                activeNickname = GetActiveNicknameForServer(_selectedServer),
                running,
                launching,
                status = running ? "ИГРА ЗАПУЩЕНА" : launching ? "ЗАПУСК КЛИЕНТА" : "КЛИЕНТ ГОТОВ",
                playLabel = running ? "ИГРА УЖЕ ЗАПУЩЕНА" : launching ? "ЗАПУСК…" : "ЗАПУСТИТЬ ИГРУ",
                launcherVersion = GetLauncherVersionString()
            };
            LauncherWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(state));
        }

        /// <summary>Отправляет библиотеку локальных сборок в интерфейс WebView2.</summary>
        private async Task SendRecommendedBuildsAsync()
        {
            try
            {
                using var response = await RecommendedCatalogHttpClient.GetAsync(RecommendedCatalogUrl);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var document = await JsonDocument.ParseAsync(stream);
                var root = document.RootElement;
                if (!root.TryGetProperty("builds", out var buildsElement) ||
                    buildsElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Формат каталога сборок некорректен.");

                var builds = new List<object>();
                foreach (var item in buildsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string ReadString(string name)
                    {
                        return item.TryGetProperty(name, out var value) &&
                               value.ValueKind == JsonValueKind.String
                            ? value.GetString() ?? string.Empty
                            : string.Empty;
                    }

                    string id = ReadString("id");
                    string name = ReadString("name");
                    string kind = ReadString("kind");
                    string downloadUrl = ReadString("downloadUrl");
                    if (string.IsNullOrWhiteSpace(id) ||
                        string.IsNullOrWhiteSpace(name) ||
                        (kind != "FullGame" && kind != "SaFix") ||
                        !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var parsedUrl) ||
                        parsedUrl.Scheme != Uri.UriSchemeHttps)
                        continue;

                    string ReadHttpsImage(string propertyName)
                    {
                        var value = ReadString(propertyName);
                        return Uri.TryCreate(value, UriKind.Absolute, out var imageUri) &&
                               imageUri.Scheme == Uri.UriSchemeHttps
                            ? imageUri.AbsoluteUri
                            : string.Empty;
                    }

                    var screenshots = new List<string>();
                    if (item.TryGetProperty("screenshots", out var screenshotsElement) &&
                        screenshotsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var screenshot in screenshotsElement.EnumerateArray())
                        {
                            if (screenshot.ValueKind != JsonValueKind.String) continue;
                            var value = screenshot.GetString();
                            if (Uri.TryCreate(value, UriKind.Absolute, out var imageUri) &&
                                imageUri.Scheme == Uri.UriSchemeHttps && screenshots.Count < 12)
                                screenshots.Add(imageUri.AbsoluteUri);
                        }
                    }

                    builds.Add(new
                    {
                        id,
                        name = name.Length > 120 ? name[..120] : name,
                        kind,
                        kindLabel = ReadString("kindLabel"),
                        description = ReadString("description"),
                        version = ReadString("version"),
                        updatedAt = ReadString("updatedAt"),
                        sizeLabel = ReadString("sizeLabel"),
                        coverUrl = ReadHttpsImage("coverUrl"),
                        screenshots,
                        downloadUrl = parsedUrl.AbsoluteUri
                    });
                }

                PostRecommendedBuilds(new { type = "recommendedBuilds", success = true, builds });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Не удалось загрузить рекомендуемые сборки: " + ex);
                PostRecommendedBuilds(new
                {
                    type = "recommendedBuilds",
                    success = false,
                    error = "Не удалось получить каталог. Проверь подключение к интернету и доступность репозитория.",
                    builds = Array.Empty<object>()
                });
            }
        }

        private void PostRecommendedBuilds(object payload)
        {
            void Send()
            {
                try
                {
                    LauncherWebView.CoreWebView2?.PostWebMessageAsJson(
                        JsonSerializer.Serialize(payload));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Не удалось передать каталог в WebView2: " + ex.Message);
                }
            }

            if (Dispatcher.CheckAccess()) Send();
            else Dispatcher.BeginInvoke((Action)Send);
        }

        private async Task DownloadAndImportRecommendedBuildAsync(string url, string buildName)
        {
            if (_webImportCancellation != null)
            {
                SendBuildImportStatus("busy", "Другая загрузка или операция импорта уже выполняется…", 0);
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            {
                SendBuildImportStatus("error", "Ссылка на рекомендуемую сборку недействительна.", 0);
                return;
            }

            var cancellation = new CancellationTokenSource();
            _webImportCancellation = cancellation;
            string? archivePath = null;
            try
            {
                string downloadDirectory = Path.Combine(@"C:\TWM", "Downloads");
                Directory.CreateDirectory(downloadDirectory);
                string safeName = string.Concat((string.IsNullOrWhiteSpace(buildName) ? "TWM-сборка" : buildName)
                    .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim().TrimEnd('.');
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "TWM-сборка";
                if (safeName.Length > 80) safeName = safeName[..80];
                string fileName = $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
                archivePath = Path.Combine(downloadDirectory, fileName);

                SendBuildImportStatus("busy", "Подключаемся к серверу загрузки…", 0);
                using var response = await RecommendedBuildDownloadClient.GetAsync(
                    uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
                response.EnsureSuccessStatusCode();
                await using (var sourceStream = await response.Content.ReadAsStreamAsync(cancellation.Token))
                await using (var destinationStream = new FileStream(archivePath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    long? totalBytes = response.Content.Headers.ContentLength;
                    long downloadedBytes = 0;
                    byte[] buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation.Token)) > 0)
                    {
                        await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellation.Token);
                        downloadedBytes += read;
                        int percent = totalBytes is > 0
                            ? (int)Math.Clamp(downloadedBytes * 100d / totalBytes.Value, 0d, 100d)
                            : 0;
                        string progressText = totalBytes is > 0
                            ? $"Скачиваем {safeName}: {FormatBuildSize(downloadedBytes)} из {FormatBuildSize(totalBytes.Value)}…"
                            : $"Скачиваем {safeName}: {FormatBuildSize(downloadedBytes)}…";
                        SendBuildImportStatus("busy", progressText, percent);
                    }
                }

                var fileInfo = new FileInfo(archivePath);
                if (!fileInfo.Exists || fileInfo.Length == 0)
                    throw new InvalidDataException("Сервер вернул пустой архив.");

                SendBuildImportStatus("busy", "Загрузка завершена. Импортируем сборку в библиотеку…", 100);
                var importProgress = new Progress<LocalBuildImportProgress>(state =>
                    SendBuildImportStatus("busy", state.Stage, Math.Clamp(state.Percent, 0, 100)));
                LocalBuildInfo imported = await Task.Run(
                    () => LocalBuildStore.ImportArchive(archivePath, importProgress, cancellation.Token),
                    cancellation.Token);
                SendLocalBuilds();
                string installedMessage = $"Сборка «{imported.Name}» установлена и добавлена в «Мои сборки». Тип: {imported.KindLabel}. Файлов: {imported.FileCount:N0}.";
                SendBuildImportStatus("success", installedMessage, 100);
                PostRecommendedBuilds(new
                {
                    type = "recommendedBuildInstalled",
                    name = imported.Name,
                    message = installedMessage
                });
            }
            catch (OperationCanceledException)
            {
                if (!string.IsNullOrWhiteSpace(archivePath))
                {
                    try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { }
                }
                SendBuildImportStatus("info", "Загрузка или импорт отменены.", 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Ошибка скачивания рекомендуемой сборки: " + ex);
                SendBuildImportStatus("error", "Не удалось скачать или импортировать рекомендуемую сборку.", 0);
                SilverMessageBox.Show("Не удалось скачать или импортировать сборку.\n\n" + ex.Message,
                    "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (ReferenceEquals(_webImportCancellation, cancellation))
                    _webImportCancellation = null;
                cancellation.Dispose();
            }
        }

        private static void OpenRecommendedBuildUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            {
                SilverMessageBox.Show("Ссылка загрузки недействительна или не поддерживается.",
                    "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SilverMessageBox.Show("Не удалось открыть ссылку загрузки.\\n\\n" + ex.Message,
                    "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SendLocalBuilds()
        {
            if (LauncherWebView.CoreWebView2 == null) return;

            try
            {
                var builds = LocalBuildStore.GetAll().Select(build =>
                {
                    bool canRestore = build.Kind == LocalBuildKind.SaFix && LocalBuildStore.HasRestorableSaFix(build);
                    bool isActiveGame = build.Kind == LocalBuildKind.FullGame && PathsEqual(_settings.GameDirectory, LocalBuildStore.GetContentDirectory(build));
                    return new
                    {
                        id = build.Id,
                        name = build.Name,
                        kind = build.Kind.ToString(),
                        kindLabel = build.KindLabel,
                        importedAt = build.ImportedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                        fileCount = build.FileCount,
                        sizeBytes = build.SizeBytes,
                        sourceArchive = build.SourceArchive,
                        canActivate = build.Kind == LocalBuildKind.FullGame && !isActiveGame,
                        canInstall = build.Kind == LocalBuildKind.SaFix,
                        canRestore,
                        isActive = isActiveGame || canRestore,
                        activeLabel = isActiveGame ? "ИСПОЛЬЗУЕТСЯ" : canRestore ? "УСТАНОВЛЕН" : ""
                    };
                }).ToList();

                var response = new { type = "localBuilds", success = true, builds };
                LauncherWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(response));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Unable to load local builds: " + ex);
                var response = new { type = "localBuilds", success = false, error = ex.Message, builds = Array.Empty<object>() };
                LauncherWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(response));
            }
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private async void ImportLocalBuildFromWeb(bool folderImport)
        {
            if (_webImportCancellation != null)
            {
                SendBuildImportStatus("busy", "Импорт уже выполняется…", 0);
                return;
            }

            string selectedArchive = string.Empty;
            string selectedFolder = string.Empty;
            if (folderImport)
            {
                var folderDialog = new OpenFolderDialog
                {
                    Title = "Выбери папку с игровой сборкой, САМОФИКСом или DLL-модами",
                    Multiselect = false
                };
                if (folderDialog.ShowDialog(this) != true) return;
                selectedFolder = folderDialog.FolderName;
            }
            else
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Выбери ZIP или RAR со сборкой, САМОФИКСом или DLL-модами",
                    Filter = "Архивы ZIP и RAR (*.zip;*.rar)|*.zip;*.rar|ZIP-архивы (*.zip)|*.zip|RAR-архивы (*.rar)|*.rar",
                    Multiselect = false,
                    CheckFileExists = true
                };
                if (dialog.ShowDialog(this) != true) return;
                selectedArchive = dialog.FileName;
            }

            var cancellation = new CancellationTokenSource();
            _webImportCancellation = cancellation;
            SendBuildImportStatus("busy", "Подготовка импорта…", 0);
            try
            {
                var progress = new Progress<LocalBuildImportProgress>(state =>
                    SendBuildImportStatus("busy", state.Stage, Math.Clamp(state.Percent, 0, 100)));

                LocalBuildInfo imported = await Task.Run(
                    () => folderImport
                        ? LocalBuildStore.ImportFolder(selectedFolder, progress, cancellation.Token)
                        : LocalBuildStore.ImportArchive(selectedArchive, progress, cancellation.Token),
                    cancellation.Token);
                SendLocalBuilds();
                SendBuildImportStatus("success",
                    $"Импортировано: {imported.Name} · {imported.KindLabel} · {imported.FileCount:N0} файлов.", 100);
            }
            catch (OperationCanceledException)
            {
                SendBuildImportStatus("info", "Импорт отменён. Незавершённые файлы удалены.", 0);
            }
            catch (Exception ex)
            {
                SendBuildImportStatus("error", "Импорт не выполнен.", 0);
                SilverMessageBox.Show("Не удалось импортировать архив.\n\n" + ex.Message,
                    "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (ReferenceEquals(_webImportCancellation, cancellation))
                    _webImportCancellation = null;
                cancellation.Dispose();
            }
        }

        private void SendBuildImportStatus(string status, string message, int percent)
        {
            if (LauncherWebView.CoreWebView2 == null) return;
            var payload = new { type = "buildImportStatus", status, message, percent };
            LauncherWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }

        private static string FormatBuildSize(long bytes)
        {
            string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            double size = Math.Max(0, bytes);
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return $"{size:0.##} {units[unit]}";
        }

        private void HandleWebBuildAction(string id, string operation)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
                return;

            LocalBuildInfo? build = LocalBuildStore.GetAll()
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (build == null)
            {
                SilverMessageBox.Show("Эта сборка больше не найдена. Обнови библиотеку.",
                    "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
                SendLocalBuilds();
                return;
            }

            switch (operation)
            {
                case "activate" when build.Kind == LocalBuildKind.FullGame:
                    ActivateLocalBuild(build);
                    SendLocalBuilds();
                    SendWebState();
                    break;
                case "install" when build.Kind == LocalBuildKind.SaFix:
                    InstallLocalSaFix(build);
                    SendLocalBuilds();
                    break;
                case "restore" when build.Kind == LocalBuildKind.SaFix:
                    RestoreLocalSaFix(build);
                    SendLocalBuilds();
                    break;
                case "folder":
                    try
                    {
                        string folder = LocalBuildStore.GetContentDirectory(build);
                        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        SilverMessageBox.Show("Не удалось открыть папку сборки.\n\n" + ex.Message,
                            "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    break;
                case "delete":
                    var answer = SilverMessageBox.Show(
                        $"Удалить «{build.Name}» и все файлы этой импортированной сборки?\n\nУдаление необратимо. Файлы за пределами хранилища TWM Launcher не затрагиваются.",
                        "Удаление сборки", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) break;
                    try
                    {
                        LocalBuildStore.Delete(build);
                        SendLocalBuilds();
                        SendBuildImportStatus("success", $"Сборка «{build.Name}» удалена.", 100);
                    }
                    catch (Exception ex)
                    {
                        SilverMessageBox.Show("Не удалось удалить сборку.\n\n" + ex.Message,
                            "Ошибка удаления", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    break;
            }
        }

        private static void AnimateEntrance(FrameworkElement? element, double delayMilliseconds)
        {
            if (element == null)
                return;

            element.Opacity = 0;
            var translate = new TranslateTransform(0, 16);
            element.RenderTransform = translate;

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
                {
                    BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
                    EasingFunction = easing
                });
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(480))
                {
                    BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
                    EasingFunction = easing
                });
        }

        private static ScaleTransform GetOrCreateScaleTransform(Button button)
        {
            if (button.RenderTransform is ScaleTransform existingScale)
                return existingScale;

            if (button.RenderTransform is TransformGroup existingGroup)
            {
                foreach (Transform transform in existingGroup.Children)
                {
                    if (transform is ScaleTransform scale)
                        return scale;
                }

                var newScale = new ScaleTransform(1, 1);
                existingGroup.Children.Add(newScale);
                return newScale;
            }

            var group = new TransformGroup();

            if (button.RenderTransform != null &&
                !ReferenceEquals(button.RenderTransform, Transform.Identity))
            {
                group.Children.Add(button.RenderTransform);
            }

            var scaleTransform = new ScaleTransform(1, 1);
            group.Children.Add(scaleTransform);
            button.RenderTransform = group;

            return scaleTransform;
        }

        private void AnimatedButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not Button button || !button.IsEnabled)
                return;

            ScaleTransform scale = GetOrCreateScaleTransform(button);
            double target = string.Equals(button.Name, "PlayButton", StringComparison.Ordinal)
                ? 1.035
                : 1.018;

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(scale.ScaleX, target, TimeSpan.FromMilliseconds(135))
                {
                    EasingFunction = easing
                });

            scale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(scale.ScaleY, target, TimeSpan.FromMilliseconds(135))
                {
                    EasingFunction = easing
                });
        }

        private void AnimatedButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not Button button)
                return;

            ScaleTransform scale = GetOrCreateScaleTransform(button);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(scale.ScaleX, 1, TimeSpan.FromMilliseconds(165))
                {
                    EasingFunction = easing
                });

            scale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(scale.ScaleY, 1, TimeSpan.FromMilliseconds(165))
                {
                    EasingFunction = easing
                });
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
            where T : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is T matchingChild)
                    yield return matchingChild;

                foreach (T descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        // =========================================================
        // MODELS
        // =========================================================

        private sealed class ServerInfo
        {
            public string Name { get; }
            public string IpAddress { get; }
            public int Port { get; }
            public string ColorHex { get; }

            public ServerInfo(
                string name,
                string ipAddress,
                int port,
                string colorHex)
            {
                Name = name;
                IpAddress = ipAddress;
                Port = port;
                ColorHex = colorHex;
            }
        }

        // =========================================================
        // CUSTOM IN-APP NOTIFICATIONS (no Windows MessageBox dialogs)
        // =========================================================

        private void ShowNotificationMessage(
            string message,
            string title,
            MessageBoxButton buttons,
            MessageBoxImage image)
        {
            // All current call sites are informational/OK-only alerts. Keep the old
            // call signatures so the existing validation and error paths stay intact.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    ShowCustomToast(title, message, image);
                }
                catch (Exception ex)
                {
                    // Ошибка оформления уведомления не должна завершать лаунчер.
                    System.Diagnostics.Debug.WriteLine("TWM notification error: " + ex);
                }
            }), DispatcherPriority.Background);
        }

        private void ShowCustomToast(string title, string message, MessageBoxImage image)
        {
            Color accent;
            string glyph;
            switch (image)
            {
                case MessageBoxImage.Error:
                    accent = Color.FromRgb(255, 91, 112);
                    glyph = "×";
                    break;
                case MessageBoxImage.Warning:
                    accent = Color.FromRgb(255, 190, 92);
                    glyph = "!";
                    break;
                case MessageBoxImage.Information:
                    accent = Color.FromRgb(104, 211, 177);
                    glyph = "✓";
                    break;
                default:
                    accent = Color.FromRgb(125, 145, 255);
                    glyph = "i";
                    break;
            }

            var toast = new Window
            {
                Title = title,
                Width = 370,
                SizeToContent = SizeToContent.Height,
                MinHeight = 104,
                MaxHeight = 250,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = false,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            var outer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(17, 20, 31)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(49, 55, 75)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 24,
                    ShadowDepth = 5,
                    Opacity = 0.38
                }
            };

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var accentBar = new Border
            {
                Background = new SolidColorBrush(accent),
                CornerRadius = new CornerRadius(15, 0, 0, 15)
            };
            Grid.SetColumn(accentBar, 0);
            layout.Children.Add(accentBar);

            var iconBadge = new Border
            {
                Width = 34,
                Height = 34,
                // Иконка центрируется по вертикали относительно всего текстового блока.
                // Благодаря этому заголовок и сообщение не выглядят смещёнными
                // в уведомлениях с одной или несколькими строками.
                Margin = new Thickness(15, 12, 12, 12),
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(34, accent.R, accent.G, accent.B)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = glyph,
                    Foreground = new SolidColorBrush(accent),
                    FontSize = 22,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
            Grid.SetColumn(iconBadge, 1);
            layout.Children.Add(iconBadge);

            var content = new StackPanel
            {
                // Текстовый блок центрируется по той же вертикальной оси, что и иконка.
                // Положение одинаковое для информационных, предупреждающих и ошибочных уведомлений.
                Margin = new Thickness(0, 12, 8, 12),
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(246, 247, 252)),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5)
            });
            content.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(177, 184, 202)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 170,
                LineHeight = 17
            });
            Grid.SetColumn(content, 2);
            layout.Children.Add(content);

            var closeButton = new Button
            {
                Content = "×",
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 9, 9, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(132, 141, 162)),
                BorderThickness = new Thickness(0),
                FontSize = 19,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "Закрыть уведомление"
            };
            Grid.SetColumn(closeButton, 3);
            layout.Children.Add(closeButton);

            outer.Child = layout;
            toast.Content = outer;

            bool isClosing = false;
            void CloseWithAnimation()
            {
                if (isClosing || !toast.IsVisible)
                    return;

                isClosing = true;
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(190))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                fadeOut.Completed += (_, _) =>
                {
                    if (toast.IsVisible)
                        toast.Close();
                };
                toast.BeginAnimation(Window.OpacityProperty, fadeOut);
            }

            closeButton.Click += (_, _) => CloseWithAnimation();
            toast.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource == outer)
                    CloseWithAnimation();
            };

            // Уведомление принадлежит главному окну: оно не остаётся поверх других
            // приложений и автоматически скрывается, когда лаунчер сворачивают.
            toast.Owner = this;

            void UpdateToastPosition()
            {
                if (!toast.IsLoaded || toast.WindowState == WindowState.Minimized)
                    return;

                var workArea = SystemParameters.WorkArea;
                double toastWidth = toast.ActualWidth > 0 ? toast.ActualWidth : toast.Width;
                double toastHeight = toast.ActualHeight > 0 ? toast.ActualHeight : toast.MinHeight;
                const double edge = 10;
                const double inset = 42;

                double targetLeft = workArea.Right - toastWidth - inset;
                double targetTop = workArea.Top + inset;

                if (IsLoaded && WindowState != WindowState.Minimized &&
                    ActualWidth > 0 && ActualHeight > 0)
                {
                    double visibleLeft = Math.Max(Left, workArea.Left);
                    double visibleTop = Math.Max(Top, workArea.Top);
                    double visibleRight = Math.Min(Left + ActualWidth, workArea.Right);
                    double visibleBottom = Math.Min(Top + ActualHeight, workArea.Bottom);
                    double visibleWidth = visibleRight - visibleLeft;
                    double visibleHeight = visibleBottom - visibleTop;

                    if (visibleWidth >= toastWidth + edge * 2)
                        targetLeft = visibleRight - toastWidth - inset;
                    // Смещение применяем независимо от высоты окна: раньше проверка
                    // visibleHeight >= toastHeight + edge * 2 пропускала компактные окна,
                    // поэтому уведомление оставалось на прежней позиции у верхнего края.
                    // Значения заданы заметнее, чтобы изменение было видно визуально.
                    double topOffset = ActualHeight < 650 ? 96 : 68;
                    targetTop = visibleTop + topOffset;
                }

                double maxLeft = Math.Max(workArea.Left + edge, workArea.Right - toastWidth - edge);
                double maxTop = Math.Max(workArea.Top + edge, workArea.Bottom - toastHeight - edge);
                toast.Left = Math.Clamp(targetLeft, workArea.Left + edge, maxLeft);
                toast.Top = Math.Clamp(targetTop, workArea.Top + edge, maxTop);
            }

            toast.Loaded += (_, _) =>
            {
                UpdateToastPosition();

                // Переустанавливаем уведомление при перемещении/изменении размера лаунчера.
                // Обработчики снимаются при закрытии toast, чтобы не накапливать подписки.
                LocationChanged += OwnerWindowGeometryChanged;
                SizeChanged += OwnerWindowGeometryChanged;
                StateChanged += OwnerWindowStateChanged;

                // WPF Window нельзя анимировать через RenderTransform; анимируем карточку.
                outer.RenderTransform = new TranslateTransform(0, -12);
                toast.BeginAnimation(Window.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(230))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                if (outer.RenderTransform is TranslateTransform translate)
                {
                    translate.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(280))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        });
                }
            };

            void OwnerWindowGeometryChanged(object? sender, EventArgs e)
            {
                if (WindowState != WindowState.Minimized)
                    UpdateToastPosition();
            }

            void OwnerWindowStateChanged(object? sender, EventArgs e)
            {
                // Owned windows прячутся вместе с владельцем. После восстановления
                // пересчитываем координаты относительно нового положения лаунчера.
                if (WindowState != WindowState.Minimized)
                    UpdateToastPosition();
            }

            toast.Closed += (_, _) =>
            {
                LocationChanged -= OwnerWindowGeometryChanged;
                SizeChanged -= OwnerWindowGeometryChanged;
                StateChanged -= OwnerWindowStateChanged;
            };

            toast.Show();

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(image == MessageBoxImage.Error ? 8 : 5.5)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (toast.IsVisible)
                    CloseWithAnimation();
            };
            timer.Start();
        }

        private sealed class AppSettings
        {
            public string GameDirectory { get; set; } = "";
            public string LauncherPath { get; set; } = "";
            public string Nickname { get; set; } = "Player";
            public string SelectedServer { get; set; } = "NIGHT";
            public Dictionary<string, List<string>> ServerNicknames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> ServerActiveNicknames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

            // Необязательный запуск загрузчика модификаций.
            public bool UseQuickAsi { get; set; } = true;
            public bool FoliageEnabled { get; set; } = true;
            public string TargetProcess { get; set; } = "amazing.exe";
            public string ActiveModProfileId { get; set; } = "Default";
        }

        private sealed class ModInfo
        {
            public string FileName { get; set; } = "";
            // Относительный путь исходной DLL внутри папки игры.
            public string RelativePath { get; set; } = "";
            public string Sha256 { get; set; } = "";
            public bool Enabled { get; set; }
            // True, если у DLL есть постоянная копия в библиотеке Mods лаунчера.
            public bool StoredDisabled { get; set; }
        }

        private sealed class ModLibrary
        {
            public List<ModInfo> Mods { get; set; } = new();
        }

        private sealed class ModProfileInfo
        {
            public string Id { get; set; } = "Default";
            public string Name { get; set; } = "По умолчанию";
            public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        }

        private sealed class ModProfileCatalog
        {
            public List<ModProfileInfo> Profiles { get; set; } = new();
        }

        private string ModProfilesRoot => Path.Combine(_settings.GameDirectory, "TWMBuilds");
        private string ModProfilesCatalogFile => Path.Combine(ModProfilesRoot, "mod-profiles.json");
        private string ActiveModProfileId => "Default"; // Единая библиотека модов; профили отключены.

        private List<ModProfileInfo> LoadModProfiles()
        {
            if (string.IsNullOrWhiteSpace(_settings.GameDirectory) || !Directory.Exists(_settings.GameDirectory))
                throw new InvalidOperationException("Сначала укажи корректную папку игры в настройках.");

            Directory.CreateDirectory(ModProfilesRoot);
            ModProfileCatalog catalog = new();
            if (File.Exists(ModProfilesCatalogFile))
            {
                try { catalog = JsonSerializer.Deserialize<ModProfileCatalog>(File.ReadAllText(ModProfilesCatalogFile)) ?? new ModProfileCatalog(); }
                catch (Exception ex) { throw new IOException("Не удалось прочитать каталог профилей модов.", ex); }
            }

            if (!catalog.Profiles.Any(x => string.Equals(x.Id, "Default", StringComparison.OrdinalIgnoreCase)))
                catalog.Profiles.Insert(0, new ModProfileInfo { Id = "Default", Name = "По умолчанию" });
            catalog.Profiles = catalog.Profiles
                .Where(x => x != null && IsSafeProfileId(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (!catalog.Profiles.Any(x => string.Equals(x.Id, ActiveModProfileId, StringComparison.OrdinalIgnoreCase)))
                _settings.ActiveModProfileId = "Default";
            SaveModProfiles(catalog.Profiles);
            return catalog.Profiles;
        }

        private void SaveModProfiles(List<ModProfileInfo> profiles)
        {
            Directory.CreateDirectory(ModProfilesRoot);
            string temp = ModProfilesCatalogFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new ModProfileCatalog { Profiles = profiles }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, ModProfilesCatalogFile, true);
        }

        private static bool IsSafeProfileId(string id) => !string.IsNullOrWhiteSpace(id)
            && id.Length <= 64 && id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

        private readonly List<ModInfo> _mods = new();

        private string ModsRoot =>
            Path.Combine(_settings.GameDirectory, "TWMBuilds", "Default", "Mods");

        private string AsiDirectory => Path.Combine(_settings.GameDirectory, "asi");

        private string ModLibraryFile => Path.Combine(ModsRoot, "mods.json");

        // =========================================================
        // BACKUP / RESTORE (settings + mod library only)
        // =========================================================
        private void BackupRestore_Click()
        {
            var choice = SilverMessageBox.Show(this,
                "Да — создать резервную копию.\nНет — восстановить из ZIP.\nОтмена — ничего не делать.",
                "Резервная копия TWM Launcher", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes) CreateLauncherBackup();
            else if (choice == MessageBoxResult.No) RestoreLauncherBackup();
        }

        private void CreateLauncherBackup()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Сохранить резервную копию TWM Launcher",
                    Filter = "Архив резервной копии (*.zip)|*.zip",
                    FileName = $"TWM_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
                    AddExtension = true,
                    DefaultExt = ".zip"
                };
                if (dialog.ShowDialog(this) != true) return;
                using var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
                if (File.Exists(_settingsFile)) archive.CreateEntryFromFile(_settingsFile, "settings/settings.json", CompressionLevel.Optimal);
                if (Directory.Exists(ModProfilesRoot))
                {
                    foreach (string file in Directory.EnumerateFiles(ModProfilesRoot, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(ModProfilesRoot, file).Replace('\\', '/');
                        if (relative.Split('/').Any(part => part == "..")) continue;
                        archive.CreateEntryFromFile(file, "TWMBuilds/" + relative, CompressionLevel.Optimal);
                    }
                }
                archive.CreateEntry("backup-info.txt").Open().Dispose();
                SilverMessageBox.Show(this, "Резервная копия создана. В архив включены настройки лаунчера и библиотека модов. Файлы игры и установленные в игру DLL не копируются.", "Резервная копия готова", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { SilverMessageBox.Show(this, "Не удалось создать резервную копию:\n" + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void RestoreLauncherBackup()
        {
            var dialog = new OpenFileDialog { Title = "Выбрать резервную копию TWM Launcher", Filter = "Архив резервной копии (*.zip)|*.zip" };
            if (dialog.ShowDialog(this) != true) return;
            if (SilverMessageBox.Show(this, "Восстановить настройки и библиотеку модов из выбранного архива? Совпадающие файлы библиотеки будут заменены. Файлы игры не изменяются.", "Подтверждение восстановления", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                using var archive = ZipFile.OpenRead(dialog.FileName);
                bool hasRecognizedFiles = false;
                foreach (var entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.StartsWith("/", StringComparison.Ordinal) || name.Split('/').Any(part => part == ".."))
                        throw new InvalidDataException("Архив содержит небезопасный путь.");
                    if (name.Equals("settings/settings.json", StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(_settingsDirectory);
                        string temp = _settingsFile + ".restore.tmp";
                        using (var input = entry.Open()) using (var output = File.Create(temp)) input.CopyTo(output);
                        _ = ReadSettingsFile(temp); // Validate JSON before replacing current settings.
                        File.Move(temp, _settingsFile, true);
                        hasRecognizedFiles = true;
                    }
                    else if (name.StartsWith("TWMBuilds/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry.Name))
                    {
                        string relative = name.Substring("TWMBuilds/".Length).Replace('/', Path.DirectorySeparatorChar);
                        string root = Path.GetFullPath(ModProfilesRoot) + Path.DirectorySeparatorChar;
                        string target = Path.GetFullPath(Path.Combine(ModProfilesRoot, relative));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь файла профиля выходит за пределы папки резервной копии.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var input = entry.Open(); using var output = File.Create(target); input.CopyTo(output);
                        hasRecognizedFiles = true;
                    }
                }
                if (!hasRecognizedFiles) throw new InvalidDataException("В архиве не найдены настройки или данные TWM Launcher.");
                LoadSettings();
                ApplySettingsToInterface();
                SilverMessageBox.Show(this, "Восстановление завершено. При необходимости перезапусти лаунчер, чтобы все настройки применились.", "Восстановление готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { SilverMessageBox.Show(this, "Не удалось восстановить резервную копию:\n" + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // =========================================================
        // SETTINGS
        // =========================================================

        private string SettingsBackupFile => _settingsFile + ".bak";

        private void LoadSettings()
        {
            Exception? primaryError = null;

            if (File.Exists(_settingsFile))
            {
                try
                {
                    _settings = ReadSettingsFile(_settingsFile);
                }
                catch (Exception ex)
                {
                    primaryError = ex;
                }
            }

            // If the main JSON was interrupted or damaged, try the last known-good copy.
            if (primaryError != null && File.Exists(SettingsBackupFile))
            {
                try
                {
                    _settings = ReadSettingsFile(SettingsBackupFile);
                    ShowNotificationMessage(
                        "Основной файл настроек повреждён. Восстановлены последние сохранённые настройки.",
                        "TWM Launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch (Exception backupError)
                {
                    _settings = new AppSettings();
                    ShowNotificationMessage(
                        "Не удалось прочитать основной файл настроек или резервную копию.\n\n" +
                        primaryError.Message + "\n\nРезервная копия: " + backupError.Message,
                        "TWM Launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            else if (primaryError != null)
            {
                _settings = new AppSettings();
                ShowNotificationMessage(
                    "Не удалось загрузить настройки. Будут использованы значения по умолчанию.\n\n" +
                    primaryError.Message,
                    "TWM Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            // Normalize values from older or manually edited settings files.
            _settings.GameDirectory ??= string.Empty;
            _settings.LauncherPath ??= string.Empty;
            if (string.IsNullOrWhiteSpace(_settings.Nickname) ||
                _settings.Nickname.Length > 24 ||
                _settings.Nickname.Any(char.IsControl))
            {
                _settings.Nickname = "Player";
            }

            if (string.IsNullOrWhiteSpace(_settings.SelectedServer) ||
                !_servers.ContainsKey(_settings.SelectedServer))
            {
                _settings.SelectedServer = "NIGHT";
            }

            _selectedServer = _settings.SelectedServer;
        }

        private static AppSettings ReadSettingsFile(string path)
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json)
                   ?? throw new InvalidDataException("Файл настроек пуст или не содержит настроек.");
        }

        private void SaveSettings()
        {
            string? tempFile = null;
            try
            {
                Directory.CreateDirectory(_settingsDirectory);

                string json = JsonSerializer.Serialize(
                    _settings,
                    new JsonSerializerOptions { WriteIndented = true });

                tempFile = _settingsFile + ".tmp";
                File.WriteAllText(tempFile, json);

                // Keep a known-good previous version before replacing the main file.
                if (File.Exists(_settingsFile))
                {
                    try
                    {
                        File.Copy(_settingsFile, SettingsBackupFile, true);
                    }
                    catch
                    {
                        // A backup failure must not prevent saving the current settings.
                    }
                }

                File.Move(tempFile, _settingsFile, true);
            }
            catch (Exception ex)
            {
                ShowNotificationMessage(
                    "Не удалось сохранить настройки.\n\n" + ex.Message,
                    "TWM Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                if (tempFile != null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); }
                    catch { /* Cleanup is best-effort. */ }
                }
            }
        }

        private void PersistNicknameOnClose()
        {
            string nickname = NicknameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(nickname) || nickname.Length > 24 ||
                nickname.Any(char.IsControl))
            {
                return;
            }

            if (!string.Equals(_settings.Nickname, nickname, StringComparison.Ordinal))
            {
                _settings.Nickname = nickname;
                _settings.SelectedServer = _selectedServer;
                SaveSettings();
            }
        }

        private List<string> GetNicknamesForServer(string serverName)
        {
            if (_settings.ServerNicknames == null) _settings.ServerNicknames = new(StringComparer.OrdinalIgnoreCase);
            if (!_settings.ServerNicknames.TryGetValue(serverName, out var names) || names == null)
                return new List<string>();
            return names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private string GetActiveNicknameForServer(string serverName)
        {
            if (_settings.ServerActiveNicknames != null && _settings.ServerActiveNicknames.TryGetValue(serverName, out var nick) && !string.IsNullOrWhiteSpace(nick))
                return nick;
            return _settings.Nickname ?? "Player";
        }

        private void ApplyActiveNicknameForServer(string serverName)
        {
            string nickname = GetActiveNicknameForServer(serverName);
            if (!string.IsNullOrWhiteSpace(nickname))
            {
                _settings.Nickname = nickname;
                NicknameTextBox.Text = nickname;
            }
        }

        private void SaveNicknameProfile(JsonElement root)
        {
            string nickname = root.TryGetProperty("nickname", out var value) ? (value.GetString() ?? string.Empty).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(nickname) || nickname.Length > 24 || nickname.Any(char.IsControl))
            {
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "nicknameProfilesResult", success = false, message = "Никнейм должен содержать от 1 до 24 символов." }));
                return;
            }
            _settings.ServerNicknames ??= new(StringComparer.OrdinalIgnoreCase);
            _settings.ServerActiveNicknames ??= new(StringComparer.OrdinalIgnoreCase);
            if (!_settings.ServerNicknames.TryGetValue(_selectedServer, out var names) || names == null)
                _settings.ServerNicknames[_selectedServer] = names = new List<string>();
            if (!names.Contains(nickname, StringComparer.OrdinalIgnoreCase)) names.Add(nickname);
            _settings.ServerActiveNicknames[_selectedServer] = nickname;
            _settings.Nickname = nickname;
            NicknameTextBox.Text = nickname;
            _settings.SelectedServer = _selectedServer;
            SaveSettings();
            SendWebState();
            LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "nicknameProfilesResult", success = true, message = "Никнейм сохранён для сервера " + _selectedServer + "." }));
        }

        private void SelectNicknameProfile(JsonElement root)
        {
            string nickname = root.TryGetProperty("nickname", out var value) ? (value.GetString() ?? string.Empty).Trim() : string.Empty;
            if (!GetNicknamesForServer(_selectedServer).Contains(nickname, StringComparer.OrdinalIgnoreCase)) return;
            _settings.ServerActiveNicknames ??= new(StringComparer.OrdinalIgnoreCase);
            _settings.ServerActiveNicknames[_selectedServer] = nickname;
            _settings.Nickname = nickname;
            NicknameTextBox.Text = nickname;
            SaveSettings();
            SendWebState();
        }

        private void DeleteNicknameProfile(JsonElement root)
        {
            string nickname = root.TryGetProperty("nickname", out var value) ? (value.GetString() ?? string.Empty).Trim() : string.Empty;
            if (_settings.ServerNicknames == null || !_settings.ServerNicknames.TryGetValue(_selectedServer, out var names) || names == null) return;
            names.RemoveAll(n => string.Equals(n, nickname, StringComparison.OrdinalIgnoreCase));
            if (_settings.ServerActiveNicknames != null && _settings.ServerActiveNicknames.TryGetValue(_selectedServer, out var active) && string.Equals(active, nickname, StringComparison.OrdinalIgnoreCase))
            {
                _settings.ServerActiveNicknames.Remove(_selectedServer);
                if (names.Count > 0) _settings.ServerActiveNicknames[_selectedServer] = names[0];
                _settings.Nickname = names.FirstOrDefault() ?? "Player";
                NicknameTextBox.Text = _settings.Nickname;
            }
            SaveSettings();
            SendWebState();
            LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "nicknameProfilesResult", success = true, message = "Сохранённый никнейм удалён." }));
        }

        private void ApplySettingsToInterface()
        {
            NicknameTextBox.Text = _settings.Nickname;
        }

        private bool SaveNickname()
        {
            string nickname = NicknameTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(nickname))
            {
                ShowNotificationMessage(
                    "Введи игровой никнейм.",
                    "TWM Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                NicknameTextBox.Focus();
                return false;
            }

            if (nickname.Length > 24)
            {
                ShowNotificationMessage(
                    "Никнейм не должен превышать 24 символа.",
                    "TWM Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return false;
            }

            if (nickname.Any(char.IsControl))
            {
                ShowNotificationMessage(
                    "Никнейм содержит недопустимые символы.",
                    "TWM Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return false;
            }

            _settings.Nickname = nickname;
            _settings.SelectedServer = _selectedServer;

            SaveSettings();
            return true;
        }

        // =========================================================
        // SERVER SELECTION
        // =========================================================

        private void Server_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not string serverName)
            {
                return;
            }

            if (!_servers.ContainsKey(serverName))
                return;

            _selectedServer = serverName;
            _settings.SelectedServer = serverName;
            ApplyActiveNicknameForServer(serverName);

            UpdateSelectedServerInterface();
            SaveSettings();
        }

        private void UpdateSelectedServerInterface()
        {
            if (!_servers.TryGetValue(_selectedServer, out var server))
                return;

            SelectedServerTitle.Text = server.Name;
            SelectedServerName.Text = server.Name;
            SelectedServerDescription.Text = $"{server.IpAddress}:{server.Port}";

            UpdateServerSelectionVisuals();
            SendWebState();
        }

        private void UpdateServerSelectionVisuals()
        {
            foreach (Button button in FindVisualChildren<Button>(this))
            {
                if (button.Tag is not string serverName || !_servers.ContainsKey(serverName))
                    continue;

                bool isSelected = string.Equals(
                    serverName, _selectedServer, StringComparison.OrdinalIgnoreCase);

                button.Background = new SolidColorBrush(isSelected
                    ? Color.FromRgb(35, 43, 25)
                    : Color.FromRgb(20, 22, 34));
                button.BorderBrush = new SolidColorBrush(isSelected
                    ? Color.FromRgb(214, 255, 95)
                    : Color.FromRgb(42, 44, 59));
                button.BorderThickness = new Thickness(isSelected ? 1.5 : 1);
                button.ToolTip = isSelected
                    ? $"{serverName} — выбранный сервер"
                    : $"Выбрать сервер {serverName}";
            }
        }

        // =========================================================
        // GAME LAUNCH
        // =========================================================

        private async void LaunchGame_Click(object sender, RoutedEventArgs e)
        {
            if (_launchInProgress || IsGameClientRunning())
            {
                RefreshGameStatus();
                return;
            }
            // Validate the nickname currently entered in the field before starting anything.
            // Do not fall back to an older nickname saved in settings when the field is empty.
            if (!SaveNickname())
            {
                return;
            }

            if (!_servers.TryGetValue(_selectedServer, out var server))
            {
                ShowNotificationMessage(
                    "Выбранный сервер не найден.",
                    "TWM Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            string gameDirectory = _settings.GameDirectory;

            if (string.IsNullOrWhiteSpace(gameDirectory) ||
                !Directory.Exists(gameDirectory))
            {
                ShowNotificationMessage(
                    "Сначала укажи папку игры в настройках лаунчера.",
                    "Папка игры не найдена",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                ShowSettings_Click(this, new RoutedEventArgs());
                return;
            }

            string starterPath = Path.Combine(
                gameDirectory,
                "aggstarter.exe");

            if (!File.Exists(starterPath))
            {
                ShowNotificationMessage(
                    "В выбранной папке не найден aggstarter.exe.\n\n" +
                    "Проверь путь к игре в настройках.",
                    "Файл запуска не найден",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            string nickname = _settings.Nickname.Trim();

            if (nickname.Length > 24 ||
                nickname.Any(char.IsControl))
            {
                ShowNotificationMessage(
                    "Проверь игровой никнейм.",
                    "Некорректный никнейм",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            try
            {
                _launchInProgress = true;
                RefreshGameStatus();

                var startInfo = new ProcessStartInfo
                {
                    FileName = starterPath,
                    WorkingDirectory = gameDirectory,
                    UseShellExecute = true
                };

                // Формируем аргументы отдельно, без ручной конкатенации
                // командной строки.
                startInfo.ArgumentList.Add("-amazing");
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("-h");
                startInfo.ArgumentList.Add(server.IpAddress);
                startInfo.ArgumentList.Add("-p");
                startInfo.ArgumentList.Add(server.Port.ToString());
                startInfo.ArgumentList.Add("-n");
                startInfo.ArgumentList.Add(nickname);
                startInfo.ArgumentList.Add("--launcher");
                // Режим gta_sa.exe использует потоковую схему клиента и QuickASI.
                // Для amazing.exe --stream не передаётся.
                bool useGtaSaTarget = string.Equals(NormalizeTargetProcess(_settings.TargetProcess), "gta_sa.exe", StringComparison.OrdinalIgnoreCase);
                if (useGtaSaTarget)
                    startInfo.ArgumentList.Add("--stream");

                Process? starterProcess = Process.Start(startInfo);

                if (starterProcess == null)
                {
                    ShowNotificationMessage(
                        "Windows не смогла запустить aggstarter.exe.",
                        "Ошибка запуска",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    return;
                }

                _settings.SelectedServer = server.Name;
                SaveSettings();

                // Не считаем сам запуск aggstarter.exe подтверждением запуска игры.
                // Ждём появления настоящего игрового процесса ограниченное время.
                bool gameProcessFound = await WaitForGameClientAsync(45);
                if (!gameProcessFound)
                {
                    ShowNotificationMessage(
                        "aggstarter.exe был запущен, но игровой процесс не появился в течение 45 секунд.\n\n" +
                        "Проверь запуск игры и настройки клиента. Кнопка запуска будет доступна повторно.",
                        "Игра не запустилась",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Игра уже обнаружена: QuickASI не должен удерживать кнопку «Играть»
                // в состоянии запуска во время собственного ожидания.
                _launchInProgress = false;
                RefreshGameStatus();

                // QuickASI работает с gta_sa.exe, а не с amazing.exe.
                // Если целевого файла нет в выбранном каталоге, не запускаем
                // QuickASI вслепую: сам игровой клиент всё равно можно запустить.
                bool gtaSaExists = File.Exists(Path.Combine(gameDirectory, "gta_sa.exe"));
                bool quickAsiAllowed = useGtaSaTarget && _settings.UseQuickAsi;
                if (useGtaSaTarget && _settings.UseQuickAsi && !gtaSaExists)
                {
                    ShowNotificationMessage(
                        "Игра запускается, но QuickASI пропущен: в выбранной папке не найден gta_sa.exe.\n\n" +
                        "Проверь путь к игре в настройках. Остальной запуск не отменён.",
                        "QuickASI недоступен",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                // Ошибки необязательного загрузчика не должны трактоваться как ошибка запуска игры.
                try
                {
                    // Возможность отключить загрузчик сохраняется в настройках.
                    if (quickAsiAllowed && gtaSaExists)
                    {
                    // quick_asi.exe включён в сборку лаунчера как EmbeddedResource.
                    // Извлекаем его в папку данных пользователя, а не ищем в папке игры.
                    string quickAsiPath = EnsureQuickAsiExtracted();

                    if (string.IsNullOrWhiteSpace(quickAsiPath) || !File.Exists(quickAsiPath))
                    {
                        ShowNotificationMessage(
                            "QuickASI недоступен: файл quick_asi.exe не включён в эту сборку.\n\n" +
                            "Игра продолжит запускаться без QuickASI. Чтобы использовать загрузчик, " +
                            "проверь источник и разрешение на распространение файла.",
                            "Загрузчик модификаций недоступен",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    else
                    {
                        bool processFound = await WaitForProcessAsync("gta_sa", 120);

                        if (processFound)
                        {
                            var asiInfo = new ProcessStartInfo
                            {
                                FileName = quickAsiPath,
                                WorkingDirectory = gameDirectory,
                                UseShellExecute = true
                            };

                            asiInfo.ArgumentList.Add("-p");
                            asiInfo.ArgumentList.Add("gta_sa.exe");
                            asiInfo.ArgumentList.Add("-silent");

                            Process? asiProcess = Process.Start(asiInfo);
                        }
                        else
                        {
                            ShowNotificationMessage(
                                "Не удалось обнаружить gta_sa.exe в течение 120 секунд.\n\n" +
                                "Игра могла запускаться дольше обычного или использовать другой процесс. " +
                                "quick_asi.exe не был запущен.",
                                "Игровой процесс не найден",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        }
                    }
                    }
                }
                catch (Exception asiException)
                {
                    ShowNotificationMessage(
                        "Игра запущена, но QuickASI не удалось запустить.\n\n" + asiException.Message,
                        "Ошибка QuickASI",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                ShowNotificationMessage(
                    "Windows не смогла запустить процесс.\n\n" + ex.Message,
                    "Ошибка запуска",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                ShowNotificationMessage(
                    "Произошла ошибка при запуске игры.\n\n" + ex.Message,
                    "Ошибка запуска",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _launchInProgress = false;
                RefreshGameStatus();
            }
        }

        private async Task<bool> WaitForGameClientAsync(int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
            while (DateTime.UtcNow < deadline)
            {
                if (HasLiveProcess(GetTargetProcessName()))
                    return true;

                await Task.Delay(500).ConfigureAwait(true);
            }

            return HasLiveProcess(GetTargetProcessName());
        }

        private static bool HasLiveProcess(string processName)
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        try
                        {
                            if (!process.HasExited)
                                return true;
                        }
                        catch (InvalidOperationException) { }
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is System.Security.SecurityException)
            {
                Debug.WriteLine($"Unable to inspect process {processName}: {ex.Message}");
            }

            return false;
        }

        private bool IsGameClientRunning()
        {
            // Не считаем aggstarter.exe самой игрой: он может закрыться или
            // оставаться активным отдельно от GTA SA.
            foreach (string processName in new[] { GetTargetProcessName() })
            {
                try
                {
                    foreach (Process process in Process.GetProcessesByName(processName))
                    {
                        using (process)
                        {
                            try
                            {
                                if (!process.HasExited)
                                    return true;
                            }
                            catch (InvalidOperationException) { }
                        }
                    }
                }
                catch
                {
                    // Ошибка проверки не должна приводить к аварийному закрытию UI.
                }
            }
            return false;
        }

        private void RefreshGameStatus()
        {
            if (PlayButton == null)
                return;

            bool running = IsGameClientRunning();
            _lastGameRunning = running;

            if (running)
            {
                PlayButton.IsEnabled = false;
                PlayButton.ToolTip = "Игровой процесс уже запущен";
                PlayButton.Background = new SolidColorBrush(Color.FromRgb(53, 39, 68));
                if (PlayButtonLabel != null) PlayButtonLabel.Text = "ИГРА УЖЕ ЗАПУЩЕНА";
                if (PlayButtonIcon != null) PlayButtonIcon.Text = "●";
                if (QuickStatusText != null) QuickStatusText.Text = "Игра запущена";
                if (QuickStatusDot != null) QuickStatusDot.Fill = new SolidColorBrush(Color.FromRgb(105, 217, 162));
                if (SessionStatusText != null) SessionStatusText.Text = "Игра запущена";
            }
            else if (_launchInProgress)
            {
                PlayButton.IsEnabled = false;
                PlayButton.ToolTip = "Запуск игрового клиента";
                PlayButton.Background = new SolidColorBrush(Color.FromRgb(92, 58, 125));
                if (PlayButtonLabel != null) PlayButtonLabel.Text = "ЗАПУСК...";
                if (PlayButtonIcon != null) PlayButtonIcon.Text = "◌";
                if (QuickStatusText != null) QuickStatusText.Text = "Запускаем клиент...";
                if (QuickStatusDot != null) QuickStatusDot.Fill = new SolidColorBrush(Color.FromRgb(181, 108, 255));
                if (SessionStatusText != null) SessionStatusText.Text = "Запуск клиента...";
            }
            else
            {
                PlayButton.IsEnabled = true;
                PlayButton.ToolTip = "Запустить выбранный игровой мир";
                PlayButton.ClearValue(Control.BackgroundProperty);
                if (PlayButtonLabel != null) PlayButtonLabel.Text = "ЗАПУСТИТЬ ИГРУ";
                if (PlayButtonIcon != null) PlayButtonIcon.Text = "▶";
                if (QuickStatusText != null) QuickStatusText.Text = "Клиент готов к запуску";
                if (QuickStatusDot != null) QuickStatusDot.Fill = new SolidColorBrush(Color.FromRgb(181, 108, 255));
                if (SessionStatusText != null) SessionStatusText.Text = "Готов к запуску";
            }
            SendWebState();
        }

        private static string? GetManifestResourceNameForQuickAsi()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith("Resources.quick_asi.exe", StringComparison.OrdinalIgnoreCase)
                                         || name.Equals("quick_asi.exe", StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        // =========================================================
        // MAIN WINDOW NAVIGATION
        // =========================================================

        private void ScrollToTop_Click(object sender, RoutedEventArgs e)
        {
            if (MainScrollViewer != null)
                MainScrollViewer.ScrollToVerticalOffset(0);
        }

        private void OpenServerBrowser_Click(object sender, RoutedEventArgs e)
        {
            var browser = new ServerBrowserWindow(_selectedServer, SelectServerFromBrowser)
            {
                Owner = this
            };
            browser.ShowDialog();
        }

        private void SelectServerFromBrowser(string serverName)
        {
            if (!_servers.ContainsKey(serverName)) return;
            _selectedServer = serverName;
            _settings.SelectedServer = serverName;
            UpdateSelectedServerInterface();
            SaveSettings();
        }

        private void ScrollToServers_Click(object sender, RoutedEventArgs e)
        {
            if (MainScrollViewer == null)
                return;

            try
            {
                if (ServersSectionHeader != null)
                {
                    System.Windows.Point point = ServersSectionHeader
                        .TransformToAncestor(MainScrollViewer)
                        .Transform(new System.Windows.Point(0, 0));

                    MainScrollViewer.ScrollToVerticalOffset(
                        MainScrollViewer.VerticalOffset + point.Y - 18);
                }
            }
            catch (InvalidOperationException)
            {
                // If layout has not completed yet, leave the current scroll position unchanged.
            }
        }

        private void MainScrollViewer_PreviewMouseWheel(
            object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (MainScrollViewer == null)
                return;

            e.Handled = true;
            MainScrollViewer.ScrollToVerticalOffset(
                MainScrollViewer.VerticalOffset - e.Delta);
        }

        private void OpenMods_Click(object sender, RoutedEventArgs e)
        {
            // Команда из WebView «DLL / ASI МОДЫ» должна открывать именно
            // вкладку управления модификациями, а не библиотеку сборок.
            ShowSettings_Click(new Button { Tag = 2 }, new RoutedEventArgs());
        }

        private void InstallLocalSaFix(LocalBuildInfo build)
        {
            try
            {
                string gameDirectory = _settings.GameDirectory ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
                {
                    SilverMessageBox.Show(
                        "Сначала укажи существующую папку игры в настройках лаунчера.",
                        "Папка игры не настроена", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                bool confirmed = SilverConfirmWindow.Show(
                    this,
                    "Установка САМОФИКСа",
                    $"Установить САМОФИКС «{build.Name}» в папку игры?",
                    $"Папка назначения:\n{gameDirectory}\n\nФайлы с совпадающими именами будут заменены. Перед заменой TWM Launcher сохранит резервные копии, чтобы можно было восстановить оригиналы.",
                    "УСТАНОВИТЬ", "ОТМЕНА", warning: true);
                if (!confirmed) return;

                var result = LocalBuildStore.InstallSaFix(build, gameDirectory);
                string backupMessage = result.BackedUpFiles > 0
                    ? $"\nРезервные копии: {result.BackupDirectory}"
                    : "\nСуществующие файлы не заменялись, резервная копия не потребовалась.";
                SilverMessageBox.Show(
                    $"САМОФИКС установлен.\n\nФайлов обработано: {result.InstalledFiles:N0}.\nЗаменено существующих файлов: {result.BackedUpFiles:N0}.{backupMessage}\n\nТеперь можно запускать игру обычной кнопкой лаунчера.",
                    "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SilverMessageBox.Show(
                    "Не удалось установить САМОФИКС.\n\n" + ex.Message +
                    "\n\nЕсли часть файлов уже была изменена, проверь папку резервных копий TWMBuilds\\Backups.",
                    "Ошибка установки САМОФИКСа", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RestoreLocalSaFix(LocalBuildInfo build)
        {
            try
            {
                if (!LocalBuildStore.HasRestorableSaFix(build))
                {
                    SilverMessageBox.Show(
                        "Для этого САМОФИКСа нет доступной резервной копии. Сначала установи его в официальную версию игры.",
                        "Восстановление недоступно", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string gameDirectory = LocalBuildStore.GetRestorableSaFixTargetDirectory(build);
                bool confirmed = SilverConfirmWindow.Show(
                    this,
                    "Возврат стандартных файлов",
                    $"Вернуть стандартные файлы вместо САМОФИКСа «{build.Name}»?",
                    $"Будут восстановлены резервные копии файлов, существовавших до установки, а файлы, добавленные САМОФИКСом, будут удалены.\n\nПапка игры:\n{gameDirectory}",
                    "ВОССТАНОВИТЬ", "ОТМЕНА", warning: true);
                if (!confirmed) return;

                var result = LocalBuildStore.RestoreLatestSaFix(build);
                SilverMessageBox.Show(
                    $"Стандартные файлы восстановлены.\n\nВосстановлено файлов: {result.RestoredFiles:N0}.\nУдалено файлов, добавленных САМОФИКСом: {result.RemovedFiles:N0}.",
                    "Восстановление завершено", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SilverMessageBox.Show(
                    "Не удалось вернуть стандартные файлы.\n\n" + ex.Message,
                    "Ошибка восстановления", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ActivateLocalBuild(LocalBuildInfo build)
        {
            try
            {
                if (build.Kind != LocalBuildKind.FullGame)
                {
                    SilverMessageBox.Show("Выбрать для запуска можно только полную игровую сборку.",
                        "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string contentDirectory = LocalBuildStore.GetContentDirectory(build);
                string starterPath = Path.Combine(contentDirectory, "aggstarter.exe");
                if (!File.Exists(starterPath))
                {
                    SilverMessageBox.Show(
                        "Архив распознан как игровая сборка, но в её корневой папке не найден aggstarter.exe.\n\n" +
                        "Сборка сохранена и доступна для просмотра, однако выбрать её для запуска нельзя. Проверь содержимое архива.",
                        "Сборка не готова к запуску", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _settings.GameDirectory = contentDirectory;
                SaveSettings();
                SilverMessageBox.Show(
                    $"Сборка «{build.Name}» выбрана для запуска.\n\nПапка игры:\n{contentDirectory}\n\nКнопка «Запустить игру» теперь использует эту папку.",
                    "Сборка выбрана", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SilverMessageBox.Show("Не удалось выбрать сборку.\n\n" + ex.Message,
                    "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EnsureQuickAsiExtracted()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string? resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith("Resources.quick_asi.exe", StringComparison.OrdinalIgnoreCase)
                                         || name.Equals("quick_asi.exe", StringComparison.OrdinalIgnoreCase));

                if (resourceName == null)
                    return string.Empty;

                string outputDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TWM Launcher",
                    "Tools");
                Directory.CreateDirectory(outputDirectory);

                string outputPath = Path.Combine(outputDirectory, "quick_asi.exe");

                using Stream? resource = assembly.GetManifestResourceStream(resourceName);
                if (resource == null)
                    return string.Empty;

                using FileStream output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                resource.CopyTo(output);
                output.Flush();

                return outputPath;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return string.Empty;
            }
        }

        private static async Task<bool> WaitForProcessAsync(string processName, int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                Process[] processes = Process.GetProcessesByName(processName);
                bool found = false;

                foreach (Process process in processes)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            found = true;
                            break;
                        }
                    }
                    catch
                    {
                        // Процесс мог завершиться между получением списка и проверкой.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                if (found)
                    return true;

                await Task.Delay(500);
            }

            return false;
        }

        private void SendLaunchDiagnosticsToWeb()
        {
            if (LauncherWebView.CoreWebView2 == null) return;
            string gameDirectory = _settings.GameDirectory ?? string.Empty;
            bool gameFolderExists = !string.IsNullOrWhiteSpace(gameDirectory) && Directory.Exists(gameDirectory);
            bool starterExists = gameFolderExists && File.Exists(Path.Combine(gameDirectory, "aggstarter.exe"));
            bool gameExeExists = gameFolderExists && File.Exists(Path.Combine(gameDirectory, "gta_sa.exe"));
            bool quickAsiEmbedded = GetManifestResourceNameForQuickAsi() != null;
            string asiDirectory = gameFolderExists ? Path.Combine(gameDirectory, "asi") : string.Empty;
            bool asiDirectoryExists = gameFolderExists && Directory.Exists(asiDirectory);
            bool nicknameValid = !string.IsNullOrWhiteSpace(_settings.Nickname) && _settings.Nickname.Trim().Length <= 24;
            var checks = new[]
            {
                new { title = "Папка игры", state = gameFolderExists ? "ok" : "fail", message = gameFolderExists ? gameDirectory : "Папка не выбрана или не существует. Укажи правильную папку игры." },
                new { title = "aggstarter.exe", state = starterExists ? "ok" : "fail", message = starterExists ? "Файл запуска клиента найден." : "Не найден aggstarter.exe в выбранной папке." },
                new { title = "gta_sa.exe для QuickASI", state = !_settings.UseQuickAsi ? "warning" : gameExeExists ? "ok" : "fail", message = !_settings.UseQuickAsi ? "QuickASI отключён — проверка файла не требуется." : gameExeExists ? "Файл найден; QuickASI может быть запущен с этой целью." : "Файл не найден. Игра может запуститься, но QuickASI будет пропущен." },
                new { title = "QuickASI в ресурсах лаунчера", state = !_settings.UseQuickAsi ? "warning" : quickAsiEmbedded ? "ok" : "fail", message = !_settings.UseQuickAsi ? "QuickASI отключён в настройках." : quickAsiEmbedded ? "Встроенный quick_asi.exe найден." : "Встроенный quick_asi.exe не найден в сборке лаунчера." },
                new { title = "Папка asi", state = !gameFolderExists ? "warning" : asiDirectoryExists ? "ok" : "warning", message = !gameFolderExists ? "Проверка пропущена: сначала выбери папку игры." : asiDirectoryExists ? "Папка для ASI-модов существует." : "Папка asi отсутствует. Она может быть создана при установке модов." },
                new { title = "Игровой никнейм", state = nicknameValid ? "ok" : "warning", message = nicknameValid ? "Никнейм задан и не превышает 24 символа." : "Проверь никнейм: он не должен быть пустым и должен содержать не более 24 символов." }
            };
            int failures = checks.Count(c => c.state == "fail");
            int warnings = checks.Count(c => c.state == "warning");
            string summary = failures == 0
                ? (warnings == 0 ? "Все основные проверки пройдены. Клиент готов к запуску." : $"Критических ошибок не найдено; предупреждений: {warnings}.")
                : $"Найдено проблем: {failures}. Исправь ошибки перед запуском.";
            var payload = new { type = "launchDiagnostics", success = failures == 0, summary, checks };
            LauncherWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }

        // =========================================================
        // SETTINGS WINDOW CONTENT
        // =========================================================

        private void SendSettingsToWeb()
        {
            if (LauncherWebView.CoreWebView2 == null) return;
            var data = new
            {
                type = "settingsData",
                gameDirectory = _settings.GameDirectory ?? string.Empty,
                nickname = _settings.Nickname ?? "Player",
                launcherPath = _settings.LauncherPath ?? string.Empty,
                useQuickAsi = _settings.UseQuickAsi && NormalizeTargetProcess(_settings.TargetProcess) == "gta_sa.exe",
                dllAsiAllowed = NormalizeTargetProcess(_settings.TargetProcess) == "gta_sa.exe",
                foliageEnabled = IsFoliageEnabledFromFiles(),
                targetProcess = NormalizeTargetProcess(_settings.TargetProcess),
                f1ImageData = GetF1ImageDataUrl(),
                f1ImageExists = File.Exists(Path.Combine(_settings.GameDirectory ?? string.Empty, "amazing", "f1_help_overlay.png")),
                dataDirectory = _settingsDirectory,
                gameFolderExists = Directory.Exists(_settings.GameDirectory),
                starterExists = File.Exists(Path.Combine(_settings.GameDirectory ?? string.Empty, "aggstarter.exe")),
                gameExeExists = File.Exists(Path.Combine(_settings.GameDirectory ?? string.Empty, "gta_sa.exe"))
            };
            // Send data only. Navigation is owned by Web/app.js; calling twmOpenSettings
            // here caused a feedback loop: getSettings -> SendSettingsToWeb -> twmOpenSettings -> getSettings.
            LauncherWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data));
        }

        private object[] SafeLoadWebProfiles()
        {
            try { return LoadModProfiles().Select(p => (object)new { id = p.Id, name = p.Name, active = string.Equals(p.Id, ActiveModProfileId, StringComparison.OrdinalIgnoreCase) }).ToArray(); }
            catch { return Array.Empty<object>(); }
        }

        private void SendModLibraryToWeb()
        {
            if (LauncherWebView.CoreWebView2 == null) return;
            try
            {
                LoadModLibrary();
                var orderedMods = _mods.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList();
                var payload = new
                {
                    type = "modLibrary",
                    success = true,
                    mods = orderedMods.Select(x =>
                    {
                        string deployedPath = Path.Combine(AsiDirectory, x.FileName);
                        bool deployedExists = File.Exists(deployedPath);
                        bool deployedChanged = deployedExists && !string.IsNullOrWhiteSpace(x.Sha256) &&
                            !string.Equals(CalculateSha256(deployedPath), x.Sha256, StringComparison.OrdinalIgnoreCase);
                        ModInfo? sameContent = !string.IsNullOrWhiteSpace(x.Sha256)
                            ? orderedMods.FirstOrDefault(other => !ReferenceEquals(other, x) &&
                                string.Equals(other.Sha256, x.Sha256, StringComparison.OrdinalIgnoreCase))
                            : null;
                        return new
                        {
                            fileName = x.FileName, enabled = x.Enabled, storedDisabled = x.StoredDisabled,
                            sha256 = x.Sha256, source = x.StoredDisabled ? "Библиотека TWM" : "Папка игры\\asi",
                            conflict = deployedChanged,
                            conflictReason = deployedChanged ? "Файл в папке игры изменён вне лаунчера. Отключение или перезапись заблокированы." : string.Empty,
                            duplicateOf = sameContent?.FileName ?? string.Empty
                        };
                    }).ToArray()
                };
                LauncherWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            }
            catch (Exception ex)
            {
                LauncherWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "modLibrary", success = false, error = ex.Message, mods = Array.Empty<object>() }));
            }
        }

        private void ToggleModFromWeb(JsonElement root)
        {
            try
            {
                if (NormalizeTargetProcess(_settings.TargetProcess) == "amazing.exe")
                    throw new InvalidOperationException("DLL / ASI-модификации отключены для целевого процесса amazing.exe. Выбери gta_sa.exe в настройках.");
                if (!root.TryGetProperty("fileName", out var fileElement)) return;
                string fileName = fileElement.GetString() ?? string.Empty;
                if (!IsSafeModName(fileName)) throw new InvalidOperationException("Недопустимое имя модификации. Разрешены только .dll и .asi.");
                LoadModLibrary();
                ModInfo? mod = _mods.FirstOrDefault(x => string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                if (mod == null) throw new FileNotFoundException("Модификация не найдена в библиотеке.");
                if (SetDllEnabled(mod.RelativePath, !mod.Enabled))
                    LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "modActionResult", success = true, message = mod.Enabled ? "Мод включён." : "Мод отключён." }));
                else
                    LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "modActionResult", success = false, message = "Операция отменена. Убедись, что игра закрыта." }));
            }
            catch (Exception ex)
            {
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "modActionResult", success = false, message = ex.Message }));
            }
            SendModLibraryToWeb();
        }

        private void DeleteModFromWeb(JsonElement root)
        {
            try
            {
                if (NormalizeTargetProcess(_settings.TargetProcess) == "amazing.exe")
                    throw new InvalidOperationException("Управление DLL / ASI недоступно для amazing.exe. Выбери gta_sa.exe в настройках.");
                if (!root.TryGetProperty("fileName", out var fileElement))
                    throw new InvalidOperationException("Не указано имя модификации.");
                string fileName = fileElement.GetString() ?? string.Empty;
                if (!IsSafeModName(fileName))
                    throw new InvalidOperationException("Недопустимое имя модификации. Разрешены только .dll и .asi.");
                if (!EnsureGameClosed())
                    throw new InvalidOperationException("Закрой игру перед удалением модификации.");

                LoadModLibrary();
                ModInfo? mod = _mods.FirstOrDefault(x => string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                if (mod == null) throw new FileNotFoundException("Модификация не найдена в библиотеке.");

                string libraryRoot = Path.GetFullPath(ModsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string libraryPath = Path.GetFullPath(Path.Combine(ModsRoot, mod.FileName));
                string deployedPath = Path.GetFullPath(Path.Combine(AsiDirectory, mod.FileName));
                if (!libraryPath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Путь к файлу библиотеки недопустим.");

                // Не удаляем файл из папки игры, если он был изменён вне лаунчера.
                if (File.Exists(deployedPath))
                {
                    string deployedHash = CalculateSha256(deployedPath);
                    if (!string.IsNullOrWhiteSpace(mod.Sha256) &&
                        !string.Equals(deployedHash, mod.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Файл в папке asi отличается от сохранённой копии. Удаление отменено, чтобы не потерять изменения.");
                    File.Delete(deployedPath);
                }
                if (File.Exists(libraryPath)) File.Delete(libraryPath);

                _mods.RemoveAll(x => string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                SaveModLibrary();
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "modActionResult", success = true, message = $"Модификация {fileName} удалена из библиотеки и папки asi."
                }));
            }
            catch (Exception ex)
            {
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "modActionResult", success = false, message = ex.Message
                }));
            }
            SendModLibraryToWeb();
        }

        private void ChooseGameDirectoryFromWeb()
        {
            var dialog = new OpenFolderDialog { Title = "Выбери папку с aggstarter.exe", Multiselect = false };
            if (Directory.Exists(_settings.GameDirectory)) dialog.InitialDirectory = _settings.GameDirectory;
            if (dialog.ShowDialog() != true) return;
            if (!File.Exists(Path.Combine(dialog.FolderName, "aggstarter.exe")))
            {
                ShowNotificationMessage("В выбранной папке не найден aggstarter.exe.", "Неверная папка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _settings.GameDirectory = dialog.FolderName;
            SaveSettings();
            ApplySettingsToInterface();
            SendSettingsToWeb();
        }

        private void OpenDirectoryFromWeb(string path, string label)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    throw new DirectoryNotFoundException($"{label} не найдена.");
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
            }
            catch (Exception ex) { ShowNotificationMessage("Не удалось открыть папку.\n\n" + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void SaveWebSettings(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("useQuickAsi", out var quick) &&
                    (quick.ValueKind == JsonValueKind.True || quick.ValueKind == JsonValueKind.False))
                    _settings.UseQuickAsi = quick.GetBoolean();
                if (root.TryGetProperty("foliageEnabled", out var foliage) &&
                    (foliage.ValueKind == JsonValueKind.True || foliage.ValueKind == JsonValueKind.False))
                    _settings.FoliageEnabled = foliage.GetBoolean();
                if (root.TryGetProperty("targetProcess", out var target) && target.ValueKind == JsonValueKind.String)
                    _settings.TargetProcess = NormalizeTargetProcess(target.GetString());
                // amazing.exe не поддерживает DLL/ASI-инжекцию и QuickASI.
                if (NormalizeTargetProcess(_settings.TargetProcess) == "amazing.exe")
                    _settings.UseQuickAsi = false;

                if (root.TryGetProperty("nickname", out var nick))
                {
                    var value = nick.GetString() ?? "Player";
                    if (!string.IsNullOrWhiteSpace(value) && value.Length <= 24 && !value.Any(char.IsControl))
                        _settings.Nickname = value.Trim();
                }

                SaveSettings();
                ApplySettingsToInterface();
                SendSettingsToWeb();
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "settingsSaveResult",
                    success = true,
                    message = "Настройки успешно сохранены."
                }));
                ShowNotificationMessage("Настройки сохранены.", "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SaveWebSettings failed: " + ex);
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "settingsSaveResult",
                    success = false,
                    message = "Не удалось сохранить настройки: " + ex.Message
                }));
            }
        }

        private static string NormalizeTargetProcess(string? value) =>
            string.Equals(value, "gta_sa.exe", StringComparison.OrdinalIgnoreCase) ? "gta_sa.exe" : "amazing.exe";

        private string GetF1ImageDataUrl()
        {
            try
            {
                string path = Path.Combine(_settings.GameDirectory ?? string.Empty, "amazing", "f1_help_overlay.png");
                if (!File.Exists(path)) return string.Empty;
                byte[] bytes = File.ReadAllBytes(path);
                if (!IsPng(bytes)) return string.Empty;
                return "data:image/png;base64," + Convert.ToBase64String(bytes);
            }
            catch { return string.Empty; }
        }

        private static bool IsPng(byte[] bytes) => bytes.Length >= 8 &&
            bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 &&
            bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;

        private static readonly Dictionary<string, Dictionary<string, string[]>> SkinReplacementGroups =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["russian"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["male"] = new[] { "hmyri", "bmymoun", "maffa", "maffb", "vla2", "vla3", "sfr2", "forelli" },
                    ["female"] = new[] { "wfyst" }
                },
                ["caucasian"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["male"] = new[] { "wmyri", "mafboss", "vmaff1", "vmaff2", "vmaff4", "shmycr" },
                    ["female"] = new[] { "swfyri" }
                },
                ["skinheads"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["male"] = new[] { "skin1", "skin2", "skin3", "skin4", "skin5", "skin8", "skin7" },
                    ["female"] = new[] { "skin6" }
                },
                ["bikers"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["male"] = new[] { "baiker1", "baiker2", "baiker3", "baiker4", "baiker5", "bikera", "bikerb" },
                    ["female"] = new[] { "baiker6", "baiker8", "VHFYST3" }
                }
            };

        private sealed class SkinBackupManifest
        {
            public string Organization { get; set; } = string.Empty;
            public string Gender { get; set; } = string.Empty;
            public DateTime CreatedUtc { get; set; }
            public List<SkinBackupEntry> Files { get; set; } = new();
        }

        private sealed class SkinBackupEntry
        {
            public string FileName { get; set; } = string.Empty;
            public bool Existed { get; set; }
        }

        private void ChooseSkinFileFromWeb(JsonElement root)
        {
            string kind = root.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() ?? string.Empty : string.Empty;
            if (kind != "dff" && kind != "txd") return;
            var dialog = new OpenFileDialog
            {
                Title = kind == "dff" ? "Выбери модель пользовательского скина (.dff)" : "Выбери текстуру пользовательского скина (.txd)",
                Filter = kind == "dff" ? "Модели GTA (*.dff)|*.dff" : "Текстуры GTA (*.txd)|*.txd",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;
            if (!string.Equals(Path.GetExtension(dialog.FileName), "." + kind, StringComparison.OrdinalIgnoreCase)) return;
            if (kind == "dff") _selectedSkinDffPath = dialog.FileName; else _selectedSkinTxdPath = dialog.FileName;
            LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "skinFileSelected", kind, path = dialog.FileName, name = Path.GetFileName(dialog.FileName)
            }));
        }

        private static bool TryGetSkinGroup(JsonElement root, out string organization, out string gender, out string[] ids, out string error)
        {
            organization = root.TryGetProperty("organization", out var orgElement) ? orgElement.GetString() ?? string.Empty : string.Empty;
            gender = root.TryGetProperty("gender", out var genderElement) ? genderElement.GetString() ?? string.Empty : string.Empty;
            ids = Array.Empty<string>(); error = string.Empty;
            if (!SkinReplacementGroups.TryGetValue(organization, out var groups) || !groups.TryGetValue(gender, out var selectedIds))
            {
                error = "Неизвестная организация или группа скинов."; return false;
            }
            ids = selectedIds;
            return true;
        }

        private string SkinBackupRoot => Path.Combine(_settingsDirectory, "SkinReplacerBackups");

        private void ApplySkinReplacementFromWeb(JsonElement root)
        {
            string backupFolder = string.Empty;
            try
            {
                if (!TryGetSkinGroup(root, out string organization, out string gender, out string[] ids, out string error))
                    throw new InvalidOperationException(error);
                if (string.IsNullOrWhiteSpace(_settings.GameDirectory) || !Directory.Exists(_settings.GameDirectory))
                    throw new DirectoryNotFoundException("Сначала укажи корень игры в настройках.");
                if (string.IsNullOrWhiteSpace(_selectedSkinDffPath) || !File.Exists(_selectedSkinDffPath) ||
                    string.IsNullOrWhiteSpace(_selectedSkinTxdPath) || !File.Exists(_selectedSkinTxdPath))
                    throw new FileNotFoundException("Выбери оба исходных файла .dff и .txd заново.");
                if (!string.Equals(Path.GetExtension(_selectedSkinDffPath), ".dff", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetExtension(_selectedSkinTxdPath), ".txd", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Расширения выбранных файлов должны быть .dff и .txd.");

                string destinationRoot = Path.Combine(_settings.GameDirectory, "models", "assets", "skins");
                if (!Directory.Exists(destinationRoot)) throw new DirectoryNotFoundException("Не найдена папка models\\assets\\skins.");
                var targets = ids.SelectMany(id => new[] { id + ".dff", id + ".txd" }).ToArray();
                foreach (string fileName in targets)
                {
                    string path = Path.Combine(destinationRoot, fileName);
                    if (Directory.Exists(path)) throw new IOException($"Вместо файла обнаружена папка: {fileName}");
                }

                Directory.CreateDirectory(SkinBackupRoot);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                backupFolder = Path.Combine(SkinBackupRoot, $"{organization}_{gender}_{stamp}");
                string backupFiles = Path.Combine(backupFolder, "files");
                Directory.CreateDirectory(backupFiles);
                var manifest = new SkinBackupManifest { Organization = organization, Gender = gender, CreatedUtc = DateTime.UtcNow };
                foreach (string fileName in targets)
                {
                    string current = Path.Combine(destinationRoot, fileName);
                    bool existed = File.Exists(current);
                    manifest.Files.Add(new SkinBackupEntry { FileName = fileName, Existed = existed });
                    if (existed) File.Copy(current, Path.Combine(backupFiles, fileName), false);
                }
                File.WriteAllText(Path.Combine(backupFolder, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

                byte[] dffBytes = File.ReadAllBytes(_selectedSkinDffPath);
                byte[] txdBytes = File.ReadAllBytes(_selectedSkinTxdPath);
                foreach (string id in ids)
                {
                    WriteBytesSafely(Path.Combine(destinationRoot, id + ".dff"), dffBytes);
                    WriteBytesSafely(Path.Combine(destinationRoot, id + ".txd"), txdBytes);
                }
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "skinReplacementResult", success = true,
                    message = $"Установка завершена: заменено {ids.Length} моделей ({targets.Length} файлов). Резервная копия создана."
                }));
            }
            catch (Exception ex)
            {
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "skinReplacementResult", success = false,
                    message = "Не удалось установить скины. " + ex.Message + (string.IsNullOrEmpty(backupFolder) ? string.Empty : " Резервная копия создана в папке данных лаунчера.")
                }));
            }
        }

        private static void WriteBytesSafely(string destination, byte[] bytes)
        {
            string temp = destination + ".twm.tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, destination, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private void RestoreSkinReplacementFromWeb(JsonElement root)
        {
            try
            {
                if (!TryGetSkinGroup(root, out string organization, out string gender, out _, out string error))
                    throw new InvalidOperationException(error);
                if (string.IsNullOrWhiteSpace(_settings.GameDirectory) || !Directory.Exists(_settings.GameDirectory))
                    throw new DirectoryNotFoundException("Сначала укажи корень игры в настройках.");
                if (!Directory.Exists(SkinBackupRoot)) throw new DirectoryNotFoundException("Резервные копии для восстановления не найдены.");
                string? latest = Directory.GetDirectories(SkinBackupRoot, $"{organization}_{gender}_*")
                    .OrderByDescending(dir => Path.GetFileName(dir), StringComparer.Ordinal).FirstOrDefault(dir => File.Exists(Path.Combine(dir, "manifest.json")));
                if (latest == null) throw new FileNotFoundException("Для выбранной организации и группы нет резервной копии.");
                var manifest = JsonSerializer.Deserialize<SkinBackupManifest>(File.ReadAllText(Path.Combine(latest, "manifest.json")))
                    ?? throw new InvalidDataException("Файл резервной копии повреждён.");
                string destinationRoot = Path.Combine(_settings.GameDirectory, "models", "assets", "skins");
                if (!Directory.Exists(destinationRoot)) throw new DirectoryNotFoundException("Не найдена папка models\\assets\\skins.");
                foreach (var entry in manifest.Files)
                {
                    string destination = Path.Combine(destinationRoot, entry.FileName);
                    if (entry.Existed)
                    {
                        string backup = Path.Combine(latest, "files", entry.FileName);
                        if (!File.Exists(backup)) throw new FileNotFoundException("Не найден файл резервной копии: " + entry.FileName);
                        WriteBytesSafely(destination, File.ReadAllBytes(backup));
                    }
                    else if (File.Exists(destination)) File.Delete(destination);
                }
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "skinReplacementResult", success = true,
                    message = "Исходное состояние выбранной группы восстановлено."
                }));
            }
            catch (Exception ex)
            {
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "skinReplacementResult", success = false, message = "Не удалось восстановить скины. " + ex.Message }));
            }
        }


        private string AudioCacheDirectory => Path.Combine(_settings.GameDirectory ?? string.Empty, "aggvoice", "storage", "cache", "data");
        private string AudioBackupRoot => Path.Combine(_settingsDirectory, "AudioReplacerBackups");

        private static bool IsKnownAudioId(string id) => ReplaceableAudioFiles.Any(x => x.Id == id);

        private void ChooseAudioReplacementFromWeb(JsonElement root)
        {
            string id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            if (!IsKnownAudioId(id)) return;
            var dialog = new OpenFileDialog
            {
                Title = "Выбери новый звук в формате MP3",
                Filter = "Аудиофайлы MP3 (*.mp3)|*.mp3",
                CheckFileExists = true, Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;
            if (!string.Equals(Path.GetExtension(dialog.FileName), ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                PostAudioResult(id, false, "Поддерживаются только файлы .mp3."); return;
            }
            _selectedAudioReplacementPaths[id] = dialog.FileName;
            LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "audioFileSelected", id, name = Path.GetFileName(dialog.FileName)
            }));
        }

        private void PreviewAudioFromWeb(JsonElement root)
        {
            string id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            if (!IsKnownAudioId(id)) return;
            try
            {
                if (string.IsNullOrWhiteSpace(_settings.GameDirectory) || !Directory.Exists(_settings.GameDirectory))
                    throw new DirectoryNotFoundException("Сначала укажи корень игры в настройках.");
                string source = Path.Combine(AudioCacheDirectory, id);
                if (!File.Exists(source)) throw new FileNotFoundException("Файл этого звука не найден в кэше игры.", source);
                StopAudioPreview();
                string previewRoot = Path.Combine(_settingsDirectory, "AudioPreview");
                Directory.CreateDirectory(previewRoot);
                _audioPreviewTempPath = Path.Combine(previewRoot, id + "_preview.mp3");
                File.Copy(source, _audioPreviewTempPath, true);
                _audioPreviewPlayer = new MediaPlayer();
                _audioPreviewPlayer.MediaFailed += (_, args) => Dispatcher.BeginInvoke(new Action(() =>
                    PostAudioResult(id, false, "Не удалось воспроизвести файл как MP3: " + args.ErrorException?.Message)));
                _audioPreviewPlayer.MediaEnded += (_, __) => Dispatcher.BeginInvoke(new Action(StopAudioPreview));
                _audioPreviewPlayer.Open(new Uri(_audioPreviewTempPath, UriKind.Absolute));
                _audioPreviewPlayer.Play();
            }
            catch (Exception ex) { PostAudioResult(id, false, "Не удалось прослушать звук. " + ex.Message); }
        }

        private void StopAudioPreview()
        {
            try { _audioPreviewPlayer?.Stop(); _audioPreviewPlayer?.Close(); } catch { }
            _audioPreviewPlayer = null;
            try { if (!string.IsNullOrWhiteSpace(_audioPreviewTempPath) && File.Exists(_audioPreviewTempPath)) File.Delete(_audioPreviewTempPath); } catch { }
            _audioPreviewTempPath = string.Empty;
        }

        private static bool LooksLikeMp3(byte[] bytes)
        {
            if (bytes.Length < 4) return false;
            if (bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3') return true;
            for (int i = 0; i < Math.Min(bytes.Length - 1, 4096); i++)
                if (bytes[i] == 0xFF && (bytes[i + 1] & 0xE0) == 0xE0) return true;
            return false;
        }

        private void ApplyAudioReplacementFromWeb(JsonElement root)
        {
            string id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            string backupFolder = string.Empty;
            try
            {
                if (!IsKnownAudioId(id)) throw new InvalidOperationException("Неизвестный ID звука.");
                if (string.IsNullOrWhiteSpace(_settings.GameDirectory) || !Directory.Exists(_settings.GameDirectory))
                    throw new DirectoryNotFoundException("Сначала укажи корень игры в настройках.");
                if (!_selectedAudioReplacementPaths.TryGetValue(id, out string? selected) || string.IsNullOrWhiteSpace(selected) || !File.Exists(selected))
                    throw new FileNotFoundException("Сначала выбери MP3-файл для этого звука.");
                if (!string.Equals(Path.GetExtension(selected), ".mp3", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Поддерживаются только файлы .mp3.");
                byte[] replacementBytes = File.ReadAllBytes(selected);
                if (!LooksLikeMp3(replacementBytes)) throw new InvalidDataException("Файл имеет расширение .mp3, но не похож на MP3-аудио.");
                string destinationRoot = AudioCacheDirectory;
                if (!Directory.Exists(destinationRoot)) throw new DirectoryNotFoundException("Не найдена папка aggvoice\\storage\\cache\\data.");
                string destination = Path.Combine(destinationRoot, id);
                if (Directory.Exists(destination)) throw new IOException("По пути целевого звука находится папка, а не файл.");
                Directory.CreateDirectory(AudioBackupRoot);
                backupFolder = Path.Combine(AudioBackupRoot, id + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
                Directory.CreateDirectory(backupFolder);
                bool existed = File.Exists(destination);
                if (existed) File.Copy(destination, Path.Combine(backupFolder, id), false);
                File.WriteAllText(Path.Combine(backupFolder, "manifest.json"), JsonSerializer.Serialize(new { Id = id, Existed = existed, CreatedUtc = DateTime.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
                StopAudioPreview();
                WriteBytesSafely(destination, replacementBytes);
                _selectedAudioReplacementPaths.Remove(id);
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "audioReplacementResult", id, success = true, message = "Звук заменён. Файл установлен под исходным именем «" + id + "» без расширения. Резервная копия создана." }));
            }
            catch (Exception ex) { PostAudioResult(id, false, "Не удалось заменить звук. " + ex.Message + (string.IsNullOrEmpty(backupFolder) ? string.Empty : " Резервная копия создана.")); }
        }

        private void RestoreAudioReplacementFromWeb(JsonElement root)
        {
            string id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            try
            {
                if (!IsKnownAudioId(id)) throw new InvalidOperationException("Неизвестный ID звука.");
                if (string.IsNullOrWhiteSpace(_settings.GameDirectory) || !Directory.Exists(_settings.GameDirectory))
                    throw new DirectoryNotFoundException("Сначала укажи корень игры в настройках.");
                if (!Directory.Exists(AudioBackupRoot)) throw new DirectoryNotFoundException("Резервные копии звуков не найдены.");
                string? latest = Directory.GetDirectories(AudioBackupRoot, id + "_*").OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal).FirstOrDefault(x => File.Exists(Path.Combine(x, "manifest.json")));
                if (latest == null) throw new FileNotFoundException("Для этого звука нет резервной копии.");
                using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(latest, "manifest.json")));
                bool existed = manifestDoc.RootElement.TryGetProperty("Existed", out var existedElement) && existedElement.GetBoolean();
                string destination = Path.Combine(AudioCacheDirectory, id);
                if (!Directory.Exists(AudioCacheDirectory)) throw new DirectoryNotFoundException("Не найдена папка кэша звуков игры.");
                if (existed)
                {
                    string backup = Path.Combine(latest, id);
                    if (!File.Exists(backup)) throw new FileNotFoundException("Файл резервной копии повреждён.");
                    WriteBytesSafely(destination, File.ReadAllBytes(backup));
                }
                else if (File.Exists(destination)) File.Delete(destination);
                PostAudioResult(id, true, "Исходный звук восстановлен.");
            }
            catch (Exception ex) { PostAudioResult(id, false, "Не удалось восстановить звук. " + ex.Message); }
        }

        private void PostAudioResult(string id, bool success, string message) =>
            LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "audioReplacementResult", id, success, message }));

        private void ChooseF1ImageFromWeb()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_settings.GameDirectory) || !Directory.Exists(_settings.GameDirectory))
                    throw new DirectoryNotFoundException("Сначала укажи корень игры в настройках.");
                var dialog = new OpenFileDialog { Title = "Выбери изображение подсказки F1", Filter = "Изображения PNG (*.png)|*.png", CheckFileExists = true, Multiselect = false };
                if (dialog.ShowDialog() != true) return;
                byte[] bytes = File.ReadAllBytes(dialog.FileName);
                if (!IsPng(bytes)) throw new InvalidDataException("Выбранный файл не является корректным PNG (неверная сигнатура).");
                string folder = Path.Combine(_settings.GameDirectory, "amazing");
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Не найдена папка amazing внутри корня игры.");
                string destination = Path.Combine(folder, "f1_help_overlay.png");
                string temp = destination + ".twm.tmp";
                File.WriteAllBytes(temp, bytes);
                try { File.Move(temp, destination, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                SendSettingsToWeb();
                ShowNotificationMessage("Подсказка F1 успешно заменена.", "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { ShowNotificationMessage("Не удалось заменить подсказку F1.\n\n" + ex.Message, "Подсказка F1", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void ApplyFoliageFromWeb(JsonElement root)
        {
            if (!root.TryGetProperty("enabled", out var value) || (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)) return;
            bool enabled = value.GetBoolean();
            if (!ApplyFoliageState(enabled, out string message))
            {
                LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "foliageResult", success = false, message }));
                return;
            }
            _settings.FoliageEnabled = enabled;
            SaveSettings();
            SendSettingsToWeb();
            LauncherWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "foliageResult", success = true, message = enabled ? "Листва включена." : "Листва отключена." }));
        }

        private string GetTargetProcessName() => Path.GetFileNameWithoutExtension(NormalizeTargetProcess(_settings.TargetProcess));

        private bool IsFoliageEnabledFromFiles()
        {
            try
            {
                string vegetation = Path.Combine(_settings.GameDirectory ?? string.Empty, "models", "assets", "vegetation");
                string foliage = Path.Combine(vegetation, "foliage");
                string noFoliage = Path.Combine(vegetation, "no_foliage");
                string markerNoFoliage = Path.Combine(noFoliage, ".ignored");
                string markerFoliage = Path.Combine(foliage, ".ignored");
                if (File.Exists(markerNoFoliage)) return true;
                if (File.Exists(markerFoliage)) return false;

                // Если маркер отсутствует в обеих папках, создаём его в no_foliage.
                // Это соответствует исходному состоянию игры: листва включена.
                if (Directory.Exists(foliage) && Directory.Exists(noFoliage))
                {
                    File.WriteAllBytes(markerNoFoliage, Array.Empty<byte>());
                    return true;
                }
            }
            catch { }
            return _settings.FoliageEnabled;
        }

        private bool ApplyFoliageState(bool enabled, out string message)
        {
            message = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(_settings.GameDirectory) || !Directory.Exists(_settings.GameDirectory))
                    throw new DirectoryNotFoundException("Не задан корень игры.");
                string vegetation = Path.Combine(_settings.GameDirectory, "models", "assets", "vegetation");
                string foliage = Path.Combine(vegetation, "foliage");
                string noFoliage = Path.Combine(vegetation, "no_foliage");
                if (!Directory.Exists(foliage) || !Directory.Exists(noFoliage))
                    throw new DirectoryNotFoundException("Не найдены обе папки foliage и no_foliage.");
                string foliageMarker = Path.Combine(foliage, ".ignored");
                string noFoliageMarker = Path.Combine(noFoliage, ".ignored");
                string source = enabled ? foliageMarker : noFoliageMarker;
                string destination = enabled ? noFoliageMarker : foliageMarker;
                if (File.Exists(destination)) { message = enabled ? "Листва уже включена." : "Листва уже отключена."; return true; }

                // При отсутствии маркера в обеих папках создаём пустой .ignored
                // сразу в папке, соответствующей выбранному состоянию.
                if (!File.Exists(source) && !File.Exists(destination))
                {
                    File.WriteAllBytes(destination, Array.Empty<byte>());
                    message = enabled
                        ? "Файл .ignored создан в no_foliage. Листва включена."
                        : "Файл .ignored создан в foliage. Листва отключена.";
                    return true;
                }

                if (!File.Exists(source)) throw new FileNotFoundException("Не удалось найти файл .ignored для переключения состояния.");
                File.Move(source, destination);
                return true;
            }
            catch (Exception ex) { message = ex.Message; return false; }
        }

        private void ShowSettings_Click(object sender, RoutedEventArgs e)
        {
            // Настройки открываются внутри основного WebView2, а не в отдельном WPF-окне.
            SendSettingsToWeb();
            return;
#pragma warning disable CS0162

            var settingsWindow = new Window
            {
                Title = "TWM LAUNCHER  /  SETTINGS",
                Width = 1040,
                Height = 820,
                MinWidth = 820,
                MinHeight = 650,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(10, 8, 16)),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false
            };

            var root = new StackPanel
            {
                Margin = new Thickness(24)
            };

            // Settings hero: a clear visual entry point that matches the launcher's
            // dark/purple identity without changing any of the existing settings logic.
            var settingsHero = new Border
            {
                Background = new LinearGradientBrush(
                    Color.FromRgb(39, 24, 55), Color.FromRgb(19, 14, 27), 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(83, 49, 111)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(20, 18, 20, 18),
                Margin = new Thickness(0, 0, 0, 24),
                Child = new Grid()
            };
            var heroGrid = (Grid)settingsHero.Child;
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heroText = new StackPanel();
            heroText.Children.Add(new TextBlock
            {
                Text = "ЦЕНТР УПРАВЛЕНИЯ",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(181, 108, 255)),
                Margin = new Thickness(0, 0, 0, 7)
            });
            heroText.Children.Add(new TextBlock
            {
                Text = "Настройки лаунчера",
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 5)
            });
            heroText.Children.Add(new TextBlock
            {
                Text = "Игровой клиент, параметры запуска и библиотека модификаций",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 158, 185)),
                TextWrapping = TextWrapping.Wrap
            });
            heroGrid.Children.Add(heroText);
            var heroBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(31, 22, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(101, 64, 132)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "TWM  /  CONFIG",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(205, 168, 255))
                }
            };
            Grid.SetColumn(heroBadge, 1);
            heroGrid.Children.Add(heroBadge);
            root.Children.Add(settingsHero);

            // Разделяем настройки по вкладкам: игра, сервер, модификации и данные.
            var settingsTabs = new TabControl
            {
                Background = new SolidColorBrush(Color.FromRgb(10, 8, 16)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 38, 74)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8),
                MinHeight = 420
            };

            // Единый контрастный стиль вкладок: без системной бело-голубой подсветки WPF.
            var tabItemStyle = new Style(typeof(TabItem));
            tabItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(23, 17, 32))));
            tabItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(207, 198, 220))));
            tabItemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(65, 48, 82))));
            tabItemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            tabItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 11, 16, 11)));
            tabItemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 3, 0)));
            tabItemStyle.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            var tabTemplate = new ControlTemplate(typeof(TabItem));
            var tabBorder = new FrameworkElementFactory(typeof(Border));
            tabBorder.Name = "TabBorder";
            tabBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            tabBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            tabBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            tabBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(9, 9, 0, 0));
            tabBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var tabHeader = new FrameworkElementFactory(typeof(ContentPresenter));
            tabHeader.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            tabHeader.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            tabBorder.AppendChild(tabHeader);
            tabTemplate.VisualTree = tabBorder;
            var tabHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            tabHover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(48, 34, 65))));
            tabHover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            tabHover.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(139, 92, 246))));
            tabTemplate.Triggers.Add(tabHover);
            var tabSelected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            tabSelected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(92, 55, 132))));
            tabSelected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            tabSelected.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(181, 108, 255))));
            tabSelected.Setters.Add(new Setter(Panel.ZIndexProperty, 1));
            tabTemplate.Triggers.Add(tabSelected);
            tabItemStyle.Setters.Add(new Setter(Control.TemplateProperty, tabTemplate));
            settingsTabs.ItemContainerStyle = tabItemStyle;

            // Равномерно распределяем заголовки вкладок по всей доступной ширине.
            var tabHeadersPanel = new FrameworkElementFactory(typeof(UniformGrid));
            tabHeadersPanel.SetValue(UniformGrid.RowsProperty, 1);
            settingsTabs.ItemsPanel = new ItemsPanelTemplate(tabHeadersPanel);

            var gameTabPanel = CreateSettingsTabPanel();
            var serverTabPanel = CreateSettingsTabPanel();
            var modsTabPanel = CreateSettingsTabPanel();
            var generalTabPanel = CreateSettingsTabPanel();
            StackPanel activeTabPanel = gameTabPanel;

            settingsTabs.Items.Add(CreateSettingsTab("Игра и пути", "ПАПКА ИГРЫ", gameTabPanel));
            settingsTabs.Items.Add(CreateSettingsTab("Сервер", "ПОДКЛЮЧЕНИЕ", serverTabPanel));
            settingsTabs.Items.Add(CreateSettingsTab("Модификации", "ASI / DLL", modsTabPanel));
            settingsTabs.Items.Add(CreateSettingsTab("Данные", "ПАПКА ЛАУНЧЕРА", generalTabPanel));
            if ((sender as FrameworkElement)?.Tag is int requestedTab &&
                requestedTab >= 0 && requestedTab < settingsTabs.Items.Count)
            {
                settingsTabs.SelectedIndex = requestedTab;
            }
            root.Children.Add(settingsTabs);

            activeTabPanel.Children.Add(CreateSectionLabel("ПАПКА ИГРЫ"));

            var gamePathBox = new TextBox
            {
                Text = _settings.GameDirectory,
                IsReadOnly = true,
                Padding = new Thickness(10),
                Background = new SolidColorBrush(
                    Color.FromRgb(18, 13, 26)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(
                    Color.FromRgb(56, 38, 74)),
                Margin = new Thickness(0, 8, 0, 8)
            };

            activeTabPanel.Children.Add(gamePathBox);

            // Live validation panel: updates when the user chooses a different folder.
            var gameFolderStatus = CreatePathStatusText();
            var starterStatus = CreatePathStatusText();
            var gameExeStatus = CreatePathStatusText();
            var quickAsiStatus = CreatePathStatusText();

            var pathStatusPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(18, 13, 26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 38, 74)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4, 0, 10),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "СОСТОЯНИЕ ИГРОВОГО КЛИЕНТА",
                            FontSize = 10,
                            FontWeight = FontWeights.Bold,
                            Foreground = new SolidColorBrush(Color.FromRgb(181, 108, 255)),
                            Margin = new Thickness(0, 0, 0, 6)
                        },
                        gameFolderStatus,
                        starterStatus,
                        gameExeStatus,
                        quickAsiStatus
                    }
                }
            };

            void RefreshPathStatus()
            {
                string directory = _settings.GameDirectory ?? string.Empty;
                bool folderExists = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory);
                bool starterExists = folderExists && File.Exists(Path.Combine(directory, "aggstarter.exe"));
                bool gameExeExists = folderExists && File.Exists(Path.Combine(directory, "gta_sa.exe"));

                SetPathStatus(gameFolderStatus,
                    folderExists ? "✓ Папка игры найдена" : "✕ Папка игры не найдена",
                    folderExists);
                SetPathStatus(starterStatus,
                    starterExists ? "✓ aggstarter.exe найден" : "✕ aggstarter.exe не найден",
                    starterExists);
                SetPathStatus(gameExeStatus,
                    gameExeExists ? "✓ gta_sa.exe найден" : "! gta_sa.exe не найден (нужен для QuickASI)",
                    gameExeExists);
                SetPathStatus(quickAsiStatus,
                    !_settings.UseQuickAsi ? "— QuickASI отключён" :
                    gameExeExists ? "✓ QuickASI: путь к игре подходит" : "! QuickASI не сможет запуститься без gta_sa.exe",
                    !_settings.UseQuickAsi || gameExeExists);
            }

            activeTabPanel.Children.Add(pathStatusPanel);
            RefreshPathStatus();

            var chooseGameButton = CreateSettingsButton(
                "Выбрать папку игры");

            chooseGameButton.Click += (_, _) =>
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "Выбери папку с aggstarter.exe",
                    Multiselect = false
                };

                if (Directory.Exists(_settings.GameDirectory))
                    dialog.InitialDirectory = _settings.GameDirectory;

                if (dialog.ShowDialog() == true)
                {
                    string selectedDirectory = dialog.FolderName;

                    if (!File.Exists(Path.Combine(
                            selectedDirectory, "aggstarter.exe")))
                    {
                        ShowNotificationMessage(
                            "В выбранной папке не найден aggstarter.exe.",
                            "Неверная папка",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        return;
                    }

                    _settings.GameDirectory = selectedDirectory;
                    gamePathBox.Text = selectedDirectory;
                    SaveSettings();
                    RefreshPathStatus();
                }
            };

            activeTabPanel.Children.Add(chooseGameButton);

            var openGameFolderButton = CreateSettingsButton("Открыть папку игры");
            openGameFolderButton.Margin = new Thickness(0, 8, 0, 0);
            openGameFolderButton.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_settings.GameDirectory) ||
                    !Directory.Exists(_settings.GameDirectory))
                {
                    ShowNotificationMessage(
                        "Папка игры не найдена. Сначала выбери корректный путь.",
                        "Папка игры недоступна",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{_settings.GameDirectory}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    ShowNotificationMessage(
                        "Не удалось открыть папку игры.\n\n" + ex.Message,
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };
            activeTabPanel.Children.Add(openGameFolderButton);

            activeTabPanel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(
                    Color.FromRgb(40, 31, 49)),
                Margin = new Thickness(0, 20, 0, 20)
            });

            activeTabPanel = serverTabPanel;
            activeTabPanel.Children.Add(CreateSectionLabel("ВЫБРАННЫЙ СЕРВЕР"));

            var serverText = new TextBlock
            {
                Text = GetSelectedServerDescription(),
                FontSize = 14,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 8, 0, 4)
            };

            activeTabPanel.Children.Add(serverText);

            activeTabPanel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(
                    Color.FromRgb(40, 31, 49)),
                Margin = new Thickness(0, 20, 0, 20)
            });

            activeTabPanel = modsTabPanel;
            activeTabPanel.Children.Add(CreateSectionLabel("МОДИФИКАЦИИ"));

            var quickAsiCheckBox = new CheckBox
            {
                Content = "Запускать quick_asi.exe вместе с игрой",
                IsChecked = _settings.UseQuickAsi,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(35, 24, 47)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(181, 108, 255)),
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 4),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            activeTabPanel.Children.Add(quickAsiCheckBox);

            activeTabPanel.Children.Add(new TextBlock
            {
                Text = "Используй этот параметр только для сборок, которым нужен ASI-загрузчик.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = new SolidColorBrush(
                    Color.FromRgb(164, 154, 174)),
                Margin = new Thickness(0, 4, 0, 0)
            });

            activeTabPanel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(40, 31, 49)),
                Margin = new Thickness(0, 20, 0, 20)
            });

            activeTabPanel.Children.Add(CreateDllSettingsPanel());

            activeTabPanel = generalTabPanel;
            activeTabPanel.Children.Add(CreateSectionLabel("ДАННЫЕ И ФАЙЛЫ ЛАУНЧЕРА"));
            activeTabPanel.Children.Add(new TextBlock { Text = "Настройки хранятся отдельно от файлов игры. Открытие папки не изменяет содержимое.", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(170, 158, 185)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            var openDataFolderButton = CreateSettingsButton("Открыть папку данных TWM Launcher");
            openDataFolderButton.Margin = new Thickness(0, 18, 0, 0);
            openDataFolderButton.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(_settingsDirectory);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{_settingsDirectory}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    ShowNotificationMessage(
                        "Не удалось открыть папку данных лаунчера.\n\n" + ex.Message,
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };
            activeTabPanel.Children.Add(openDataFolderButton);

            var bottomButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 26, 0, 0)
            };

            var saveButton = CreateSettingsButton("Сохранить");
            saveButton.Background = new SolidColorBrush(
                Color.FromRgb(139, 92, 246));
            saveButton.BorderBrush = saveButton.Background;
            saveButton.Padding = new Thickness(22, 10, 22, 10);

            saveButton.Click += (_, _) =>
            {
                _settings.UseQuickAsi =
                    quickAsiCheckBox.IsChecked == true;

                SaveSettings();
                settingsWindow.Close();
            };

            var closeButton = CreateSettingsButton("Закрыть");
            closeButton.Margin = new Thickness(10, 0, 0, 0);

            closeButton.Click += (_, _) => settingsWindow.Close();

            bottomButtons.Children.Add(closeButton);
            bottomButtons.Children.Add(saveButton);
            root.Children.Add(bottomButtons);

            var settingsScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 8, 4),
                Content = root
            };
            settingsScrollViewer.Resources.Add(typeof(ScrollBar), CreateSettingsScrollBarStyle());
            settingsWindow.Content = settingsScrollViewer;

            settingsWindow.ShowDialog();
#pragma warning restore CS0162
        }

        // =========================================================
        // DLL LIBRARY: recursive discovery + deployment to game\asi
        // =========================================================

        private bool IsGameRunning()
        {
            string[] processNames = { "amazing", "gta_sa", "aggstarter", "aggstarter_r" };
            foreach (string processName in processNames)
            {
                try
                {
                    if (Process.GetProcessesByName(processName).Length > 0)
                        return true;
                }
                catch
                {
                    // При невозможности проверить процессы безопаснее запретить изменения.
                    return true;
                }
            }
            return false;
        }

        private bool EnsureGameClosed()
        {
            if (!IsGameRunning())
                return true;

            ShowNotificationMessage(
                "Сначала полностью закрой игру и её стартовые процессы, затем повтори действие.",
                "Игра ещё запущена",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        private bool IsSafeModName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
                || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || fileName == "." || fileName == "..")
                return false;

            string extension = Path.GetExtension(fileName);
            return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".asi", StringComparison.OrdinalIgnoreCase);
        }

        private string CalculateSha256(string path)
        {
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(sha256.ComputeHash(stream));
        }

        private void EnsureModDirectories()
        {
            if (string.IsNullOrWhiteSpace(_settings.GameDirectory) ||
                !Directory.Exists(_settings.GameDirectory))
            {
                throw new InvalidOperationException(
                    "Сначала укажи корректную папку игры в настройках.");
            }

            Directory.CreateDirectory(ModsRoot);
            Directory.CreateDirectory(AsiDirectory);
        }

        private bool IsInsideManagedMods(string path)
        {
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string managedPath = Path.GetFullPath(ModsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(managedPath, StringComparison.OrdinalIgnoreCase);
        }

        private IEnumerable<string> EnumerateGameDlls()
        {
            // Менеджер показывает DLL и ASI непосредственно в game\asi.
            // Файлы из корня игры и других папок не добавляются автоматически.
            if (!Directory.Exists(AsiDirectory))
                yield break;

            string[] files;
            try
            {
                files = Directory.GetFiles(AsiDirectory, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(file => IsSafeModName(Path.GetFileName(file)))
                    .ToArray();
            }
            catch
            {
                yield break;
            }

            foreach (string file in files)
                yield return file;
        }

        private void LoadModLibrary()
        {
            EnsureModDirectories();
            _mods.Clear();

            // Совместимость со старой версией: переносим сохранённые DLL из Disabled
            // в корень библиотеки Mods и обновляем путь в mods.json.
            string legacyDisabledDirectory = Path.Combine(ModsRoot, "Disabled");
            if (Directory.Exists(legacyDisabledDirectory))
            {
                foreach (string legacyFile in Directory.GetFiles(legacyDisabledDirectory, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(legacyFile);
                    if (!IsSafeModName(name)) continue;
                    string newPath = Path.Combine(ModsRoot, name);
                    if (!File.Exists(newPath))
                    {
                        try { File.Move(legacyFile, newPath); }
                        catch { /* Оставляем файл на месте, если ОС не разрешает перенос. */ }
                    }
                    else
                    {
                        // Если новая копия совпадает со старой побайтно, удаляем дубликат.
                        // При различии содержимого сохраняем старый файл, чтобы не потерять данные.
                        try
                        {
                            if (string.Equals(CalculateSha256(legacyFile), CalculateSha256(newPath), StringComparison.OrdinalIgnoreCase))
                                File.Delete(legacyFile);
                        }
                        catch { }
                    }
                }
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(legacyDisabledDirectory).Any())
                        Directory.Delete(legacyDisabledDirectory);
                }
                catch { }
            }

            if (File.Exists(ModLibraryFile))
            {
                try
                {
                    ModLibrary stored = JsonSerializer.Deserialize<ModLibrary>(File.ReadAllText(ModLibraryFile))
                                        ?? new ModLibrary();
                    foreach (ModInfo item in stored.Mods)
                    {
                        if (item == null || !IsSafeModName(item.FileName) ||
                            string.IsNullOrWhiteSpace(item.RelativePath))
                            continue;

                        // Старые записи из Disabled переводим на путь в корне Mods.
                        string normalized = item.RelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                        if (normalized.IndexOf(Path.DirectorySeparatorChar + "Disabled" + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase) >= 0 ||
                            normalized.StartsWith("Disabled" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            string libraryPath = Path.Combine(ModsRoot, item.FileName);
                            if (File.Exists(libraryPath))
                            {
                                item.RelativePath = Path.GetRelativePath(_settings.GameDirectory, libraryPath);
                                item.StoredDisabled = true;
                            }
                        }

                        // Список содержит DLL/ASI из папки asi и импортированные файлы библиотеки.
                        if (!item.StoredDisabled && !IsAsiSource(item.RelativePath))
                            continue;

                        if (_mods.Any(m => string.Equals(m.FileName, item.FileName, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        _mods.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    throw new IOException("Не удалось прочитать список DLL (mods.json).", ex);
                }
            }

            // Обнаруживаем внешние DLL/ASI в game\asi. Если файл уже зарегистрирован,
            // не создаём дубликат.
            foreach (string path in EnumerateGameDlls())
            {
                string relative = Path.GetRelativePath(_settings.GameDirectory, path);
                string fileName = Path.GetFileName(path);
                ModInfo? existing = _mods.FirstOrDefault(m =>
                    string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    _mods.Add(new ModInfo
                    {
                        FileName = fileName,
                        RelativePath = relative,
                        Sha256 = CalculateSha256(path),
                        Enabled = true,
                        StoredDisabled = false
                    });
                }
                else if (string.IsNullOrWhiteSpace(existing.Sha256))
                {
                    existing.Sha256 = CalculateSha256(path);
                }
            }

            _mods.RemoveAll(item =>
            {
                string sourcePath = Path.Combine(_settings.GameDirectory, item.RelativePath);
                string libraryPath = Path.Combine(ModsRoot, item.FileName);
                string deployedPath = Path.Combine(AsiDirectory, item.FileName);
                return !File.Exists(sourcePath) && !File.Exists(libraryPath) && !File.Exists(deployedPath);
            });

            foreach (ModInfo item in _mods)
            {
                string sourcePath = Path.Combine(_settings.GameDirectory, item.RelativePath);
                string libraryPath = Path.Combine(ModsRoot, item.FileName);
                string deployedPath = Path.Combine(AsiDirectory, item.FileName);
                bool sourceIsAsi = IsAsiSource(item.RelativePath);

                // Импортированные DLL всегда хранятся в Mods, а в asi находится
                // только рабочая копия, когда переключатель включён.
                if (!sourceIsAsi && File.Exists(libraryPath))
                {
                    item.RelativePath = Path.GetRelativePath(_settings.GameDirectory, libraryPath);
                    item.StoredDisabled = true;
                    if (File.Exists(deployedPath))
                    {
                        string deployedHash = CalculateSha256(deployedPath);
                        item.Enabled = string.Equals(deployedHash, item.Sha256, StringComparison.OrdinalIgnoreCase);
                    }
                    else item.Enabled = false;
                    if (string.IsNullOrWhiteSpace(item.Sha256)) item.Sha256 = CalculateSha256(libraryPath);
                    continue;
                }

                if (sourceIsAsi && File.Exists(sourcePath))
                {
                    item.Enabled = true;
                    if (string.IsNullOrWhiteSpace(item.Sha256)) item.Sha256 = CalculateSha256(sourcePath);
                }
                else if (File.Exists(deployedPath) && !string.IsNullOrWhiteSpace(item.Sha256))
                {
                    item.Enabled = string.Equals(CalculateSha256(deployedPath), item.Sha256, StringComparison.OrdinalIgnoreCase);
                }
                else item.Enabled = false;
            }

            SaveModLibrary();
        }

        private bool IsAsiSource(string relativePath)
        {
            string? firstFolder = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
            return string.Equals(firstFolder, "asi", StringComparison.OrdinalIgnoreCase);
        }

        private void SaveModLibrary()
        {
            EnsureModDirectories();
            string json = JsonSerializer.Serialize(new ModLibrary { Mods = _mods },
                new JsonSerializerOptions { WriteIndented = true });
            string tempFile = ModLibraryFile + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, ModLibraryFile, true);
        }

        private void ImportDll_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureGameClosed()) return;

            try
            {
                EnsureModDirectories();
                var dialog = new OpenFileDialog
                {
                    Title = "Выбери DLL- или ASI-модификацию для библиотеки TWM Launcher",
                    Filter = "Модификации DLL / ASI (*.dll;*.asi)|*.dll;*.asi|Библиотеки DLL (*.dll)|*.dll|ASI-модификации (*.asi)|*.asi",
                    Multiselect = false,
                    CheckFileExists = true
                };
                if (dialog.ShowDialog(this) != true) return;

                string fileName = Path.GetFileName(dialog.FileName);
                if (!IsSafeModName(fileName))
                    throw new InvalidOperationException("Выбери корректный файл с расширением .dll или .asi.");

                string destination = Path.Combine(ModsRoot, fileName);
                if (File.Exists(destination) || File.Exists(Path.Combine(AsiDirectory, fileName)))
                    throw new IOException("Модификация с таким именем уже есть в библиотеке или папке asi. Переименуй файл либо удали существующую запись.");

                string hash = CalculateSha256(dialog.FileName);
                File.Copy(dialog.FileName, destination, false);
                if (!string.Equals(hash, CalculateSha256(destination), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(destination);
                    throw new IOException("Контрольная сумма DLL после копирования не совпала.");
                }

                string relativePath = Path.GetRelativePath(_settings.GameDirectory, destination);
                _mods.RemoveAll(m => string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                _mods.Add(new ModInfo
                {
                    FileName = fileName,
                    RelativePath = relativePath,
                    Sha256 = hash,
                    Enabled = false,
                    StoredDisabled = true
                });
                SaveModLibrary();
                ShowNotificationMessage("Модификация добавлена в библиотеку TWM Launcher и пока отключена. Включи её переключателем, чтобы установить файл в папку игры\asi.",
                    "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Information);

                // Импорт может быть вызван из WebView, где sender — главное окно.
                // Не закрываем окно лаунчера и не переключаем раздел принудительно:
                // вызывающий обработчик обновит список модификаций после импорта.
            }
            catch (Exception ex)
            {
                ShowNotificationMessage("Не удалось добавить DLL.\n\n" + ex.Message,
                    "Ошибка библиотеки модификаций", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool SetDllEnabled(string relativePath, bool enabled)
        {
            if (!EnsureGameClosed()) return false;

            try
            {
                LoadModLibrary();
                ModInfo? mod = _mods.FirstOrDefault(m => string.Equals(
                    m.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
                if (mod == null)
                    mod = _mods.FirstOrDefault(m => string.Equals(Path.GetFileName(m.RelativePath),
                        Path.GetFileName(relativePath), StringComparison.OrdinalIgnoreCase));
                if (mod == null) throw new InvalidOperationException("DLL не найдена в библиотеке.");

                string destinationPath = Path.Combine(AsiDirectory, mod.FileName);
                string libraryPath = Path.Combine(ModsRoot, mod.FileName);
                string sourcePath = Path.Combine(_settings.GameDirectory, mod.RelativePath);
                bool sourceIsAsi = IsAsiSource(mod.RelativePath);

                if (enabled)
                {
                    string actualSource = File.Exists(libraryPath) ? libraryPath : sourcePath;
                    if (!File.Exists(actualSource))
                        throw new FileNotFoundException("Исходный файл модификации не найден в библиотеке лаунчера.", actualSource);

                    string actualHash = CalculateSha256(actualSource);
                    if (!string.IsNullOrWhiteSpace(mod.Sha256) &&
                        !string.Equals(actualHash, mod.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Контрольная сумма модификации изменилась. Для безопасности файл не включён.");

                    if (File.Exists(destinationPath))
                    {
                        string deployedHash = CalculateSha256(destinationPath);
                        if (!string.Equals(deployedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("В папке asi уже есть другой файл с таким именем. Файл не перезаписан.");
                    }
                    else
                    {
                        File.Copy(actualSource, destinationPath, false);
                    }

                    // Для старой DLL, которая раньше находилась непосредственно в asi,
                    // сохраняем резервную копию в библиотеке, после чего она управляется как обычная.
                    if (sourceIsAsi && !File.Exists(libraryPath))
                    {
                        File.Copy(destinationPath, libraryPath, false);
                        mod.RelativePath = Path.GetRelativePath(_settings.GameDirectory, libraryPath);
                    }
                    mod.Sha256 = actualHash;
                    mod.StoredDisabled = true;
                    mod.Enabled = true;
                }
                else
                {
                    if (File.Exists(destinationPath))
                    {
                        string currentHash = CalculateSha256(destinationPath);
                        if (!string.IsNullOrWhiteSpace(mod.Sha256) &&
                            !string.Equals(currentHash, mod.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Файл модификации в папке asi изменился. Отключение отменено, чтобы не удалить изменённый файл.");

                        // Сначала гарантируем наличие копии в библиотеке и только потом удаляем DLL из asi.
                        if (!File.Exists(libraryPath))
                            File.Copy(destinationPath, libraryPath, false);
                        File.Delete(destinationPath);
                    }

                    if (File.Exists(libraryPath))
                    {
                        mod.RelativePath = Path.GetRelativePath(_settings.GameDirectory, libraryPath);
                        mod.Sha256 = CalculateSha256(libraryPath);
                        mod.StoredDisabled = true;
                    }
                    mod.Enabled = false;
                }

                SaveModLibrary();
                return true;
            }
            catch (Exception ex)
            {
                ShowNotificationMessage("Не удалось изменить состояние модификации.\n\n" + ex.Message,
                    "Ошибка библиотеки модификаций", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void SendWebProfileActionResult(bool success, string message)
        {
            if (LauncherWebView.CoreWebView2 == null) return;
            var payload = new { type = "modProfileActionResult", success, message };
            LauncherWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }

        private void HandleWebModProfileAction(JsonElement root)
        {
            string operation = root.TryGetProperty("operation", out var op) ? (op.GetString() ?? string.Empty) : string.Empty;
            string profileId = root.TryGetProperty("profileId", out var idElement) ? (idElement.GetString() ?? string.Empty) : string.Empty;
            try
            {
                var profiles = LoadModProfiles();
                switch (operation)
                {
                    case "create":
                    {
                        string? name = PromptForModProfileName();
                        if (string.IsNullOrWhiteSpace(name)) { SendWebProfileActionResult(false, "Создание профиля отменено."); return; }
                        name = name.Trim();
                        if (profiles.Any(x => string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("Профиль с таким названием уже существует.");
                        string newId = "p_" + Guid.NewGuid().ToString("N");
                        profiles.Add(new ModProfileInfo { Id = newId, Name = name, CreatedAtUtc = DateTime.UtcNow });
                        SaveModProfiles(profiles);
                        SendWebProfileActionResult(true, $"Профиль «{name}» создан. Чтобы начать использовать его, нажми «Сделать активным».");
                        break;
                    }
                    case "switch":
                    {
                        if (!IsSafeProfileId(profileId)) throw new InvalidOperationException("Некорректный идентификатор профиля.");
                        SwitchModProfile(profileId);
                        break;
                    }
                    case "rename":
                    {
                        if (!IsSafeProfileId(profileId) || string.Equals(profileId, "Default", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Профиль «По умолчанию» нельзя переименовать.");
                        var selected = profiles.FirstOrDefault(x => string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("Профиль не найден.");
                        string? name = PromptForModProfileName("Переименовать профиль", selected.Name);
                        if (string.IsNullOrWhiteSpace(name)) { SendWebProfileActionResult(false, "Переименование отменено."); return; }
                        name = name.Trim();
                        if (profiles.Any(x => !string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("Профиль с таким названием уже существует.");
                        selected.Name = name;
                        SaveModProfiles(profiles);
                        SendWebProfileActionResult(true, "Название профиля обновлено.");
                        break;
                    }
                    case "delete":
                    {
                        if (!IsSafeProfileId(profileId) || string.Equals(profileId, "Default", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Профиль «По умолчанию» нельзя удалить.");
                        if (string.Equals(profileId, ActiveModProfileId, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Сначала сделай активным другой профиль.");
                        var selected = profiles.FirstOrDefault(x => string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("Профиль не найден.");
                        if (SilverMessageBox.Show(this, $"Удалить профиль «{selected.Name}» и его библиотеку модов?\n\nУдалится только папка профиля в TWMBuilds. Файлы игры не удаляются.", "Удаление профиля", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        { SendWebProfileActionResult(false, "Удаление отменено."); return; }
                        string rootDirectory = Path.GetFullPath(ModProfilesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        string profileDirectory = Path.GetFullPath(Path.Combine(ModProfilesRoot, profileId));
                        if (!profileDirectory.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase)) throw new IOException("Путь профиля не прошёл проверку безопасности.");
                        if (Directory.Exists(profileDirectory)) Directory.Delete(profileDirectory, true);
                        profiles.RemoveAll(x => string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase));
                        SaveModProfiles(profiles);
                        SendWebProfileActionResult(true, "Профиль и его библиотека удалены.");
                        break;
                    }
                    default: throw new InvalidOperationException("Неизвестная операция с профилем.");
                }
            }
            catch (Exception ex) { SendWebProfileActionResult(false, ex.Message); }
            SendSettingsToWeb();
            SendModLibraryToWeb();
        }

        private string? PromptForModProfileName(string title = "Новый профиль модов", string initialName = "Новый профиль")
        {
            var dialog = new Window
            {
                Title = title, Width = 390, Height = 190,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
                ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(13, 18, 30)),
                Foreground = Brushes.White, WindowStyle = WindowStyle.ToolWindow
            };
            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock { Text = "Название профиля", Margin = new Thickness(0, 0, 0, 8), FontSize = 13 });
            var nameBox = new TextBox { Text = initialName, Padding = new Thickness(9), MaxLength = 48 };
            root.Children.Add(nameBox);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            string? result = null;
            var cancel = CreateSettingsButton("Отмена");
            var create = CreateSettingsButton("Создать"); create.Margin = new Thickness(8, 0, 0, 0);
            cancel.Click += (_, _) => dialog.Close();
            create.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(nameBox.Text)) result = nameBox.Text.Trim(); dialog.Close(); };
            buttons.Children.Add(cancel); buttons.Children.Add(create); root.Children.Add(buttons);
            dialog.Content = root;
            dialog.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
            dialog.ShowDialog();
            return result;
        }

        private void SwitchModProfile(string nextId)
        {
            if (!IsSafeProfileId(nextId) || !EnsureGameClosed()) return;
            try
            {
                var profiles = LoadModProfiles();
                if (!profiles.Any(x => string.Equals(x.Id, nextId, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Выбранный профиль не найден.");
                if (string.Equals(nextId, ActiveModProfileId, StringComparison.OrdinalIgnoreCase)) return;

                // Before switching, remove only files managed by the current profile and only if hashes match.
                LoadModLibrary();
                foreach (ModInfo mod in _mods.Where(x => x.Enabled).ToList())
                {
                    string deployed = Path.Combine(AsiDirectory, mod.FileName);
                    if (!File.Exists(deployed)) continue;
                    string currentHash = CalculateSha256(deployed);
                    if (!string.IsNullOrWhiteSpace(mod.Sha256) && !string.Equals(currentHash, mod.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Файл {mod.FileName} изменён после установки. Профиль не переключён, чтобы не потерять файл.");
                }
                foreach (ModInfo mod in _mods.Where(x => x.Enabled).ToList())
                {
                    if (!SetDllEnabled(mod.RelativePath, false))
                        throw new IOException($"Не удалось безопасно отключить {mod.FileName}. Профиль не переключён.");
                }

                // Read the target profile's desired enabled set before LoadModLibrary reconciles
                // its state against the currently deployed files in game\asi.
                string targetModsFile = Path.Combine(ModProfilesRoot, nextId, "Mods", "mods.json");
                HashSet<string> targetEnabledNames = new(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(targetModsFile))
                {
                    try
                    {
                        ModLibrary targetLibrary = JsonSerializer.Deserialize<ModLibrary>(File.ReadAllText(targetModsFile)) ?? new ModLibrary();
                        foreach (ModInfo item in targetLibrary.Mods.Where(x => x != null && x.Enabled && IsSafeModName(x.FileName)))
                            targetEnabledNames.Add(item.FileName);
                    }
                    catch (Exception ex) { throw new IOException("Не удалось прочитать набор DLL целевого профиля. Переключение отменено.", ex); }
                }

                _settings.ActiveModProfileId = nextId;
                SaveSettings();
                LoadModLibrary();
                foreach (string fileName in targetEnabledNames)
                {
                    ModInfo? targetMod = _mods.FirstOrDefault(x => string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                    if (targetMod != null && !targetMod.Enabled && !SetDllEnabled(targetMod.RelativePath, true))
                        throw new IOException($"Не удалось включить {fileName} из целевого профиля. Проверь состояние папки asi.");
                }
                SendWebProfileActionResult(true, $"Активен профиль «{profiles.First(x => string.Equals(x.Id, nextId, StringComparison.OrdinalIgnoreCase)).Name}». Сохранённый набор DLL восстановлен.");
            }
            catch (Exception ex)
            {
                SendWebProfileActionResult(false, "Не удалось переключить профиль: " + ex.Message);
            }
        }

        private UIElement CreateDllSettingsPanel()
        {
            var panel = new StackPanel();
            panel.Children.Add(CreateSectionLabel("БИБЛИОТЕКА DLL"));
            panel.Children.Add(new TextBlock
            {
                Text = "Управляй игровыми библиотеками: ищи DLL, проверяй состояние и включай или отключай моды. Автоматически обнаруживаются только DLL из папки игры\\asi.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(139, 152, 174)),
                Margin = new Thickness(0, 8, 0, 12)
            });

            panel.Children.Add(CreateSectionLabel("ПРОФИЛЬ МОДОВ"));
            panel.Children.Add(new TextBlock
            {
                Text = "У каждого профиля отдельная библиотека DLL и свой список включённых модов. Переключение отключает управляемые DLL текущего профиля и загружает выбранный набор. Файлы с изменённой контрольной суммой автоматически не удаляются.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(139, 152, 174)),
                Margin = new Thickness(0, 8, 0, 10)
            });
            var profileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var profileCombo = new ComboBox
            {
                MinWidth = 220, Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromRgb(16, 23, 37)),
                Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(52, 65, 90)),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var createProfileButton = CreateSettingsButton("+ Новый профиль");
            createProfileButton.Margin = new Thickness(8, 0, 0, 0);
            var renameProfileButton = CreateSettingsButton("Переименовать");
            renameProfileButton.Margin = new Thickness(8, 0, 0, 0);
            var deleteProfileButton = CreateSettingsButton("Удалить");
            deleteProfileButton.Margin = new Thickness(8, 0, 0, 0);
            void RefreshProfiles()
            {
                var profiles = LoadModProfiles();
                profileCombo.ItemsSource = null;
                profileCombo.ItemsSource = profiles;
                profileCombo.DisplayMemberPath = nameof(ModProfileInfo.Name);
                profileCombo.SelectedValuePath = nameof(ModProfileInfo.Id);
                profileCombo.SelectedValue = ActiveModProfileId;
                bool isDefault = string.Equals(profileCombo.SelectedValue as string, "Default", StringComparison.OrdinalIgnoreCase);
                renameProfileButton.IsEnabled = !isDefault;
                deleteProfileButton.IsEnabled = !isDefault;
            }
            profileCombo.SelectionChanged += (_, _) =>
            {
                if (profileCombo.SelectedValue is string nextId && !string.Equals(nextId, ActiveModProfileId, StringComparison.OrdinalIgnoreCase))
                    SwitchModProfile(nextId);
            };
            createProfileButton.Click += (_, _) =>
            {
                string? name = PromptForModProfileName();
                if (string.IsNullOrWhiteSpace(name)) return;
                try
                {
                    var profiles = LoadModProfiles();
                    if (profiles.Any(x => string.Equals(x.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Профиль с таким названием уже существует.");
                    string id = "p_" + Guid.NewGuid().ToString("N");
                    profiles.Add(new ModProfileInfo { Id = id, Name = name.Trim(), CreatedAtUtc = DateTime.UtcNow });
                    SaveModProfiles(profiles);
                    RefreshProfiles();
                    profileCombo.SelectedValue = id;
                }
                catch (Exception ex) { ShowNotificationMessage("Не удалось создать профиль.\n\n" + ex.Message, "Профили модов", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            renameProfileButton.Click += (_, _) =>
            {
                try
                {
                    string? selectedId = profileCombo.SelectedValue as string;
                    if (string.IsNullOrWhiteSpace(selectedId) || string.Equals(selectedId, "Default", StringComparison.OrdinalIgnoreCase)) return;
                    var profiles = LoadModProfiles();
                    var selected = profiles.FirstOrDefault(x => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase));
                    if (selected == null) throw new InvalidOperationException("Профиль не найден.");
                    string? name = PromptForModProfileName("Переименовать профиль", selected.Name);
                    if (string.IsNullOrWhiteSpace(name)) return;
                    if (profiles.Any(x => !string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Профиль с таким названием уже существует.");
                    selected.Name = name.Trim();
                    SaveModProfiles(profiles);
                    RefreshProfiles();
                    ShowNotificationMessage("Название профиля обновлено.", "Профили модов", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { ShowNotificationMessage("Не удалось переименовать профиль.\n\n" + ex.Message, "Профили модов", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            deleteProfileButton.Click += (_, _) =>
            {
                try
                {
                    string? selectedId = profileCombo.SelectedValue as string;
                    if (string.IsNullOrWhiteSpace(selectedId) || string.Equals(selectedId, "Default", StringComparison.OrdinalIgnoreCase)) return;
                    if (string.Equals(selectedId, ActiveModProfileId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Сначала переключись на другой профиль, затем удаляй этот.");
                    var profiles = LoadModProfiles();
                    var selected = profiles.FirstOrDefault(x => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase));
                    if (selected == null) throw new InvalidOperationException("Профиль не найден.");
                    var answer = SilverMessageBox.Show(this,
                        $"Удалить профиль «{selected.Name}» и его библиотеку модов?\n\nУдалится только папка профиля в TWMBuilds. Файлы игры вне этой папки не удаляются.",
                        "Удаление профиля", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) return;
                    string profileDirectory = Path.GetFullPath(Path.Combine(ModProfilesRoot, selectedId));
                    string rootDirectory = Path.GetFullPath(ModProfilesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!profileDirectory.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Путь профиля не прошёл проверку безопасности.");
                    if (Directory.Exists(profileDirectory)) Directory.Delete(profileDirectory, true);
                    profiles.RemoveAll(x => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase));
                    SaveModProfiles(profiles);
                    RefreshProfiles();
                    ShowNotificationMessage("Профиль и его библиотека удалены.", "Профили модов", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { ShowNotificationMessage("Не удалось удалить профиль.\n\n" + ex.Message, "Профили модов", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            profileRow.Children.Add(profileCombo);
            profileRow.Children.Add(createProfileButton);
            panel.Children.Add(profileRow);
            var profileActionsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, -6, 0, 14) };
            profileActionsRow.Children.Add(renameProfileButton);
            profileActionsRow.Children.Add(deleteProfileButton);
            var exportProfileButton = CreateSettingsButton("Экспорт ZIP");
            exportProfileButton.Margin = new Thickness(8, 0, 0, 0);
            var importProfileButton = CreateSettingsButton("Импорт ZIP");
            importProfileButton.Margin = new Thickness(8, 0, 0, 0);
            exportProfileButton.Click += (_, _) =>
            {
                try
                {
                    string? selectedId = profileCombo.SelectedValue as string;
                    if (string.IsNullOrWhiteSpace(selectedId) || !IsSafeProfileId(selectedId))
                        throw new InvalidOperationException("Сначала выбери профиль для экспорта.");
                    string sourceDir = Path.GetFullPath(Path.Combine(ModProfilesRoot, selectedId));
                    string rootDir = Path.GetFullPath(ModProfilesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!sourceDir.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(sourceDir))
                        throw new IOException("Папка профиля не найдена или путь не прошёл проверку безопасности.");
                    var profile = LoadModProfiles().FirstOrDefault(x => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase));
                    var dialog = new SaveFileDialog { Title = "Экспорт профиля модов", Filter = "Архив профиля TWM (*.zip)|*.zip", FileName = (profile?.Name ?? selectedId) + ".zip", AddExtension = true, DefaultExt = ".zip" };
                    if (dialog.ShowDialog(this) != true) return;
                    if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
                    ZipFile.CreateFromDirectory(sourceDir, dialog.FileName, CompressionLevel.Optimal, false);
                    ShowNotificationMessage("Профиль экспортирован в ZIP. Архив содержит библиотеку профиля и список модов, но не переносит DLL, уже установленные непосредственно в папку игры.", "Экспорт профиля", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { ShowNotificationMessage("Не удалось экспортировать профиль.\n\n" + ex.Message, "Экспорт профиля", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            importProfileButton.Click += (_, _) =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "TWM_ProfileImport_" + Guid.NewGuid().ToString("N"));
                try
                {
                    var dialog = new OpenFileDialog { Title = "Импорт профиля модов", Filter = "Архив профиля TWM (*.zip)|*.zip", CheckFileExists = true, Multiselect = false };
                    if (dialog.ShowDialog(this) != true) return;
                    var profiles = LoadModProfiles();
                    string suggestedName = Path.GetFileNameWithoutExtension(dialog.FileName).Trim();
                    if (string.IsNullOrWhiteSpace(suggestedName)) suggestedName = "Импортированный профиль";
                    string? name = PromptForModProfileName("Название импортированного профиля", suggestedName);
                    if (string.IsNullOrWhiteSpace(name)) return;
                    if (profiles.Any(x => string.Equals(x.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Профиль с таким названием уже существует.");
                    Directory.CreateDirectory(tempDir);
                    using (var archive = ZipFile.OpenRead(dialog.FileName))
                    {
                        if (archive.Entries.Count > 2000) throw new InvalidDataException("В архиве слишком много файлов.");
                        long total = 0;
                        foreach (var entry in archive.Entries)
                        {
                            total += entry.Length;
                            if (total > 1024L * 1024 * 1024) throw new InvalidDataException("Размер распакованных данных превышает 1 ГБ.");
                            string target = Path.GetFullPath(Path.Combine(tempDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                            string safeRoot = Path.GetFullPath(tempDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            if (!target.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase) && !string.Equals(target, Path.GetFullPath(tempDir), StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Архив содержит небезопасный путь.");
                            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal)) { Directory.CreateDirectory(target); continue; }
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            entry.ExtractToFile(target, true);
                        }
                    }
                    string importedModsFile = Path.Combine(tempDir, "Mods", "mods.json");
                    if (!File.Exists(importedModsFile)) throw new InvalidDataException("Это не похоже на архив профиля TWM: не найден Mods/mods.json.");
                    // Validate the JSON before touching the profile catalog.
                    using (JsonDocument.Parse(File.ReadAllText(importedModsFile))) { }
                    string id = "p_" + Guid.NewGuid().ToString("N");
                    string targetDir = Path.GetFullPath(Path.Combine(ModProfilesRoot, id));
                    string profilesRoot = Path.GetFullPath(ModProfilesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!targetDir.StartsWith(profilesRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Путь импорта не прошёл проверку безопасности.");
                    Directory.CreateDirectory(ModProfilesRoot);
                    Directory.Move(tempDir, targetDir);
                    profiles.Add(new ModProfileInfo { Id = id, Name = name.Trim(), CreatedAtUtc = DateTime.UtcNow });
                    SaveModProfiles(profiles);
                    RefreshProfiles();
                    profileCombo.SelectedValue = id;
                    ShowNotificationMessage("Профиль импортирован. Проверь состав DLL перед запуском игры.", "Импорт профиля", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { ShowNotificationMessage("Не удалось импортировать профиль.\n\n" + ex.Message, "Импорт профиля", MessageBoxButton.OK, MessageBoxImage.Error); }
                finally { try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { } }
            };
            profileActionsRow.Children.Add(exportProfileButton);
            profileActionsRow.Children.Add(importProfileButton);
            panel.Children.Add(profileActionsRow);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var refreshButton = CreateSettingsButton("↻ Обновить");
            var importButton = CreateSettingsButton("+ Добавить DLL");
            importButton.Margin = new Thickness(8, 0, 0, 0);

            var searchBox = new TextBox
            {
                Margin = new Thickness(0, 14, 0, 8),
                Padding = new Thickness(12, 10, 12, 10),
                FontSize = 12,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(16, 23, 37)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(52, 65, 90)),
                BorderThickness = new Thickness(1),
                CaretBrush = Brushes.White,
                ToolTip = "Поиск по имени DLL или пути"
            };

            var searchHint = new TextBlock
            {
                Text = "Поиск по названию или пути...",
                Foreground = new SolidColorBrush(Color.FromRgb(110, 120, 142)),
                FontSize = 11,
                IsHitTestVisible = false,
                Margin = new Thickness(13, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var searchContainer = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            searchContainer.Children.Add(searchBox);
            searchContainer.Children.Add(searchHint);

            var countText = new TextBlock
            {
                Text = "Загрузка библиотеки...",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(214, 255, 95)),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var listPanel = new StackPanel();

            refreshButton.Click += (_, _) =>
            {
                try
                {
                    LoadModLibrary();
                    RenderDllLists(listPanel, searchBox.Text ?? string.Empty, countText);
                }
                catch (Exception ex)
                {
                    ShowNotificationMessage("Не удалось обновить список DLL.\n\n" + ex.Message,
                        "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            importButton.Click += ImportDll_Click;
            buttons.Children.Add(refreshButton);
            buttons.Children.Add(importButton);
            panel.Children.Add(buttons);
            panel.Children.Add(searchContainer);
            panel.Children.Add(countText);

            panel.Children.Add(new TextBlock
            {
                Text = "Для изменения состояния DLL игра и стартовые процессы должны быть закрыты. Изменённые файлы не удаляются и не перезаписываются без проверки SHA-256.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(245, 190, 100)),
                Margin = new Thickness(0, 0, 0, 10)
            });

            panel.Children.Add(listPanel);

            searchBox.TextChanged += (_, _) =>
            {
                searchHint.Visibility = string.IsNullOrEmpty(searchBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RenderDllLists(listPanel, searchBox.Text ?? string.Empty, countText);
            };

            try
            {
                RefreshProfiles();
                LoadModLibrary();
                RenderDllLists(listPanel, searchBox.Text ?? string.Empty, countText);
            }
            catch (Exception ex)
            {
                countText.Text = "Не удалось загрузить библиотеку";
                listPanel.Children.Add(new TextBlock
                {
                    Text = ex.Message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113))
                });
            }

            return panel;
        }

        private void RenderDllLists(Panel target, string? searchText, TextBlock countText)
        {
            target.Children.Clear();
            string query = (searchText ?? string.Empty).Trim();

            bool Matches(ModInfo mod)
            {
                return query.Length == 0
                    || mod.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || mod.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase);
            }

            List<ModInfo> activeMods = _mods
                .Where(m => File.Exists(Path.Combine(AsiDirectory, m.FileName)))
                .Where(Matches)
                .OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<ModInfo> disabledMods = _mods
                .Where(m => !File.Exists(Path.Combine(AsiDirectory, m.FileName))
                            && File.Exists(Path.Combine(ModsRoot, m.FileName)))
                .Where(Matches)
                .OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            countText.Text = $"Найдено: {activeMods.Count + disabledMods.Count}  •  " +
                             $"Включено: {activeMods.Count}  •  Отключено: {disabledMods.Count}";

            target.Children.Add(new TextBlock
            {
                Text = $"ВКЛЮЧЁННЫЕ DLL ({activeMods.Count})",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 8, 0, 6)
            });

            if (activeMods.Count == 0)
            {
                target.Children.Add(new TextBlock
                {
                    Text = query.Length > 0
                        ? "Нет включённых DLL, соответствующих поиску."
                        : "В папке asi пока нет DLL-файлов.",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 152, 174)),
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }

            AddModRows(target, activeMods, () => RenderDllLists(target, searchText ?? string.Empty, countText));

            target.Children.Add(new TextBlock
            {
                Text = $"ОТКЛЮЧЁННЫЕ DLL ({disabledMods.Count})",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 12, 0, 6)
            });

            if (disabledMods.Count == 0)
            {
                target.Children.Add(new TextBlock
                {
                    Text = query.Length > 0
                        ? "Нет отключённых DLL, соответствующих поиску."
                        : "Нет отключённых DLL в библиотеке.",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 152, 174)),
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }

            AddModRows(target, disabledMods, () => RenderDllLists(target, searchText ?? string.Empty, countText));
        }

        private void AddModRows(Panel listPanel, IEnumerable<ModInfo> mods, Action? refresh = null)
        {
            foreach (ModInfo mod in mods)
            {
                bool enabled = File.Exists(Path.Combine(AsiDirectory, mod.FileName));
                var row = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(16, 23, 37)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(38, 50, 72)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                var rowPanel = new StackPanel();
                var header = new DockPanel();
                var stateLabel = new TextBlock
                {
                    Text = enabled ? "ВКЛЮЧЕНА" : "ОТКЛЮЧЕНА",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = enabled
                        ? new SolidColorBrush(Color.FromRgb(214, 255, 95))
                        : new SolidColorBrush(Color.FromRgb(170, 175, 193)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                DockPanel.SetDock(stateLabel, Dock.Right);
                header.Children.Add(stateLabel);

                var checkBox = new CheckBox
                {
                    Content = mod.FileName,
                    IsChecked = enabled,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    ToolTip = mod.RelativePath
                };
                header.Children.Add(checkBox);
                rowPanel.Children.Add(header);

                rowPanel.Children.Add(new TextBlock
                {
                    Text = mod.RelativePath,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 152, 174)),
                    Margin = new Thickness(0, 5, 0, 0)
                });

                if (!string.IsNullOrWhiteSpace(mod.Sha256))
                {
                    rowPanel.Children.Add(new TextBlock
                    {
                        Text = "SHA-256: " + mod.Sha256,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 110, 132)),
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                }

                string dllRelativePath = mod.RelativePath;
                bool reverting = false;
                checkBox.Checked += (_, _) =>
                {
                    if (reverting) return;
                    if (!SetDllEnabled(dllRelativePath, true))
                    {
                        reverting = true;
                        checkBox.IsChecked = false;
                        reverting = false;
                    }
                    else
                    {
                        refresh?.Invoke();
                    }
                };

                checkBox.Unchecked += (_, _) =>
                {
                    if (reverting) return;
                    if (!SetDllEnabled(dllRelativePath, false))
                    {
                        reverting = true;
                        checkBox.IsChecked = true;
                        reverting = false;
                    }
                    else
                    {
                        refresh?.Invoke();
                    }
                };

                row.Child = rowPanel;
                listPanel.Children.Add(row);
            }
        }

        private string GetSelectedServerDescription()
        {
            if (!_servers.TryGetValue(_selectedServer, out var server))
                return "Сервер не выбран.";

            return $"{server.Name} — {server.IpAddress}:{server.Port}";
        }

        private static TextBlock CreateSectionLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(
                    Color.FromRgb(139, 152, 174))
            };
        }

        private static StackPanel CreateSettingsTabPanel()
        {
            return new StackPanel
            {
                Margin = new Thickness(20, 18, 20, 20)
            };
        }

        private static TabItem CreateSettingsTab(string header, string tag, StackPanel content)
        {
            var headerPanel = new StackPanel { Margin = new Thickness(4, 3, 4, 3) };
            headerPanel.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = tag,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(181, 108, 255)),
                Margin = new Thickness(0, 3, 0, 0)
            });

            return new TabItem
            {
                Header = headerPanel,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = content,
                    Padding = new Thickness(0, 0, 6, 0)
                },
                Background = new SolidColorBrush(Color.FromRgb(16, 11, 23)),
                Foreground = new SolidColorBrush(Color.FromRgb(244, 243, 245)),
                Padding = new Thickness(0)
            };
        }

        private static TextBlock CreatePathStatusText()
        {
            return new TextBlock
            {
                FontSize = 14,
                Margin = new Thickness(0, 4, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };
        }

        private static void SetPathStatus(TextBlock? target, string text, bool isOkay)
        {
            if (target is null) return;

            target.Text = text;
            target.Foreground = isOkay
                ? new SolidColorBrush(Color.FromRgb(105, 217, 162))
                : new SolidColorBrush(Color.FromRgb(255, 177, 105));
        }

        private static Button CreateSettingsButton(string text)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(16, 11, 16, 11),
                Margin = new Thickness(0, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(34, 24, 46)),
                Foreground = new SolidColorBrush(Color.FromRgb(250, 248, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(72, 48, 94)),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                MinHeight = 44,
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        private static Style CreateSettingsScrollBarStyle()
        {
            // Keep the WPF ScrollBar control template intact. Track exposes Thumb and
            // RepeatButton parts as CLR properties, not dependency properties, so they
            // cannot be assigned with FrameworkElementFactory.SetValue.
            var style = new Style(typeof(ScrollBar));
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 8.0));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(18, 12, 25))));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(181, 108, 255))));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(55, 38, 69))));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            return style;
        }
    }
}
