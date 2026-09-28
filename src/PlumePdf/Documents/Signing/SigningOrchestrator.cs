using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PlumePdf.Objects;
using PlumePdf.Objects.Signing;

namespace PlumePdf.Documents.Signing;

/// <summary>
/// Drives one <c>PAdES</c> signing pass end to end (Task C3): registers a signature dictionary
/// (with <c>Objects.PdfContentsPlaceholder</c>/<c>PdfByteRangePlaceholder</c>) and its owning
/// <c>/FT /Sig</c> field/widget against the target document's live object graph, wraps
/// exactly one <c>Objects.SigningWriteSession</c> pass, builds the CMS via
/// <c>Objects.Signing.CmsSignatureBuilder</c>, and writes the finished, signed file.
/// </summary>
internal static class SigningOrchestrator
{
    private static readonly PdfName FieldsName = PdfName.Fields;
    private static readonly PdfName SigFlagsName = PdfName.SigFlags;
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName ByteRangeName = PdfName.ByteRange;
    private static readonly PdfName FilterName = PdfName.Get("Filter");
    private static readonly PdfName SubFilterName = PdfName.SubFilter;
    private static readonly PdfName FTName = PdfName.FT;
    private static readonly PdfName SigName = PdfName.Sig;
    private static readonly PdfName TName = PdfName.T;
    private static readonly PdfName VName = PdfName.V;
    private static readonly PdfName MName = PdfName.M;
    private static readonly PdfName RectName = PdfName.Get("Rect");
    private static readonly PdfName FName = PdfName.Get("F");
    private static readonly PdfName PName = PdfName.Get("P");
    private static readonly PdfName AnnotName = PdfName.Get("Annot");
    private static readonly PdfName WidgetName = PdfName.Widget;
    private static readonly PdfName TypeName = PdfName.Type;
    private static readonly PdfName ReferenceName = PdfName.Reference;
    private static readonly PdfName TransformMethodName = PdfName.TransformMethod;
    private static readonly PdfName TransformParamsName = PdfName.TransformParams;
    private static readonly PdfName DocMDPName = PdfName.DocMDP;
    private static readonly PdfName PermsName = PdfName.Perms;
    private static readonly PdfName SigRefName = PdfName.Get("SigRef");
    private static readonly PdfName PValueName = PdfName.Get("P");
    private static readonly PdfName VersionName = PdfName.Get("V");

    /// <summary>
    /// Asynchronously signs <paramref name="document"/> and writes the result to
    /// <paramref name="outputPath"/>, at whatever <see cref="PdfSignatureLevel"/>
    /// <paramref name="signOptions"/> requests, including one requiring network I/O (an RFC
    /// 3161 timestamp fetch) — real awaits down to <c>HttpClient</c>, never <c>Task.Run</c>.
    /// </summary>
    public static async Task SignAsync(PdfDocument document, string outputPath, PdfSignOptions signOptions, PdfOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(signOptions);

        var effectiveOptions = options ?? document.Options;
        ValidateWritePreconditions(document);

        var signer = signOptions.ResolveSigner();
        if (signer.DigestAlgorithm != signOptions.DigestAlgorithm)
        {
            throw new PlumePdfException(
                "PLUME6055",
                $"The signer's IPdfSigner.DigestAlgorithm ('{signer.DigestAlgorithm.Name}') does not match PdfSignOptions.DigestAlgorithm ('{signOptions.DigestAlgorithm.Name}'). The document digest passed to IPdfSigner.SignAsync is always hashed with PdfSignOptions.DigestAlgorithm — a signer that hashes/signs with a different algorithm internally (e.g. an HSM declaring SHA-384) would silently produce a CMS whose declared digest algorithm does not match what was actually signed. Set PdfSignOptions.DigestAlgorithm to match your IPdfSigner's DigestAlgorithm.");
        }

        var signingTime = ValidateDeterminismAndResolveSigningTime(document, effectiveOptions, signOptions, signer);
        var (timestampAuthority, ownsTimestampAuthority) = signOptions.ResolveTimestampAuthority(effectiveOptions);
        if (signOptions.Level == PdfSignatureLevel.T && timestampAuthority is null)
        {
            throw new PlumePdfException("PLUME4012", "PAdES B-T requires PdfSignOptions.TimestampAuthority or .TimestampAuthorityUrl but neither was supplied.");
        }

        // Every ObjectRegistry mutation this pass performs (the new signature dictionary, its
        // field/widget, and any pre-existing catalog/page/AcroForm dictionary it edits in
        // place) is routed through this scope rather than document.Objects directly, so that a
        // failure anywhere below — a TSA timeout, a signer/HSM error, a CMS build failure, a
        // write failure — can roll every one of them back in the catch below instead of leaving
        // the document permanently poisoned for a caller's retry.
        var scope = new SignatureMutationScope(document.Objects);
        try
        {
            var fieldName = ResolveFieldName(document, signOptions.FieldName);
            var reservationBytes = signOptions.ContentsReservationBytes ?? EstimateReservationBytes(signer, signOptions.Level);
            var sigDictReference = document.Objects.AllocateNumber();

            var sigDict = new PdfDictionary();
            sigDict.Set(TypeName, SigName);
            sigDict.Set(FilterName, PdfName.Get("Adobe.PPKLite"));
            sigDict.Set(SubFilterName, PdfName.ETSICAdESDetached);
            sigDict.Set(ContentsName, new PdfContentsPlaceholder(reservationBytes));
            sigDict.Set(ByteRangeName, new PdfByteRangePlaceholder(12));
            sigDict.Set(MName, PdfString.FromLiteral(System.Text.Encoding.ASCII.GetBytes(FormatPdfDate(signingTime))));

            if (signOptions.Reason is { Length: > 0 } reason)
            {
                sigDict.Set(PdfName.Reason, PdfString.FromLiteral(EncodeTextString(reason)));
            }

            if (signOptions.Location is { Length: > 0 } location)
            {
                sigDict.Set(PdfName.Location, PdfString.FromLiteral(EncodeTextString(location)));
            }

            if (signOptions.ContactInfo is { Length: > 0 } contactInfo)
            {
                sigDict.Set(PdfName.ContactInfo, PdfString.FromLiteral(EncodeTextString(contactInfo)));
            }

            if (signOptions.CertifyNoChanges)
            {
                ApplyDocMdpCertification(document, sigDict, sigDictReference, scope);
            }

            scope.RegisterNew(sigDictReference, sigDict);

            var fieldReference = BuildAndAttachField(document, fieldName, sigDictReference, scope);
            EnsureAcroFormReferencesField(document, fieldReference, scope);

            var pages = document.Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
            var topPagesReference = ResolveTopPagesReference(document);

            document.DetachMappedSourceIfSamePath(outputPath);
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            var tempPath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, $".{Path.GetFileName(outputPath)}.plumepdf-tmp-{Guid.NewGuid():N}");

            try
            {
                var session = SigningWriteSession.Create(document.Source!, document.Objects, document.PagesTreeDirty, topPagesReference, pages, document.StartXrefOffset!.Value, effectiveOptions);

                using (var hash = IncrementalHash.CreateHash(signOptions.DigestAlgorithm))
                {
                    session.HashDocument(hash);
                    var digest = hash.GetHashAndReset();
                    var cms = await CmsSignatureBuilder.BuildAsync(digest, signOptions.DigestAlgorithm, signer, signOptions.ResolveCmsLevel(), timestampAuthority, cancellationToken).ConfigureAwait(false);
                    session.PatchContents(cms);
                }

                using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    session.WriteTo(tempStream);
                }

                File.Move(tempPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch
        {
            scope.Rollback();
            throw;
        }
        finally
        {
            if (ownsTimestampAuthority && timestampAuthority is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// Synchronous door: valid only for a request that never touches the
    /// network (<see cref="PdfSignatureLevel.B"/>, no <see cref="PdfSignOptions.TimestampAuthority"/>/
    /// <see cref="PdfSignOptions.TimestampAuthorityUrl"/>) — <c>Objects.Signing.CertificateSigner</c>
    /// (and any well-behaved local <see cref="IPdfSigner"/>) completes synchronously despite
    /// its <c>async</c> signature, so blocking on the resulting <see cref="Task"/> here performs
    /// no real asynchronous wait.
    /// </summary>
    /// <exception cref="PlumePdfException"><paramref name="signOptions"/> requires a network round trip (<c>PLUME6049</c>) — use <see cref="SignAsync"/> instead.</exception>
    public static void Sign(PdfDocument document, string outputPath, PdfSignOptions signOptions, PdfOptions? options)
    {
        ArgumentNullException.ThrowIfNull(signOptions);
        if (signOptions.Level != PdfSignatureLevel.B || signOptions.TimestampAuthority is not null || signOptions.TimestampAuthorityUrl is not null)
        {
            throw new PlumePdfException("PLUME6049", "Sign (synchronous) only supports PdfSignatureLevel.B with no timestamp authority — this request requires a network round trip; use SignAsync instead.");
        }

        SignAsync(document, outputPath, signOptions, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void ValidateWritePreconditions(PdfDocument document)
    {
        if (document.HasEncryptedSource)
        {
            throw new PlumePdfException("PLUME6054", "Signing an encrypted-source document is out of scope for this phase — verification of an already-signed encrypted document is supported; creating a new signature on one is not.");
        }

        if (document.Source is null)
        {
            throw new PlumePdfException("PLUME5003", "Signing requires a document opened from a file, stream, or byte buffer; this document has no backing byte source (e.g. the result of Pdf.Merge).");
        }

        if (document.StartXrefOffset is null)
        {
            throw new PlumePdfException("PLUME5005", "The source's cross-reference data was recovered via a brute-force scan and has no reliable prior 'startxref' offset to chain a signature revision onto.");
        }

        if (document.Catalog is null)
        {
            throw new PlumePdfException("PLUME6047", "This document's catalog could not be resolved (see Diagnostics) — signing requires a resolvable /Root catalog to attach the /AcroForm and signature field to.");
        }
    }

    private static DateTimeOffset ValidateDeterminismAndResolveSigningTime(PdfDocument document, PdfOptions options, PdfSignOptions signOptions, IPdfSigner signer)
    {
        if (!options.Deterministic)
        {
            return signOptions.SigningTime ?? DateTimeOffset.UtcNow;
        }

        // Deterministic signing is permitted ONLY for RSA PKCS#1 v1.5 with a
        // caller-supplied signing time and no TSA/LTV — every other combination embeds either
        // wall-clock time, a TSA's own clock/nonce, or randomized signature octets (ECDSA),
        // none of which two runs can agree on. Refuse rather than silently break the promise.
        using var rsa = signer.Certificate.GetRSAPublicKey();
        var isRsa = rsa is not null;
        if (signOptions.Level != PdfSignatureLevel.B || !isRsa || signOptions.SigningTime is null)
        {
            throw new PlumePdfException(
                "PLUME6053",
                "PdfOptions.Deterministic signing is only defined for RSA PKCS#1 v1.5 at PdfSignatureLevel.B with a caller-supplied PdfSignOptions.SigningTime — ECDSA/RSASSA-PSS signature octets, TSA tokens, and wall-clock signing times are none of them reproducible across runs.");
        }

        return signOptions.SigningTime.Value;
    }

    /// <summary>
    /// The refusal for the B-T/B-LT/B-LTA maintenance verbs: a TSA token embeds the
    /// authority's own clock and nonce, and freshly fetched OCSP/CRL material embeds
    /// <c>producedAt</c>/<c>thisUpdate</c> times — none of it is reproducible across runs, so
    /// these operations can never honour <see cref="PdfOptions.Deterministic"/>. Called by
    /// <see cref="DocumentTimestamper"/> and <see cref="DssWriter"/> before any mutation.
    /// </summary>
    internal static void RefuseDeterministicMaintenance(PdfOptions options, string operation)
    {
        if (options.Deterministic)
        {
            throw new PlumePdfException(
                "PLUME6053",
                $"{operation} embeds a TSA token or freshly fetched revocation material whose bytes differ on every run — it cannot honour PdfOptions.Deterministic. Save with Deterministic disabled for B-T/B-LT/B-LTA maintenance.");
        }
    }

    internal static string ResolveFieldName(PdfDocument document, string? requested)
    {
        var read = AcroFormReader.Read(document, document.Diagnostics);
        var existingSignatureCount = read.Fields.Count(static f => f.Kind == AcroFieldKind.Signature);

        var name = requested ?? $"Signature{existingSignatureCount + 1}";
        if (read.Fields.Any(f => f.FullyQualifiedName == name))
        {
            throw new PlumePdfException("PLUME6047", $"A field named '{name}' already exists — choose a different PdfSignOptions.FieldName.");
        }

        return name;
    }

    private static void ApplyDocMdpCertification(PdfDocument document, PdfDictionary sigDict, IndirectReference sigDictReference, SignatureMutationScope scope)
    {
        var read = AcroFormReader.Read(document, document.Diagnostics);
        if (read.Fields.Any(static f => f.Kind == AcroFieldKind.Signature))
        {
            throw new PlumePdfException("PLUME6051", "PdfSignOptions.CertifyNoChanges requires this to be the document's first signature (ISO 32000-1 §12.8.2.2) — an existing signature field was found.");
        }

        var transformParams = new PdfDictionary();
        transformParams.Set(TypeName, TransformParamsName);
        transformParams.Set(PValueName, PdfNumber.Get(1));
        transformParams.Set(VersionName, PdfName.Get("1.2"));

        var sigRef = new PdfDictionary();
        sigRef.Set(TypeName, SigRefName);
        sigRef.Set(TransformMethodName, DocMDPName);
        sigRef.Set(TransformParamsName, transformParams);

        // sigDict is not yet registered with the registry at this point (SignAsync registers it
        // right after this call returns) — an in-place edit to it needs no scope tracking, since
        // rolling back its eventual RegisterNew discards this edit along with everything else.
        sigDict.Set(ReferenceName, new PdfArray([sigRef]));

        if (document.Catalog is { } catalog)
        {
            var perms = new PdfDictionary();
            if (catalog.Dictionary.TryGetValue(PermsName, out var existingPerms) && existingPerms is PdfDictionary existingPermsDict)
            {
                foreach (var (key, value) in existingPermsDict)
                {
                    perms.Set(key, value);
                }
            }

            perms.Set(DocMDPName, new PdfReference(sigDictReference));
            scope.Set(catalog.Dictionary, PermsName, perms);
            scope.MarkDirty(catalog.Reference);
        }
    }

    internal static IndirectReference BuildAndAttachField(PdfDocument document, string fieldName, IndirectReference sigDictReference, SignatureMutationScope scope)
    {
        var fieldReference = document.Objects.AllocateNumber();
        var field = new PdfDictionary();
        field.Set(TypeName, AnnotName);
        field.Set(PdfName.Subtype, WidgetName);
        field.Set(FTName, SigName);
        // ISO 32000-1 §7.9.2.2: a UTF-16BE-with-BOM text string round-trips any Unicode field
        // name, matching /Reason, /Location, and /ContactInfo below — ASCII would silently
        // replace every non-ASCII character with '?' instead.
        field.Set(TName, PdfString.FromLiteral(EncodeTextString(fieldName)));
        field.Set(RectName, new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(0)]));
        field.Set(FName, PdfNumber.Get(2)); // annotation flags bit 2: Hidden — no /AP needed.
        field.Set(VName, new PdfReference(sigDictReference));

        if (document.Pages.Count > 0)
        {
            var page = document.Pages[0];
            field.Set(PName, new PdfReference(page.Reference));

            var annots = new PdfArray();
            if (page.Dictionary.TryGetValue(PdfName.Annots, out var existingAnnots) && existingAnnots is PdfArray existingArray)
            {
                foreach (var item in existingArray)
                {
                    annots.Add(item);
                }
            }

            annots.Add(new PdfReference(fieldReference));
            scope.Set(page.Dictionary, PdfName.Annots, annots);
            scope.MarkDirty(page.Reference);
        }

        scope.RegisterNew(fieldReference, field);
        return fieldReference;
    }

    internal static void EnsureAcroFormReferencesField(PdfDocument document, IndirectReference fieldReference, SignatureMutationScope scope)
    {
        var read = AcroFormReader.Read(document, document.Diagnostics);

        if (read.AcroFormReference is { } acroFormReference && read.AcroFormDictionary is { } acroFormDictionary)
        {
            AppendFieldAndSigFlags(acroFormDictionary, fieldReference, scope);
            scope.MarkDirty(acroFormReference);
            return;
        }

        var newAcroForm = new PdfDictionary();
        if (read.AcroFormDictionary is { } inline)
        {
            foreach (var (key, value) in inline)
            {
                newAcroForm.Set(key, value);
            }
        }

        // newAcroForm is brand-new and not yet registered — the fields/flags edit below needs
        // no scope tracking for the same reason as ApplyDocMdpCertification's sigDict edit.
        AppendFieldAndSigFlagsUnscoped(newAcroForm, fieldReference);

        var acroFormRef = document.Objects.AllocateNumber();
        scope.RegisterNew(acroFormRef, newAcroForm);

        var catalog = document.Catalog!;
        scope.Set(catalog.Dictionary, PdfName.AcroForm, new PdfReference(acroFormRef));
        scope.MarkDirty(catalog.Reference);
    }

    private static void AppendFieldAndSigFlags(PdfDictionary acroForm, IndirectReference fieldReference, SignatureMutationScope scope)
    {
        var fields = BuildAppendedFields(acroForm, fieldReference);
        scope.Set(acroForm, FieldsName, fields);
        scope.Set(acroForm, SigFlagsName, PdfNumber.Get(3)); // bit 1 SignaturesExist | bit 2 AppendOnly (ISO 32000-1 Table 225).
    }

    private static void AppendFieldAndSigFlagsUnscoped(PdfDictionary acroForm, IndirectReference fieldReference)
    {
        var fields = BuildAppendedFields(acroForm, fieldReference);
        acroForm.Set(FieldsName, fields);
        acroForm.Set(SigFlagsName, PdfNumber.Get(3)); // bit 1 SignaturesExist | bit 2 AppendOnly (ISO 32000-1 Table 225).
    }

    private static PdfArray BuildAppendedFields(PdfDictionary acroForm, IndirectReference fieldReference)
    {
        var fields = new PdfArray();
        if (acroForm.TryGetValue(FieldsName, out var existingFields) && existingFields is PdfArray existingArray)
        {
            foreach (var item in existingArray)
            {
                fields.Add(item);
            }
        }

        fields.Add(new PdfReference(fieldReference));
        return fields;
    }

    internal static IndirectReference? ResolveTopPagesReference(PdfDocument document) =>
        document.Catalog?.Dictionary.TryGetValue(PdfName.Get("Pages"), out var pagesValue) == true && pagesValue is PdfReference pagesRef
            ? pagesRef.Target
            : null;

    /// <summary>
    /// Approximates the <c>/Contents</c> reservation: certificate bytes plus a
    /// key-size-derived signature estimate plus fixed CMS/ASN.1 framing overhead, with 50%
    /// headroom and a level-dependent floor. Not a literal dry-run CMS build — a caller who hits <c>PLUME5011</c> (reservation overflow) anyway
    /// can override via <see cref="PdfSignOptions.ContentsReservationBytes"/>; overflow is
    /// always a coded refusal from <see cref="SigningWriteSession.PatchContents"/>, never
    /// truncation.
    /// </summary>
    private static int EstimateReservationBytes(IPdfSigner signer, PdfSignatureLevel level)
    {
        var certificateBytes = signer.Certificate.RawData.Length;
        foreach (var certificate in signer.AdditionalCertificates)
        {
            certificateBytes += certificate.RawData.Length;
        }

        int signatureBytes;
        using (var rsa = signer.Certificate.GetRSAPublicKey())
        {
            if (rsa is not null)
            {
                signatureBytes = rsa.KeySize / 8;
            }
            else
            {
                using var ecdsa = signer.Certificate.GetECDsaPublicKey();
                signatureBytes = ((ecdsa?.KeySize ?? 256) / 8 * 2) + 16;
            }
        }

        const int BaseOverhead = 1500; // CMS SignedData framing, algorithm identifiers, ESS signing-certificate-v2.
        var total = BaseOverhead + certificateBytes + signatureBytes;
        if (level == PdfSignatureLevel.T)
        {
            total += 10_000; // RFC 3161 token: TSA's own cert chain plus its CMS wrapper.
        }

        var withHeadroom = (int)(total * 1.5);
        var floor = level == PdfSignatureLevel.B ? 12_000 : 24_000;
        return Math.Max(withHeadroom, floor);
    }

    /// <summary>Encodes a text string per ISO 32000-1 §7.9.2.2: a UTF-16BE byte-order mark followed by UTF-16BE code units — round-trips through <see cref="PdfString.GetText"/> exactly.</summary>
    private static byte[] EncodeTextString(string text) => [.. Bom, .. System.Text.Encoding.BigEndianUnicode.GetBytes(text)];

    private static readonly byte[] Bom = [0xFE, 0xFF];

    private static string FormatPdfDate(DateTimeOffset time)
    {
        var sign = time.Offset < TimeSpan.Zero ? '-' : '+';
        var offset = time.Offset.Duration();
        return $"D:{time:yyyyMMddHHmmss}{sign}{offset.Hours:D2}'{offset.Minutes:D2}'";
    }
}
