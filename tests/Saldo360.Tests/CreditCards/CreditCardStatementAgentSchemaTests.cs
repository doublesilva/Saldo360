using System.Text.Json;
using System.Text.Json.Nodes;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class CreditCardStatementAgentSchemaTests
{
    internal const string Valid = """
        {"schemaVersion":"2.0","header":{"issuer":"Synthetic Bank","cardLastDigits":"1234","competence":"2026-10","closingDate":null,"dueDate":"2026-10-10","officialTotal":"10.00","cardProduct":null,"holderName":null,"identityEvidence":null,"identityPage":null},
        "items":[{"originalDescription":"SHOP","amount":"10.00","nature":"Debit","type":"Purchase","installmentNumber":null,"installmentCount":null,"section":"CurrentInvoice","includedInCurrentTotal":true,"page":1,"evidence":"SHOP 10.00","confidence":"High","transactionDate":null,"normalizedDescription":null,"suggestedCategory":null,"categoryConfidence":null}]}
        """;

    [Theory]
    [InlineData("{", "SchemaInvalidJson:$")]
    [InlineData("null", "SchemaIncompatibleType:$")]
    [InlineData("[]", "SchemaIncompatibleType:$")]
    [InlineData("{}", "SchemaRequiredFieldMissing:$.schemaVersion")]
    public void InvalidJsonAndRootHaveSafeSpecificCodes(string json, string code) => Reject(json, code);

    [Theory]
    [InlineData("schemaVersion", "3.0", "SchemaVersionInvalid:$.schemaVersion")]
    [InlineData("header.officialTotal", "10,00", "SchemaInvalidMoney:$.header.officialTotal")]
    [InlineData("header.dueDate", "2026-02-30", "SchemaInvalidDate:$.header.dueDate")]
    [InlineData("header.competence", "2026-13", "SchemaInvalidDate:$.header.competence")]
    [InlineData("header.cardLastDigits", "12345", "SchemaInvalidIdentity:$.header.cardLastDigits")]
    [InlineData("header.issuer", "", "SchemaInvalidIdentity:$.header.issuer")]
    [InlineData("items.amount", "-10.00", "SchemaInvalidMoney:$.items[].amount")]
    [InlineData("items.amount", "10000000000000.00", "SchemaInvalidMoney:$.items[].amount")]
    [InlineData("items.amount", "10.0", "SchemaInvalidMoney:$.items[].amount")]
    [InlineData("items.amount", "1e1", "SchemaInvalidMoney:$.items[].amount")]
    [InlineData("items.amount", "010.00", "SchemaInvalidMoney:$.items[].amount")]
    [InlineData("items.transactionDate", "10/10/2026", "SchemaInvalidDate:$.items[].transactionDate")]
    [InlineData("items.nature", "debit", "SchemaInvalidEnum:$.items[].nature")]
    [InlineData("items.type", "InternationalPurchase", "SchemaInvalidEnum:$.items[].type")]
    [InlineData("items.section", "Invoice", "SchemaInvalidEnum:$.items[].section")]
    [InlineData("items.confidence", "Sure", "SchemaInvalidEnum:$.items[].confidence")]
    [InlineData("items.categoryConfidence", "Sure", "SchemaInvalidEnum:$.items[].categoryConfidence")]
    [InlineData("items.evidence", "", "SchemaInvalidEvidence:$.items[].evidence")]
    public void InvalidValuesRemainRejected(string path, string value, string code)
    {
        var node = JsonNode.Parse(Valid)!;
        var parts = path.Split('.');
        var parent = parts.Length == 1 ? node : parts[0] == "items" ? node["items"]![0]! : node["header"]!;
        parent[parts[^1]] = value;
        Reject(node.ToJsonString(), code);
    }

    [Theory]
    [InlineData("includedInCurrentTotal")]
    [InlineData("installmentNumber")]
    [InlineData("transactionDate")]
    [InlineData("page")]
    public void MissingItemFieldsNeverUseClrDefaults(string field)
    {
        var node = JsonNode.Parse(Valid)!; node["items"]![0]!.AsObject().Remove(field);
        Reject(node.ToJsonString(), "SchemaRequiredFieldMissing:$.items[]." + field);
    }

    [Fact]
    public void UnknownPropertiesAndDuplicatesNeverEchoUntrustedNamesOrValues()
    {
        var node = JsonNode.Parse(Valid)!; node["header"]!["private-value-secret"] = "sensitive-response";
        Reject(node.ToJsonString(), "SchemaUnexpectedProperty:$.header");
        Reject(Valid.Replace("\"schemaVersion\":\"2.0\"", "\"schemaVersion\":\"2.0\",\"schemaVersion\":\"2.0\""), "SchemaDuplicateProperty:$");
    }

    [Fact]
    public void IncompatibleDtoTypesNullHeaderAndInvalidEvidenceRemainRejected()
    {
        var node = JsonNode.Parse(Valid)!; node["items"]![0]!["amount"] = 10;
        Reject(node.ToJsonString(), "SchemaDeserializationFailed:$");
        node = JsonNode.Parse(Valid)!; node["header"] = null;
        Reject(node.ToJsonString(), "SchemaIncompatibleType:$.header");
        node = JsonNode.Parse(Valid)!; node["header"]!["identityPage"] = 2;
        Reject(node.ToJsonString(), "SchemaInvalidEvidence:$.header.identityPage");
        node = JsonNode.Parse(Valid)!; node["items"]![0]!["evidence"] = new string('x', 301);
        Reject(node.ToJsonString(), "SchemaInvalidEvidence:$.items[].evidence");
        node = JsonNode.Parse(Valid)!; node["items"]![0]!["installmentNumber"] = 3; node["items"]![0]!["installmentCount"] = 2;
        Reject(node.ToJsonString(), "SchemaInvalidInstallment:$.items[]");
    }

    [Fact]
    public void NullableFactsAreAcceptedButDoNotInventStatementEvidence()
    {
        Assert.True(CreditCardStatementAgentEvaluator.TryEvaluate(Valid, "synthetic.pdf", 1, out var preview, out var error), error);
        Assert.Equal("Reconciled", preview!.Reconciliation.Status); Assert.Equal("10.00", preview.Reconciliation.ComponentsTotal);
        var node = JsonNode.Parse(Valid)!;
        foreach (var field in node["header"]!.AsObject().Select(p => p.Key).ToArray()) node["header"]![field] = null;
        node["items"] = new JsonArray();
        Assert.True(CreditCardStatementAgentSchema.TryValidate(node.ToJsonString(), 1, out _, out _));
        Assert.False(CreditCardStatementAgentEvaluator.TryEvaluate(node.ToJsonString(), "synthetic.pdf", 1, out _, out _));
    }

    [Fact]
    public void LegacyVersionStillAcceptedAndFinancialDifferenceStillRequiresReview()
    {
        var node = JsonNode.Parse(Valid)!; node["schemaVersion"] = "1.0";
        foreach (var field in new[] { "transactionDate", "normalizedDescription", "suggestedCategory", "categoryConfidence" }) node["items"]![0]!.AsObject().Remove(field);
        Assert.True(CreditCardStatementAgentSchema.TryValidate(node.ToJsonString(), 1, out _, out _));
        node["header"]!["officialTotal"] = "11.00";
        Assert.True(CreditCardStatementAgentEvaluator.TryEvaluate(node.ToJsonString(), "synthetic.pdf", 1, out var preview, out _));
        Assert.Equal("Divergent", preview!.Reconciliation.Status);
        Assert.Equal(AgentExtractionAssessment.NeedsReview, CreditCardStatementAgentEvaluator.Assess(preview));
    }

    private static void Reject(string json, string code)
    {
        Assert.False(CreditCardStatementAgentSchema.TryValidate(json, 1, out _, out var error));
        Assert.Equal(code, error); // Exact allowlisted output, never provider values/names/exception text.
    }
}
