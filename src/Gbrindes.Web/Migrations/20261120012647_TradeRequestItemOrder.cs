using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class TradeRequestItemOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "RequestItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                WITH ordered_items AS (
                    SELECT "Id",
                           (ROW_NUMBER() OVER (PARTITION BY "RequestId" ORDER BY "Id") - 1)::integer AS "Position"
                    FROM "RequestItems"
                )
                UPDATE "RequestItems" AS request_item
                SET "Position" = ordered_items."Position"
                FROM ordered_items
                WHERE request_item."Id" = ordered_items."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RequestItems_RequestId_Position",
                table: "RequestItems",
                columns: new[] { "RequestId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RequestItems_RequestId_Position",
                table: "RequestItems");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "RequestItems");
        }
    }
}
