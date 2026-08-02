using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdaPanel.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "error_reports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PanelVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OsVersion = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExceptionType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    StackTrace = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    RoutePattern = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_error_reports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_error_reports_fingerprint",
                table: "error_reports",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "ix_error_reports_sent_last_seen",
                table: "error_reports",
                columns: new[] { "SentAtUtc", "LastSeenAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "error_reports");
        }
    }
}
