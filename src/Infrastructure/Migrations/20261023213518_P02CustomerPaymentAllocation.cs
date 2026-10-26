using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERPSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class P02CustomerPaymentAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerPaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptVoucherId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesInvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Direction = table.Column<short>(type: "smallint", nullable: false),
                    ReversalOfAllocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    AllocatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPaymentAllocations", x => x.Id);
                    table.CheckConstraint("ck_cpa_amount_positive", "\"Amount\" > 0");
                    table.CheckConstraint("ck_cpa_direction", "\"Direction\" IN (1, -1)");
                    table.CheckConstraint("ck_cpa_reversal_shape", "((\"Direction\" = -1) = (\"ReversalOfAllocationId\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocations_CustomerPaymentAllocations_Rever~",
                        column: x => x.ReversalOfAllocationId,
                        principalTable: "CustomerPaymentAllocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocations_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocations_ReceiptVouchers_ReceiptVoucherId",
                        column: x => x.ReceiptVoucherId,
                        principalTable: "ReceiptVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocations_SalesInvoices_SalesInvoiceId",
                        column: x => x.SalesInvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocations_Customer_Date",
                table: "CustomerPaymentAllocations",
                columns: new[] { "CustomerId", "AllocatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocations_Invoice_Direction",
                table: "CustomerPaymentAllocations",
                columns: new[] { "SalesInvoiceId", "Direction" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocations_ReversalOf",
                table: "CustomerPaymentAllocations",
                column: "ReversalOfAllocationId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocations_Voucher",
                table: "CustomerPaymentAllocations",
                column: "ReceiptVoucherId");

            migrationBuilder.CreateIndex(
                name: "UX_CustomerPaymentAllocations_IdempotencyKey",
                table: "CustomerPaymentAllocations",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerPaymentAllocations");
        }
    }
}
