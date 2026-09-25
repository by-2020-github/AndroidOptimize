namespace AndroidOptimize.Core.Apk;

/// <summary>
/// 读取 resources.arsc（ResTable），把资源 ID 换成一个字符串。
/// 应用名通常是 @string/app_name 这样的资源引用，必须查表才能拿到真实文字。
/// </summary>
internal sealed class ResourceTable
{
    private readonly ByteReader _reader;
    private readonly StringPool _globalStrings;
    private readonly int _packageStart;
    private readonly int _packageSize;

    private ResourceTable(ByteReader reader, StringPool globalStrings, int packageStart, int packageSize)
    {
        _reader = reader;
        _globalStrings = globalStrings;
        _packageStart = packageStart;
        _packageSize = packageSize;
    }

    public static ResourceTable? Parse(byte[] arsc)
    {
        var reader = new ByteReader(arsc);
        if (reader.Length < 12 || reader.UInt16(0) != ChunkTypes.Table) return null;

        var headerSize = reader.UInt16(2);
        var packageCount = reader.UInt32(8);
        if (packageCount > 200) return null;

        // 全局字符串池紧跟 ARSC 头部
        var globalStrings = StringPool.Parse(reader, headerSize, out var p);
        if (globalStrings is null) return null;

        // 应用资源包的 id 一般是 0x7f，这里不写死，直接取第一个 package chunk。
        if (p + 8 > reader.Length) return null;

        var header = new ChunkHeader(reader.UInt16(p), reader.UInt16(p + 2), reader.UInt32(p + 4));
        if (header.Type != ChunkTypes.TablePackage || !header.IsValid || header.Size > reader.Length - p) return null;

        return new ResourceTable(reader, globalStrings, p, (int)header.Size);
    }

    /// <summary>把 0x7f0e0001 这样的资源 ID 解析成字符串。</summary>
    public string? Resolve(uint resourceId)
    {
        var typeId = (byte)((resourceId >> 16) & 0xFF);
        var entryIndex = (int)(resourceId & 0xFFFF);

        var headerSize = _reader.UInt16(_packageStart + 2);
        var p = _packageStart + headerSize;
        var end = _packageStart + _packageSize;

        var found = (string?)null;

        while (p + 8 <= end)
        {
            var header = new ChunkHeader(_reader.UInt16(p), _reader.UInt16(p + 2), _reader.UInt32(p + 4));
            if (!header.IsValid || header.Size > end - p) break;

            if (header.Type == ChunkTypes.TableType)
            {
                var value = ReadEntry(p, header, typeId, entryIndex, out var isDefaultConfig);
                if (isDefaultConfig) return value;   // 默认配置（不限语言/分辨率）优先
                found ??= value;
            }

            p += (int)header.Size;
        }

        return found;
    }

    private string? ReadEntry(int chunkStart, ChunkHeader header, byte typeId, int entryIndex, out bool isDefaultConfig)
    {
        isDefaultConfig = false;

        if (_reader.Byte(chunkStart + 8) != typeId) return null;

        var entryCount = _reader.UInt32(chunkStart + 12);
        var entriesStart = _reader.UInt32(chunkStart + 16);
        if (entryIndex >= entryCount) return null;

        // 每个条目在 chunk 头之后有一个 uint32 偏移
        var offsetTablePos = chunkStart + header.HeaderSize + entryIndex * 4;
        if (!_reader.InRange(offsetTablePos, 4)) return null;

        var entryOffset = _reader.UInt32(offsetTablePos);
        if (entryOffset == 0xFFFFFFFF) return null;   // 这个条目在这个配置下不存在

        var entryPos = chunkStart + (int)entriesStart + (int)entryOffset;
        if (!_reader.InRange(entryPos, 8)) return null;

        var entrySize = _reader.UInt16(entryPos);
        var entryFlags = _reader.UInt16(entryPos + 2);
        if ((entryFlags & 0x0001) != 0) return null;   // 复杂条目（bag/map），不是单个值

        var valuePos = entryPos + entrySize;
        if (!_reader.InRange(valuePos, 8)) return null;

        var dataType = _reader.Byte(valuePos + 3);
        var data = _reader.UInt32(valuePos + 4);
        if (dataType != ValueTypes.String) return null;

        // config 紧跟在 chunk 头之后（第 20 字节起），除了 size 之外全 0 就是默认配置
        var configStart = chunkStart + 20;
        var configEnd = chunkStart + header.HeaderSize;
        isDefaultConfig = true;
        for (var i = configStart + 4; i < configEnd; i++)
        {
            if (_reader.Byte(i) != 0)
            {
                isDefaultConfig = false;
                break;
            }
        }

        return _globalStrings.At((int)data);
    }
}
