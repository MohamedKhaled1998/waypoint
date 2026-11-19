using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waypoint.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(WaypointDb))]
    [Migration("20261119130000_AddGuides")]
    public partial class AddGuides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "GuideJson", table: "Habits", type: "longtext", nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<string>(name: "GuideJson", table: "PlanTasks", type: "longtext", nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<string>(name: "GuideJson", table: "ScheduledActions", type: "longtext", nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<string>(name: "GuideLogJson", table: "PlanVersions", type: "longtext", nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "GuideJson", table: "Habits");
            migrationBuilder.DropColumn(name: "GuideJson", table: "PlanTasks");
            migrationBuilder.DropColumn(name: "GuideJson", table: "ScheduledActions");
            migrationBuilder.DropColumn(name: "GuideLogJson", table: "PlanVersions");
        }
    }
}
