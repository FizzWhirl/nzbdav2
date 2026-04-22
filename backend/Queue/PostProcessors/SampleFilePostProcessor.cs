using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.PostProcessors;

public class SampleFilePostProcessor(DavDatabaseClient dbClient)
{
    public void RemoveSampleFiles()
    {
        var sampleFiles = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .Where(x => FilenameUtil.IsSampleFileName(x.Name))
            .ToList();

        foreach (var sampleFile in sampleFiles)
            RemoveSampleFile(sampleFile);

        if (sampleFiles.Count > 0)
        {
            Log.Information("[QueueItemProcessor] Filtered {Count} sample file(s) from queued item", sampleFiles.Count);
        }
    }

    private void RemoveSampleFile(DavItem davItem)
    {
        if (davItem.Type == DavItem.ItemType.NzbFile)
        {
            var file = dbClient.Ctx.ChangeTracker.Entries<DavNzbFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .First(x => x.Id == davItem.Id);
            dbClient.Ctx.NzbFiles.Remove(file);
        }

        else if (davItem.Type == DavItem.ItemType.RarFile)
        {
            var file = dbClient.Ctx.ChangeTracker.Entries<DavRarFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .First(x => x.Id == davItem.Id);
            dbClient.Ctx.RarFiles.Remove(file);
        }

        else if (davItem.Type == DavItem.ItemType.MultipartFile)
        {
            var file = dbClient.Ctx.ChangeTracker.Entries<DavMultipartFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .First(x => x.Id == davItem.Id);
            dbClient.Ctx.MultipartFiles.Remove(file);
        }

        else
        {
            Log.Error("Error filtering sample files from downloading.");
            return;
        }

        dbClient.Ctx.Items.Remove(davItem);
    }
}