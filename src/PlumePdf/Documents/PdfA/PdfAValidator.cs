using System.Text.RegularExpressions;
using PlumePdf.Objects;

namespace PlumePdf.Documents.PdfA;

/// <summary>
/// PlumePDF's own bounded PDF/A structural self-check. This is deliberately narrow — an honestly enumerated slice of the PDF/A-1b
/// and PDF/A-2b structural rules, TDD'd against the labelled veraPDF-corpus fixtures under
/// <c>corpora/veraPDF-corpus-master/PDF_A-1b</c> and <c>PDF_A-2b</c> — never a claim of
/// veraPDF-equivalent completeness. The veraPDF CLI (<c>PlumePdf.CorpusTests.VeraPdfInteropTests</c>,
/// run over CI's <c>corpus</c> lane) is the CI-gating exit-demo oracle; this validator is a
/// cheap, in-process, always-available companion a caller can run without any external tool
/// installed.
/// </summary>
/// <remarks>
/// Rule authority (extending the clean-room policy in AGENTS.md for PDF/A/UA): every rule below traces to
/// either free ISO 32000-1 base grammar or a veraPDF-corpus fixture's own self-documenting
/// outline (many corpus fixtures carry an <c>/Outlines</c> bookmark titled
/// <c>"expected message: ..."</c> describing exactly what that fixture tests — read here as
/// plain fixture metadata, the same way a test file's own docstring would be read). veraPDF's
/// validation profiles and source are never consulted (off-limits, GPLv3/MPLv2, not on
/// the clean-room policy's permissive allowlist).
/// </remarks>
public static class PdfAValidator
{
    private const string RuleEncryptionAbsent = "EncryptionAbsent";
    private const string RuleVersionCeiling = "VersionCeiling";
    private const string RuleForbiddenFilters = "ForbiddenFilters";
    private const string RuleOutputIntent = "OutputIntent";
    private const string RuleNeedAppearancesAbsent = "NeedAppearancesAbsent";
    private const string RuleIdentification = "PdfAIdentification";
    private const string RuleDocInfoXmpAgreement = "DocInfoXmpAgreement";

    /// <summary>
    /// Validates <paramref name="document"/> against the enumerated PDF/A-1b/2b structural
    /// subset. The target part/conformance is read from the document's own XMP
    /// <c>pdfaid:part</c>/<c>pdfaid:conformance</c> declaration (never a caller-supplied
    /// target) — a document that declares no conformance fails the identification rule and
    /// every part-specific rule is reported <see cref="PdfARuleStatus.NotChecked"/> (this
    /// self-check cannot know which rule set applies).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static PdfAValidationResult Validate(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var xmpText = document.GetXmpMetadataText();
        var (part, conformance, extraneousProperty) = ReadPdfaId(xmpText);

        var findings = new List<PdfARuleFinding>();
        AddIdentificationFindings(findings, xmpText, part, conformance, extraneousProperty);
        AddEncryptionFinding(findings, document);
        AddVersionCeilingFinding(findings, document, part);
        AddNeedAppearancesFinding(findings, document);
        AddOutputIntentFinding(findings, document);
        AddDocInfoXmpAgreementFindings(findings, document, xmpText, part);

        if (part == "1")
        {
            findings.AddRange(PdfARuleSet1b.Evaluate(document));
        }
        else if (part == "2")
        {
            findings.AddRange(PdfARuleSet2b.Evaluate(document));
        }
        else
        {
            findings.Add(NotChecked(RuleForbiddenFilters, "No PDF/A part could be determined from XMP; the part-specific forbidden-filter rule cannot be evaluated.", clauseId: null));
        }

        return new PdfAValidationResult(part, conformance, findings);
    }

    // ---- Identification (pdfaid) --------------------------------------------------------
    // veraPDF-corpus clause "6.7.11" (PDF_A-1b, 0 pass / 4 fail) and "6.6.4" (PDF_A-2b, 2
    // pass / 6 fail): "Version and conformance level identification". Fixture outlines
    // observed: "part"/"conformance" property missing, wrong prefix, wrong namespace,
    // "part"/"conformance" property contains an incorrect value.

    // Every Regex in this validator runs over document-supplied XMP text, which is exactly as
    // adversarial as any other document-supplied bytes — RegexOptions.NonBacktracking (none of
    // these patterns need backreferences or lookarounds) guarantees linear-time matching, so a
    // hostile packet full of repeated near-miss prefixes (e.g. thousands of unclosed start
    // tags) can never drive catastrophic backtracking. Belt braces the suspenders:
    // PdfOptions.MaxXmpPacketReadBytes already bounds the packet's size before any scan runs.
    private const RegexOptions SafeScan = RegexOptions.NonBacktracking;

    // The PDF/A Identification Extension Schema (a small, widely-documented XMP schema —
    // e.g. the AIIM/PDF Association's own published PDF/A-1 technical corrigenda note it
    // directly; not ISO 19005 prose, not veraPDF profile text) declares exactly three
    // properties under the conventional "pdfaid" prefix: part, conformance, and (for
    // corrigendum tracking) amd. The corpus's "wrong prefix" fixtures use a working but
    // non-conventional prefix (e.g. "nonpdfaid") bound to the correct namespace URI — the
    // literal "pdfaid" prefix is therefore required, not just "some prefix bound to this
    // namespace". Anchored on the whole element (not just its xmlns declaration) so the
    // part/conformance/extraneous-property reads below are scoped to the one
    // rdf:Description that actually declares the schema, never a same-named attribute
    // elsewhere in the packet. (Under NonBacktracking a lazy quantifier matches greedily, so
    // [^>] deliberately keeps every quantifier from crossing a tag boundary instead.)
    private static readonly Regex PdfaidElement = new(
        """<[\w:]+\s[^>]*xmlns:pdfaid\s*=\s*["']http://www\.aiim\.org/pdfa/ns/id/?["'][^>]*/?>""",
        SafeScan | RegexOptions.Singleline);

    private static readonly Regex PdfaidAttributeName = new("""pdfaid:(?<name>[A-Za-z0-9_]+)\s*=""", SafeScan);

    // part/conformance are the two required properties; amd (amendment) and corr
    // (corrigendum) are the schema's two optional revision markers. corr's *value* is
    // checked against ISO 19005's published corrigenda list by veraPDF (a check this
    // self-check does not attempt — no ClauseId is claimed for that specific sub-case); its
    // mere presence is legitimate and must not be flagged.
    private static readonly string[] KnownPdfaidProperties = ["part", "conformance", "amd", "corr"];

    // Matches an element-form pdfaid property's opening tag (<pdfaid:part>…): closing tags
    // never match (their '<' is followed by '/'), so this finds declared property names only.
    private static readonly Regex PdfaidElementPropertyName = new("""<pdfaid:(?<name>[A-Za-z0-9_]+)[\s>/]""", SafeScan);

    internal static (string? Part, string? Conformance, string? ExtraneousProperty) ReadPdfaId(string? xmpText)
    {
        if (string.IsNullOrEmpty(xmpText))
        {
            return (null, null, null);
        }

        var elementMatch = PdfaidElement.Match(xmpText);
        if (!elementMatch.Success)
        {
            return (null, null, null);
        }

        // The schema's properties come in both mainstream RDF serializations: attributes on
        // the declaring rdf:Description's start tag, or child elements inside it (PlumePDF's
        // own XmpWriter emits the element form). The scope for reading them is therefore the
        // whole declaring element — start tag through its closing tag — not just the start
        // tag, while still never straying into an unrelated same-named property elsewhere in
        // the packet.
        var elementText = ScopeToWholeElement(xmpText, elementMatch);
        var part = ReadDeclaredProperty(elementText, "pdfaid", "part");
        var conformance = ReadDeclaredProperty(elementText, "pdfaid", "conformance");

        string? extraneous = null;
        foreach (Match attribute in PdfaidAttributeName.Matches(elementText))
        {
            var name = attribute.Groups["name"].Value;
            if (Array.IndexOf(KnownPdfaidProperties, name) < 0)
            {
                extraneous = name;
                break;
            }
        }

        if (extraneous is null)
        {
            foreach (Match property in PdfaidElementPropertyName.Matches(elementText))
            {
                var name = property.Groups["name"].Value;
                if (Array.IndexOf(KnownPdfaidProperties, name) < 0)
                {
                    extraneous = name;
                    break;
                }
            }
        }

        return (part, conformance, extraneous);
    }

    /// <summary>
    /// Widens a matched start tag to its whole element: a self-closing tag is already complete;
    /// otherwise the scope runs through the element's own closing tag (or, defensively, to the
    /// end of the packet when no closing tag is found in malformed XMP).
    /// </summary>
    private static string ScopeToWholeElement(string xmpText, Match startTagMatch)
    {
        var startTag = startTagMatch.Value;
        if (startTag.EndsWith("/>", StringComparison.Ordinal))
        {
            return startTag;
        }

        var nameMatch = Regex.Match(startTag, @"^<(?<name>[\w:]+)", SafeScan);
        if (!nameMatch.Success)
        {
            return startTag;
        }

        var closingTag = $"</{nameMatch.Groups["name"].Value}>";
        var closeIndex = xmpText.IndexOf(closingTag, startTagMatch.Index + startTag.Length, StringComparison.Ordinal);
        return closeIndex < 0
            ? xmpText[startTagMatch.Index..]
            : xmpText[startTagMatch.Index..(closeIndex + closingTag.Length)];
    }

    private static string? ReadDeclaredProperty(string xmpText, string prefix, string localName)
    {
        // Both common RDF serializations: an attribute (pdfaid:part="1") and an element
        // (<pdfaid:part>1</pdfaid:part>) — mainstream XMP writers, including PlumePDF's own
        // future XmpWriter, use one or the other.
        var attribute = Regex.Match(xmpText, $"""{prefix}:{localName}\s*=\s*["'](?<value>[^"']*)["']""", SafeScan);
        if (attribute.Success)
        {
            return attribute.Groups["value"].Value;
        }

        var element = Regex.Match(xmpText, $"<{prefix}:{localName}>(?<value>[^<]*)</{prefix}:{localName}>", SafeScan);
        return element.Success ? element.Groups["value"].Value : null;
    }

    private static void AddIdentificationFindings(List<PdfARuleFinding> findings, string? xmpText, string? part, string? conformance, string? extraneousProperty)
    {
        if (string.IsNullOrEmpty(xmpText))
        {
            findings.Add(Fail(RuleIdentification, "No XMP metadata stream is present; PDF/A requires a pdfaid:part/pdfaid:conformance declaration.", ClauseFor(null)));
            return;
        }

        if (part is null)
        {
            findings.Add(Fail(RuleIdentification, "The XMP packet declares no pdfaid:part property under the conventional 'pdfaid' prefix bound to the http://www.aiim.org/pdfa/ns/id/ namespace (missing, wrong namespace, or wrong prefix).", ClauseFor(null)));
            return;
        }

        if (conformance is null)
        {
            findings.Add(Fail(RuleIdentification, "The XMP packet declares pdfaid:part but no pdfaid:conformance property.", ClauseFor(part)));
            return;
        }

        if (extraneousProperty is not null)
        {
            findings.Add(Fail(RuleIdentification, $"The PDF/A identification schema declares an unexpected property 'pdfaid:{extraneousProperty}' — only part, conformance, and amd are defined.", ClauseFor(part)));
            return;
        }

        var validPart = part is "1" or "2";
        var validConformance = conformance.Equals("B", StringComparison.Ordinal);
        if (!validPart || !validConformance)
        {
            findings.Add(Fail(RuleIdentification, $"pdfaid:part/pdfaid:conformance declare an unsupported combination ({part}/{conformance}); PlumePDF's self-check covers PDF/A-1B and PDF/A-2B only.", ClauseFor(part)));
            return;
        }

        findings.Add(Pass(RuleIdentification, $"Document declares PDF/A-{part}{conformance}.", ClauseFor(part)));

        // Only PDF_A-2b's "6.6.4" clause directory is claimed as covered (see
        // PdfAValidatorCorpusTests.CoveredClauses' remark: PDF_A-1b's sibling "6.7.11"
        // directory includes a pdfaid:corr *value* check this self-check does not attempt,
        // so that whole directory is intentionally left unclaimed, part "1" included).
        static string? ClauseFor(string? part) => part == "2" ? "6.6.4" : null;
    }

    // ---- Encryption absent ---------------------------------------------------------------
    // No dedicated corpus clause directory exists for this — it is base ISO 32000-1 /
    // ISO 19005 grammar (a PDF/A file's trailer never carries /Encrypt), so no ClauseId is
    // claimed.

    private static void AddEncryptionFinding(List<PdfARuleFinding> findings, PdfDocument document)
    {
        findings.Add(document.HasEncryptedSource
            ? Fail(RuleEncryptionAbsent, "The document's source carries an /Encrypt dictionary; PDF/A forbids encryption.", clauseId: null)
            : Pass(RuleEncryptionAbsent, "No /Encrypt dictionary present.", clauseId: null));
    }

    // ---- Version ceiling -------------------------------------------------------------------
    // No dedicated corpus clause directory exists for this specific check (the "6.1.2 File
    // header" fixtures test a subtler byte-level header-well-formedness rule this self-check
    // does not attempt) — this is the plain ISO 32000-1 §7.5.2 ceiling PDF/A-1 (based on PDF
    // 1.4) and PDF/A-2 (based on PDF 1.7) impose, so no ClauseId is claimed.

    private static void AddVersionCeilingFinding(List<PdfARuleFinding> findings, PdfDocument document, string? part)
    {
        var header = ReadHeaderVersion(document);
        if (header is null)
        {
            findings.Add(NotChecked(RuleVersionCeiling, "The document's original header bytes are not available (a composed, not-yet-saved document) — the version ceiling cannot be checked before Save.", clauseId: null));
            return;
        }

        var ceiling = part switch { "1" => (1, 4), "2" => (1, 7), _ => ((int, int)?)null };
        if (ceiling is null)
        {
            findings.Add(NotChecked(RuleVersionCeiling, "No PDF/A part was determined; the version ceiling depends on the part.", clauseId: null));
            return;
        }

        var (major, minor) = header.Value;
        var (ceilingMajor, ceilingMinor) = ceiling.Value;
        var withinCeiling = major < ceilingMajor || (major == ceilingMajor && minor <= ceilingMinor);
        findings.Add(withinCeiling
            ? Pass(RuleVersionCeiling, $"Header declares PDF {major}.{minor}, within the PDF/A-{part}B ceiling of {ceilingMajor}.{ceilingMinor}.", clauseId: null)
            : Fail(RuleVersionCeiling, $"Header declares PDF {major}.{minor}, exceeding the PDF/A-{part}B ceiling of {ceilingMajor}.{ceilingMinor}.", clauseId: null));
    }

    private static (int Major, int Minor)? ReadHeaderVersion(PdfDocument document)
    {
        if (document.Source is not { } source)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[16];
        var read = source.Read(0, buffer);
        var text = System.Text.Encoding.ASCII.GetString(buffer[..read]);
        var match = Regex.Match(text, @"%PDF-(?<major>\d+)\.(?<minor>\d+)", SafeScan);
        if (!match.Success)
        {
            return null;
        }

        return (int.Parse(match.Groups["major"].Value), int.Parse(match.Groups["minor"].Value));
    }

    // ---- /NeedAppearances absent ------------------------------------------------------------
    // No dedicated corpus clause directory tests this exact flag (the "6.9 Interactive Forms"
    // fixtures test missing widget appearance streams, a related but distinct rule this
    // self-check does not attempt) — PDF/A-1/2 both require every appearance to be
    // author-supplied rather than viewer-regenerated, so no ClauseId is claimed.

    private static void AddNeedAppearancesFinding(List<PdfARuleFinding> findings, PdfDocument document)
    {
        var acroForm = ResolveAcroForm(document);
        if (acroForm is null)
        {
            findings.Add(Pass(RuleNeedAppearancesAbsent, "No /AcroForm dictionary present.", clauseId: null));
            return;
        }

        var needAppearances = acroForm.TryGetValue(PdfName.NeedAppearances, out var value) && value is PdfBoolean { Value: true };
        findings.Add(needAppearances
            ? Fail(RuleNeedAppearancesAbsent, "/AcroForm's /NeedAppearances is true; PDF/A requires every widget's appearance to be author-supplied, not viewer-regenerated.", clauseId: null)
            : Pass(RuleNeedAppearancesAbsent, "/NeedAppearances is absent or false.", clauseId: null));
    }

    private static PdfDictionary? ResolveAcroForm(PdfDocument document)
    {
        var catalog = document.Catalog;
        if (catalog is null || !catalog.Dictionary.TryGetValue(PdfName.AcroForm, out var value))
        {
            return null;
        }

        return Resolve(document.Objects, value) as PdfDictionary;
    }

    // ---- Output intent ------------------------------------------------------------------
    // A bounded presence + ICC-header-shape check only. The full "6.2.2"/"6.2.3" corpus
    // clauses also cover edge cases this self-check does not attempt (multiple OutputIntents
    // disagreeing on their DestOutputProfile, malformed ICC profile parsing, uncalibrated
    // colour space usage without a matching intent) — no ClauseId is claimed, so those edge
    // cases stay honestly out of scope rather than silently misreported.

    private static void AddOutputIntentFinding(List<PdfARuleFinding> findings, PdfDocument document)
    {
        var catalog = document.Catalog;
        if (catalog is null || !catalog.Dictionary.TryGetValue(PdfName.Get("OutputIntents"), out var intentsValue))
        {
            findings.Add(Fail(RuleOutputIntent, "The catalog has no /OutputIntents array; PDF/A requires a GTS_PDFA1 output intent with an embedded ICC profile.", clauseId: null));
            return;
        }

        if (Resolve(document.Objects, intentsValue) is not PdfArray intents)
        {
            findings.Add(Fail(RuleOutputIntent, "/OutputIntents did not resolve to an array.", clauseId: null));
            return;
        }

        foreach (var entryValue in intents)
        {
            if (Resolve(document.Objects, entryValue) is not PdfDictionary intent)
            {
                continue;
            }

            // ISO 32000-1 §14.11.5 Table 365: an output intent dictionary's subtype key is /S
            // (not /Subtype — reading /Subtype here was a bug that exactly mirrored the
            // same bug in OutputIntentBuilder, so the two masked each other until the veraPDF
            // oracle saw the created output).
            if (!intent.TryGetValue(PdfName.Get("S"), out var subtype) || subtype is not PdfName { Value: "GTS_PDFA1" })
            {
                continue;
            }

            var hasConditionIdentifier = intent.TryGetValue(PdfName.Get("OutputConditionIdentifier"), out var identifierValue)
                && identifierValue is PdfString identifier && identifier.Bytes.Length > 0;
            if (!hasConditionIdentifier)
            {
                findings.Add(Fail(RuleOutputIntent, "The GTS_PDFA1 output intent has no non-empty /OutputConditionIdentifier.", clauseId: null));
                return;
            }

            if (!intent.TryGetValue(PdfName.Get("DestOutputProfile"), out var profileValue) || Resolve(document.Objects, profileValue) is not PdfStream profileStream)
            {
                findings.Add(Fail(RuleOutputIntent, "The GTS_PDFA1 output intent has no /DestOutputProfile ICC stream.", clauseId: null));
                return;
            }

            var iccFinding = ValidateIccProfileShape(profileStream, document.Options, document.Objects);
            findings.Add(iccFinding ?? Pass(RuleOutputIntent, "A GTS_PDFA1 output intent with a well-formed ICC profile is present.", clauseId: null));
            return;
        }

        findings.Add(Fail(RuleOutputIntent, "No /OutputIntents entry declares /S /GTS_PDFA1.", clauseId: null));
    }

    private static PdfARuleFinding? ValidateIccProfileShape(PdfStream profileStream, PdfOptions options, ObjectRegistry objects)
    {
        byte[] icc;
        try
        {
            icc = profileStream.GetDecodedBytes(PdfFilterRegistry.Default, options, r => objects[r]);
        }
        catch (PlumePdfException ex)
        {
            return Fail(RuleOutputIntent, $"The /DestOutputProfile stream could not be decoded: {ex.Message}", clauseId: null);
        }

        // ICC.1:2010 §7.2: a profile header is at least 128 bytes; the device class
        // (offset 12, 4 ASCII bytes) must be "mntr" (display) for a PDF output intent, and the
        // colour space (offset 16, 4 ASCII bytes) must name the space the intent's profile
        // actually represents.
        if (icc.Length < 128)
        {
            return Fail(RuleOutputIntent, $"The embedded ICC profile is only {icc.Length} bytes — shorter than a valid 128-byte ICC header.", clauseId: null);
        }

        var deviceClass = System.Text.Encoding.ASCII.GetString(icc, 12, 4);
        if (deviceClass != "mntr")
        {
            return Fail(RuleOutputIntent, $"The embedded ICC profile's deviceClass is '{deviceClass}', not the required 'mntr' (display).", clauseId: null);
        }

        var colorSpace = System.Text.Encoding.ASCII.GetString(icc, 16, 4).TrimEnd();
        if (colorSpace is not ("RGB" or "GRAY" or "CMYK"))
        {
            return Fail(RuleOutputIntent, $"The embedded ICC profile's colorSpace is '{colorSpace}', not one of RGB/GRAY/CMYK.", clauseId: null);
        }

        return null;
    }

    // ---- DocInfo / XMP agreement ----------------------------------------------------------
    // veraPDF-corpus clause "6.7.3" (PDF_A-1b only — the 2b corpus carries no equivalent
    // directory, so this rule is only claimed/evaluated for part 1): "Document information
    // dictionary". Nine fail fixtures observed, one per field pairing below.

    private static readonly (string InfoField, string XmpPrefix, string XmpLocalName)[] AgreementFields =
    [
        ("Title", "dc", "title"),
        ("Author", "dc", "creator"),
        ("Subject", "dc", "description"),
        ("Keywords", "pdf", "Keywords"),
        ("Creator", "xmp", "CreatorTool"),
        ("Producer", "pdf", "Producer"),
    ];

    private static void AddDocInfoXmpAgreementFindings(List<PdfARuleFinding> findings, PdfDocument document, string? xmpText, string? part)
    {
        if (part != "1")
        {
            findings.Add(NotChecked(RuleDocInfoXmpAgreement, "DocInfo/XMP field agreement (clause 6.7.3) is only evaluated for PDF/A-1B; the corpus carries no equivalent clause for PDF/A-2B.", "6.7.3"));
            return;
        }

        if (string.IsNullOrEmpty(xmpText))
        {
            findings.Add(NotChecked(RuleDocInfoXmpAgreement, "No XMP metadata stream is present; agreement cannot be evaluated (already reported by the identification rule).", "6.7.3"));
            return;
        }

        var info = document.GetInfo();
        var infoFieldValues = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Title"] = info.Title,
            ["Author"] = info.Author,
            ["Subject"] = info.Subject,
            ["Keywords"] = info.Keywords,
            ["Creator"] = info.Creator,
            ["Producer"] = info.Producer,
        };

        foreach (var (infoField, xmpPrefix, xmpLocalName) in AgreementFields)
        {
            var infoValue = infoFieldValues[infoField];
            var ruleId = $"{RuleDocInfoXmpAgreement}.{infoField}";

            // ISO 19005-1 6.7.3 only requires synchronization for entries the legacy Info
            // dictionary actually carries — corpus fixtures under this clause routinely omit
            // every Info field except the one specific field being tested, while XMP (the
            // richer, authoritative source) still carries the corresponding Dublin Core/XMP
            // properties for every field. An absent Info entry is therefore nothing to
            // disagree over, never a violation.
            if (infoValue is null)
            {
                findings.Add(Pass(ruleId, $"/{infoField} is not present in the Info dictionary (nothing to synchronize).", "6.7.3"));
                continue;
            }

            var (xmpValue, hasMultipleEntries) = ReadSimpleOrContainerText(xmpText, xmpPrefix, xmpLocalName);
            if (hasMultipleEntries)
            {
                // A container (rdf:Seq/rdf:Alt) with more than one rdf:li entry can never
                // synchronize with the Info dictionary's single plain-string value — always a
                // mismatch, regardless of which entry happens to equal it.
                findings.Add(Fail(ruleId, $"XMP {xmpPrefix}:{xmpLocalName} carries more than one entry, which cannot synchronize with the single-valued /{infoField} ('{infoValue}').", "6.7.3"));
            }
            else if (string.Equals(infoValue, xmpValue, StringComparison.Ordinal))
            {
                findings.Add(Pass(ruleId, $"/{infoField} agrees with {xmpPrefix}:{xmpLocalName}.", "6.7.3"));
            }
            else
            {
                findings.Add(Fail(ruleId, $"/{infoField} ('{infoValue}') does not match XMP {xmpPrefix}:{xmpLocalName} ('{xmpValue}').", "6.7.3"));
            }
        }

        var creationDateFinding = CompareDate("CreationDate", info.CreationDate, ReadXmpDate(xmpText, "xmp", "CreateDate"));
        findings.Add(creationDateFinding);
        var modDateFinding = CompareDate("ModDate", info.ModDate, ReadXmpDate(xmpText, "xmp", "ModifyDate"));
        findings.Add(modDateFinding);
    }

    private static PdfARuleFinding CompareDate(string infoField, DateTimeOffset? infoValue, DateTimeOffset? xmpValue)
    {
        var ruleId = $"{RuleDocInfoXmpAgreement}.{infoField}";

        // Same "nothing to disagree over" rule as the string fields above.
        if (infoValue is null)
        {
            return Pass(ruleId, $"/{infoField} is not present in the Info dictionary (nothing to synchronize).", "6.7.3");
        }

        if (infoValue == xmpValue)
        {
            return Pass(ruleId, $"/{infoField} agrees with its XMP counterpart.", "6.7.3");
        }

        return Fail(ruleId, $"/{infoField} ('{infoValue}') does not match its XMP date counterpart ('{xmpValue}').", "6.7.3");
    }

    // dc:title/dc:description are RDF "language alternative" containers
    // (<dc:title><rdf:Alt><rdf:li xml:lang="x-default">text</rdf:li></rdf:Alt></dc:title>);
    // dc:creator is an RDF "sequence" (<rdf:Seq><rdf:li>text</rdf:li></rdf:Seq>) — both are
    // read as their first <rdf:li> entry. Every other field here is a simple text element or
    // attribute. This is a bounded reader for the common mainstream-writer serializations
    // (including PlumePDF's own future XmpWriter), not a general RDF/XML processor.
    private static (string? Value, bool HasMultipleEntries) ReadSimpleOrContainerText(string xmpText, string prefix, string localName)
    {
        var containerBlock = Regex.Match(xmpText, $"<{prefix}:{localName}>(?<body>.*?)</{prefix}:{localName}>", SafeScan | RegexOptions.Singleline);
        if (containerBlock.Success)
        {
            var entries = Regex.Matches(containerBlock.Groups["body"].Value, "<rdf:li[^>]*>(?<value>.*?)</rdf:li>", SafeScan | RegexOptions.Singleline);
            if (entries.Count > 0)
            {
                return (System.Net.WebUtility.HtmlDecode(entries[0].Groups["value"].Value), entries.Count > 1);
            }
        }

        var declared = ReadDeclaredProperty(xmpText, prefix, localName);
        return (declared is { } value ? System.Net.WebUtility.HtmlDecode(value) : null, false);
    }

    private static DateTimeOffset? ReadXmpDate(string xmpText, string prefix, string localName)
    {
        var text = ReadDeclaredProperty(xmpText, prefix, localName);
        return text is not null && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    // ---- Shared helpers used by PdfARuleSet1b/2b too ---------------------------------------

    internal static PdfObject Resolve(ObjectRegistry objects, PdfObject value) =>
        value is PdfReference reference ? objects[reference.Target] : value;

    internal static PdfARuleFinding Pass(string ruleId, string message, string? clauseId) => new(ruleId, PdfARuleStatus.Pass, message, clauseId);

    internal static PdfARuleFinding Fail(string ruleId, string message, string? clauseId) => new(ruleId, PdfARuleStatus.Fail, message, clauseId);

    internal static PdfARuleFinding NotChecked(string ruleId, string message, string? clauseId) => new(ruleId, PdfARuleStatus.NotChecked, message, clauseId);
}

/// <summary>
/// A bounded, cycle-safe walk of a document's reachable object graph, used by
/// <see cref="PdfARuleSet1b"/>'s forbidden-filter rule to find a stream matching a predicate
/// regardless of whether it is referenced from a content stream (the corpus's
/// <c>6-1-10-t01-fail-c</c> fixture specifically tests an XObject present in a resources
/// dictionary but never painted — a pure content-stream-usage walk would miss it).
/// </summary>
internal static class PdfAObjectGraphWalker
{
    // A resource-safety cap, not a document-validity concern: the visited
    // set already bounds work to the document's distinct object count, so this only guards
    // against a pathologically cyclic or oversized graph doing unbounded work.
    private const int MaxVisits = 5_000_000;

    /// <summary>
    /// Walks every object reachable from <paramref name="root"/> (typically the catalog
    /// dictionary), returning the <see cref="IndirectReference"/> of the first stream for
    /// which <paramref name="predicate"/> returns <see langword="true"/>, or
    /// <see langword="null"/> if none matches.
    /// </summary>
    public static IndirectReference? FindFirstStreamMatching(ObjectRegistry objects, PdfDictionary root, PdfOptions options, Func<PdfStream, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(predicate);

        var visited = new HashSet<int>();
        var stack = new Stack<PdfObject>();
        stack.Push(root);
        var visits = 0;

        while (stack.Count > 0 && visits++ < MaxVisits)
        {
            switch (stack.Pop())
            {
                case PdfReference reference:
                    if (!visited.Add(reference.Target.Number))
                    {
                        continue;
                    }

                    var resolved = objects[reference.Target];
                    if (resolved is PdfStream candidate && predicate(candidate))
                    {
                        return reference.Target;
                    }

                    stack.Push(resolved);
                    break;

                case PdfDictionary dictionary:
                    foreach (var value in dictionary.Values)
                    {
                        stack.Push(value);
                    }

                    break;

                case PdfArray array:
                    foreach (var item in array)
                    {
                        stack.Push(item);
                    }

                    break;

                case PdfStream stream:
                    // Walk into the stream's own dictionary (e.g. a Form XObject's /Resources)
                    // even though this stream itself already failed the predicate check above
                    // (or was the root's direct child, never itself indirect).
                    stack.Push(stream.Dictionary);
                    break;
            }
        }

        return null;
    }
}
