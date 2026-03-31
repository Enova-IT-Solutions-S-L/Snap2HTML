namespace Snap2HTML.Services.Validation.Document;

/// <summary>
/// Interface for document integrity validation.
/// Covers Office (legacy OLE2 and modern OOXML), OpenDocument (ODF), and RTF formats.
/// Extends <see cref="IFileIntegrityValidator"/> with document-specific capabilities.
/// </summary>
public interface IDocumentIntegrityValidator : IFileIntegrityValidator
{
}
