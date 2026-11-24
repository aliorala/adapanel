using System.Buffers.Binary;

namespace AdaPanel.Infrastructure.Migration.Packages;

public sealed partial class PortableMigrationPackageStore
{
    // ZipArchive materializes its central directory on Entries access. Bound metadata before
    // letting it allocate entries, including ZIP64 archives with multi-gigabyte payloads.
    private async Task PreflightZipAsync(FileStream source, CancellationToken token)
    {
        if (source.Length < 22) throw Error("zip_invalid");
        var tail = new byte[(int)Math.Min(source.Length, 65535 + 22)];
        source.Position = source.Length - tail.Length;
        await source.ReadExactlyAsync(tail, token);
        var offset = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50
                && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length)
            { offset = i; break; }
        if (offset < 0) throw Error("zip_invalid");
        var eocd = tail.AsMemory(offset);
        ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(eocd.Span[at..]);
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(eocd.Span[at..]);
        if (U16(4) != 0 || U16(6) != 0 || U16(8) != U16(10)) throw Error("multidisk_zip");
        ulong count = U16(10), size = U32(12), start = U32(16);
        var eocdStart = source.Length - tail.Length + offset;
        long directoryEnd = eocdStart;
        if (count == ushort.MaxValue || size == uint.MaxValue || start == uint.MaxValue)
        {
            if (eocdStart < 20) throw Error("zip64_invalid");
            var locator = new byte[20];
            source.Position = eocdStart - 20;
            await source.ReadExactlyAsync(locator, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50
                || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1) throw Error("zip64_invalid");
            var recordAt = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (recordAt > (ulong)Math.Max(0, eocdStart - 20 - 56)) throw Error("zip64_invalid");
            source.Position = (long)recordAt;
            var record = new byte[56];
            await source.ReadExactlyAsync(record, token);
            ulong R64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(at));
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50 || R64(4) < 44
                || BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20)) != 0 || R64(24) != R64(32)) throw Error("zip64_invalid");
            count = R64(32); size = R64(40); start = R64(48);
            directoryEnd = (long)recordAt;
        }
        if (count > (ulong)limits.MaxItems + 1 || size > 128UL * 1024 * 1024 || start > (ulong)directoryEnd || size > (ulong)directoryEnd - start) throw Error("zip_metadata_limit");
        source.Position = 0;
    }
}
