using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnifiedInbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseDWhatsAppOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalBusinessId",
                table: "Channels",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConnectionAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatingUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StateHash = table.Column<string>(type: "text", nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectionAttempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionAttempts_StateHash",
                table: "ConnectionAttempts",
                column: "StateHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionAttempts_TenantId",
                table: "ConnectionAttempts",
                column: "TenantId");

            // New tenant-scoped table inherits the fail-closed RLS posture.
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation ON "ConnectionAttempts";
                DROP POLICY IF EXISTS tenant_isolation_strict ON "ConnectionAttempts";
                CREATE POLICY tenant_isolation_strict ON "ConnectionAttempts" USING ("TenantId" = NULLIF(current_setting('app.current_tenant', true), '')::uuid) WITH CHECK ("TenantId" = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
                ALTER TABLE "ConnectionAttempts" FORCE ROW LEVEL SECURITY;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectionAttempts");

            migrationBuilder.DropColumn(
                name: "ExternalBusinessId",
                table: "Channels");
        }
    }
}
