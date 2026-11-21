using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gbrindes.Web.Migrations
{
    /// <inheritdoc />
    public partial class RequestSubmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "Requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StatusChangedAt",
                table: "Requests",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "Requests");
        }
    }
}
