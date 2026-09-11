namespace NetSwitch.Core.Abstractions;

/// <summary>
/// 桌面通知抽象。
/// </summary>
public interface INotifier
{
    void Notify(string title, string message);
}
