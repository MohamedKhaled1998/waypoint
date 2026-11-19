using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waypoint.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(WaypointDb))]
    [Migration("20261119180000_AddMessageChanges")]
    public partial class AddMessageChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "IntentJson", table: "ChatMessages", type: "longtext", nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<DateOnly>(name: "RevertOn", table: "PlanVersions", type: "date", nullable: true);
            migrationBuilder.AddColumn<double>(name: "ShareBeforeHours", table: "PlanVersions", type: "double", nullable: true);
            migrationBuilder.AddColumn<double>(name: "ShareAfterHours", table: "PlanVersions", type: "double", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "SourceVersionId", table: "PlanVersions", type: "char(36)", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IntentJson", table: "ChatMessages");
            migrationBuilder.DropColumn(name: "RevertOn", table: "PlanVersions");
            migrationBuilder.DropColumn(name: "ShareBeforeHours", table: "PlanVersions");
            migrationBuilder.DropColumn(name: "ShareAfterHours", table: "PlanVersions");
            migrationBuilder.DropColumn(name: "SourceVersionId", table: "PlanVersions");
        }
    }
}
