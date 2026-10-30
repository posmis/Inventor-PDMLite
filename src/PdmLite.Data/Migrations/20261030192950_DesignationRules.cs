using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PdmLite.Data.Migrations
{
    /// <inheritdoc />
    public partial class DesignationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "standard_is_manual",
                schema: "library",
                table: "items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "designation_rules",
                schema: "library",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    pattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    replacement = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_regex = table.Column<bool>(type: "boolean", nullable: false),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_designation_rules_sort_order",
                schema: "library",
                table: "designation_rules",
                column: "sort_order");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "designation_rules",
                schema: "library");

            migrationBuilder.DropColumn(
                name: "standard_is_manual",
                schema: "library",
                table: "items");
        }
    }
}
