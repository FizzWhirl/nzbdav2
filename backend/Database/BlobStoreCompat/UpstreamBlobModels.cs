using MemoryPack;

namespace NzbWebDAV.Database.BlobStoreCompat;

// MemoryPack contracts that exactly mirror upstream nzbdav-dev's BlobStore-era schema.
// Used ONLY to deserialize migrated v1 (upstream blobstore-era) blob files; never written.
//
// CRITICAL: Every type here MUST use [MemoryPackable(GenerateType.VersionTolerant)] because
// upstream's BlobStore.WriteBlob<T> serialises using the VersionTolerant format. The default
// GenerateType.Object format has a completely different on-the-wire layout and produces the
// "property count is 2 but binary's header marked as N" error from MemoryPack. LongRange is a
// `partial record` (reference type) NOT a struct — upstream models it that way and the binary
// layout differs between struct and class for VersionTolerant.
//
// Source contracts captured from:
//   https://github.com/nzbdav-dev/nzbdav (BlobStore-era commits, see docs/upstream-sync-2026-03-10.md)

[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class UpstreamDavNzbFile
{
    [MemoryPackOrder(0)] public Guid Id { get; set; }
    [MemoryPackOrder(1)] public string[] SegmentIds { get; set; } = [];
}

[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class UpstreamDavRarFile
{
    [MemoryPackOrder(0)] public Guid Id { get; set; }
    [MemoryPackOrder(1)] public UpstreamRarPart[] RarParts { get; set; } = [];
}

[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class UpstreamRarPart
{
    [MemoryPackOrder(0)] public string[] SegmentIds { get; set; } = [];
    [MemoryPackOrder(1)] public long PartSize { get; set; }
    [MemoryPackOrder(2)] public long Offset { get; set; }
    [MemoryPackOrder(3)] public long ByteCount { get; set; }
}

[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class UpstreamDavMultipartFile
{
    [MemoryPackOrder(0)] public Guid Id { get; set; }
    [MemoryPackOrder(1)] public UpstreamMultipartMeta Metadata { get; set; } = new();
}

[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class UpstreamMultipartMeta
{
    [MemoryPackOrder(0)] public UpstreamAesParams? AesParams { get; set; }
    [MemoryPackOrder(1)] public UpstreamFilePart[] FileParts { get; set; } = [];
}

[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class UpstreamFilePart
{
    [MemoryPackOrder(0)] public string[] SegmentIds { get; set; } = [];
    [MemoryPackOrder(1)] public UpstreamLongRange SegmentIdByteRange { get; set; } = new();
    [MemoryPackOrder(2)] public UpstreamLongRange FilePartByteRange { get; set; } = new();
}

// LongRange is a partial RECORD (reference type) in upstream — MemoryPack VersionTolerant
// has a different layout for structs vs classes/records, and upstream uses the class layout.
[MemoryPackable(GenerateType.VersionTolerant)]
internal partial record UpstreamLongRange
{
    [MemoryPackOrder(0)] public long StartInclusive { get; set; }
    [MemoryPackOrder(1)] public long EndExclusive { get; set; }
}

[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class UpstreamAesParams
{
    [MemoryPackOrder(0)] public long DecodedSize { get; set; }
    [MemoryPackOrder(1)] public byte[] Iv { get; set; } = [];
    [MemoryPackOrder(2)] public byte[] Key { get; set; } = [];
}
