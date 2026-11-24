using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdaPanel.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiTokenScopeAndIpAllowList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedIps",
                table: "api_tokens",
                type: "TEXT",
                maxLength: 1536,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReadOnly",
                table: "api_tokens",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedIps",
                table: "api_tokens");

            migrationBuilder.DropColumn(
                name: "ReadOnly",
                table: "api_tokens");
        }
    }
}
