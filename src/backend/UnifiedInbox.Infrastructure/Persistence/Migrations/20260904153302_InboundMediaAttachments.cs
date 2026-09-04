using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnifiedInbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InboundMediaAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Attachments_TenantId_MessageId",
                table: "Attachments");

            migrationBuilder.AlterColumn<Guid>(
                name: "UploaderId",
                table: "Attachments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "ProviderMediaId",
                table: "Attachments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_TenantId_MessageId_ProviderMediaId",
                table: "Attachments",
                columns: new[] { "TenantId", "MessageId", "ProviderMediaId" },
                unique: true,
                filter: "\"ProviderMediaId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Attachments_TenantId_MessageId_ProviderMediaId",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "ProviderMediaId",
                table: "Attachments");

            migrationBuilder.AlterColumn<Guid>(
                name: "UploaderId",
                table: "Attachments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_TenantId_MessageId",
                table: "Attachments",
                columns: new[] { "TenantId", "MessageId" });
        }
    }
}
