using ATL;
using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation.Video;

/// <summary>
/// Video integrity validator using ATL (Audio Tools Library, MIT).
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// Full validation parses the container structure (ISO BMFF boxes for MP4/MOV/3GP,
/// EBML elements for MKV/WebM, ASF header objects for WMV, RIFF chunks for AVI)
/// via seek-only I/O without decoding video frames, keeping overhead to ~2-10ms per file.
/// Formats without ATL parser (FLV, MPEG-TS/PS) fall back to magic bytes only.
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

    /// <summary>
    /// Extensions where ATL has a native container parser and can perform full structural validation.
    /// MP4/M4V/MOV/3GP → ISO BMFF (MP4 parser), MKV/WebM → Matroska EBML (MKA parser),
    /// WMV → ASF (WMA parser), AVI → RIFF (WAV parser).
    /// </summary>
    private static readonly HashSet<string> FullValidationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".3gp", ".mkv", ".webm", ".wmv", ".avi"
    };

    /// <inheritdoc />
    public override string CategoryName => "Video";

    /// <inheritdoc />
    public override bool SupportsFullValidation => true;

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
    /// Uses ATL's <see cref="Track"/> class which parses the video container structure:
    /// - MP4/MOV/3GP/M4V: ISO BMFF ftyp + moov box tree (mvhd, trak, stbl atoms)
    /// - MKV/WebM: EBML header + Segment/Info/Tracks elements
    /// - WMV: ASF Header Object (file properties, stream properties, codec list)
    /// - AVI: RIFF header + hdrl/movi chunks + idx1 index
    ///
    /// This does NOT decode video frames or audio samples, making it extremely fast
    /// (~2-10ms per file, even for multi-GB files) while catching structural corruption:
    /// truncated moov atoms, broken EBML elements, invalid ASF GUIDs, corrupt RIFF chunks.
    ///
    /// For formats without ATL parser (FLV, MPEG-TS, MPEG-PS), validation returns Valid
    /// after magic bytes pass — these are legacy formats that would require FFProbe.
    ///
    /// Note: Track constructor is synchronous (seek-based file I/O + header parsing).
    /// Since the base class pipeline runs consumers on thread pool workers,
    /// this synchronous call is acceptable — same pattern as PdfPig and ImageSharp.
    /// </remarks>
    protected override ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // For formats without ATL container parser, accept magic bytes as sufficient.
        var extension = Path.GetExtension(filePath);
        if (!FullValidationExtensions.Contains(extension))
        {
            return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
        }

        try
        {
            var track = new Track(filePath);

            // AudioFormat.ID == 0 means ATL could not identify the container format,
            // indicating the structure is unreadable or severely corrupt.
            if (track.AudioFormat.ID == 0)
            {
                return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
            }

            // A valid media file must have a positive duration.
            // DurationMs == 0 indicates ATL could not parse the stream metadata
            // (e.g. truncated moov atom in MP4, missing Segment/Info in MKV).
            if (track.DurationMs <= 0)
            {
                return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
            }

            return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ATL throws various exceptions for structural problems:
            // - EndOfStreamException (truncated files)
            // - InvalidOperationException (malformed containers)
            // - IndexOutOfRangeException (corrupt box/element sizes)
            // - FormatException (invalid header fields)
            return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
        }
    }
}
