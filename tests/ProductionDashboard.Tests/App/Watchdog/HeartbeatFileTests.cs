using FluentAssertions;
using ProductionDashboard.Watchdog;

namespace ProductionDashboard.Tests.App.Watchdog;

/// <summary>
/// 看门狗心跳文件读写测试。覆盖:
/// <list type="bullet">
///   <item>字段往返(序列化 / 反序列化所有字段保持一致)</item>
///   <item>多次写入只保留最新一次</item>
///   <item>文件不存在 / 内容损坏时安全返回 null,不抛异常</item>
///   <item>父目录缺失时自动创建</item>
/// </list>
/// </summary>
public sealed class HeartbeatFileTests : IDisposable
{
    // 每个测试用独立的临时文件,避免测试间互相污染
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"pdhb_{Guid.NewGuid():N}.json");

    /// <summary>Heartbeat 写入后再读出,所有字段完整还原。</summary>
    [Fact]
    public void Write_then_TryRead_roundtrips_all_fields()
    {
        var hb = new Heartbeat
        {
            Pid = 12345,
            Timestamp = new DateTimeOffset(2026, 5, 20, 10, 23, 45, TimeSpan.Zero),
            Version = "1.2.3.0",
            GracefulExit = false,
        };

        HeartbeatFile.Write(_path, hb);

        var read = HeartbeatFile.TryRead(_path);
        read.Should().NotBeNull();
        read!.Pid.Should().Be(12345);
        read.Timestamp.Should().Be(hb.Timestamp);
        read.Version.Should().Be("1.2.3.0");
        read.GracefulExit.Should().BeFalse();
    }

    /// <summary>对同一路径多次写入,后写覆盖前写,读取得到最新一次。</summary>
    [Fact]
    public void Write_overwrites_previous_content()
    {
        HeartbeatFile.Write(_path, new Heartbeat
        {
            Pid = 1,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        HeartbeatFile.Write(_path, new Heartbeat
        {
            Pid = 2,
            Timestamp = DateTimeOffset.UtcNow,
            GracefulExit = true,
        });

        var read = HeartbeatFile.TryRead(_path);
        read!.Pid.Should().Be(2);
        read.GracefulExit.Should().BeTrue();
    }

    /// <summary>文件不存在时安全返回 null(看门狗启动早于主程序的常见场景)。</summary>
    [Fact]
    public void TryRead_returns_null_when_file_missing()
    {
        HeartbeatFile.TryRead(_path).Should().BeNull();
    }

    /// <summary>内容损坏(非合法 JSON)时返回 null,不抛异常,保证守护进程稳定。</summary>
    [Fact]
    public void TryRead_returns_null_on_garbage_content()
    {
        File.WriteAllText(_path, "this is not json {{{");
        HeartbeatFile.TryRead(_path).Should().BeNull();
    }

    /// <summary>父目录不存在时 Write 应自动建好目录,避免首次启动失败。</summary>
    [Fact]
    public void Write_creates_missing_parent_directory()
    {
        var nested = Path.Combine(
            Path.GetTempPath(),
            $"pdhb_{Guid.NewGuid():N}",
            "sub",
            "heartbeat.json");
        try
        {
            HeartbeatFile.Write(nested, new Heartbeat
            {
                Pid = 1,
                Timestamp = DateTimeOffset.UtcNow,
            });
            File.Exists(nested).Should().BeTrue();
        }
        finally
        {
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(nested));
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>清理临时文件以及可能残留的 .tmp 中间文件。</summary>
    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        var tmp = _path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
    }
}
