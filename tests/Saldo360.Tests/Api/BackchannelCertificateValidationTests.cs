using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Saldo360.Api.Authentication;

namespace Saldo360.Tests.Api;

/// <summary>
/// Validação do certificado do provedor quando uma CA própria é configurada (ex.: CA interna do Caddy,
/// que emite por uma intermediária). Usa uma cadeia raiz → intermediária → folha gerada no teste.
/// </summary>
[Trait("Category", "CORE")]
public sealed class BackchannelCertificateValidationTests : IDisposable
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";

    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _intermediate;
    private readonly X509Certificate2 _leaf;
    private readonly X509Certificate2 _otherRoot;

    public BackchannelCertificateValidationTests()
    {
        _root = CreateCa("CN=Teste Root");
        _intermediate = CreateCa("CN=Teste Intermediate", _root);
        _leaf = CreateLeaf(_intermediate, ServerAuth);
        _otherRoot = CreateCa("CN=Outra Root");
    }

    [Fact]
    public void ChainThroughPresentedIntermediate_ToConfiguredCa_IsAccepted()
    {
        using var presented = PresentedChain();

        Assert.True(AuthenticationSetup.ValidateServerCertificate(
            _root, _leaf, presented, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void NameMismatch_IsRejected_EvenWithValidChain()
    {
        using var presented = PresentedChain();

        Assert.False(AuthenticationSetup.ValidateServerCertificate(
            _root, _leaf, presented, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void MissingCertificate_IsRejected()
    {
        Assert.False(AuthenticationSetup.ValidateServerCertificate(
            _root, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Fact]
    public void ChainToAnotherCa_IsRejected_EvenIfItsRootWasPresented()
    {
        using var presented = PresentedChain();

        Assert.False(AuthenticationSetup.ValidateServerCertificate(
            _otherRoot, _leaf, presented, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void WithoutIntermediate_ChainCannotBeBuilt_AndIsRejected()
    {
        Assert.False(AuthenticationSetup.ValidateServerCertificate(
            _root, _leaf, presentedChain: null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void CertificateNotMeantForServers_IsRejected()
    {
        using var clientCertificate = CreateLeaf(_intermediate, ClientAuth);
        using var presented = PresentedChain(clientCertificate);

        Assert.False(AuthenticationSetup.ValidateServerCertificate(
            _root, clientCertificate, presented, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    public void Dispose()
    {
        _root.Dispose();
        _intermediate.Dispose();
        _leaf.Dispose();
        _otherRoot.Dispose();
    }

    /// <summary>Cadeia como o TLS a entregaria ao callback: folha, intermediária e raiz.</summary>
    private X509Chain PresentedChain(X509Certificate2? leaf = null)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_root);
        chain.ChainPolicy.ExtraStore.Add(_intermediate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(leaf ?? _leaf);
        return chain;
    }

    private static X509Certificate2 CreateCa(string subject, X509Certificate2? issuer = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(1);
        if (issuer is null)
        {
            return request.CreateSelfSigned(notBefore, notAfter);
        }

        using var certificate = request.Create(issuer, notBefore, notAfter.AddDays(-1), RandomNumberGenerator.GetBytes(8));
        return certificate.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 issuer, string extendedUsage)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=auth.teste", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("auth.teste");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(extendedUsage)], false));

        return request.Create(
            issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), RandomNumberGenerator.GetBytes(8));
    }
}
