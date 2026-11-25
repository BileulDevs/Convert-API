using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConvertApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_OwnerId_CreatedAt",
                table: "Jobs");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ExpiresAt",
                table: "Jobs",
                column: "ExpiresAt",
                filter: "\"Status\" = 'Succeeded'");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_OwnerId_Id",
                table: "Jobs",
                columns: new[] { "OwnerId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_ExpiresAt",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_OwnerId_Id",
                table: "Jobs");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_OwnerId_CreatedAt",
                table: "Jobs",
                columns: new[] { "OwnerId", "CreatedAt" });
        }
    }
}
