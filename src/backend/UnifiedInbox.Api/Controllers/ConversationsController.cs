using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/conversations")]
public sealed class ConversationsController(InMemoryInboxStore store) : ControllerBase
{
    private bool Auth(out Guid tenantId, out Guid userId) { tenantId = HttpContext.Items["tenantId"] is Guid t ? t : default; userId = HttpContext.Items["userId"] is Guid u ? u : default; return tenantId != default && userId != default; }
    [HttpGet]
    public IActionResult List([FromQuery] string? search = null, [FromQuery] ConversationStatus? status = null) { if (!Auth(out var tenant, out _)) return Unauthorized(); var q = store.Conversations.Where(x => x.TenantId == tenant); if (status is not null) q = q.Where(x => x.Status == status); var items = q.Join(store.Contacts, c => c.ContactId, p => p.Id, (c, p) => new ConversationSummary(c.Id, p.DisplayName, p.Platform, store.Messages.Where(m => m.ConversationId == c.Id).OrderByDescending(m => m.Sequence).Select(m => m.Body).FirstOrDefault() ?? "", c.Status, store.Messages.Any(m => m.ConversationId == c.Id && m.Direction == MessageDirection.Inbound && m.Sequence > c.LastReadSequence), c.UpdatedAt)).Where(x => search is null || $"{x.ContactName} {x.Preview} {x.Platform} {x.Id}".Contains(search, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.UpdatedAt); return Ok(items); }
    [HttpGet("{id:guid}")]
    public IActionResult Get(Guid id) { if (!Auth(out var tenant, out _)) return Unauthorized(); var c = store.Conversations.FirstOrDefault(x => x.Id == id && x.TenantId == tenant); return c is null ? NotFound() : Ok(c); }
    [HttpGet("{id:guid}/activity")]
    public IActionResult Activity(Guid id, [FromQuery] long? before = null, [FromQuery] int limit = 50) { if (!Auth(out var tenant, out _)) return Unauthorized(); if (!store.Conversations.Any(x => x.Id == id && x.TenantId == tenant)) return NotFound(); var items = store.Messages.Where(x => x.ConversationId == id && (before is null || x.Sequence < before)).Select(x => new ActivityItem(ActivityKind.Message, x.Id, id, x.Body, x.CreatedAt, x.Sequence, x.SenderUserId, x.Status)).Concat(store.Notes.Where(x => x.ConversationId == id && (before is null || x.Sequence < before)).Select(x => new ActivityItem(ActivityKind.InternalNote, x.Id, id, x.Body, x.CreatedAt, x.Sequence, x.AuthorId, null))).OrderByDescending(x => x.Sequence).Take(Math.Clamp(limit, 1, 100)).ToList(); return Ok(new ActivityResponse(items, items.Count == Math.Clamp(limit, 1, 100) ? items[^1].Sequence.ToString() : null)); }
    [HttpPost("{id:guid}/notes")]
    public IActionResult Note(Guid id, NoteRequest request) { if (!Auth(out var tenant, out var user)) return Unauthorized(); if (!store.Conversations.Any(x => x.Id == id && x.TenantId == tenant)) return NotFound(); return Ok(store.AddNote(tenant, id, user, request.Body)); }
    [HttpPatch("{id:guid}/status")]
    public IActionResult Status(Guid id, StatusRequest request) { if (!Auth(out var tenant, out _)) return Unauthorized(); try { return Ok(store.SetStatus(tenant, id, request.Status)); } catch (InvalidOperationException) { return NotFound(); } }
    [HttpPut("{id:guid}/read")]
    public IActionResult Read(Guid id, ReadRequest request) { if (!Auth(out var tenant, out _)) return Unauthorized(); try { return Ok(store.MarkRead(tenant, id, request.ThroughSequence)); } catch (InvalidOperationException) { return NotFound(); } }
    [HttpPost("{id:guid}/messages")]
    public IActionResult Message(Guid id, MessageRequest request) { if (!Auth(out var tenant, out var user)) return Unauthorized(); if (!Request.Headers.TryGetValue("Idempotency-Key", out var key) || string.IsNullOrWhiteSpace(key)) return BadRequest(new { error = "Idempotency-Key is required" }); try { return Ok(store.AddOutbound(tenant, id, user, request.Body, key.ToString())); } catch (InvalidOperationException) { return NotFound(); } }
}
public sealed record NoteRequest(string Body); public sealed record StatusRequest(ConversationStatus Status); public sealed record ReadRequest(long ThroughSequence); public sealed record MessageRequest(string Body);
