using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class TradeRequestPublicCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicCode",
                table: "Requests",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.Sql("UPDATE \"Requests\" SET \"PublicCode\" = \"Code\" WHERE \"Flow\" = 'trade'");
            migrationBuilder.Sql("""
                INSERT INTO "Sequences" ("Name", "Period", "LastValue")
                SELECT 'trade', substring("PublicCode" from 5 for 4), max(split_part("PublicCode", '-', 3)::integer)
                FROM "Requests"
                WHERE "Flow" = 'trade' AND "PublicCode" ~ '^EME-[0-9]{4}-[0-9]{6,}$'
                GROUP BY substring("PublicCode" from 5 for 4)
                ON CONFLICT ("Name", "Period") DO UPDATE
                SET "LastValue" = GREATEST("Sequences"."LastValue", EXCLUDED."LastValue")
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Requests_PublicCode",
                table: "Requests",
                column: "PublicCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Requests_PublicCode",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "PublicCode",
                table: "Requests");
        }
    }
}
