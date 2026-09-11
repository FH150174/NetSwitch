namespace NetSwitch.Core.Abstractions;

/// <summary>
/// 日志抽象。
/// </summary>
public interface ILogger
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}
