using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnifiedInbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenedTenantIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FamilyId",
                table: "RefreshTokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "Invitations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "InvitedById",
                table: "Invitations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAt",
                table: "Invitations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelHealth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsHealthy = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelHealth", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ProviderAssetId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderRoutes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VerificationTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationTokens", x => x.Id);
                });

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

            migrationBuilder.CreateIndex(
                name: "IX_ChannelHealth_TenantId",
                table: "ChannelHealth",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderRoutes_Provider_ProviderAssetId",
                table: "ProviderRoutes",
                columns: new[] { "Provider", "ProviderAssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VerificationTokens_TenantId",
                table: "VerificationTokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationTokens_TokenHash",
                table: "VerificationTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_FamilyId",
                table: "RefreshTokens",
                column: "FamilyId");

            // Backfill non-nullable columns for pre-existing rows.
            migrationBuilder.Sql("UPDATE \"RefreshTokens\" SET \"FamilyId\" = gen_random_uuid() WHERE \"FamilyId\" = '00000000-0000-0000-0000-000000000000';");
            migrationBuilder.Sql("UPDATE \"Invitations\" SET \"CreatedAt\" = now() WHERE \"CreatedAt\" = '0001-01-01 00:00:00+00';");

            // Replace the permissive tenant policy (which allowed an unset tenant) with a
            // fail-closed policy: an unset app.current_tenant matches no rows. FORCE ROW
            // LEVEL SECURITY extends enforcement to table owners as well.
            migrationBuilder.Sql("""
                DO $rls$
                DECLARE table_name text;
                BEGIN
                  FOREACH table_name IN ARRAY ARRAY['Attachments','AuditEntries','CannedResponses','ChannelCredentials','ChannelHealth','Channels','Contacts','Conversations','InternalNotes','Invitations','Messages','Notifications','Outbox','RefreshTokens','Users','VerificationTokens','WebhookReceipts']
                  LOOP
                    EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON %I', table_name);
                    EXECUTE format('DROP POLICY IF EXISTS tenant_isolation_strict ON %I', table_name);
                    EXECUTE format('CREATE POLICY tenant_isolation_strict ON %I USING ("TenantId" = NULLIF(current_setting(''app.current_tenant'', true), '''')::uuid) WITH CHECK ("TenantId" = NULLIF(current_setting(''app.current_tenant'', true), '''')::uuid)', table_name);
                    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', table_name);
                  END LOOP;
                END $rls$;
                """);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

            migrationBuilder.DropTable(
                name: "ChannelHealth");

            migrationBuilder.DropTable(
                name: "ProviderRoutes");

            migrationBuilder.DropTable(
                name: "VerificationTokens");

            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId",
                table: "Messages");
            migrationBuilder.DropIndex(name: "IX_VerificationTokens_TokenHash", table: "VerificationTokens");
            migrationBuilder.DropIndex(name: "IX_RefreshTokens_FamilyId", table: "RefreshTokens");
            migrationBuilder.Sql("""
                DO $rls$
                DECLARE table_name text;
                BEGIN
                  FOREACH table_name IN ARRAY ARRAY['Attachments','AuditEntries','CannedResponses','ChannelCredentials','Channels','Contacts','Conversations','InternalNotes','Invitations','Messages','Notifications','Outbox','RefreshTokens','Users','WebhookReceipts']
                  LOOP
                    EXECUTE format('DROP POLICY IF EXISTS tenant_isolation_strict ON %I', table_name);
                    EXECUTE format('CREATE POLICY tenant_isolation ON %I USING (NULLIF(current_setting(''app.current_tenant'', true), '''') IS NULL OR "TenantId" = NULLIF(current_setting(''app.current_tenant'', true), '''')::uuid) WITH CHECK (NULLIF(current_setting(''app.current_tenant'', true), '''') IS NULL OR "TenantId" = NULLIF(current_setting(''app.current_tenant'', true), '''')::uuid)', table_name);
                    EXECUTE format('ALTER TABLE %I NO FORCE ROW LEVEL SECURITY', table_name);
                  END LOOP;
                END $rls$;
                """);            migrationBuilder.DropIndex(
                name: "IX_InternalNotes_ConversationId",
                table: "InternalNotes");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_ChannelId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Invitations");

            migrationBuilder.DropColumn(
                name: "InvitedById",
                table: "Invitations");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "Invitations");
        }
    }
}
