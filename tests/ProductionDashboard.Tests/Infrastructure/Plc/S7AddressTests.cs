using FluentAssertions;
using ProductionDashboard.Infrastructure.Plc;

namespace ProductionDashboard.Tests.Infrastructure.Plc;

/// <summary>
/// S7 地址解析器测试。覆盖各种合法 / 非法地址格式,
/// 以及不同区域类型(Bit/Byte/Word/DWord)对应的字节宽度。
/// </summary>
public sealed class S7AddressTests
{
    /// <summary>各种合法地址都能正确解析出 DB 号、字节偏移、位偏移、类型。</summary>
    [Theory]
    [InlineData("DB100.DBX0.0", 100, 0, 0, S7AreaKind.Bit)]
    [InlineData("DB1.DBX10.7", 1, 10, 7, S7AreaKind.Bit)]
    [InlineData("DB100.DBB5", 100, 5, 0, S7AreaKind.Byte)]
    [InlineData("DB100.DBW2", 100, 2, 0, S7AreaKind.Word)]
    [InlineData("DB100.DBD4", 100, 4, 0, S7AreaKind.DWord)]
    [InlineData("db100.dbx0.0", 100, 0, 0, S7AreaKind.Bit)]   // 大小写不敏感
    public void Parse_valid_addresses(
        string input, int db, int byteOffset, int bitOffset, S7AreaKind kind)
    {
        var addr = S7Address.Parse(input);
        addr.Db.Should().Be(db);
        addr.ByteOffset.Should().Be(byteOffset);
        addr.BitOffset.Should().Be(bitOffset);
        addr.Kind.Should().Be(kind);
    }

    /// <summary>
    /// 非法地址应抛出异常:空 / 空白抛 <see cref="ArgumentException"/>,
    /// 其它格式错误抛 <see cref="FormatException"/>。
    /// </summary>
    [Theory]
    [InlineData("DB100.DBX0")]      // 位地址缺少位偏移
    [InlineData("DB100.DBW2.0")]    // 字地址不应带位偏移
    [InlineData("M100.0")]          // Marker (M) 区目前未支持
    [InlineData("DB100")]           // 缺少类型后缀
    [InlineData("")]
    [InlineData("garbage")]
    public void Parse_rejects_malformed(string input)
    {
        var act = () => S7Address.Parse(input);
        if (string.IsNullOrWhiteSpace(input))
        {
            act.Should().Throw<ArgumentException>();
        }
        else
        {
            act.Should().Throw<FormatException>();
        }
    }

    /// <summary>
    /// 不同区域类型对应的字节宽度:
    /// Bit/Byte=1 字节,Word=2 字节,DWord=4 字节。
    /// </summary>
    [Theory]
    [InlineData(S7AreaKind.Bit, 1)]
    [InlineData(S7AreaKind.Byte, 1)]
    [InlineData(S7AreaKind.Word, 2)]
    [InlineData(S7AreaKind.DWord, 4)]
    public void ByteSize_matches_kind(S7AreaKind kind, int expected)
    {
        var addr = new S7Address(1, 0, 0, kind);
        addr.ByteSize.Should().Be(expected);
    }
}
