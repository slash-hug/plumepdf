# Add a timestamp and LTV material

A PAdES B-T signature embeds an RFC 3161 signature-timestamp, proving the signature existed no later than the TSA's clock — set `PdfSignOptions.Level = PdfSignatureLevel.T` and supply an `ITimestampAuthority` (or `PdfSignOptions.TimestampAuthorityUrl` for the in-box `IO.Http.HttpTimestampAuthority`). `doc.Signatures.AddLtvAsync` then embeds a `/DSS` (Document Security Store) of OCSP/CRL revocation material for B-LT, fetched through an `IRevocationFetcher` (`IO.Http.HttpRevocationFetcher` for the in-box HTTP-backed implementation — it restricts AIA/CRL-Distribution-Point URIs to `http`/`https` only, since they come from a certificate embedded in the document being processed). Both seams are opt-in: PlumePDF never calls out to the network on its own.

<!-- snippet: timestamp-and-ltv -->
<a id='snippet-timestamp-and-ltv'></a>
```cs
// IO.Http.HttpTimestampAuthority / HttpRevocationFetcher are the in-box HTTP-backed
// choice for a real TSA/OCSP responder; this recipe implements the same
// ITimestampAuthority/IRevocationFetcher seams against minimal in-process stand-ins so
// it runs offline and deterministically (mirroring PlumePdf.Tests.Signing.FakeTimestampAuthority).
await Pdf.SignAsync("output/lta-agreement.pdf", "output/lta-agreement-signed.pdf", new PdfSignOptions
{
    Certificate = certificate,
    Level = PdfSignatureLevel.T,
    TimestampAuthority = new CookbookTimestampAuthority(),
});

using (var withTimestamp = PdfDocument.Open("output/lta-agreement-signed.pdf"))
{
    await withTimestamp.Signatures.AddLtvAsync("output/lta-agreement-ltv.pdf", new CookbookRevocationFetcher());
}

using var withLtv = PdfDocument.Open("output/lta-agreement-ltv.pdf");
var result = withLtv.Signatures[0].Verify();
var catalog = withLtv.Objects.Trailer[PdfName.Root] is PdfReference rootRef ? withLtv.Objects[rootRef.Target] as PdfDictionary : null;
report.AppendLine($"HasTimestamp: {result.HasTimestamp}");
report.AppendLine($"Has /DSS (LTV material embedded): {catalog?.ContainsKey(PdfName.DSS) == true}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L1006-L1028' title='Snippet source file'>snippet source</a> | <a href='#snippet-timestamp-and-ltv' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.TimestampAndLtv`):

<!-- snippet: CookbookTests.TimestampAndLtv.verified.txt -->
<a id='snippet-CookbookTests.TimestampAndLtv.verified.txt'></a>
```txt
HasTimestamp: True
Has /DSS (LTV material embedded): True
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.TimestampAndLtv.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.TimestampAndLtv.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

An RFC 3161 timestamp is a CMS *unsigned* attribute (outside the signature's own protection): `HasTimestamp`/`TimestampTime` are populated whenever a token decodes, but `IsTimestampTrusted` (see [Verify a signature](verify-signatures.md)) only turns `true` once the token's own signature verifies *and* its TSA certificate chain-builds to a trust anchor you supplied — never treat `TimestampTime` as proven time without checking it.

For a standalone document timestamp (B-LTA maintenance, no new signature), see `doc.Signatures.AddDocumentTimestampAsync`.
