using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdaPanel.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogSubscriptionOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SubscriptionUserId",
                table: "audit_logs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_subscription",
                table: "audit_logs",
                column: "SubscriptionUserId");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_user",
                table: "audit_logs",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_logs_subscription",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_user",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "SubscriptionUserId",
                table: "audit_logs");
        }
    }
}
