using NetSwitch.Core.Models;
using NetSwitch.Infrastructure.Storage;

namespace NetSwitch.Tests;

/// <summary>
/// 静默启动默认值语义（规格 §15 方案 2）。
///
/// 首次运行（配置文件中尚无 <c>startSilently</c> 字段）默认 **显示窗口**——
/// 默认静默会让「双击 exe 就是想看界面」的用户以为程序没启动（V1.2.0 实测复现）。
/// 一旦用户显式设置过该开关，则完全以用户选择为准。
/// </summary>
public sealed class StartSilentlyTests : IDisposable
{
    private readonly string _directory;

    public StartSilentlyTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "NetSwitchTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public void 配置文件不存在时_首次运行默认显示窗口()
    {
        var config = new JsonConfigStore(_directory).Load();

        Assert.False(config.ResolveStartSilently());
    }

    [Fact]
    public void 配置文件中无该字段时_首次运行默认显示窗口并被物化()
    {
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            """
            {
              "autoArbitrate": true,
              "pollIntervalSeconds": 2
            }
            """);

        var config = new JsonConfigStore(_directory).Load();

        Assert.False(config.ResolveStartSilently());
        Assert.False(config.StartSilently); // 已物化为具体值，不再是 null
    }

    [Fact]
    public void 用户显式开启静默启动后_以用户选择为准()
    {
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            """{ "startSilently": true }""");

        var config = new JsonConfigStore(_directory).Load();

        Assert.True(config.ResolveStartSilently());
    }

    [Fact]
    public void 用户显式关闭静默启动后_以用户选择为准()
    {
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            """{ "startSilently": false }""");

        var config = new JsonConfigStore(_directory).Load();

        Assert.False(config.ResolveStartSilently());
    }

    [Fact]
    public void 首次运行默认值会被落盘_后续加载不再依赖首次逻辑()
    {
        var store = new JsonConfigStore(_directory);

        var config = store.Load(); // 首次：物化为 FirstRunStartSilently
        store.Save(config);

        var json = File.ReadAllText(Path.Combine(_directory, "config.json"));
        Assert.Contains("\"startSilently\": false", json);

        // 再次加载：读到的已是显式值，语义不变。
        Assert.False(new JsonConfigStore(_directory).Load().ResolveStartSilently());
    }

    [Fact]
    public void 用户设置能覆盖首次运行默认值并持久化()
    {
        var store = new JsonConfigStore(_directory);

        var config = store.Load();
        Assert.False(config.ResolveStartSilently());

        config.StartSilently = true;
        store.Save(config);

        Assert.True(new JsonConfigStore(_directory).Load().ResolveStartSilently());
    }

    [Fact]
    public void 首次运行默认值为false_而非常量硬编码在调用点()
    {
        // 把「首次运行默认值」钉在 AppConfig 上，避免将来在别处又写死一个 true。
        Assert.False(AppConfig.FirstRunStartSilently);
    }
}
