using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERPSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Sub2bP3CustomerRefundOnPaymentVoucher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "SupplierId",
                table: "PaymentVouchers",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "PaymentVouchers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalesReturnId",
                table: "PaymentVouchers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SupplierId",
                table: "PaymentVoucherReversals",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "PaymentVoucherReversals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVouchers_Customer_Date",
                table: "PaymentVouchers",
                columns: new[] { "CustomerId", "VoucherDate" },
                filter: "\"CustomerId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVouchers_SalesReturnId",
                table: "PaymentVouchers",
                column: "SalesReturnId",
                filter: "\"CustomerId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pv_customer_requires_return",
                table: "PaymentVouchers",
                sql: "(\"CustomerId\" IS NULL) OR (\"SalesReturnId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pv_party_xor",
                table: "PaymentVouchers",
                sql: "((\"SupplierId\" IS NULL) AND (\"CustomerId\" IS NOT NULL)) OR ((\"SupplierId\" IS NOT NULL) AND (\"CustomerId\" IS NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVoucherReversals_CustomerId",
                table: "PaymentVoucherReversals",
                column: "CustomerId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pvr_party_xor",
                table: "PaymentVoucherReversals",
                sql: "((\"SupplierId\" IS NULL) AND (\"CustomerId\" IS NOT NULL)) OR ((\"SupplierId\" IS NOT NULL) AND (\"CustomerId\" IS NULL))");

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentVoucherReversals_Customers_CustomerId",
                table: "PaymentVoucherReversals",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentVouchers_Customers_CustomerId",
                table: "PaymentVouchers",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentVouchers_SalesReturns_SalesReturnId",
                table: "PaymentVouchers",
                column: "SalesReturnId",
                principalTable: "SalesReturns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // حارس تراجع صريح (SUB-2b/P3): لا SET NOT NULL على SupplierId مع وجود سندات رد عملاء.
            // التراجع بعد وجود ردود يتطلب معالجة/أرشفة السندات بموافقة صريحة — لا حذف تلقائي.
            migrationBuilder.Sql("""
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM "PaymentVouchers" WHERE "CustomerId" IS NOT NULL)
       OR EXISTS (SELECT 1 FROM "PaymentVoucherReversals" WHERE "CustomerId" IS NOT NULL) THEN
        RAISE EXCEPTION 'لا يمكن التراجع عن الهجرة: توجد سندات رد أموال عملاء. عالج/أرشف السندات بموافقة صريحة أولًا.';
    END IF;
END $$;
""");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentVoucherReversals_Customers_CustomerId",
                table: "PaymentVoucherReversals");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentVouchers_Customers_CustomerId",
                table: "PaymentVouchers");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentVouchers_SalesReturns_SalesReturnId",
                table: "PaymentVouchers");

            migrationBuilder.DropIndex(
                name: "IX_PaymentVouchers_Customer_Date",
                table: "PaymentVouchers");

            migrationBuilder.DropIndex(
                name: "IX_PaymentVouchers_SalesReturnId",
                table: "PaymentVouchers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pv_customer_requires_return",
                table: "PaymentVouchers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pv_party_xor",
                table: "PaymentVouchers");

            migrationBuilder.DropIndex(
                name: "IX_PaymentVoucherReversals_CustomerId",
                table: "PaymentVoucherReversals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pvr_party_xor",
                table: "PaymentVoucherReversals");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "PaymentVouchers");

            migrationBuilder.DropColumn(
                name: "SalesReturnId",
                table: "PaymentVouchers");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "PaymentVoucherReversals");

            migrationBuilder.AlterColumn<Guid>(
                name: "SupplierId",
                table: "PaymentVouchers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SupplierId",
                table: "PaymentVoucherReversals",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
