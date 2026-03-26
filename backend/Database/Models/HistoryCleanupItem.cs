namespace NzbWebDAV.Database.Models;

public class HistoryCleanupItem
{
    public Guid Id { get; set; }
    public bool DeleteMountedFiles { get; set; }
    public Guid? DownloadDirId { get; set; }
}
