namespace AndroidOptimize.Core.Apk;

/// <summary>
/// 小端读取工具。APK 里的 ZIP、AndroidManifest.xml、resources.arsc 全是小端格式。
/// 所有读取都做边界检查：格式不认识时返回 null / false，绝不抛异常——
/// 应用名读不到只是少显示一个名字，不能影响整个扫描。
/// </summary>
internal sealed class ByteReader(byte[] data)
{
    private readonly byte[] _data = data;

    public byte[] Data => _data;
    public int Length => _data.Length;

    public bool InRange(int offset, int count) => offset >= 0 && count >= 0 && offset + count <= _data.Length;

    public ushort UInt16(int offset) =>
        InRange(offset, 2) ? (ushort)(_data[offset] | (_data[offset + 1] << 8)) : (ushort)0;

    public uint UInt32(int offset) =>
        InRange(offset, 4)
            ? (uint)(_data[offset] | (_data[offset + 1] << 8) | (_data[offset + 2] << 16) | (_data[offset + 3] << 24))
            : 0u;

    public byte Byte(int offset) => InRange(offset, 1) ? _data[offset] : (byte)0;

    public byte[] Slice(int offset, int count) =>
        InRange(offset, count) ? _data.AsSpan(offset, count).ToArray() : [];
}

/// <summary>AXML / ARSC 通用的 chunk 头。</summary>
internal readonly record struct ChunkHeader(ushort Type, ushort HeaderSize, uint Size)
{
    public bool IsValid => Size >= HeaderSize && HeaderSize >= 8;
}

internal static class ChunkTypes
{
    public const ushort StringPool = 0x0001;
    public const ushort Table = 0x0002;
    public const ushort TablePackage = 0x0200;
    public const ushort TableType = 0x0201;
    public const ushort TableTypeSpec = 0x0202;
    public const ushort Xml = 0x0003;
    public const ushort XmlStartElement = 0x0102;
    public const ushort XmlResourceMap = 0x0180;
}

internal static class ValueTypes
{
    public const byte Reference = 0x01;
    public const byte String = 0x03;
}
