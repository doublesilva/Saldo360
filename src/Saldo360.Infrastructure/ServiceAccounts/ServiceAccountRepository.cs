using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using Saldo360.Application.ServiceAccounts;
using Saldo360.Domain.ServiceAccounts;
using Saldo360.Infrastructure.Persistence;
using Npgsql;

namespace Saldo360.Infrastructure.ServiceAccounts;

internal sealed class ServiceAccountRepository(Saldo360DbContext db) : IServiceAccountRepository
{
    public async Task<IReadOnlyList<ServiceAccountDto>> ListAsync(Guid userId, CancellationToken ct)
    {
        var accounts = await db.ServiceAccounts.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.Id).ToListAsync(ct);
        var ids = await db.ServiceAccountIdentifiers.AsNoTracking().Where(i => i.UserId == userId).ToListAsync(ct);
        return accounts.Select(a => new ServiceAccountDto(a.Id, a.ServiceType, a.Issuer, a.DisplayName, a.IsActive,
            ids.Where(i => i.ServiceAccountId == a.Id).Select(i => new ServiceAccountIdentifierDto(i.Id, i.Type, i.MaskedValue, i.IsActive)).ToList())).ToList();
    }
    public Task<ServiceAccount?> GetAsync(Guid userId, Guid id, CancellationToken ct) => db.ServiceAccounts.SingleOrDefaultAsync(a => a.UserId == userId && a.Id == id, ct);
    public async Task AddAsync(ServiceAccount account, CancellationToken ct) { db.ServiceAccounts.Add(account); await db.SaveChangesAsync(ct); }
    public async Task<bool> TryAddIdentifierAsync(ServiceAccountIdentifier identifier, CancellationToken ct)
    {
        db.ServiceAccountIdentifiers.Add(identifier);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { db.Entry(identifier).State = EntityState.Detached; return false; }
    }
    public Task<bool> HasActiveAsync(Guid userId, ServiceType type, CancellationToken ct) => db.ServiceAccounts.AnyAsync(a => a.UserId == userId && a.ServiceType == type && a.IsActive, ct);
    public async Task<IReadOnlyList<ServiceAccount>> FindAsync(Guid userId, ServiceType type, IReadOnlyList<(ServiceIdentifierType Type, string Hash)> hashes, CancellationToken ct)
    {
        var values = hashes.Select(h => h.Hash).Distinct().ToList();
        // HMAC includes identifier type, user and service type. The compound FK also enforces that ownership.
        return await (from a in db.ServiceAccounts.AsNoTracking()
                      join i in db.ServiceAccountIdentifiers.AsNoTracking() on a.Id equals i.ServiceAccountId
                      where a.UserId == userId && a.ServiceType == type && a.IsActive && i.UserId == userId && i.ServiceType == type
                        && i.IsActive && i.Type != ServiceIdentifierType.Keyword && values.Contains(i.LookupHash)
                      select a).Distinct().ToListAsync(ct);
    }
}

public sealed class ServiceAccountIdentifierProtection(IDataProtectionProvider provider, IConfiguration configuration) : IServiceAccountIdentifierProtection
{
    public void ValidateEnvironment(string environmentName)
    {
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase) && !IsConfigured)
            throw new InvalidOperationException("Service account identifier protection must be configured before Production startup.");
    }

    private byte[]? ReadKey()
    {
        try
        {
            var file = configuration["ServiceAccounts:IdentifierHmacKeyFile"];
            var raw = string.IsNullOrWhiteSpace(file) ? configuration["ServiceAccounts:IdentifierHmacKey"] : File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var key = Convert.FromBase64String(raw.Trim());
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
            return null;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { return null; }
    }
    public bool IsConfigured
    {
        get { var key = ReadKey(); if (key is null) return false; CryptographicOperations.ZeroMemory(key); return true; }
    }
    public string Hash(Guid userId, ServiceType serviceType, ServiceIdentifierType type, string normalized)
    {
        var key = ReadKey() ?? throw new InvalidOperationException("Proteção de identificadores não configurada.");
        try { return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"v1|{userId:N}|{serviceType}|{type}|{normalized}"))); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public byte[] Protect(Guid userId, string normalized) => provider.CreateProtector("Saldo360.ServiceAccountIdentifiers.v1", userId.ToString("N")).Protect(Encoding.UTF8.GetBytes(normalized));
}
