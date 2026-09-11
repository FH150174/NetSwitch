using CommunityToolkit.Mvvm.ComponentModel;
using NetSwitch.Core.Models;

namespace NetSwitch.App.ViewModels;

/// <summary>
/// 单个网卡在列表中的展示模型。
/// </summary>
public partial class AdapterItemViewModel : ObservableObject
{
    public string Guid { get; }
    public string Name { get; }
    public string Description { get; }
    public InterfaceType InterfaceType { get; }

    [ObservableProperty]
    private AdapterState state;

    [ObservableProperty]
    private int priority;

    [ObservableProperty]
    private bool isIgnored;

    [ObservableProperty]
    private bool isPresent;

    public AdapterItemViewModel(KnownAdapter known, Adapter? adapter)
    {
        Guid = known.Guid;
        Name = known.Name;
        Description = known.Description;
        InterfaceType = known.InterfaceType;
        priority = known.Priority;
        isIgnored = known.IsIgnored;
        isPresent = adapter is not null;
        state = adapter?.State ?? AdapterState.NotPresent;
    }

    public string StateText => State switch
    {
        AdapterState.Up => "已连接",
        AdapterState.Disconnected => "已断开",
        AdapterState.Disabled => "已禁用",
        _ => "不存在",
    };

    public string InterfaceTypeText => InterfaceType switch
    {
        InterfaceType.Ethernet => "以太网",
        InterfaceType.WiFi => "WiFi",
        InterfaceType.Virtual => "虚拟",
        _ => "其他",
    };

    public void UpdateFrom(Adapter? adapter)
    {
        IsPresent = adapter is not null;
        State = adapter?.State ?? AdapterState.NotPresent;
    }
}
