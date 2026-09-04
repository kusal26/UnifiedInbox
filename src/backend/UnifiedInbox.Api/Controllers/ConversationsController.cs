using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;
using UnifiedInbox.Application.Messaging;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Api.Controllers;

[Authorize, ApiController, Route("api/v1/conversations")]
public sealed class ConversationsController(IInboxService inbox) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] ConversationStatus? status, [FromQuery] string? channel, [FromQuery] bool unreadOnly, [FromQuery] string? cursor, [FromQuery] int pageSize = 30, CancellationToken token = default) { var page = await inbox.ListAsync(search, status, channel, unreadOnly, cursor, pageSize, token); Response.Headers.Append("X-Next-Cursor", page.NextCursor); return Ok(page); }
    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken token) => await inbox.GetAsync(id, token) is { } item ? Ok(item) : NotFound();
    [HttpGet("{id:guid}/activity")] public async Task<IActionResult> Activity(Guid id, [FromQuery] long? before, [FromQuery] int limit = 50, CancellationToken token = default) => await inbox.ActivityAsync(id, before, limit, token) is { } page ? Ok(page) : NotFound();
    [HttpPost("{id:guid}/notes")] public async Task<IActionResult> Note(Guid id, NoteRequest request, CancellationToken token) => await inbox.AddNoteAsync(id, request.Body, token) is { } item ? Created($"/api/v1/conversations/{id}/activity", item) : NotFound();
    [HttpPost("{id:guid}/messages")] public async Task<IActionResult> Message(Guid id, MessageRequest request, CancellationToken token) { if (!Request.Headers.TryGetValue("Idempotency-Key", out var key) || string.IsNullOrWhiteSpace(key)) return BadRequest(Problem(title: "Idempotency-Key is required", statusCode: 400)); var command = new OutboundMessageCommand(request.Body, key.ToString(), request.AttachmentIds, request.Template is { } template ? new OutboundTemplate(template.Name, template.Language, template.Components) : null); return await inbox.SendAsync(id, command, token) is { } item ? Accepted(item) : NotFound(); }
    [HttpPatch("{id:guid}/status")] public async Task<IActionResult> Status(Guid id, StatusRequest request, CancellationToken token) => await inbox.SetStatusAsync(id, request.Status, token) is { } item ? Ok(item) : NotFound();
    [HttpPut("{id:guid}/read")] public async Task<IActionResult> Read(Guid id, ReadRequest request, CancellationToken token) => await inbox.MarkReadAsync(id, request.ThroughSequence, token) is { } item ? Ok(item) : NotFound();
    [HttpPut("{id:guid}/customer-notes")] public async Task<IActionResult> CustomerNotes(Guid id, CustomerNotesRequest request, CancellationToken token) => await inbox.UpdateCustomerNotesAsync(id, request.Notes, token) ? NoContent() : NotFound();
}
public sealed record NoteRequest(string Body);
public sealed record MessageTemplateRequest(string Name, string Language, IReadOnlyList<JsonElement>? Components = null);
public sealed record MessageRequest(string Body, MessageTemplateRequest? Template = null, IReadOnlyList<Guid>? AttachmentIds = null);
public sealed record StatusRequest(ConversationStatus Status); public sealed record ReadRequest(long ThroughSequence); public sealed record CustomerNotesRequest(string? Notes);
