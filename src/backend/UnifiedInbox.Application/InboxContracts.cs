using UnifiedInbox.Domain;

namespace UnifiedInbox.Application;

public sealed record ActivityResponse(IReadOnlyList<ActivityItem> Items, string? NextCursor);
public sealed record ConversationSummary(Guid Id, string ContactName, string Platform, string Preview, ConversationStatus Status, bool Unread, DateTimeOffset UpdatedAt);
