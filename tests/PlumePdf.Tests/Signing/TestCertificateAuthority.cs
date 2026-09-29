using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// A synthetic certificate authority for hermetic signing/verification tests (Task B1): a
/// self-signed root, an intermediate the root signs, an RSA leaf and an ECDSA leaf both signed
/// by the intermediate, and an OCSP-signing certificate (intermediate-signed, carrying
/// <c>id-kp-OCSPSigning</c> and the OCSP-no-check extension). Every certificate here carries
/// its private key, so tests can sign with it directly via
/// <see cref="Objects.Signing.CertificateSigner"/> with no external fixture files, keeping the
/// gating lane fully offline.
/// </summary>
public sealed class TestCertificateAuthority : IDisposable
{
    /// <summary>The self-signed root CA.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>The intermediate CA, signed by <see cref="Root"/>.</summary>
    public X509Certificate2 Intermediate { get; }

    /// <summary>An RSA-2048 end-entity signing certificate, signed by <see cref="Intermediate"/>.</summary>
    public X509Certificate2 LeafRsa { get; }

    /// <summary>An ECDSA (P-256) end-entity signing certificate, signed by <see cref="Intermediate"/>.</summary>
    public X509Certificate2 LeafEcdsa { get; }

    /// <summary>An OCSP-responder-signing certificate, signed by <see cref="Intermediate"/>, carrying <c>id-kp-OCSPSigning</c> and the OCSP-no-check extension.</summary>
    public X509Certificate2 OcspSigner { get; }

    /// <summary><see cref="LeafRsa"/>'s chain, leaf-first.</summary>
    public X509Certificate2[] RsaChain => [LeafRsa, Intermediate, Root];

    /// <summary><see cref="LeafEcdsa"/>'s chain, leaf-first.</summary>
    public X509Certificate2[] EcdsaChain => [LeafEcdsa, Intermediate, Root];

    public TestCertificateAuthority()
    {
        Root = CreateRoot();
        Intermediate = CreateIntermediate(Root);
        LeafRsa = CreateRsaLeaf(Intermediate);
        LeafEcdsa = CreateEcdsaLeaf(Intermediate);
        OcspSigner = CreateOcspSigner(Intermediate);
    }

    private static X509Certificate2 CreateRoot()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=PlumePDF Test Root CA, O=PlumePDF Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 1, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return Persist(selfSigned);
    }

    private static X509Certificate2 CreateIntermediate(X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=PlumePDF Test Intermediate CA, O=PlumePDF Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        using var issued = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(4), NewSerialNumber());
        using var withKey = issued.CopyWithPrivateKey(key);
        return Persist(withKey);
    }

    private static X509Certificate2 CreateRsaLeaf(X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=PlumePDF Test RSA Signer, O=PlumePDF Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddLeafSigningExtensions(request, issuer);

        using var issued = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), NewSerialNumber());
        using var withKey = issued.CopyWithPrivateKey(key);
        return Persist(withKey);
    }

    private static X509Certificate2 CreateEcdsaLeaf(X509Certificate2 issuer)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PlumePDF Test ECDSA Signer, O=PlumePDF Tests", key, HashAlgorithmName.SHA256);
        AddLeafSigningExtensions(request, issuer);

        // The convenience Create(X509Certificate2, ...) overload requires the issuer's key
        // algorithm to match the request's own — it doesn't here (an RSA intermediate signing
        // an ECDSA leaf), so the issuer's signing operation goes through an explicit
        // X509SignatureGenerator instead, exactly as CertificateRequest's own exception for
        // this case names as the fix.
        using var issuerKey = issuer.GetRSAPrivateKey()!;
        var generator = X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1);
        using var issued = request.Create(issuer.SubjectName, generator, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), NewSerialNumber());
        using var withKey = issued.CopyWithPrivateKey(key);
        return Persist(withKey);
    }

    private static void AddLeafSigningExtensions(CertificateRequest request, X509Certificate2 issuer)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [
                new Oid("1.3.6.1.5.5.7.3.4"), // id-kp-emailProtection — widely accepted by PDF signature validators as a signing EKU.
                new Oid("1.3.6.1.5.5.7.3.36"), // Adobe/ETSI "Document Signing"
            ],
            critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));
    }

    private static X509Certificate2 CreateOcspSigner(X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=PlumePDF Test OCSP Responder, O=PlumePDF Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.9")], critical: false)); // id-kp-OCSPSigning
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.48.1.5", [0x05, 0x00], critical: false)); // id-pkix-ocsp-nocheck — extnValue is DER NULL (RFC 6960 §4.2.2.2.1)
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        using var issued = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), NewSerialNumber());
        using var withKey = issued.CopyWithPrivateKey(key);
        return Persist(withKey);
    }

    private static byte[] NewSerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // keep the DER INTEGER encoding unambiguously non-negative.
        return serial;
    }

    /// <summary>
    /// Re-imports <paramref name="certificateWithKey"/> from a PFX export so the returned
    /// certificate's private key is fully independent of whatever <see cref="RSA"/>/<see cref="ECDsa"/>
    /// object it was originally attached to — that source key is disposed (a <c>using</c>
    /// local in every caller here) once its owning method returns, and an ephemeral-key
    /// association surviving that disposal is exactly the class of bug this sidesteps.
    /// </summary>
    private static X509Certificate2 Persist(X509Certificate2 certificateWithKey) =>
        TestPfx.Load(certificateWithKey.Export(X509ContentType.Pfx));

    /// <inheritdoc/>
    public void Dispose()
    {
        Root.Dispose();
        Intermediate.Dispose();
        LeafRsa.Dispose();
        LeafEcdsa.Dispose();
        OcspSigner.Dispose();
    }
}

/// <summary>
/// The hermetic-lane sentinel for <see cref="TestCertificateAuthority"/> (Task B1's "zero-row"
/// pattern): fails loudly if certificate generation silently produced nothing usable, rather
/// than letting every dependent signing test fail with an unrelated-looking error.
/// </summary>
public sealed class TestCertificateAuthoritySentinelTests
{
    [Fact]
    public void FixtureSet_IsNonEmptyAndUsable()
    {
        using var ca = new TestCertificateAuthority();

        Assert.NotNull(ca.Root);
        Assert.NotNull(ca.Intermediate);
        Assert.NotNull(ca.LeafRsa);
        Assert.NotNull(ca.LeafEcdsa);
        Assert.NotNull(ca.OcspSigner);

        Assert.True(ca.Root.HasPrivateKey);
        Assert.True(ca.Intermediate.HasPrivateKey);
        Assert.True(ca.LeafRsa.HasPrivateKey);
        Assert.True(ca.LeafEcdsa.HasPrivateKey);
        Assert.True(ca.OcspSigner.HasPrivateKey);
    }

    [Fact]
    public void Chains_LinkIssuerToSubjectInOrder()
    {
        using var ca = new TestCertificateAuthority();

        Assert.Equal(ca.Intermediate.Subject, ca.LeafRsa.Issuer);
        Assert.Equal(ca.Root.Subject, ca.Intermediate.Issuer);
        Assert.Equal(ca.Root.Subject, ca.Root.Issuer); // self-signed

        Assert.Equal(ca.Intermediate.Subject, ca.LeafEcdsa.Issuer);
        Assert.Equal(ca.Intermediate.Subject, ca.OcspSigner.Issuer);
    }

    [Fact]
    public void LeafCertificates_UseDistinctKeyAlgorithms()
    {
        using var ca = new TestCertificateAuthority();

        using var rsaKey = ca.LeafRsa.GetRSAPrivateKey();
        using var ecdsaKey = ca.LeafEcdsa.GetECDsaPrivateKey();

        Assert.NotNull(rsaKey);
        Assert.NotNull(ecdsaKey);
        Assert.Null(ca.LeafRsa.GetECDsaPrivateKey());
        Assert.Null(ca.LeafEcdsa.GetRSAPrivateKey());
    }

    [Fact]
    public void ChainProperties_AreLeafFirstAndConsistent()
    {
        using var ca = new TestCertificateAuthority();

        Assert.Equal([ca.LeafRsa, ca.Intermediate, ca.Root], ca.RsaChain);
        Assert.Equal([ca.LeafEcdsa, ca.Intermediate, ca.Root], ca.EcdsaChain);
    }
}
