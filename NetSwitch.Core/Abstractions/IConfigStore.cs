using NetSwitch.Core.Models;

namespace NetSwitch.Core.Abstractions;

/// <summary>
/// 配置与已知库的读写抽象。
/// </summary>
public interface IConfigStore
{
    AppConfig Load();

    void Save(AppConfig config);
}
