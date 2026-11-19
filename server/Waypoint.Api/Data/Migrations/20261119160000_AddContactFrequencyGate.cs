using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waypoint.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(WaypointDb))]
    [Migration("20261119160000_AddContactFrequencyGate")]
    public partial class AddContactFrequencyGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "QuietStart", table: "Users", type: "varchar(5)", maxLength: 5, nullable: false, defaultValue: "21:30")
                .Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<string>(name: "QuietEnd", table: "Users", type: "varchar(5)", maxLength: 5, nullable: false, defaultValue: "07:00")
                .Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<bool>(name: "Unprompted", table: "ChatMessages", type: "tinyint(1)", nullable: false, defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "QuietStart", table: "Users");
            migrationBuilder.DropColumn(name: "QuietEnd", table: "Users");
            migrationBuilder.DropColumn(name: "Unprompted", table: "ChatMessages");
        }
    }
}
