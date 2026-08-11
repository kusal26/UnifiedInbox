using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/attachments")]
public sealed class AttachmentsController(InMemoryInboxStore store) : ControllerBase
{
    [HttpPost]
    public IActionResult Stage(StageAttachmentRequest request) { if (!Context(out var tenant, out var user)) return Unauthorized(); try { return Ok(store.StageAttachment(tenant, user, request.FileName, request.ContentType, request.Size)); } catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException) { return BadRequest(new { error = ex.Message }); } }
    private bool Context(out Guid tenant, out Guid user) { tenant = HttpContext.Items["tenantId"] is Guid t ? t : default; user = HttpContext.Items["userId"] is Guid u ? u : default; return tenant != default && user != default; }
}
public sealed record StageAttachmentRequest(string FileName, string ContentType, long Size);
