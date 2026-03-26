namespace Snap2HTML.Core.Models;

/// <summary>
/// Settings for creating a directory snapshot.
/// </summary>
public class SnapSettings
{
    /// <summary>
    /// The root folder to scan.
    /// </summary>
    public string RootFolder { get; set; } = string.Empty;

    /// <summary>
    /// The title for the generated HTML output.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The output file path for the generated HTML.
    /// </summary>
    public string OutputFile { get; set; } = string.Empty;

    /// <summary>
    /// Whether to skip hidden files and folders.
    /// </summary>
    public bool SkipHiddenItems { get; set; } = true;

    /// <summary>
    /// Whether to skip system files and folders.
    /// </summary>
    public bool SkipSystemItems { get; set; } = true;

    /// <summary>
    /// Whether to open the output in browser after generation.
    /// </summary>
    public bool OpenInBrowser { get; set; }

    /// <summary>
    /// Whether to make files clickable links in the output.
    /// </summary>
    public bool LinkFiles { get; set; }

    /// <summary>
    /// The root path for file links.
    /// </summary>
    public string LinkRoot { get; set; } = string.Empty;

    /// <summary>
    /// Whether to compute file hashes during scanning.
    /// </summary>
    public bool EnableHashing { get; set; }

    /// <summary>
    /// The level of file integrity validation to perform during scanning.
    /// Applies to all supported file types (images, PDFs, documents, etc.).
    /// </summary>
    public IntegrityValidationLevel IntegrityLevel { get; set; } = IntegrityValidationLevel.None;
}
