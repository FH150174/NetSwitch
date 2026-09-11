using System.ComponentModel;
using System.Windows;

namespace NetSwitch.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // 关闭窗口时最小化到托盘，而非退出进程。
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
