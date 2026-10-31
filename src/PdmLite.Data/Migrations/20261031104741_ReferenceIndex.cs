using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PdmLite.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "project");

            migrationBuilder.CreateTable(
                name: "files",
                schema: "project",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    path_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    root_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    modified_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    scanned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    part_number = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "file_references",
                schema: "project",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    file_id = table.Column<long>(type: "bigint", nullable: false),
                    target_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    target_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_references", x => x.id);
                    table.ForeignKey(
                        name: "fk_file_references_files_file_id",
                        column: x => x.file_id,
                        principalSchema: "project",
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_file_references_file_id",
                schema: "project",
                table: "file_references",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_file_references_target_key",
                schema: "project",
                table: "file_references",
                column: "target_key");

            migrationBuilder.CreateIndex(
                name: "ix_files_path_key",
                schema: "project",
                table: "files",
                column: "path_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_files_root_key",
                schema: "project",
                table: "files",
                column: "root_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_references",
                schema: "project");

            migrationBuilder.DropTable(
                name: "files",
                schema: "project");
        }
    }
}
