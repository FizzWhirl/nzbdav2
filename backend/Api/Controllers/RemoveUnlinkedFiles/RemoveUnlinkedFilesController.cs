using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.Controllers.RemoveUnlinkedFiles;

[ApiController]
[Route("api/remove-unlinked-files")]
public class RemoveUnlinkedFilesController(
    ConfigManager configManager,
    IServiceScopeFactory scopeFactory,
    WebsocketManager websocketManager
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var task = new RemoveUnlinkedFilesTask(configManager, scopeFactory, websocketManager, isDryRun: false);
        await task.Execute();
        return Ok(new RemoveUnlinkedFilesResponse(RemoveUnlinkedFilesTask.GetAuditReport()));
    }
}

public class RemoveUnlinkedFilesResponse(string report) : BaseApiResponse
{
    public string Report { get; } = report;
}
