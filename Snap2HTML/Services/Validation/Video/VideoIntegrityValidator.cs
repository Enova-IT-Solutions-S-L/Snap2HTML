using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation.Video;

/// <summary>
/// Video integrity validator.
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// Currently implements magic bytes validation only.
/// </summary>
public class VideoIntegrityValidator : FileIntegrityValidatorBase, IVideoIntegrityValidator
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".webm", ".flv", ".wmv", ".mpg", ".mpeg", ".3gp"
    };

    /// <summary>
    /// Magic bytes signatures for common video formats.
    /// </summary>
    private static readonly (byte[] Signature, int Offset, string Format)[] MagicSignatures =
    [
        // AVI: RIFF....AVI
        (new byte[] { 0x52, 0x49, 0x46, 0x46 }, 0, "AVI"),
        // Matroska (MKV/WebM): EBML header 0x1A 0x45 0xDF 0xA3
        (new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, 0, "Matroska"),
        // FLV: "FLV" (0x46 0x4C 0x56)
        (new byte[] { 0x46, 0x4C, 0x56 }, 0, "FLV"),
        // WMV/ASF: ASF header GUID 0x30 0x26 0xB2 0x75 0x8E 0x66 0xCF 0x11
        (new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11 }, 0, "WMV"),
        // MPEG-TS: sync byte 0x47 at offset 0 (transport stream)
        (new byte[] { 0x47 }, 0, "MPEG-TS"),
        // MPEG-PS: pack start code 0x00 0x00 0x01 0xBA
        (new byte[] { 0x00, 0x00, 0x01, 0xBA }, 0, "MPEG-PS"),
        // MPEG-1/2 video: video start code 0x00 0x00 0x01 0xB3
        (new byte[] { 0x00, 0x00, 0x01, 0xB3 }, 0, "MPEG-Video")
    ];

    /// <summary>
    /// AVI sub-type signature at offset 8.
    /// </summary>
    private static readonly byte[] AviSubSignature = { 0x41, 0x56, 0x49, 0x20 }; // "AVI "

    /// <summary>
    /// ISO Base Media File Format (MP4/MOV/3GP) "ftyp" box marker at offset 4.
    /// </summary>
    private static readonly byte[] FtypSignature = { 0x66, 0x74, 0x79, 0x70 }; // "ftyp"

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => VideoExtensions;

    /// <summary>
    /// Need at least 12 bytes to check RIFF+AVI and ftyp box signatures.
    /// </summary>
    protected override int MagicBytesBufferSize => 16;

    /// <inheritdoc />
    protected override bool CheckMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4) return false;

        // Check ISO Base Media (MP4, MOV, 3GP, M4V): "ftyp" at offset 4
        if (header.Length >= 8)
        {
            var isFtyp = true;
            for (var i = 0; i < FtypSignature.Length; i++)
            {
                if (header[4 + i] != FtypSignature[i])
                {
                    isFtyp = false;
                    break;
                }
            }
            if (isFtyp) return true;
        }

        // Check against known signatures
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
                // RIFF container: verify AVI sub-type at offset 8
                if (format == "AVI")
                {
                    if (header.Length < 12) return false;
                    for (var i = 0; i < AviSubSignature.Length; i++)
                    {
                        if (header[8 + i] != AviSubSignature[i])
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
    /// <remarks>
    /// TODO: Implement full video validation using a media library (e.g., FFProbe via wrapper, MediaInfo) in a future phase.
    /// For now, if magic bytes passed, we consider the file valid at FullDecode level.
    /// This means FullDecode behaves the same as MagicBytesOnly for videos until a library is integrated.
    /// </remarks>
    protected override ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        // TODO: Implement full video structural validation with a media library.
        // Candidates: FFProbe wrapper (e.g., Xabe.FFmpeg MIT), MediaInfo .NET wrapper.
        // When implemented, this should attempt to read container metadata/moov atom
        // and return DecodingFailed if the container structure is invalid.
        return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
    }
}
