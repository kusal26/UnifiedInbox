using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnifiedInbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseCDurableMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "WebhookReceipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvailableAt",
                table: "WebhookReceipts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "WebhookReceipts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "WebhookReceipts",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "Messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Messages",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "WebhookReceipts");

            migrationBuilder.DropColumn(
                name: "AvailableAt",
                table: "WebhookReceipts");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "WebhookReceipts");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "WebhookReceipts");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Messages");
        }
    }
}
