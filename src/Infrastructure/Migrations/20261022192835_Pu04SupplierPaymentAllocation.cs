using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERPSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Pu04SupplierPaymentAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefundReference",
                table: "PurchaseReturns",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SettlementType",
                table: "PurchaseReturns",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                table: "PaymentVouchers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentVoucherReversals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReversalNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OriginalPaymentVoucherId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    VoucherDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CashOrBankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayableAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentVoucherReversals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentVoucherReversals_Accounts_CashOrBankAccountId",
                        column: x => x.CashOrBankAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentVoucherReversals_Accounts_PayableAccountId",
                        column: x => x.PayableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentVoucherReversals_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentVoucherReversals_PaymentVouchers_OriginalPaymentVouc~",
                        column: x => x.OriginalPaymentVoucherId,
                        principalTable: "PaymentVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentVoucherReversals_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentVoucherId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseInvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Direction = table.Column<short>(type: "smallint", nullable: false),
                    ReversalOfAllocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AllocatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAllocations", x => x.Id);
                    table.CheckConstraint("ck_alloc_amount_positive", "\"Amount\" > 0");
                    table.CheckConstraint("ck_alloc_direction", "\"Direction\" IN (1, -1)");
                    table.CheckConstraint("ck_alloc_reversal_shape", "((\"Direction\" = -1) = (\"ReversalOfAllocationId\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_PaymentAllocations_ReversalOfAllocationId",
                        column: x => x.ReversalOfAllocationId,
                        principalTable: "PaymentAllocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_PaymentVoucherReversals_ReversalDocument~",
                        column: x => x.ReversalDocumentId,
                        principalTable: "PaymentVoucherReversals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_PaymentVouchers_PaymentVoucherId",
                        column: x => x.PaymentVoucherId,
                        principalTable: "PaymentVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_PurchaseInvoices_PurchaseInvoiceId",
                        column: x => x.PurchaseInvoiceId,
                        principalTable: "PurchaseInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_PaymentVouchers_IdempotencyKey",
                table: "PaymentVouchers",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_Invoice_Direction",
                table: "PaymentAllocations",
                columns: new[] { "PurchaseInvoiceId", "Direction" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_ReversalDocumentId",
                table: "PaymentAllocations",
                column: "ReversalDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_ReversalOf",
                table: "PaymentAllocations",
                column: "ReversalOfAllocationId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_Supplier_Date",
                table: "PaymentAllocations",
                columns: new[] { "SupplierId", "AllocatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_Voucher",
                table: "PaymentAllocations",
                column: "PaymentVoucherId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVoucherReversals_CashOrBankAccountId",
                table: "PaymentVoucherReversals",
                column: "CashOrBankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVoucherReversals_PayableAccountId",
                table: "PaymentVoucherReversals",
                column: "PayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVoucherReversals_SupplierId",
                table: "PaymentVoucherReversals",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "UX_PaymentVoucherReversals_IdempotencyKey",
                table: "PaymentVoucherReversals",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_PaymentVoucherReversals_JournalEntry",
                table: "PaymentVoucherReversals",
                column: "JournalEntryId",
                unique: true,
                filter: "\"JournalEntryId\" IS NOT NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "UX_PaymentVoucherReversals_Number",
                table: "PaymentVoucherReversals",
                column: "ReversalNumber",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "UX_PaymentVoucherReversals_Original",
                table: "PaymentVoucherReversals",
                column: "OriginalPaymentVoucherId",
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentAllocations");

            migrationBuilder.DropTable(
                name: "PaymentVoucherReversals");

            migrationBuilder.DropIndex(
                name: "UX_PaymentVouchers_IdempotencyKey",
                table: "PaymentVouchers");

            migrationBuilder.DropColumn(
                name: "RefundReference",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "SettlementType",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "PaymentVouchers");
        }
    }
}
