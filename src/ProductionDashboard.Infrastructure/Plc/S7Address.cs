using System.Globalization;
using System.Text.RegularExpressions;

namespace ProductionDashboard.Infrastructure.Plc;

/// <summary>S7 区域类型。当前仅用 DB 区。</summary>
public enum S7AreaKind { Bit, Byte, Word, DWord }

/// <summary>
/// 解析后的 S7 数据块地址,例如 <c>DB100.DBX0.0</c> -> DB=100, Offset=0, Bit=0, Kind=Bit。
/// </summary>
public sealed record S7Address(int Db, int ByteOffset, int BitOffset, S7AreaKind Kind)
{
    private static readonly Regex Pattern = new(
        @"^DB(?<db>\d+)\.DB(?<kind>X|B|W|D)(?<byte>\d+)(?:\.(?<bit>[0-7]))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>解析地址字符串。格式:<c>DB&lt;n&gt;.DB[X|B|W|D]&lt;offset&gt;[.&lt;bit&gt;]</c>。</summary>
    public static S7Address Parse(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var m = Pattern.Match(address.Trim());
        if (!m.Success)
        {
            throw new FormatException(
                $"无法解析 S7 地址 '{address}'。期望格式如 DB100.DBX0.0 / DB100.DBW2 / DB100.DBD4。");
        }

        var db = int.Parse(m.Groups["db"].Value, CultureInfo.InvariantCulture);
        var byteOffset = int.Parse(m.Groups["byte"].Value, CultureInfo.InvariantCulture);
        var kind = m.Groups["kind"].Value.ToUpperInvariant() switch
        {
            "X" => S7AreaKind.Bit,
            "B" => S7AreaKind.Byte,
            "W" => S7AreaKind.Word,
            "D" => S7AreaKind.DWord,
            _ => throw new FormatException($"未知 S7 区域类型: {m.Groups["kind"].Value}"),
        };

        var bitOffset = 0;
        if (kind == S7AreaKind.Bit)
        {
            if (!m.Groups["bit"].Success)
            {
                throw new FormatException($"DBX 地址必须带位偏移: '{address}'");
            }
            bitOffset = int.Parse(m.Groups["bit"].Value, CultureInfo.InvariantCulture);
        }
        else if (m.Groups["bit"].Success)
        {
            throw new FormatException($"DB{m.Groups["kind"].Value} 地址不可带位偏移: '{address}'");
        }

        return new S7Address(db, byteOffset, bitOffset, kind);
    }

    /// <summary>该地址需要读/写的字节数。</summary>
    public int ByteSize => Kind switch
    {
        S7AreaKind.Bit or S7AreaKind.Byte => 1,
        S7AreaKind.Word => 2,
        S7AreaKind.DWord => 4,
        _ => 1,
    };
}
