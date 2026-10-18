namespace Saldo360.Application.CreditCards;

public enum FinancialDocumentAnalysisPipeline { CreditCard, AwaitingExtractor, Unknown }
public interface IFinancialDocumentAnalysisRouter
{
    FinancialDocumentAnalysisPipeline Route(FinancialDocumentClassificationResult classification);
}
public sealed class FinancialDocumentAnalysisRouter : IFinancialDocumentAnalysisRouter
{
    public FinancialDocumentAnalysisPipeline Route(FinancialDocumentClassificationResult classification) => classification.DocumentType switch
    {
        "CreditCardStatement" => FinancialDocumentAnalysisPipeline.CreditCard,
        "ElectricityBill" or "WaterBill" or "BillingDocument" => FinancialDocumentAnalysisPipeline.AwaitingExtractor,
        _ => FinancialDocumentAnalysisPipeline.Unknown,
    };
}
