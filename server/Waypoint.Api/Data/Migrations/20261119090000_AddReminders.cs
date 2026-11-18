using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waypoint.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(WaypointDb))]
    [Migration("20261119090000_AddReminders")]
    public partial class AddReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReminderKind",
                table: "ScheduledActions",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "now")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ReminderLeadMinutes",
                table: "ScheduledActions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReminderOn",
                table: "ScheduledActions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ReminderText",
                table: "ScheduledActions",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ReminderKind", table: "ScheduledActions");
            migrationBuilder.DropColumn(name: "ReminderLeadMinutes", table: "ScheduledActions");
            migrationBuilder.DropColumn(name: "ReminderOn", table: "ScheduledActions");
            migrationBuilder.DropColumn(name: "ReminderText", table: "ScheduledActions");
        }
    }
}
