using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdmLite.Data.Migrations
{
    /// <inheritdoc />
    public partial class ItemGeometry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "area",
                schema: "library",
                table: "items",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "volume",
                schema: "library",
                table: "items",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "area",
                schema: "library",
                table: "items");

            migrationBuilder.DropColumn(
                name: "volume",
                schema: "library",
                table: "items");
        }
    }
}
