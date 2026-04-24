using MemoryPack;

namespace NzbWebDAV.Database.BlobStoreCompat;

// MemoryPack contracts that exactly mirror upstream nzbdav-dev's BlobStore-era schema.
// Used ONLY to deserialize migrated v1 (upstream blobstore-era) blob files; never written.
//
// These are intentionally separate POCOs from the fork's own DavNzbFile/DavRarFile/
// DavMultipartFile models because:
//   1. Upstream's contracts have fewer fields (this fork added SegmentSizes, SegmentFallbacks,
//      ObfuscationKey, etc.) and MemoryPack is field-position-sensitive — adding fields to the
//      fork's models would break direct deserialization of upstream blobs.
//   2. Keeping the upstream contract isolated lets us re-derive missing fields (e.g. cached
//      segment sizes) on demand later without polluting the production model.
//
// Source contracts captured from:
//   https://github.com/nzbdav-dev/nzbdav (BlobStore-era commits, see docs/upstream-sync-2026-03-10.md)

[MemoryPackable]
internal partial class UpstreamDavNzbFile
{
    [MemoryPackOrder(0)] public Guid Id { get; set; }
    [MemoryPackOrder(1)] public string[] SegmentIds { get; set; } = [];
}

[MemoryPackable]
internal partial class UpstreamDavRarFile
{
    [MemoryPackOrder(0)] public Guid Id { get; set; }
    [MemoryPackOrder(1)] public UpstreamRarPart[] RarParts { get; set; } = [];
}

[MemoryPackable]
internal partial class UpstreamRarPart
{
    [MemoryPackOrder(0)] public string[] SegmentIds { get; set; } = [];
    [MemoryPackOrder(1)] public long PartSize { get; set; }
    [MemoryPackOrder(2)] public long Offset { get; set; }
    [MemoryPackOrder(3)] public long ByteCount { get; set; }
}

[MemoryPackable]
internal partial class UpstreamDavMultipartFile
{
    [MemoryPackOrder(0)] public Guid Id { get; set; }
    [MemoryPackOrder(1)] public UpstreamMultipartMeta Metadata { get; set; } = new();
}

[MemoryPackable]
internal partial class UpstreamMultipartMeta
{
    [MemoryPackOrder(0)] public UpstreamAesParams? AesParams { get; set; }
    [MemoryPackOrder(1)] public UpstreamFilePart[] FileParts { get; set; } = [];
}

[MemoryPackable]
internal partial class UpstreamFilePart
{
    [MemoryPackOrder(0)] public string[] SegmentIds { get; set; } = [];
    [MemoryPackOrder(1)] public UpstreamLongRange SegmentIdByteRange { get; set; }
    [MemoryPackOrder(2)] public UpstreamLongRange FilePartByteRange { get; set; }
}

[MemoryPackable]
internal partial struct UpstreamLongRange
{
    [MemoryPackOrder(0)] public long StartInclusive { get; set; }
    [MemoryPackOrder(1)] public long EndExclusive { get; set; }
}

[MemoryPackable]
internal partial class UpstreamAesParams
{
    [MemoryPackOrder(0)] public long DecodedSize { get; set; }
    [MemoryPackOrder(1)] public byte[] Iv { get; set; } = [];
    [MemoryPackOrder(2)] public byte[] Key { get; set; } = [];
}
