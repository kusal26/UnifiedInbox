using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;

namespace UnifiedInbox.Api.Controllers;

[Authorize, ApiController, Route("api/v1/attachments")]
public sealed class AttachmentsController(IAttachmentService attachments) : ControllerBase
{
    [HttpPost("staging")] public async Task<IActionResult> Stage(StageAttachmentRequest request, CancellationToken token) => Ok(await attachments.StageAsync(request.FileName, request.ContentType, request.Size, token));
    [HttpPost] public async Task<IActionResult> StageLegacy(StageAttachmentRequest request, CancellationToken token) => Ok(await attachments.StageAsync(request.FileName, request.ContentType, request.Size, token));
    [HttpPost("{id:guid}/complete")] public async Task<IActionResult> Complete(Guid id, CancellationToken token) => await attachments.CompleteAsync(id, token) ? Ok(new { completed = true }) : NotFound();
    [HttpGet("{id:guid}/download")] public async Task<IActionResult> Download(Guid id, CancellationToken token) => await attachments.DownloadAsync(id, token) is { } file ? File(file.Content, file.ContentType, file.FileName) : NotFound();
}
public sealed record StageAttachmentRequest(string FileName, string ContentType, long Size);
