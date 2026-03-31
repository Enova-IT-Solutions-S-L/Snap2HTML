using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation.Audio;

/// <summary>
/// Audio integrity validator.
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// Currently implements magic bytes validation only.
/// </summary>
public class AudioIntegrityValidator : FileIntegrityValidatorBase, IAudioIntegrityValidator
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".aiff", ".aif"
    };

    /// <summary>
    /// Magic bytes signatures for common audio formats.
    /// </summary>
    private static readonly (byte[] Signature, int Offset, string Format)[] MagicSignatures =
    [
        // MP3: ID3 tag header (ID3v2)
        (new byte[] { 0x49, 0x44, 0x33 }, 0, "MP3-ID3"),
        // MP3: MPEG audio frame sync (0xFF 0xFB, 0xFF 0xF3, 0xFF 0xF2)
        (new byte[] { 0xFF, 0xFB }, 0, "MP3-Sync1"),
        (new byte[] { 0xFF, 0xF3 }, 0, "MP3-Sync2"),
        (new byte[] { 0xFF, 0xF2 }, 0, "MP3-Sync3"),
        // WAV: RIFF container
        (new byte[] { 0x52, 0x49, 0x46, 0x46 }, 0, "WAV"),
        // FLAC: "fLaC" (0x66 0x4C 0x61 0x43)
        (new byte[] { 0x66, 0x4C, 0x61, 0x43 }, 0, "FLAC"),
        // OGG (Vorbis/Opus): "OggS" (0x4F 0x67 0x67 0x53)
        (new byte[] { 0x4F, 0x67, 0x67, 0x53 }, 0, "OGG"),
        // WMA/ASF: same ASF header GUID as WMV
        (new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11 }, 0, "WMA"),
        // AIFF: "FORM" header
        (new byte[] { 0x46, 0x4F, 0x52, 0x4D }, 0, "AIFF")
    ];

    /// <summary>
    /// WAV sub-type signature "WAVE" at offset 8.
    /// </summary>
    private static readonly byte[] WaveSubSignature = { 0x57, 0x41, 0x56, 0x45 }; // "WAVE"

    /// <summary>
    /// AIFF sub-type signature "AIFF" at offset 8.
    /// </summary>
    private static readonly byte[] AiffSubSignature = { 0x41, 0x49, 0x46, 0x46 }; // "AIFF"

    /// <summary>
    /// AIFF-C sub-type signature "AIFC" at offset 8.
    /// </summary>
    private static readonly byte[] AifcSubSignature = { 0x41, 0x49, 0x46, 0x43 }; // "AIFC"

    /// <summary>
    /// ISO Base Media "ftyp" marker for M4A at offset 4.
    /// </summary>
    private static readonly byte[] FtypSignature = { 0x66, 0x74, 0x79, 0x70 }; // "ftyp"

    /// <inheritdoc />
    public override string CategoryName => "Audio";

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => AudioExtensions;

    /// <summary>
    /// Need at least 12 bytes to check RIFF+WAVE, FORM+AIFF, and ftyp box signatures.
    /// </summary>
    protected override int MagicBytesBufferSize => 16;

    /// <inheritdoc />
    protected override bool CheckMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2) return false;

        // Check ISO Base Media (M4A, AAC in MP4 container): "ftyp" at offset 4
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
                // RIFF container: verify WAVE sub-type at offset 8
                if (format == "WAV")
                {
                    if (header.Length < 12) return false;
                    for (var i = 0; i < WaveSubSignature.Length; i++)
                    {
                        if (header[8 + i] != WaveSubSignature[i])
                            return false;
                    }
                }

                // FORM container: verify AIFF or AIFC sub-type at offset 8
                if (format == "AIFF")
                {
                    if (header.Length < 12) return false;
                    var isAiff = true;
                    for (var i = 0; i < AiffSubSignature.Length; i++)
                    {
                        if (header[8 + i] != AiffSubSignature[i])
                        {
                            isAiff = false;
                            break;
                        }
                    }
                    if (!isAiff)
                    {
                        // Check AIFC variant
                        var isAifc = true;
                        for (var i = 0; i < AifcSubSignature.Length; i++)
                        {
                            if (header[8 + i] != AifcSubSignature[i])
                            {
                                isAifc = false;
                                break;
                            }
                        }
                        if (!isAifc) return false;
                    }
                }

                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// TODO: Implement full audio validation using a media library (e.g., NAudio, FFProbe wrapper) in a future phase.
    /// For now, if magic bytes passed, we consider the file valid at FullDecode level.
    /// This means FullDecode behaves the same as MagicBytesOnly for audio until a library is integrated.
    /// </remarks>
    protected override ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        // TODO: Implement full audio structural validation with a media library.
        // Candidates: NAudio (MIT, .NET native), FFProbe wrapper (Xabe.FFmpeg MIT).
        // When implemented, this should attempt to read audio stream metadata/frames
        // and return DecodingFailed if the file structure is invalid.
        return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
    }
}
