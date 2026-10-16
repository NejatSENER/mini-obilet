using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniObilet.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Ilk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Seferler",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Firma = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kalkis = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Varis = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    KalkisZamani = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Fiyat = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    KoltukSayisi = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Seferler", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Biletler",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Pnr = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    SeferId = table.Column<int>(type: "int", nullable: false),
                    KoltukNo = table.Column<int>(type: "int", nullable: false),
                    YolcuAdi = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Eposta = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Iptal = table.Column<bool>(type: "bit", nullable: false),
                    OlusturmaZamani = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Biletler", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Biletler_Seferler_SeferId",
                        column: x => x.SeferId,
                        principalTable: "Seferler",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Biletler_Pnr",
                table: "Biletler",
                column: "Pnr",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Biletler_SeferId_KoltukNo",
                table: "Biletler",
                columns: new[] { "SeferId", "KoltukNo" },
                unique: true,
                filter: "[Iptal] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Seferler_Kalkis_Varis_KalkisZamani",
                table: "Seferler",
                columns: new[] { "Kalkis", "Varis", "KalkisZamani" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Biletler");

            migrationBuilder.DropTable(
                name: "Seferler");
        }
    }
}
