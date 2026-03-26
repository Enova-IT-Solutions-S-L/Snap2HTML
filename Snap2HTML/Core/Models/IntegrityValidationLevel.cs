namespace Snap2HTML.Core.Models;

/// <summary>
/// Defines the level of file integrity validation.
/// Applies to all supported file types (images, PDFs, documents, etc.).
/// </summary>
public enum IntegrityValidationLevel
{
    /// <summary>
    /// No validation is performed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Validates only the magic bytes (file signature).
    /// Fast but only checks if the file header matches known formats.
    /// </summary>
    MagicBytesOnly = 1,

    /// <summary>
    /// Performs full validation using format-specific libraries.
    /// For images: ImageSharp's Image.Identify(). For PDFs: not yet implemented.
    /// More thorough but slower than magic bytes validation.
    /// </summary>
    FullDecode = 2
}
