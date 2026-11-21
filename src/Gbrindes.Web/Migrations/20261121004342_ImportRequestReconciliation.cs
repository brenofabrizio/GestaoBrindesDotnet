using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class ImportRequestReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportContentHash",
                table: "Requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportIdentityHash",
                table: "Requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requests_RequesterId_ImportIdentityHash",
                table: "Requests",
                columns: new[] { "RequesterId", "ImportIdentityHash" },
                unique: true,
                filter: "\"ImportIdentityHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Requests_RequesterId_ImportIdentityHash",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "ImportContentHash",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "ImportIdentityHash",
                table: "Requests");
        }
    }
}
