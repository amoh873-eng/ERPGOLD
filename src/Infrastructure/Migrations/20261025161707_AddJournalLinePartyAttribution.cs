using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERPSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJournalLinePartyAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "JournalEntryLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierId",
                table: "JournalEntryLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_CustomerId",
                table: "JournalEntryLines",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_SupplierId",
                table: "JournalEntryLines",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_JournalEntryLines_Customers_CustomerId",
                table: "JournalEntryLines",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalEntryLines_Suppliers_SupplierId",
                table: "JournalEntryLines",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalEntryLines_Customers_CustomerId",
                table: "JournalEntryLines");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalEntryLines_Suppliers_SupplierId",
                table: "JournalEntryLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntryLines_CustomerId",
                table: "JournalEntryLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntryLines_SupplierId",
                table: "JournalEntryLines");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "JournalEntryLines");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "JournalEntryLines");
        }
    }
}
