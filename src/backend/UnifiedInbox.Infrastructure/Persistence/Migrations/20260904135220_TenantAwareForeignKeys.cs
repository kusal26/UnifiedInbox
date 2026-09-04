using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnifiedInbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantAwareForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_Channels_ChannelId",
                table: "Conversations");

            migrationBuilder.DropForeignKey(
                name: "FK_InternalNotes_Conversations_ConversationId",
                table: "InternalNotes");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Conversations_ConversationId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_InternalNotes_ConversationId",
                table: "InternalNotes");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_ChannelId",
                table: "Conversations");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Users_TenantId_Id",
                table: "Users",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Messages_TenantId_Id",
                table: "Messages",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Conversations_TenantId_Id",
                table: "Conversations",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Contacts_TenantId_Id",
                table: "Contacts",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Channels_TenantId_Id",
                table: "Channels",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TenantId_UserId",
                table: "RefreshTokens",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_SenderUserId",
                table: "Messages",
                columns: new[] { "TenantId", "SenderUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TenantId_InvitedById",
                table: "Invitations",
                columns: new[] { "TenantId", "InvitedById" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalNotes_TenantId_AuthorId",
                table: "InternalNotes",
                columns: new[] { "TenantId", "AuthorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_ContactId",
                table: "Conversations",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionAttempts_TenantId_ChannelId",
                table: "ConnectionAttempts",
                columns: new[] { "TenantId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionAttempts_TenantId_InitiatingUserId",
                table: "ConnectionAttempts",
                columns: new[] { "TenantId", "InitiatingUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelHealth_TenantId_ChannelId",
                table: "ChannelHealth",
                columns: new[] { "TenantId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelCredentials_TenantId_ChannelId",
                table: "ChannelCredentials",
                columns: new[] { "TenantId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_TenantId_MessageId",
                table: "Attachments",
                columns: new[] { "TenantId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_TenantId_UploaderId",
                table: "Attachments",
                columns: new[] { "TenantId", "UploaderId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Attachments_Messages_TenantId_MessageId",
                table: "Attachments",
                columns: new[] { "TenantId", "MessageId" },
                principalTable: "Messages",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Attachments_Users_TenantId_UploaderId",
                table: "Attachments",
                columns: new[] { "TenantId", "UploaderId" },
                principalTable: "Users",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelCredentials_Channels_TenantId_ChannelId",
                table: "ChannelCredentials",
                columns: new[] { "TenantId", "ChannelId" },
                principalTable: "Channels",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelHealth_Channels_TenantId_ChannelId",
                table: "ChannelHealth",
                columns: new[] { "TenantId", "ChannelId" },
                principalTable: "Channels",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectionAttempts_Channels_TenantId_ChannelId",
                table: "ConnectionAttempts",
                columns: new[] { "TenantId", "ChannelId" },
                principalTable: "Channels",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectionAttempts_Users_TenantId_InitiatingUserId",
                table: "ConnectionAttempts",
                columns: new[] { "TenantId", "InitiatingUserId" },
                principalTable: "Users",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_Channels_TenantId_ChannelId",
                table: "Conversations",
                columns: new[] { "TenantId", "ChannelId" },
                principalTable: "Channels",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_Contacts_TenantId_ContactId",
                table: "Conversations",
                columns: new[] { "TenantId", "ContactId" },
                principalTable: "Contacts",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InternalNotes_Conversations_TenantId_ConversationId",
                table: "InternalNotes",
                columns: new[] { "TenantId", "ConversationId" },
                principalTable: "Conversations",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_InternalNotes_Users_TenantId_AuthorId",
                table: "InternalNotes",
                columns: new[] { "TenantId", "AuthorId" },
                principalTable: "Users",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invitations_Users_TenantId_InvitedById",
                table: "Invitations",
                columns: new[] { "TenantId", "InvitedById" },
                principalTable: "Users",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Channels_TenantId_ChannelId",
                table: "Messages",
                columns: new[] { "TenantId", "ChannelId" },
                principalTable: "Channels",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Conversations_TenantId_ConversationId",
                table: "Messages",
                columns: new[] { "TenantId", "ConversationId" },
                principalTable: "Conversations",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Users_TenantId_SenderUserId",
                table: "Messages",
                columns: new[] { "TenantId", "SenderUserId" },
                principalTable: "Users",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationPreferences_Users_TenantId_UserId",
                table: "NotificationPreferences",
                columns: new[] { "TenantId", "UserId" },
                principalTable: "Users",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RefreshTokens_Users_TenantId_UserId",
                table: "RefreshTokens",
                columns: new[] { "TenantId", "UserId" },
                principalTable: "Users",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Attachments_Messages_TenantId_MessageId",
                table: "Attachments");

            migrationBuilder.DropForeignKey(
                name: "FK_Attachments_Users_TenantId_UploaderId",
                table: "Attachments");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelCredentials_Channels_TenantId_ChannelId",
                table: "ChannelCredentials");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelHealth_Channels_TenantId_ChannelId",
                table: "ChannelHealth");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectionAttempts_Channels_TenantId_ChannelId",
                table: "ConnectionAttempts");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectionAttempts_Users_TenantId_InitiatingUserId",
                table: "ConnectionAttempts");

            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_Channels_TenantId_ChannelId",
                table: "Conversations");

            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_Contacts_TenantId_ContactId",
                table: "Conversations");

            migrationBuilder.DropForeignKey(
                name: "FK_InternalNotes_Conversations_TenantId_ConversationId",
                table: "InternalNotes");

            migrationBuilder.DropForeignKey(
                name: "FK_InternalNotes_Users_TenantId_AuthorId",
                table: "InternalNotes");

            migrationBuilder.DropForeignKey(
                name: "FK_Invitations_Users_TenantId_InvitedById",
                table: "Invitations");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Channels_TenantId_ChannelId",
                table: "Messages");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Conversations_TenantId_ConversationId",
                table: "Messages");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Users_TenantId_SenderUserId",
                table: "Messages");

            migrationBuilder.DropForeignKey(
                name: "FK_NotificationPreferences_Users_TenantId_UserId",
                table: "NotificationPreferences");

            migrationBuilder.DropForeignKey(
                name: "FK_RefreshTokens_Users_TenantId_UserId",
                table: "RefreshTokens");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Users_TenantId_Id",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_TenantId_UserId",
                table: "RefreshTokens");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Messages_TenantId_Id",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_TenantId_SenderUserId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Invitations_TenantId_InvitedById",
                table: "Invitations");

            migrationBuilder.DropIndex(
                name: "IX_InternalNotes_TenantId_AuthorId",
                table: "InternalNotes");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Conversations_TenantId_Id",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_TenantId_ContactId",
                table: "Conversations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Contacts_TenantId_Id",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "IX_ConnectionAttempts_TenantId_ChannelId",
                table: "ConnectionAttempts");

            migrationBuilder.DropIndex(
                name: "IX_ConnectionAttempts_TenantId_InitiatingUserId",
                table: "ConnectionAttempts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Channels_TenantId_Id",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_ChannelHealth_TenantId_ChannelId",
                table: "ChannelHealth");

            migrationBuilder.DropIndex(
                name: "IX_ChannelCredentials_TenantId_ChannelId",
                table: "ChannelCredentials");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_TenantId_MessageId",
                table: "Attachments");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_TenantId_UploaderId",
                table: "Attachments");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId",
                table: "Messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalNotes_ConversationId",
                table: "InternalNotes",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ChannelId",
                table: "Conversations",
                column: "ChannelId");

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_Channels_ChannelId",
                table: "Conversations",
                column: "ChannelId",
                principalTable: "Channels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InternalNotes_Conversations_ConversationId",
                table: "InternalNotes",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Conversations_ConversationId",
                table: "Messages",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
