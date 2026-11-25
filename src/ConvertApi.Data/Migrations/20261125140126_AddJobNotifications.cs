using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConvertApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NotificationAttempts",
                table: "Jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotificationDueAt",
                table: "Jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_NotificationDueAt",
                table: "Jobs",
                column: "NotificationDueAt",
                filter: "\"NotificationDueAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_NotificationDueAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "NotificationAttempts",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "NotificationDueAt",
                table: "Jobs");
        }
    }
}
