using Microsoft.Extensions.Configuration;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
public sealed class FinancialDocumentStorageTests
{
    [Fact]
    public async Task NewDocument_UsesWorkspaceNamespace_PreservesBytes_AndRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "saldo360-workspace-storage-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FinancialDocuments:StoragePath"] = root }).Build();
        var storage = new FileSystemFinancialDocumentStorage(config);
        var workspace = Guid.CreateVersion7();
        var document = Guid.CreateVersion7();
        var bytes = "%PDF-synthetic-fixture"u8.ToArray();
        try
        {
            var reference = await storage.SaveAsync(workspace, document, new MemoryStream(bytes), default);
            Assert.Equal($"workspaces/{workspace:N}/{document:N}.pdf", reference);
            await using var read = await storage.OpenReadAsync(reference, default);
            Assert.NotNull(read);
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.OpenReadAsync("../outside.pdf", default));
        }
        finally
        {
            // Only the GUID-named directory allocated by this test, never the temp root.
            Directory.Delete(root, recursive: true);
        }
    }
}
