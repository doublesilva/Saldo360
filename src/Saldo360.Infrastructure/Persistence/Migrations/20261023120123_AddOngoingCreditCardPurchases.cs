using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOngoingCreditCardPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "first_pending_competence",
                table: "financial_credit_card_purchases",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "previously_paid_installments",
                table: "financial_credit_card_purchases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_card_purchases_paid_schedule",
                table: "financial_credit_card_purchases",
                sql: "previously_paid_installments >= 0 AND previously_paid_installments < installment_count AND (previously_paid_installments = 0 OR first_pending_competence IS NOT NULL) AND (first_pending_competence IS NULL OR EXTRACT(DAY FROM first_pending_competence) = 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM financial_credit_card_purchases
                        WHERE previously_paid_installments > 0 OR first_pending_competence IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot downgrade purchases with an explicit pending schedule.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_card_purchases_paid_schedule",
                table: "financial_credit_card_purchases");

            migrationBuilder.DropColumn(
                name: "first_pending_competence",
                table: "financial_credit_card_purchases");

            migrationBuilder.DropColumn(
                name: "previously_paid_installments",
                table: "financial_credit_card_purchases");
        }
    }
}
