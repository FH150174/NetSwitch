using System.IO;
using System.Text;
using System.Windows;

namespace NetSwitch.App;

/// <summary>
/// Interaction logic for LogView.xaml
/// </summary>
public partial class LogView : Window
{
    private readonly string _logDirectory;

    public LogView(string logDirectory)
    {
        _logDirectory = logDirectory;
        InitializeComponent();
        LoadLogs();
    }

    private void LoadLogs()
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                LogTextBox.Text = "(暂无日志)";
                return;
            }

            var files = Directory.GetFiles(_logDirectory, "*.log")
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToList();

            if (files.Count == 0)
            {
                LogTextBox.Text = "(暂无日志)";
                return;
            }

            var sb = new StringBuilder();
            foreach (var file in files)
            {
                sb.AppendLine($"===== {Path.GetFileName(file)} =====");
                sb.AppendLine(File.ReadAllText(file));
            }

            LogTextBox.Text = sb.ToString();
            LogTextBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            LogTextBox.Text = $"读取日志失败：{ex.Message}";
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadLogs();

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Directory.Exists(_logDirectory))
            {
                foreach (var file in Directory.GetFiles(_logDirectory, "*.log"))
                {
                    File.Delete(file);
                }
            }

            LogTextBox.Text = "(日志已清空)";
        }
        catch (Exception ex)
        {
            LogTextBox.Text = $"清空日志失败：{ex.Message}";
        }
    }
}
