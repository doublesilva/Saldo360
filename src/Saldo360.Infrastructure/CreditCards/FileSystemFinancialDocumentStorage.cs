using Saldo360.Application.CreditCards;
using System.Runtime.InteropServices;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FileSystemFinancialDocumentStorage : IFinancialDocumentStorage
{
    private readonly string root;
    private FileSystemFinancialDocumentStorage(string existingRoot)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(existingRoot));
        if (root == Path.GetPathRoot(root)) throw new IOException("A dedicated storage root is required.");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Storage root must already exist for maintenance.");
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked storage roots are not permitted for maintenance.");
    }
    internal static FileSystemFinancialDocumentStorage ForMaintenance(string existingRoot) => new(existingRoot);
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
    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        var path = Resolve(reference);
        // A lexical prefix check alone must not allow cleanup through a symlink/junction.
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null &&
             (directory.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) || directory.FullName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)); directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Storage cleanup requires a non-linked directory.");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
    private string Resolve(string reference)
    {
        var path = Path.GetFullPath(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Referência de storage inválida.");
        return path;
    }

    internal string ResolveForMaintenance(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || reference.Contains('\\')
            || reference.Contains(':') || reference.Split('/').Any(p => p is "" or "." or ".."))
            throw new IOException("Unsafe storage reference.");
        var path = Resolve(reference); // Same canonical root validation as application storage.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("Storage root escape.");
        for (FileSystemInfo? entry = new FileInfo(path); entry is not null; entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent)
        {
            // LinkTarget also detects a dangling link. Never follow links for maintenance.
            if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Linked storage paths are not permitted for maintenance.");
            if (entry.FullName.Equals(root, comparison)) break;
        }
        return path;
    }

    internal void FlushMaintenanceDirectories(string reference)
    {
        var path = ResolveForMaintenance(reference);
        if (!OperatingSystem.IsLinux()) return;
        FlushLinuxPath(path);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
        {
            FlushLinuxPath(directory.FullName);
            if (directory.FullName.Equals(root, StringComparison.Ordinal)) break;
        }
    }
    private static void FlushLinuxPath(string path)
    {
        var descriptor = OpenDirectory(path, 0); // Linux O_RDONLY; file/parent paths have been validated.
        if (descriptor < 0) throw new IOException("Storage flush open failed.");
        try { if (SyncDirectory(descriptor) != 0) throw new IOException("Storage flush failed."); }
        finally { CloseDirectory(descriptor); }
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int SyncDirectory(int descriptor);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseDirectory(int descriptor);
}
