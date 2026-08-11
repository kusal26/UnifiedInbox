using Shouldly;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Domain.Tests;

public sealed class ConversationTests
{
    [Fact]
    public void Activity_item_keeps_notes_distinct_from_messages() => new ActivityItem(ActivityKind.InternalNote, Guid.NewGuid(), Guid.NewGuid(), "private", DateTimeOffset.UtcNow, 1, null, null).Kind.ShouldBe(ActivityKind.InternalNote);
}
