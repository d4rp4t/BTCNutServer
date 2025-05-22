using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Cashu.Data.Migrations
{
    /// <inheritdoc />
    public partial class FailedTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Proofs_FailedTransactions_FailedTransactionInvoiceId_Failed~",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.DropIndex(
                name: "IX_Proofs_FailedTransactionInvoiceId_FailedTransactionMintUrl",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_FailedTransactions",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "FailedTransactions");

            migrationBuilder.DropColumn(
                name: "FailedTransactionInvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.DropColumn(
                name: "FailedTransactionMintUrl",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.AddColumn<Guid>(
                name: "FailedTransactionId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "MintUrl",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "FailedTransactions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "InvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "FailedTransactions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddPrimaryKey(
                name: "PK_FailedTransactions",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "FailedTransactions",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Proofs_FailedTransactionId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                column: "FailedTransactionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Proofs_FailedTransactions_FailedTransactionId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                column: "FailedTransactionId",
                principalSchema: "BTCPayServer.Plugins.Cashu",
                principalTable: "FailedTransactions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Proofs_FailedTransactions_FailedTransactionId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.DropIndex(
                name: "IX_Proofs_FailedTransactionId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_FailedTransactions",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "FailedTransactions");

            migrationBuilder.DropColumn(
                name: "FailedTransactionId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.AddColumn<string>(
                name: "FailedTransactionInvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailedTransactionMintUrl",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "MintUrl",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "FailedTransactions",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "InvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "FailedTransactions",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_FailedTransactions",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "FailedTransactions",
                columns: new[] { "InvoiceId", "MintUrl" });

            migrationBuilder.CreateIndex(
                name: "IX_Proofs_FailedTransactionInvoiceId_FailedTransactionMintUrl",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                columns: new[] { "FailedTransactionInvoiceId", "FailedTransactionMintUrl" });

            migrationBuilder.AddForeignKey(
                name: "FK_Proofs_FailedTransactions_FailedTransactionInvoiceId_Failed~",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                columns: new[] { "FailedTransactionInvoiceId", "FailedTransactionMintUrl" },
                principalSchema: "BTCPayServer.Plugins.Cashu",
                principalTable: "FailedTransactions",
                principalColumns: new[] { "InvoiceId", "MintUrl" });
        }
    }
}
