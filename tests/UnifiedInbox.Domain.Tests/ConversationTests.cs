using Shouldly;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Domain.Tests;

public sealed class ConversationTests
{
    [Fact]
    public void Activity_item_keeps_notes_distinct_from_messages() => new ActivityItem(ActivityKind.InternalNote, Guid.NewGuid(), Guid.NewGuid(), "private", DateTimeOffset.UtcNow, 1, null, null).Kind.ShouldBe(ActivityKind.InternalNote);

    [Fact]
    public void Inbound_activity_reopens_a_closed_conversation()
    {
        var conversation = new Conversation { TenantId = Guid.NewGuid(), ChannelId = Guid.NewGuid(), ContactId = Guid.NewGuid(), Status = ConversationStatus.Closed };

        conversation.RecordInboundActivity(DateTimeOffset.Parse("2026-09-03T10:00:00Z"));

        conversation.Status.ShouldBe(ConversationStatus.Open);
    }

    [Theory]
    [InlineData("Action needed", ConversationStatus.Open)]
    [InlineData("Waiting", ConversationStatus.Pending)]
    [InlineData("Done", ConversationStatus.Closed)]
    public void Ui_labels_map_to_domain_statuses(string label, ConversationStatus expected) =>
        ConversationStatusLabels.Parse(label).ShouldBe(expected);
}
