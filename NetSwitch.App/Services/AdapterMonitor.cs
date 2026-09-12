using System.Windows;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Arbitration;

namespace NetSwitch.App.Services;

/// <summary>
/// 后台轮询器：在后台线程驱动仲裁状态机，通过 Dispatcher 回 UI 线程刷新，
/// 避免阻塞式 CIM 调用卡死界面。
/// </summary>
public sealed class AdapterMonitor
{
    private readonly ArbitrationService _arbitration;
    private readonly Action _onRefreshed;
    private readonly ILogger _logger;
    private readonly Func<int> _pollInterval;
    private readonly TimeSpan _initialDelay;
    private CancellationTokenSource? _cts;

    public AdapterMonitor(
        ArbitrationService arbitration,
        Action onRefreshed,
        ILogger logger,
        Func<int> pollInterval,
        TimeSpan initialDelay = default)
    {
        _arbitration = arbitration;
        _onRefreshed = onRefreshed;
        _logger = logger;
        _pollInterval = pollInterval;
        _initialDelay = initialDelay;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunLoop(_cts.Token);
    }

    public void Stop() => _cts?.Cancel();

    private async Task RunLoop(CancellationToken token)
    {
        // 登录后早期网络栈/WMI 可能尚未就绪，首次枚举会得到空快照并触发误判与抖动。
        // 静默启动（由计划任务拉起）时尤其明显，故首个仲裁周期前先等待一段短时间。
        if (_initialDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(_initialDelay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                _arbitration.Tick();
            }
            catch (Exception ex)
            {
                _logger.Error("仲裁周期失败", ex);
            }

            try
            {
                Application.Current?.Dispatcher.Invoke(_onRefreshed);
            }
            catch
            {
                // UI 刷新失败不影响监控循环。
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _pollInterval())), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
