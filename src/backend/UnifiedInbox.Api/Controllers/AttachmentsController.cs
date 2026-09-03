using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;

namespace UnifiedInbox.Api.Controllers;

[Authorize, ApiController, Route("api/v1/attachments")]
public sealed class AttachmentsController(IAttachmentService attachments) : ControllerBase
{
    [HttpPost] public async Task<IActionResult> Stage(StageAttachmentRequest request, CancellationToken token) => Ok(await attachments.StageAsync(request.FileName, request.ContentType, request.Size, token));
}
public sealed record StageAttachmentRequest(string FileName, string ContentType, long Size);
