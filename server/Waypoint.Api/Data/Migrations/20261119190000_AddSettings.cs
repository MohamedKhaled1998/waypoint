using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waypoint.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(WaypointDb))]
    [Migration("20261119190000_AddSettings")]
    public partial class AddSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(name: "RequestedTimeBudgetHours", table: "Users", type: "double", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "ExactAlarmsAllowed", table: "Users", type: "tinyint(1)", nullable: true);
            migrationBuilder.CreateTable(
                name: "SlotEdits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SlotId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Label = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                    Days = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                    Start = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                    End = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotEdits", x => x.Id);
                    table.ForeignKey("FK_SlotEdits_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.CreateIndex(name: "IX_SlotEdits_UserId", table: "SlotEdits", column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SlotEdits");
            migrationBuilder.DropColumn(name: "RequestedTimeBudgetHours", table: "Users");
            migrationBuilder.DropColumn(name: "ExactAlarmsAllowed", table: "Users");
        }
    }
}
