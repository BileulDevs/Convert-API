using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConvertApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveJobsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Active",
                table: "Jobs",
                column: "Id",
                filter: "\"Status\" IN ('Queued', 'Processing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_Active",
                table: "Jobs");
        }
    }
}
