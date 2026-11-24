using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdaPanel.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogCreatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_created_at",
                table: "audit_logs",
                column: "CreatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_logs_created_at",
                table: "audit_logs");
        }
    }
}
