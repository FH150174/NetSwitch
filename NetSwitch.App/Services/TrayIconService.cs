using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using H.NotifyIcon;
using NetSwitch.Core.Abstractions;

namespace NetSwitch.App.Services;

/// <summary>
/// 系统托盘图标 + 桌面通知。兼作 <see cref="INotifier"/> 实现。
/// </summary>
public sealed class TrayIconService : INotifier, IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _autoItem;
    private readonly MenuItem _autoStartItem;
    private readonly MenuItem _silentItem;
    private readonly Action _openMainWindow;
    private readonly Func<bool> _getAutoArbitrate;
    private readonly Action<bool> _setAutoArbitrate;
    private readonly Func<bool> _getAutoStart;
    private readonly Action<bool> _setAutoStart;
    private readonly Func<bool> _getStartSilently;
    private readonly Action<bool> _setStartSilently;
    private readonly Action _exit;

    public TrayIconService(
        Action openMainWindow,
        Func<bool> getAutoArbitrate,
        Action<bool> setAutoArbitrate,
        Func<bool> getAutoStart,
        Action<bool> setAutoStart,
        Func<bool> getStartSilently,
        Action<bool> setStartSilently,
        Action exit)
    {
        _openMainWindow = openMainWindow;
        _getAutoArbitrate = getAutoArbitrate;
        _setAutoArbitrate = setAutoArbitrate;
        _getAutoStart = getAutoStart;
        _setAutoStart = setAutoStart;
        _getStartSilently = getStartSilently;
        _setStartSilently = setStartSilently;
        _exit = exit;

        var openItem = new MenuItem { Header = "打开 NetSwitch" };
        openItem.Click += (_, _) => _openMainWindow();

        _autoItem = new MenuItem
        {
            Header = "自动仲裁",
            IsCheckable = true,
            IsChecked = _getAutoArbitrate(),
        };
        _autoItem.Click += (_, _) => _setAutoArbitrate(_autoItem.IsChecked);

        // 规格 §10.3：托盘右键菜单提供「开机自启」与「静默启动」两个勾选项。
        _autoStartItem = new MenuItem
        {
            Header = "开机自启",
            IsCheckable = true,
            IsChecked = _getAutoStart(),
        };
        _autoStartItem.Click += (_, _) => _setAutoStart(_autoStartItem.IsChecked);

        _silentItem = new MenuItem
        {
            Header = "静默启动",
            IsCheckable = true,
            IsChecked = _getStartSilently(),
        };
        _silentItem.Click += (_, _) => _setStartSilently(_silentItem.IsChecked);

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => _exit();

        var contextMenu = new ContextMenu
        {
            Items =
            {
                openItem,
                new Separator(),
                _autoItem,
                new Separator(),
                _autoStartItem,
                _silentItem,
                new Separator(),
                exitItem,
            },
        };

        // 每次展开菜单时回读配置，保持勾选与真实状态一致（托盘与主窗口可能各改一处）。
        contextMenu.Opened += (_, _) =>
        {
            _autoItem.IsChecked = _getAutoArbitrate();
            _autoStartItem.IsChecked = _getAutoStart();
            _silentItem.IsChecked = _getStartSilently();
        };

        _icon = new TaskbarIcon
        {
            ToolTipText = "NetSwitch 网络适配器切换器",
            Icon = CreateIcon(),
            ContextMenu = contextMenu,
        };
        _icon.TrayLeftMouseDown += (_, _) => _openMainWindow();

        // 纯代码创建的 TaskbarIcon 未加入可视树，必须显式强制创建底层托盘图标，
        // 否则 ShowNotification 会抛 "TrayIcon is not created"。
        // 传 false 避免同时开启 Windows 11 效率模式（那会把应用置于隐藏运行状态）。
        _icon.ForceCreate(false);
    }

    public void Notify(string title, string message)
    {
        // 通知是尽力而为的：托盘图标可能因资源管理器重启等而失效，
        // 失败绝不应中断仲裁等核心逻辑。
        try
        {
            _icon.ShowNotification(title, message);
        }
        catch
        {
            // 静默忽略通知失败。
        }
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 紫色圆角方形背景。
            using var path = CreateRoundedRectangle(new RectangleF(0, 0, 32, 32), 7f);
            using var background = new SolidBrush(Color.FromArgb(124, 58, 237));
            g.FillPath(background, path);

            // 居中白色大写 N。
            using var font = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("N", font, brush, new RectangleF(0, 0, 32, 32), format);
        }

        // Clone 后销毁原 HICON，避免 GDI 句柄泄漏。
        var hIcon = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose() => _icon.Dispose();
}
