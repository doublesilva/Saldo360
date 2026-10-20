using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FileSystemFinancialDocumentStorage : IFinancialDocumentStorage
{
    private readonly string root;
    public FileSystemFinancialDocumentStorage(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        root = Path.GetFullPath(configuration["FinancialDocuments:StoragePath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "financial-documents"));
        Directory.CreateDirectory(root);
    }

    public async Task<string> SaveAsync(Guid workspaceId, Guid documentId, Stream content, CancellationToken cancellationToken)
    {
        var reference = $"workspaces/{workspaceId:N}/{documentId:N}.pdf";
        var target = Resolve(reference); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await content.CopyToAsync(output, cancellationToken);
            File.Move(temporary, target, false);
            return reference;
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }

    public Task<Stream?> OpenReadAsync(string reference, CancellationToken cancellationToken)
    {
        var path = Resolve(reference);
        return Task.FromResult<Stream?>(File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous) : null);
    }
    public Task DeleteAsync(string reference, CancellationToken cancellationToken) { var path = Resolve(reference); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
    private string Resolve(string reference)
    {
        var path = Path.GetFullPath(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Referência de storage inválida.");
        return path;
    }
}
