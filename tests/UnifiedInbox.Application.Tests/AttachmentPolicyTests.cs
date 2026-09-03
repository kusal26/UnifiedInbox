using Shouldly;
using UnifiedInbox.Application;

namespace UnifiedInbox.Application.Tests;

public sealed class AttachmentPolicyTests
{
    [Fact]
    public void Ten_megabytes_is_accepted() =>
        AttachmentPolicy.Validate("receipt.pdf", "application/pdf", 10 * 1024 * 1024).FileName.ShouldBe("receipt.pdf");

    [Fact]
    public void More_than_ten_megabytes_is_rejected() =>
        Should.Throw<ArgumentOutOfRangeException>(() => AttachmentPolicy.Validate("video.mp4", "video/mp4", 10 * 1024 * 1024 + 1));

    [Fact]
    public void Paths_are_removed_from_uploaded_file_names() =>
        AttachmentPolicy.Validate("../../receipt.pdf", "application/pdf", 100).FileName.ShouldBe("receipt.pdf");
}
