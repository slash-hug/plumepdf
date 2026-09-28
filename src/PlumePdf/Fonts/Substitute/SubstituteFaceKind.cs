namespace PlumePdf.Fonts.Substitute;

/// <summary>
/// The program format of a bundled substitute face — which parser <see cref="SubstituteFontStore"/>
/// must hand its bytes to. Derived from which generated manifest owns the face key: the twelve
/// Liberation faces are TrueType (<c>SubstituteFontBlobs</c>); PDFium's
/// Foxit Symbol/Dingbats faces are bare CFF (<c>SubstituteCffBlobs</c>).
/// </summary>
internal enum SubstituteFaceKind
{
    /// <summary>A TrueType/OpenType program parsed by <see cref="TrueTypeFontProgram"/>.</summary>
    TrueType,

    /// <summary>A bare CFF program parsed by <see cref="Outlines.CffParser"/>, glyphs selected by name.</summary>
    Cff,
}
