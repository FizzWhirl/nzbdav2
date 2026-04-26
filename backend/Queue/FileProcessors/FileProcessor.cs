using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Utils;
using Serilog;
using Usenet.Nzb;

namespace NzbWebDAV.Queue.FileProcessors;

public class FileProcessor(
    GetFileInfosStep.FileInfo fileInfo,
    UsenetStreamingClient usenet,
    CancellationToken ct
) : BaseProcessor
{
    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        if (fileInfo.MissingFirstSegment)
        {
            Log.Warning("File {FileName} has missing first segment. Skipping for partial completion", fileInfo.FileName);
            return null;
        }

        try
        {
            return new Result()
            {
                NzbFile = fileInfo.NzbFile,
                FileName = fileInfo.FileName,
                FileSize = fileInfo.FileSize ?? await usenet.GetFileSizeAsync(fileInfo.NzbFile, ct).ConfigureAwait(false),
                ReleaseDate = fileInfo.ReleaseDate,
                SegmentSizes = fileInfo.SegmentSizes,
            };
        }

        catch (UsenetArticleNotFoundException)
        {
            Log.Warning("File {FileName} has missing articles. Skipping for partial completion", fileInfo.FileName);
            return null;
        }
    }

    public new class Result : BaseProcessor.Result
    {
        public required NzbFile NzbFile { get; init; }
        public required string FileName { get; init; }
        public required long FileSize { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }
        public long[]? SegmentSizes { get; set; }
    }
}