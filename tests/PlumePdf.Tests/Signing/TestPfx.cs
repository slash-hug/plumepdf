using System.Security.Cryptography.X509Certificates;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// Certificate loading for tests on every target framework: .NET 9+ obsoletes the
/// <see cref="X509Certificate2"/> byte-array constructors (SYSLIB0057) in favour of
/// <c>X509CertificateLoader</c>, which net8.0 does not have.
/// </summary>
internal static class TestPfx
{
    /// <summary>Loads a PFX export with its private key, exportable.</summary>
    public static X509Certificate2 Load(byte[] pfx) =>
#if NET9_0_OR_GREATER
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
#else
        new(pfx, (string?)null, X509KeyStorageFlags.Exportable);
#endif

    /// <summary>Loads a DER-encoded certificate with no private key.</summary>
    public static X509Certificate2 LoadPublic(byte[] der) =>
#if NET9_0_OR_GREATER
        X509CertificateLoader.LoadCertificate(der);
#else
        new(der);
#endif
}
