using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDebtInstallmentCategoryForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_financial_debt_installments_workspace_id_category_id",
                table: "financial_debt_installments",
                columns: new[] { "workspace_id", "category_id" });

            // NOT VALID: vale para toda gravação nova (e para exclusões), sem varrer nem falhar por linhas antigas e sem alterar nenhum dado.
            // Como a coluna nunca teve chave estrangeira, um id órfão antigo (se existir) não derruba o deploy; a validação plena é um passo à parte.
            migrationBuilder.Sql("""
                ALTER TABLE financial_debt_installments
                ADD CONSTRAINT fk_debt_installments_categories_same_workspace
                FOREIGN KEY (workspace_id, category_id) REFERENCES financial_categories (workspace_id, id) ON DELETE RESTRICT NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_debt_installments_categories_same_workspace",
                table: "financial_debt_installments");

            migrationBuilder.DropIndex(
                name: "ix_financial_debt_installments_workspace_id_category_id",
                table: "financial_debt_installments");
        }
    }
}
