using SixLabors.ImageSharp;
using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation.Image;

/// <summary>
/// Image integrity validator using ImageSharp.
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// </summary>
public class ImageIntegrityValidator : FileIntegrityValidatorBase, IImageIntegrityValidator
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif"
    };

    /// <summary>
    /// Magic bytes signatures for common image formats.
    /// </summary>
    private static readonly (byte[] Signature, int Offset, string Format)[] MagicSignatures =
    [
        (new byte[] { 0xFF, 0xD8, 0xFF }, 0, "JPEG"),
        (new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, "PNG"),
        (new byte[] { 0x47, 0x49, 0x46, 0x38 }, 0, "GIF"),
        (new byte[] { 0x42, 0x4D }, 0, "BMP"),
        (new byte[] { 0x52, 0x49, 0x46, 0x46 }, 0, "WebP"), // RIFF
        (new byte[] { 0x49, 0x49, 0x2A, 0x00 }, 0, "TIFF LE"),
        (new byte[] { 0x4D, 0x4D, 0x00, 0x2A }, 0, "TIFF BE")
    ];

    /// <summary>
    /// WebP signature continuation (after RIFF header).
    /// </summary>
    private static readonly byte[] WebPSignature = { 0x57, 0x45, 0x42, 0x50 };

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => ImageExtensions;

    /// <summary>
    /// Checks if the file extension is a supported image format.
    /// </summary>
    public static bool IsImageExtension(string extension)
    {
        return ImageExtensions.Contains(extension);
    }

    /// <inheritdoc />
    protected override bool CheckMagicBytes(ReadOnlySpan<byte> header)
    {
        foreach (var (signature, offset, format) in MagicSignatures)
        {
            if (header.Length < offset + signature.Length) continue;

            var match = true;
            for (var i = 0; i < signature.Length; i++)
            {
                if (header[offset + i] != signature[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                // Additional check for WebP (RIFF header + WEBP at offset 8)
                if (format == "WebP")
                {
                    if (header.Length < 12) return false;
                    for (var i = 0; i < WebPSignature.Length; i++)
                    {
                        if (header[8 + i] != WebPSignature[i])
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    protected override async ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        try
        {
            // Use Image.Identify which is cheaper than Image.Load
            // It only reads metadata without fully decoding the image
            var info = await SixLabors.ImageSharp.Image.IdentifyAsync(filePath, ct);
            return info != null ? IntegrityStatus.Valid : IntegrityStatus.DecodingFailed;
        }
        catch (UnknownImageFormatException)
        {
            return IntegrityStatus.DecodingFailed;
        }
        catch (InvalidImageContentException)
        {
            return IntegrityStatus.DecodingFailed;
        }
        catch (NotSupportedException)
        {
            return IntegrityStatus.DecodingFailed;
        }
    }
}
