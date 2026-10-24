using MemoryPack;

namespace NzbWebDAV.Database.Export;

// MemoryPack mirrors of InfiniDysk's streaming-metadata contracts.
//
// These exist because the destination stores per-file metadata twice: the EF row (which we
// export as SQL) *and* a typed blob under `blobs/`. Its reader prefers the blob and silently
// falls back to the EF row when the payload cannot be deserialized
// (`DavDatabaseClient.ReadTypedBlobOrFallbackAsync`), so a mismatch here is a performance
// detail rather than a correctness risk — but the wire format still has to line up.
//
// MemoryPack's version-tolerant format is a leading member-count byte followed by each
// member in `MemoryPackOrder`. It is structural: no type names are written, so a mirror only
// needs the same members, in the same order, with the same types.
//
// Two rules follow, both learned from deserializing our output with InfiniDysk's own types:
//
//   * A *missing* reference-type member deserializes to null, not to the property's
//     initialiser. `Meta.PendingParts` is therefore written explicitly (always empty) rather
//     than omitted, or the destination would see a null collection.
//   * A trailing member the destination defines but we never populate may be omitted: it
//     becomes null, which `VerificationProof` (the only such member) tolerates.

/// <summary>Mirror of InfiniDysk's <c>NzbWebDAV.Models.LongRange</c>.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
internal partial record InfiniDyskLongRangeContract
{
    [MemoryPackOrder(0)] public long StartInclusive { get; set; }
    [MemoryPackOrder(1)] public long EndExclusive { get; set; }
}

/// <summary>Mirror of <c>NzbWebDAV.Models.AesParams</c>.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class InfiniDyskAesParamsContract
{
    [MemoryPackOrder(0)] public long DecodedSize { get; set; }
    [MemoryPackOrder(1)] public byte[] Iv { get; set; } = [];
    [MemoryPackOrder(2)] public byte[] Key { get; set; } = [];
}

/// <summary>Mirror of <c>NzbWebDAV.Database.Models.DavNzbFile</c>.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class InfiniDyskDavNzbFileContract
{
    [MemoryPackOrder(0)] public Guid Id { get; set; }

    [MemoryPackOrder(1)] public string[] SegmentIds { get; set; } = [];

    /// <summary>Absolute byte range of each segment within the NZB file. Null when unknown.</summary>
    [MemoryPackOrder(2)] public InfiniDyskLongRangeContract[]? SegmentByteRanges { get; set; }

    /// <summary>Per-segment alternate message ids, aligned with <see cref="SegmentIds"/>.</summary>
    [MemoryPackOrder(3)] public string[][]? SegmentFallbackIds { get; set; }

    [MemoryPackOrder(4)] public int[]? MissingSegmentIndices { get; set; }

    [MemoryPackOrder(5)] public byte? ContainerClass { get; set; }

    [MemoryPackOrder(6)] public long? CriticalHeadEndExclusive { get; set; }

    [MemoryPackOrder(7)] public int[]? CorruptSegmentIndices { get; set; }

    /// <summary>True only when the ranges above are arithmetically complete (never guessed).</summary>
    [MemoryPackOrder(8)] public bool? SegmentByteRangesTrusted { get; set; }
}

/// <summary>Mirror of <c>NzbWebDAV.Database.Models.DavRarFile</c>.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class InfiniDyskDavRarFileContract
{
    [MemoryPackOrder(0)] public Guid Id { get; set; }

    [MemoryPackOrder(1)] public InfiniDyskRarPartContract[] RarParts { get; set; } = [];
}

[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class InfiniDyskRarPartContract
{
    [MemoryPackOrder(0)] public string[] SegmentIds { get; set; } = [];

    [MemoryPackOrder(1)] public long PartSize { get; set; }

    [MemoryPackOrder(2)] public long Offset { get; set; }

    [MemoryPackOrder(3)] public long ByteCount { get; set; }

    [MemoryPackOrder(4)] public InfiniDyskLongRangeContract[]? SegmentByteRanges { get; set; }

    [MemoryPackOrder(5)] public string[][]? SegmentFallbackIds { get; set; }

    [MemoryPackOrder(6)] public bool? SegmentByteRangesTrusted { get; set; }
}

/// <summary>Mirror of <c>NzbWebDAV.Database.Models.DavMultipartFile</c>.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class InfiniDyskDavMultipartFileContract
{
    [MemoryPackOrder(0)] public Guid Id { get; set; }

    [MemoryPackOrder(1)] public InfiniDyskMultipartMetaContract Metadata { get; set; } = new();
}

[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class InfiniDyskMultipartMetaContract
{
    [MemoryPackOrder(0)] public InfiniDyskAesParamsContract? AesParams { get; set; }

    [MemoryPackOrder(1)] public InfiniDyskFilePartContract[] FileParts { get; set; } = [];

    [MemoryPackOrder(2)] public bool IsLazy { get; set; }

    [MemoryPackOrder(3)] public string? PathInArchive { get; set; }

    [MemoryPackOrder(4)] public string? ArchivePassword { get; set; }

    /// <summary>
    /// Always empty: we export fully-resolved parts. The member is still written because an
    /// omitted reference-type member deserializes to null rather than to `[]`.
    /// </summary>
    [MemoryPackOrder(5)] public InfiniDyskPendingPartContract[] PendingParts { get; set; } = [];

    [MemoryPackOrder(6)] public long? ExpectedFileSize { get; set; }
}

/// <summary>Mirror of <c>DavMultipartFile.PendingPart</c>; never populated, only typed.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class InfiniDyskPendingPartContract
{
    [MemoryPackOrder(0)] public string[] SegmentIds { get; set; } = [];

    [MemoryPackOrder(1)] public InfiniDyskLongRangeContract SegmentIdByteRange { get; set; } = new();

    [MemoryPackOrder(2)] public long EstimatedDataSize { get; set; }

    [MemoryPackOrder(3)] public string[][]? SegmentFallbackIds { get; set; }
}

[MemoryPackable(GenerateType.VersionTolerant)]
internal partial class InfiniDyskFilePartContract
{
    [MemoryPackOrder(0)] public string[] SegmentIds { get; set; } = [];

    // Non-nullable on purpose: InfiniDysk declares these as `LongRange` with a `default!`
    // initialiser, so a null here would deserialize into a broken object rather than a
    // clean failure.
    [MemoryPackOrder(1)] public InfiniDyskLongRangeContract SegmentIdByteRange { get; set; } = new();

    [MemoryPackOrder(2)] public InfiniDyskLongRangeContract FilePartByteRange { get; set; } = new();

    [MemoryPackOrder(3)] public InfiniDyskLongRangeContract[]? SegmentByteRanges { get; set; }

    [MemoryPackOrder(4)] public string[][]? SegmentFallbackIds { get; set; }

    [MemoryPackOrder(5)] public bool? IsSplitAfter { get; set; }

    [MemoryPackOrder(6)] public bool? SegmentByteRangesTrusted { get; set; }
}
