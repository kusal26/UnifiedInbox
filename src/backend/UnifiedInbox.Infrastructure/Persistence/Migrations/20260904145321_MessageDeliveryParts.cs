using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnifiedInbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MessageDeliveryParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Attachments_TenantId_Id",
                table: "Attachments",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "MessageDeliveryParts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateName = table.Column<string>(type: "text", nullable: true),
                    TemplateLanguage = table.Column<string>(type: "text", nullable: true),
                    TemplateComponentsJson = table.Column<string>(type: "text", nullable: true),
                    ExternalMessageId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProviderRequestId = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageDeliveryParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageDeliveryParts_Attachments_TenantId_AttachmentId",
                        columns: x => new { x.TenantId, x.AttachmentId },
                        principalTable: "Attachments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageDeliveryParts_Messages_TenantId_MessageId",
                        columns: x => new { x.TenantId, x.MessageId },
                        principalTable: "Messages",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryParts_TenantId",
                table: "MessageDeliveryParts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryParts_TenantId_AttachmentId",
                table: "MessageDeliveryParts",
                columns: new[] { "TenantId", "AttachmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryParts_TenantId_ExternalMessageId",
                table: "MessageDeliveryParts",
                columns: new[] { "TenantId", "ExternalMessageId" },
                unique: true,
                filter: "\"ExternalMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryParts_TenantId_MessageId_Position",
                table: "MessageDeliveryParts",
                columns: new[] { "TenantId", "MessageId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryParts_TenantId_ProviderRequestId",
                table: "MessageDeliveryParts",
                columns: new[] { "TenantId", "ProviderRequestId" },
                unique: true,
                filter: "\"ProviderRequestId\" IS NOT NULL");

            // New tenant-scoped table inherits the fail-closed RLS posture and the
            // least-privilege app_runtime grants applied by PhaseFAppRoleGrants.
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation ON "MessageDeliveryParts";
                DROP POLICY IF EXISTS tenant_isolation_strict ON "MessageDeliveryParts";
                CREATE POLICY tenant_isolation_strict ON "MessageDeliveryParts" USING ("TenantId" = NULLIF(current_setting('app.current_tenant', true), '')::uuid) WITH CHECK ("TenantId" = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
                ALTER TABLE "MessageDeliveryParts" FORCE ROW LEVEL SECURITY;
                DO $grants$
                BEGIN
                  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_runtime') THEN
                    GRANT SELECT, INSERT, UPDATE, DELETE ON "MessageDeliveryParts" TO app_runtime;
                  END IF;
                END $grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation_strict ON "MessageDeliveryParts";
                DROP POLICY IF EXISTS tenant_isolation ON "MessageDeliveryParts";
                DO $grants$
                BEGIN
                  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_runtime') THEN
                    REVOKE ALL ON "MessageDeliveryParts" FROM app_runtime;
                  END IF;
                END $grants$;
                """);
            migrationBuilder.DropTable(
                name: "MessageDeliveryParts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Attachments_TenantId_Id",
                table: "Attachments");
        }
    }
}
