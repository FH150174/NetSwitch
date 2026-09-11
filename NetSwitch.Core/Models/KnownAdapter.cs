namespace NetSwitch.Core.Models;

/// <summary>
/// 已知库中持久化的一条网卡记录，保存用户自定义的优先级与忽略标记。
/// </summary>
public sealed class KnownAdapter
{
    public string Guid { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public InterfaceType InterfaceType { get; set; } = InterfaceType.Other;

    /// <summary>优先级，数值越小越优先。</summary>
    public int Priority { get; set; }

    /// <summary>为 true 时不纳入仲裁，永不被触碰。</summary>
    public bool IsIgnored { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }
}
