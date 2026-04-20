using Microsoft.AspNetCore.StaticFiles;

namespace NzbWebDAV.Utils;

public static class ContentTypeUtil
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider;

    static ContentTypeUtil()
    {
        ContentTypeProvider = new FileExtensionContentTypeProvider();
        ContentTypeProvider.Mappings[".flac"] = "audio/flac";
        ContentTypeProvider.Mappings[".mkv"] = "video/x-matroska";
        ContentTypeProvider.Mappings[".mkv2"] = "video/x-matroska";
        ContentTypeProvider.Mappings[".mk3d"] = "video/x-matroska";
        ContentTypeProvider.Mappings[".mka"] = "audio/x-matroska";
    }

    public static string GetContentType(string fileName)
    {
        return !ContentTypeProvider.TryGetContentType(Path.GetFileName(fileName), out var contentType)
            ? "application/octet-stream"
            : contentType;
    }
}
