using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnifiedInbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseBAdminGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreferences_TenantId",
                table: "NotificationPreferences",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreferences_TenantId_UserId_Kind",
                table: "NotificationPreferences",
                columns: new[] { "TenantId", "UserId", "Kind" },
                unique: true);

            // Backfill the new non-nullable column for pre-existing rows.
            migrationBuilder.Sql("UPDATE \"Conversations\" SET \"CreatedAt\" = \"UpdatedAt\" WHERE \"CreatedAt\" = '0001-01-01 00:00:00+00';");

            // New tenant-scoped table inherits the fail-closed RLS posture.
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation ON "NotificationPreferences";
                DROP POLICY IF EXISTS tenant_isolation_strict ON "NotificationPreferences";
                CREATE POLICY tenant_isolation_strict ON "NotificationPreferences" USING ("TenantId" = NULLIF(current_setting('app.current_tenant', true), '')::uuid) WITH CHECK ("TenantId" = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
                ALTER TABLE "NotificationPreferences" FORCE ROW LEVEL SECURITY;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Conversations");
        }
    }
}
