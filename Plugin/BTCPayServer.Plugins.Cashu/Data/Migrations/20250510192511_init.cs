using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BTCPayServer.Plugins.Cashu.Data.Migrations
{
    /// <inheritdoc />
    public partial class init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "BTCPayServer.Plugins.Cashu");

            migrationBuilder.CreateTable(
                name: "ExportedTokens",
                schema: "BTCPayServer.Plugins.Cashu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SerializedToken = table.Column<string>(type: "text", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Unit = table.Column<string>(type: "text", nullable: true),
                    Mint = table.Column<string>(type: "text", nullable: true),
                    IsUsed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportedTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FailedTransactions",
                schema: "BTCPayServer.Plugins.Cashu",
                columns: table => new
                {
                    InvoiceId = table.Column<string>(type: "text", nullable: false),
                    MintUrl = table.Column<string>(type: "text", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: true),
                    Unit = table.Column<string>(type: "text", nullable: true),
                    OperationType = table.Column<int>(type: "integer", nullable: false),
                    OutputData_BlindedMessages = table.Column<string>(type: "text", nullable: true),
                    OutputData_Secrets = table.Column<string>(type: "text", nullable: true),
                    OutputData_BlindingFactors = table.Column<string>(type: "text", nullable: true),
                    MeltDetails_MeltQuoteId = table.Column<string>(type: "text", nullable: true),
                    MeltDetails_Expiry = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MeltDetails_LightningInvoiceId = table.Column<string>(type: "text", nullable: true),
                    MeltDetails_Status = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastRetried = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: true),
                    Resolved = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailedTransactions", x => new { x.InvoiceId, x.MintUrl });
                });

            migrationBuilder.CreateTable(
                name: "Mints",
                schema: "BTCPayServer.Plugins.Cashu",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Url = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Proofs",
                schema: "BTCPayServer.Plugins.Cashu",
                columns: table => new
                {
                    ProofId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: true),
                    FailedTransactionInvoiceId = table.Column<string>(type: "text", nullable: true),
                    FailedTransactionMintUrl = table.Column<string>(type: "text", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Id = table.Column<string>(type: "text", nullable: false),
                    Secret = table.Column<string>(type: "text", nullable: false),
                    C = table.Column<string>(type: "text", nullable: false),
                    Witness = table.Column<string>(type: "text", nullable: true),
                    DLEQ = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proofs", x => x.ProofId);
                    table.ForeignKey(
                        name: "FK_Proofs_FailedTransactions_FailedTransactionInvoiceId_Failed~",
                        columns: x => new { x.FailedTransactionInvoiceId, x.FailedTransactionMintUrl },
                        principalSchema: "BTCPayServer.Plugins.Cashu",
                        principalTable: "FailedTransactions",
                        principalColumns: new[] { "InvoiceId", "MintUrl" });
                });

            migrationBuilder.CreateTable(
                name: "MintKeys",
                schema: "BTCPayServer.Plugins.Cashu",
                columns: table => new
                {
                    MintId = table.Column<int>(type: "integer", nullable: false),
                    KeysetId = table.Column<string>(type: "text", nullable: false),
                    Unit = table.Column<string>(type: "text", nullable: true),
                    Keyset = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MintKeys", x => new { x.MintId, x.KeysetId });
                    table.ForeignKey(
                        name: "FK_MintKeys_Mints_MintId",
                        column: x => x.MintId,
                        principalSchema: "BTCPayServer.Plugins.Cashu",
                        principalTable: "Mints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FailedTransactions_InvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "FailedTransactions",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_MintKeys_MintId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "MintKeys",
                column: "MintId");

            migrationBuilder.CreateIndex(
                name: "IX_Proofs_Amount",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                column: "Amount");

            migrationBuilder.CreateIndex(
                name: "IX_Proofs_FailedTransactionInvoiceId_FailedTransactionMintUrl",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                columns: new[] { "FailedTransactionInvoiceId", "FailedTransactionMintUrl" });

            migrationBuilder.CreateIndex(
                name: "IX_Proofs_Id",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Proofs_StoreId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                column: "StoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExportedTokens",
                schema: "BTCPayServer.Plugins.Cashu");

            migrationBuilder.DropTable(
                name: "MintKeys",
                schema: "BTCPayServer.Plugins.Cashu");

            migrationBuilder.DropTable(
                name: "Proofs",
                schema: "BTCPayServer.Plugins.Cashu");

            migrationBuilder.DropTable(
                name: "Mints",
                schema: "BTCPayServer.Plugins.Cashu");

            migrationBuilder.DropTable(
                name: "FailedTransactions",
                schema: "BTCPayServer.Plugins.Cashu");
        }
    }
}
