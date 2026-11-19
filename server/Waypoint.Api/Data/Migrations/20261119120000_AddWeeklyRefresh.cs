using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waypoint.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyRefresh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RefreshDay",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RefreshTime",
                table: "Users",
                type: "varchar(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "18:00")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "AutoApplied",
                table: "PlanVersions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ChangesJson",
                table: "PlanVersions",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "RefreshId",
                table: "PlanVersions",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "WeeklyRefreshes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RunAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OpenedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AutoApplyAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyRefreshes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklyRefreshes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyRefreshes_UserId_Status",
                table: "WeeklyRefreshes",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeeklyRefreshes");

            migrationBuilder.DropColumn(
                name: "RefreshDay",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RefreshTime",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AutoApplied",
                table: "PlanVersions");

            migrationBuilder.DropColumn(
                name: "ChangesJson",
                table: "PlanVersions");

            migrationBuilder.DropColumn(
                name: "RefreshId",
                table: "PlanVersions");
        }
    }
}
