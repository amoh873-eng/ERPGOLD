using System;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERPSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260919183652_AddItemSalesTax")]
    public partial class AddItemSalesTax : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // نسبة ضريبة المبيعات لكل صنف (تُعدَّل من بطاقة المادة فقط، تُطبَّق على الفواتير الجديدة)
            migrationBuilder.AddColumn<decimal>(
                name: "TaxRate",
                table: "Items",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 0m);

            // نسبة ومبلغ ضريبة المبيعات لكل سطر (لقطة عند إنشاء الفاتورة)
            migrationBuilder.AddColumn<decimal>(
                name: "TaxRate",
                table: "SalesInvoiceLines",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "SalesInvoiceLines",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            // ── تعبئة الصفوف القديمة (الفواتير السابقة تبقى كما هي — مجاميعها لا تُمس) ──
            // نحفظ نسبة الفاتورة على كل سطر ونوزّع مبلغ ضريبة الفاتورة تناسبياً على بنودها،
            // لتبقى تفاصيل الضريبة التاريخية متاحة للمراجعة دون تغيير مجاميع الفاتورة.
            migrationBuilder.Sql(
                @"UPDATE ""SalesInvoiceLines"" AS l
                  SET ""TaxRate"" = i.""TaxRate"",
                      ""TaxAmount"" = ROUND(i.""TaxAmount"" * (l.""LineTotal"" / NULLIF(i.""SubTotal"", 0)), 2)
                  FROM ""SalesInvoices"" AS i
                  WHERE l.""SalesInvoiceId"" = i.""Id"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "TaxRate",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "TaxRate",
                table: "Items");
        }
    }
}
