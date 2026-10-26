using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERPSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PartyOpeningDetailAndAccountingPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "OpeningBalanceLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ItemId",
                table: "OpeningBalanceLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Quantity",
                table: "OpeningBalanceLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StockMovementId",
                table: "OpeningBalanceLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierId",
                table: "OpeningBalanceLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                table: "OpeningBalanceLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WarehouseId",
                table: "OpeningBalanceLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountingPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYear = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ClosedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReopenedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ReopenedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPeriods", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalanceLines_CustomerId",
                table: "OpeningBalanceLines",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalanceLines_ItemId",
                table: "OpeningBalanceLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalanceLines_StockMovementId",
                table: "OpeningBalanceLines",
                column: "StockMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalanceLines_SupplierId",
                table: "OpeningBalanceLines",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalanceLines_WarehouseId",
                table: "OpeningBalanceLines",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPeriods_FiscalYear_Month",
                table: "AccountingPeriods",
                columns: new[] { "FiscalYear", "Month" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPeriods_Status",
                table: "AccountingPeriods",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_OpeningBalanceLines_Customers_CustomerId",
                table: "OpeningBalanceLines",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OpeningBalanceLines_Items_ItemId",
                table: "OpeningBalanceLines",
                column: "ItemId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OpeningBalanceLines_StockMovements_StockMovementId",
                table: "OpeningBalanceLines",
                column: "StockMovementId",
                principalTable: "StockMovements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OpeningBalanceLines_Suppliers_SupplierId",
                table: "OpeningBalanceLines",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OpeningBalanceLines_Warehouses_WarehouseId",
                table: "OpeningBalanceLines",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OpeningBalanceLines_Customers_CustomerId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_OpeningBalanceLines_Items_ItemId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_OpeningBalanceLines_StockMovements_StockMovementId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_OpeningBalanceLines_Suppliers_SupplierId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_OpeningBalanceLines_Warehouses_WarehouseId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropTable(
                name: "AccountingPeriods");

            migrationBuilder.DropIndex(
                name: "IX_OpeningBalanceLines_CustomerId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropIndex(
                name: "IX_OpeningBalanceLines_ItemId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropIndex(
                name: "IX_OpeningBalanceLines_StockMovementId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropIndex(
                name: "IX_OpeningBalanceLines_SupplierId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropIndex(
                name: "IX_OpeningBalanceLines_WarehouseId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropColumn(
                name: "ItemId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "OpeningBalanceLines");

            migrationBuilder.DropColumn(
                name: "StockMovementId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "OpeningBalanceLines");

            migrationBuilder.DropColumn(
                name: "UnitCost",
                table: "OpeningBalanceLines");

            migrationBuilder.DropColumn(
                name: "WarehouseId",
                table: "OpeningBalanceLines");
        }
    }
}
