using System.Threading;
using System.Windows;
using NetSwitch.App.Services;
using NetSwitch.App.ViewModels;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Arbitration;
using NetSwitch.Infrastructure.Cim;
using NetSwitch.Infrastructure.Logging;
using NetSwitch.Infrastructure.Storage;

namespace NetSwitch.App;

/// <summary>
/// Interaction logic for App.xaml
/// 组合根：手工装配各层依赖，启动托盘与轮询监控。
/// </summary>
public partial class App : Application
{
    private const string MutexName = "NetSwitch.SingleInstance";
    private const string ShowWindowEventName = "NetSwitch.ShowWindow";

    /// <summary>静默启动时首个仲裁周期前的等待时间，等网络栈/WMI 就绪（规格 §10.3）。</summary>
    private static readonly TimeSpan StartupSettleDelay = TimeSpan.FromSeconds(3);

    private Mutex? _mutex;
    private EventWaitHandle? _showWindowEvent;
    private LogService? _logger;
    private TrayIconService? _tray;
    private AdapterMonitor? _monitor;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;

    /// <summary>应用是否正在退出（用于区分「关闭窗口」与「退出进程」）。</summary>
    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例保护：已有一个实例运行时，第二个实例通知已有实例显示主窗口，然后自身退出。
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowWindowEventName);
                evt.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 事件尚未创建（第一实例启动极早期），忽略即可。
            }

            Shutdown();
            return;
        }

        // 第一实例创建命名事件并监听，供后续实例唤起窗口。
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var listener = new Thread(ListenForShowWindow) { IsBackground = true };
        listener.Start();

        _logger = new LogService();
        _logger.Info("NetSwitch 启动");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.Error("未处理异常", args.ExceptionObject as Exception);

        // 系统注销/关机时置位，使 MainWindow 不拦截关闭，进程可正常退出。
        SessionEnding += (_, _) => IsExiting = true;

        var configStore = new JsonConfigStore();
        var repository = new CimAdapterRepository { Warn = message => _logger.Warn(message) };

        // 启动模式：命令行 > 配置项（规格 §10.3）。
        var startupMode = StartupOptions.ParseMode(e.Args);
        var config = configStore.Load();
        var silent = StartupOptions.ResolveSilent(e.Args, config.StartSilently);
        _logger.Info(
            $"启动模式={startupMode}，配置 StartSilently={config.StartSilently}，实际{(silent ? "静默" : "显示窗口")}");

        var autoStart = new AutoStartService(_logger);

        // 托盘服务兼作通知器（先创建，供仲裁服务注入）。
        _tray = TryCreateTray(configStore, autoStart, out var trayReady);
        if (!trayReady)
        {
            // 静默模式下托盘是唯一入口，创建失败必须降级为显示窗口（规格 §11）。
            _logger.Error("托盘图标创建失败，降级为显示主窗口");
            silent = false;
        }

        var arbitration = new ArbitrationService(repository, configStore, _logger, _tray);

        _mainViewModel = new MainViewModel(
            repository,
            configStore,
            arbitration,
            autoStart,
            _logger,
            openLog: OpenLog,
            confirmEnableSilent: ConfirmEnableSilent);
        _mainViewModel.Initialize();

        // 静默时仍创建窗口对象（保持隐藏），以保留单实例唤起与托盘打开能力。
        // ShutdownMode 必须为 OnExplicitShutdown，否则无可见窗口时 WPF 会立即退出。
        _mainWindow = new MainWindow { DataContext = _mainViewModel };
        if (silent)
        {
            _logger.Info("静默启动：主窗口保持隐藏，驻留托盘");
        }
        else
        {
            _mainWindow.Show();
        }

        _monitor = new AdapterMonitor(
            arbitration,
            onRefreshed: _mainViewModel.Refresh,
            logger: _logger,
            pollInterval: () => configStore.Load().PollIntervalSeconds,
            initialDelay: StartupSettleDelay);
        _monitor.Start();
    }

    private TrayIconService? TryCreateTray(IConfigStore configStore, AutoStartService autoStart, out bool ready)
    {
        try
        {
            var tray = new TrayIconService(
                openMainWindow: ShowMainWindow,
                getAutoArbitrate: () => configStore.Load().AutoArbitrate,
                setAutoArbitrate: value =>
                {
                    var config = configStore.Load();
                    config.AutoArbitrate = value;
                    configStore.Save(config);
                    _mainViewModel?.SyncSettingsFromConfig();
                },
                getAutoStart: () => configStore.Load().AutoStartEnabled,
                setAutoStart: value =>
                {
                    try
                    {
                        if (value)
                        {
                            autoStart.Enable();
                        }
                        else
                        {
                            autoStart.Disable();
                        }

                        var config = configStore.Load();
                        config.AutoStartEnabled = value;
                        configStore.Save(config);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error("托盘切换开机自启失败", ex);
                    }

                    _mainViewModel?.SyncSettingsFromConfig();
                },
                getStartSilently: () => configStore.Load().StartSilently,
                setStartSilently: value =>
                {
                    var config = configStore.Load();
                    config.StartSilently = value;
                    configStore.Save(config);
                    _mainViewModel?.SyncSettingsFromConfig();
                },
                exit: ExitApp);

            ready = true;
            return tray;
        }
        catch (Exception ex)
        {
            _logger?.Error("创建托盘图标失败", ex);
            ready = false;
            return null;
        }
    }

    /// <summary>
    /// 后台线程：等待后续实例发出的唤起信号，在 UI 线程显示主窗口。
    /// </summary>
    private void ListenForShowWindow()
    {
        try
        {
            while (true)
            {
                _showWindowEvent!.WaitOne();
                Dispatcher.Invoke(ShowMainWindow);
            }
        }
        catch (ObjectDisposedException)
        {
            // 应用退出时事件被释放，结束监听。
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void OpenLog()
    {
        if (_logger is null)
        {
            return;
        }

        new LogView(_logger.DirectoryPath) { Owner = _mainWindow }.Show();
    }

    /// <summary>规格 §10.3：开启自启时若未勾选静默启动，提示并建议勾选。</summary>
    private static bool ConfirmEnableSilent()
        => MessageBox.Show(
            "已开启开机自启。是否同时启用「静默启动」？\n\n" +
            "启用后登录时将直接驻留托盘，不弹出主窗口；随时可在主窗口或托盘菜单中关闭。",
            "NetSwitch",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void ExitApp()
    {
        IsExiting = true;
        _monitor?.Stop();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Info("NetSwitch 退出");
        _showWindowEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
