namespace Snap2HTML.Core.Models;

/// <summary>
/// Represents the integrity status of a file after validation.
/// Applies to all supported file types (images, PDFs, documents, etc.).
/// </summary>
public enum IntegrityStatus
{
    /// <summary>
    /// File has not been validated.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// File passed integrity validation.
    /// </summary>
    Valid = 1,

    /// <summary>
    /// File has invalid magic bytes (file signature doesn't match expected format).
    /// </summary>
    InvalidMagicBytes = 2,

    /// <summary>
    /// File failed full validation (corrupt or invalid data).
    /// </summary>
    DecodingFailed = 3,

    /// <summary>
    /// File type is not supported by any registered integrity validator.
    /// </summary>
    NotSupported = 4
}
