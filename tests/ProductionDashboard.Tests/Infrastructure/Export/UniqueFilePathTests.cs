using FluentAssertions;
using ProductionDashboard.Infrastructure.Export;

namespace ProductionDashboard.Tests.Infrastructure.Export;

/// <summary><see cref="UniqueFilePath"/>:归档 / 备份文件名绝不与已有文件重名。</summary>
public sealed class UniqueFilePathTests : IDisposable
{
    private static readonly DateTime Now = new(2027, 1, 1, 0, 5, 9);
    private readonly string _dir = Directory.CreateTempSubdirectory("pdufp_").FullName;

    [Fact]
    public void Uses_the_plain_name_when_it_is_free()
    {
        UniqueFilePath.Next(_dir, "2026", ".xlsx", Now).Should().Be(Path.Combine(_dir, "2026.xlsx"));
    }

    [Fact]
    public void Adds_a_timestamp_when_the_plain_name_exists()
    {
        File.WriteAllText(Path.Combine(_dir, "2026.xlsx"), "first archive");

        UniqueFilePath.Next(_dir, "2026", ".xlsx", Now)
            .Should().Be(Path.Combine(_dir, "2026_20270101-000509.xlsx"));
    }

    [Fact]
    public void Adds_a_counter_when_the_timestamped_name_also_exists()
    {
        File.WriteAllText(Path.Combine(_dir, "2026.xlsx"), "");
        File.WriteAllText(Path.Combine(_dir, "2026_20270101-000509.xlsx"), "");

        UniqueFilePath.Next(_dir, "2026", ".xlsx", Now)
            .Should().Be(Path.Combine(_dir, "2026_20270101-000509_2.xlsx"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
