using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PdmLite.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "library");

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "library",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.ForeignKey(
                        name: "fk_categories_categories_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "library",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "log",
                schema: "library",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    user = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "category_rules",
                schema: "library",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    pattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category_id = table.Column<int>(type: "integer", nullable: false),
                    is_learned = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_category_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_category_rules_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "library",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "items",
                schema: "library",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    document_type = table.Column<int>(type: "integer", nullable: false),
                    designation = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    designation_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    standard = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    manufacturer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    relative_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    file_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    source_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    modified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_items_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "library",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "item_properties",
                schema: "library",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_properties", x => new { x.item_id, x.name });
                    table.ForeignKey(
                        name: "fk_item_properties_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "library",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_references",
                schema: "library",
                columns: table => new
                {
                    assembly_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_references", x => new { x.assembly_id, x.component_id });
                    table.ForeignKey(
                        name: "fk_item_references_items_assembly_id",
                        column: x => x.assembly_id,
                        principalSchema: "library",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_references_items_component_id",
                        column: x => x.component_id,
                        principalSchema: "library",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "item_versions",
                schema: "library",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    relative_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    file_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_item_versions_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "library",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_parent_id_name",
                schema: "library",
                table: "categories",
                columns: new[] { "parent_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_category_rules_category_id",
                schema: "library",
                table: "category_rules",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_category_rules_kind_pattern",
                schema: "library",
                table: "category_rules",
                columns: new[] { "kind", "pattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_references_component_id",
                schema: "library",
                table: "item_references",
                column: "component_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_versions_item_id_version",
                schema: "library",
                table: "item_versions",
                columns: new[] { "item_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_category_id",
                schema: "library",
                table: "items",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_designation_key",
                schema: "library",
                table: "items",
                column: "designation_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_file_hash",
                schema: "library",
                table: "items",
                column: "file_hash");

            migrationBuilder.CreateIndex(
                name: "ix_items_manufacturer",
                schema: "library",
                table: "items",
                column: "manufacturer");

            migrationBuilder.CreateIndex(
                name: "ix_items_relative_path",
                schema: "library",
                table: "items",
                column: "relative_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_source_hash",
                schema: "library",
                table: "items",
                column: "source_hash");

            migrationBuilder.CreateIndex(
                name: "ix_items_standard",
                schema: "library",
                table: "items",
                column: "standard");

            migrationBuilder.CreateIndex(
                name: "ix_log_at",
                schema: "library",
                table: "log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_log_item_id",
                schema: "library",
                table: "log",
                column: "item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "category_rules",
                schema: "library");

            migrationBuilder.DropTable(
                name: "item_properties",
                schema: "library");

            migrationBuilder.DropTable(
                name: "item_references",
                schema: "library");

            migrationBuilder.DropTable(
                name: "item_versions",
                schema: "library");

            migrationBuilder.DropTable(
                name: "log",
                schema: "library");

            migrationBuilder.DropTable(
                name: "items",
                schema: "library");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "library");
        }
    }
}
