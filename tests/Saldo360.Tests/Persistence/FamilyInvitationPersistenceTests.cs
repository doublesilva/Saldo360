using System.Buffers.Text;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Saldo360.Application.Families;
using Saldo360.Domain.Families;
using Saldo360.Infrastructure.Families;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Users;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>Convites sobre PostgreSQL real: concorrência, transação, estados e constraints.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
public sealed class FamilyInvitationPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Issuer = "https://auth.test.invalid";

    private string _connectionString = null!;
    private Guid _ownerId;
    private Guid _familyId;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        _ownerId = await UserAsync(db, "dono");
        var family = Family.Create("Família", _ownerId, DateTimeOffset.UtcNow);
        await new FamilyRepository(db).AddAsync(family, CancellationToken.None);
        _familyId = family.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_StoresOnlyTheSha256OfTheToken()
    {
        var created = await CreateInvitationAsync();
        var tokenBytes = Base64Url.DecodeFromChars(created.Token);

        await using var db = Db();
        var stored = await db.FamilyInvitations.SingleAsync();
        Assert.Equal(32, tokenBytes.Length);
        Assert.Equal(FamilyInvitation.HashToken(tokenBytes), stored.TokenHash);
        Assert.NotEqual(tokenBytes, stored.TokenHash);

        // Nenhuma coluna da linha contém o token (nem bytes, nem texto).
        var rowAsText = await db.Database
            .SqlQueryRaw<string>("SELECT row_to_json(i)::text AS \"Value\" FROM family_invitations i")
            .SingleAsync();
        Assert.DoesNotContain(created.Token, rowAsText);
        Assert.DoesNotContain(Convert.ToHexString(tokenBytes), rowAsText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TimeSpan.FromHours(48), stored.ExpiresAt - stored.CreatedAt);
    }

    [Fact]
    public async Task Accept_AddsMemberAndConsumesInvitation_InOneStep()
    {
        var created = await CreateInvitationAsync();
        var guestId = await UserAsync("convidado");

        var outcome = await AcceptAsync(created.Token, guestId);

        Assert.Equal(new AcceptInvitationOutcome(AcceptInvitationStatus.Joined, _familyId), outcome);
        await using var db = Db();
        var member = await db.FamilyMembers.SingleAsync(m => m.UserId == guestId);
        Assert.Equal(FamilyRole.Member, member.Role);
        var invitation = await db.FamilyInvitations.SingleAsync();
        Assert.Equal(guestId, invitation.ConsumedByUserId);
        Assert.NotNull(invitation.ConsumedAt);
    }

    [Fact]
    public async Task ConcurrentAcceptsByDifferentUsers_AddExactlyOneMember()
    {
        var created = await CreateInvitationAsync();
        var guests = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            guests.Add(await UserAsync($"concorrente-{i}"));
        }

        var outcomes = await Task.WhenAll(guests.Select(guest => AcceptAsync(created.Token, guest)));

        var winner = Assert.Single(outcomes, o => o.Status == AcceptInvitationStatus.Joined);
        Assert.All(outcomes.Where(o => o != winner), o => Assert.Equal(AcceptInvitationStatus.Unavailable, o.Status));
        await using var db = Db();
        Assert.Equal(2, await db.FamilyMembers.CountAsync(m => m.FamilyId == _familyId));
        var invitation = await db.FamilyInvitations.SingleAsync();
        Assert.Contains(invitation.ConsumedByUserId!.Value, guests);
    }

    [Fact]
    public async Task ConcurrentRevokeAndAccept_OnlyOneWins()
    {
        for (var round = 0; round < 10; round++)
        {
            var created = await CreateInvitationAsync();
            var guestId = await UserAsync($"corrida-{round}");

            var acceptTask = AcceptAsync(created.Token, guestId);
            var revokeTask = RevokeAsync(created.Id);
            await Task.WhenAll(acceptTask, revokeTask);
            var accept = await acceptTask;
            var revoke = await revokeTask;

            await using var db = Db();
            var invitation = await db.FamilyInvitations.SingleAsync(i => i.Id == created.Id);
            var joined = await db.FamilyMembers.AnyAsync(m => m.UserId == guestId);
            if (accept.Status == AcceptInvitationStatus.Joined)
            {
                Assert.True(joined);
                Assert.Null(invitation.RevokedAt);
                Assert.Equal(RevokeInvitationOutcome.NotPending, revoke.Outcome);
            }
            else
            {
                Assert.False(joined);
                Assert.NotNull(invitation.RevokedAt);
                Assert.Null(invitation.ConsumedAt);
                Assert.Equal(RevokeInvitationOutcome.Revoked, revoke.Outcome);
            }
        }
    }

    [Fact]
    public async Task Accept_WhenMemberInsertFails_DoesNotConsumeInvitation()
    {
        var created = await CreateInvitationAsync();
        var guestId = await UserAsync("convidado");
        var failingInsert = new FailMemberInsert();
        // O UPDATE condicional consome o convite dentro da transação; em seguida, a gravação do
        // vínculo falha. Tudo deve ser desfeito.
        await using (var db = Db(failingInsert))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => new FamilyInvitationRepository(db).AcceptAsync(
                FamilyInvitation.HashToken(Base64Url.DecodeFromChars(created.Token)),
                guestId,
                DateTimeOffset.UtcNow,
                CancellationToken.None));
        }

        Assert.True(failingInsert.Triggered, "A falha deveria ocorrer na gravação do vínculo, após o consumo.");

        await using var verify = Db();
        var invitation = await verify.FamilyInvitations.SingleAsync();
        Assert.Null(invitation.ConsumedAt);
        Assert.Null(invitation.ConsumedByUserId);
        Assert.Equal(1, await verify.FamilyMembers.CountAsync());

        // Continua utilizável.
        Assert.Equal(AcceptInvitationStatus.Joined, (await AcceptAsync(created.Token, await UserAsync("depois"))).Status);
    }

    [Fact]
    public async Task Accept_ByExistingMember_KeepsRoleAndDoesNotConsume()
    {
        var created = await CreateInvitationAsync();

        var outcome = await AcceptAsync(created.Token, _ownerId);

        Assert.Equal(new AcceptInvitationOutcome(AcceptInvitationStatus.AlreadyMember, _familyId), outcome);
        await using var db = Db();
        Assert.Equal(FamilyRole.Owner, (await db.FamilyMembers.SingleAsync()).Role);
        Assert.Null((await db.FamilyInvitations.SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task Accept_ConsumedInvitation_IsUnavailable_ForAnotherUser()
    {
        var created = await CreateInvitationAsync();
        await AcceptAsync(created.Token, await UserAsync("primeiro"));

        var second = await AcceptAsync(created.Token, await UserAsync("segundo"));

        Assert.Equal(AcceptInvitationStatus.Unavailable, second.Status);
        await using var db = Db();
        Assert.Equal(2, await db.FamilyMembers.CountAsync());
    }

    [Fact]
    public async Task Accept_ExpiredInvitation_IsUnavailable()
    {
        var created = await CreateInvitationAsync(DateTimeOffset.UtcNow.AddHours(-49));

        var outcome = await AcceptAsync(created.Token, await UserAsync("atrasado"));

        Assert.Equal(AcceptInvitationStatus.Unavailable, outcome.Status);
        await using var db = Db();
        Assert.Equal(1, await db.FamilyMembers.CountAsync());
        Assert.Null((await db.FamilyInvitations.SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task Accept_RevokedInvitation_IsUnavailable()
    {
        var created = await CreateInvitationAsync();
        Assert.Equal(RevokeInvitationOutcome.Revoked, (await RevokeAsync(created.Id)).Outcome);

        var outcome = await AcceptAsync(created.Token, await UserAsync("tarde"));

        Assert.Equal(AcceptInvitationStatus.Unavailable, outcome.Status);
        await using var db = Db();
        Assert.Equal(1, await db.FamilyMembers.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-base64url!")]
    [InlineData("AAAA")] // base64url válido, mas não tem 32 bytes
    public async Task Accept_MalformedToken_IsUnavailable(string token)
    {
        Assert.Equal(AcceptInvitationStatus.Unavailable, (await AcceptAsync(token, await UserAsync("x"))).Status);
    }

    [Fact]
    public async Task Family_GrowsBeyondTwoPeople_ThroughSeveralInvitations()
    {
        for (var i = 0; i < 3; i++)
        {
            var created = await CreateInvitationAsync();
            Assert.Equal(AcceptInvitationStatus.Joined, (await AcceptAsync(created.Token, await UserAsync($"pessoa-{i}"))).Status);
        }

        await using var db = Db();
        Assert.Equal(4, await db.FamilyMembers.CountAsync(m => m.FamilyId == _familyId));
    }

    [Fact]
    public async Task Invitation_CannotBeBothConsumedAndRevoked()
    {
        var created = await CreateInvitationAsync();

        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE family_invitations SET consumed_at = now(), consumed_by_user_id = {_ownerId}, revoked_at = now() WHERE id = {created.Id}"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Invitation_TokenHashMustHave32Bytes()
    {
        await using var db = Db();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO family_invitations (id, family_id, created_by_user_id, token_hash, created_at, expires_at) VALUES ({Guid.CreateVersion7()}, {_familyId}, {_ownerId}, {Encoding.UTF8.GetBytes("texto-puro")}, now(), now() + interval '1 hour')"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_connectionString);

    private Saldo360DbContext Db(IInterceptor interceptor) =>
        new((DbContextOptions<Saldo360DbContext>)Saldo360DbContext
            .Configure(new DbContextOptionsBuilder<Saldo360DbContext>(), _connectionString)
            .AddInterceptors(interceptor)
            .Options);

    /// <summary>Simula falha ao gravar o vínculo (SaveChanges); o UPDATE em lote não passa por aqui.</summary>
    private sealed class FailMemberInsert : SaveChangesInterceptor
    {
        public bool Triggered { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<FamilyMember>().Any(e => e.State == EntityState.Added))
            {
                Triggered = true;
                throw new DbUpdateException("Falha simulada ao gravar o vínculo.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private async Task<Guid> UserAsync(string subject)
    {
        await using var db = Db();
        return await UserAsync(db, subject);
    }

    private static Task<Guid> UserAsync(Saldo360DbContext db, string subject) =>
        new UserDirectory(db, TimeProvider.System).ResolveAsync(Issuer, subject, subject, CancellationToken.None);

    private async Task<CreatedInvitation> CreateInvitationAsync(DateTimeOffset? now = null)
    {
        await using var db = Db();
        var result = await Service(db, now).CreateAsync(_familyId, _ownerId, CancellationToken.None);
        Assert.Equal(InvitationAccess.Allowed, result.Access);
        return result.Invitation!;
    }

    private async Task<AcceptInvitationOutcome> AcceptAsync(string token, Guid userId)
    {
        await using var db = Db();
        return await Service(db).AcceptAsync(token, userId, CancellationToken.None);
    }

    private async Task<RevokeInvitationResult> RevokeAsync(Guid invitationId)
    {
        await using var db = Db();
        return await Service(db).RevokeAsync(_familyId, invitationId, _ownerId, CancellationToken.None);
    }

    private static FamilyInvitationService Service(Saldo360DbContext db, DateTimeOffset? now = null) =>
        new(new FamilyRepository(db), new FamilyInvitationRepository(db), now is { } fixedNow ? new FixedTime(fixedNow) : TimeProvider.System);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
