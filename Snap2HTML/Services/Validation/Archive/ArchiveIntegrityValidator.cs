using SharpCompress.Archives;
using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation.Archive;

/// <summary>
/// Archive/compressed file integrity validator.
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// Full validation uses SharpCompress (MIT) to parse archive structural metadata
/// (central directories, header blocks, entry indexes) without decompressing data.
/// Formats not supported by SharpCompress fall back to magic-bytes-only validation.
/// </summary>
public class ArchiveIntegrityValidator : FileIntegrityValidatorBase, IArchiveIntegrityValidator
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".gz", ".tar", ".bz2", ".xz", ".zst", ".lz4", ".cab", ".iso"
    };

    /// <summary>
    /// Extensions for which SharpCompress can perform full structural validation.
    /// Formats not in this set fall back to magic-bytes-only (return Valid).
    /// </summary>
    private static readonly HashSet<string> FullValidationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".zst"
    };

    /// <summary>
    /// Magic bytes signatures for common archive formats.
    /// </summary>
    private static readonly (byte[] Signature, int Offset, string Format)[] MagicSignatures =
    [
        // ZIP (also DOCX/XLSX/PPTX/JAR/APK): PK 0x03 0x04
        (new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, "ZIP"),
        // ZIP empty archive: PK 0x05 0x06
        (new byte[] { 0x50, 0x4B, 0x05, 0x06 }, 0, "ZIP-Empty"),
        // ZIP spanned: PK 0x07 0x08
        (new byte[] { 0x50, 0x4B, 0x07, 0x08 }, 0, "ZIP-Spanned"),
        // RAR4: Rar! 0x1A 0x07 0x00
        (new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, 0, "RAR4"),
        // RAR5: Rar! 0x1A 0x07 0x01 0x00
        (new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, 0, "RAR5"),
        // 7z: 7z 0xBC 0xAF 0x27 0x1C
        (new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, 0, "7z"),
        // GZIP: 0x1F 0x8B
        (new byte[] { 0x1F, 0x8B }, 0, "GZIP"),
        // BZIP2: "BZh" (0x42 0x5A 0x68)
        (new byte[] { 0x42, 0x5A, 0x68 }, 0, "BZIP2"),
        // XZ: 0xFD "7zXZ" 0x00
        (new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, 0, "XZ"),
        // Zstandard: 0x28 0xB5 0x2F 0xFD
        (new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, 0, "ZSTD"),
        // LZ4: 0x04 0x22 0x4D 0x18
        (new byte[] { 0x04, 0x22, 0x4D, 0x18 }, 0, "LZ4"),
        // CAB: "MSCF" (0x4D 0x53 0x43 0x46)
        (new byte[] { 0x4D, 0x53, 0x43, 0x46 }, 0, "CAB"),
        // ISO 9660: "CD001" at offset 0x8001 — handled separately due to large offset
    ];

    /// <summary>
    /// TAR "ustar" magic at offset 257.
    /// </summary>
    private static readonly byte[] TarUstarSignature = { 0x75, 0x73, 0x74, 0x61, 0x72 }; // "ustar"

    /// <summary>
    /// ISO 9660 "CD001" signature at offset 0x8001.
    /// </summary>
    private static readonly byte[] Iso9660Signature = { 0x43, 0x44, 0x30, 0x30, 0x31 }; // "CD001"

    /// <inheritdoc />
    public override string CategoryName => "Archives";

    /// <inheritdoc />
    public override bool SupportsFullValidation => true;

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => ArchiveExtensions;

    /// <summary>
    /// TAR requires reading offset 257 (262 bytes), ISO requires offset 0x8001 (32774 bytes).
    /// We handle ISO separately with its own stream read, so base buffer covers TAR.
    /// </summary>
    protected override int MagicBytesBufferSize => 262;

    /// <inheritdoc />
    protected override bool CheckMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2) return false;

        // Check standard signatures
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

            if (match) return true;
        }

        // Check TAR: "ustar" at offset 257
        if (header.Length >= 262)
        {
            var isTar = true;
            for (var i = 0; i < TarUstarSignature.Length; i++)
            {
                if (header[257 + i] != TarUstarSignature[i])
                {
                    isTar = false;
                    break;
                }
            }
            if (isTar) return true;
        }

        return false;
    }

    /// <summary>
    /// Overrides base to add ISO 9660 detection which requires reading at offset 0x8001,
    /// beyond the normal magic bytes buffer size.
    /// </summary>
    protected override async ValueTask<bool> ValidateMagicBytesAsync(string filePath, CancellationToken ct)
    {
        // First try the standard magic bytes check (covers ZIP, RAR, 7z, GZIP, BZIP2, XZ, ZSTD, LZ4, CAB, TAR)
        var baseResult = await ReadAndValidateMagicBytesAsync(filePath, ct);
        if (baseResult) return true;

        // For .iso files, check CD001 signature at offset 0x8001
        if (Path.GetExtension(filePath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            return await CheckIso9660Async(filePath, ct);
        }

        return false;
    }

    /// <summary>
    /// Checks for ISO 9660 "CD001" signature at offset 0x8001 (sector 16 + 1 byte).
    /// </summary>
    private static async ValueTask<bool> CheckIso9660Async(string filePath, CancellationToken ct)
    {
        const int iso9660Offset = 0x8001;
        const int readLength = 5; // "CD001"

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            if (stream.Length < iso9660Offset + readLength) return false;

            stream.Seek(iso9660Offset, SeekOrigin.Begin);
            var buffer = new byte[readLength];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, readLength), ct);
            if (bytesRead < readLength) return false;

            for (var i = 0; i < Iso9660Signature.Length; i++)
            {
                if (buffer[i] != Iso9660Signature[i]) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses SharpCompress's <see cref="ArchiveFactory"/> to open and iterate archive entries.
    /// This parses the archive structural metadata:
    /// - ZIP: central directory + local file headers
    /// - RAR: file header blocks + volume markers (header CRCs validated by SharpCompress)
    /// - 7z: header database with CRC (folder/coder/file info, header CRC validated)
    /// - TAR: 512-byte header records (name, size, checksum)
    /// - GZip: gzip header + member structure
    /// - BZip2: block headers + stream structure
    /// - XZ: stream header/footer + block headers
    /// - Zstandard: frame header + block structure
    ///
    /// Iterating entries forces the library to read and validate the archive index,
    /// catching corruption such as truncated entries, invalid header checksums,
    /// broken offsets, or malformed central directories — without decompressing data.
    ///
    /// Note on CRC: The CRC32 stored in ZIP/RAR/7z is computed on the *uncompressed*
    /// data, so verifying it would require full decompression of every entry, which is
    /// prohibitively slow for large archives. Instead, we rely on structural validation
    /// which already catches the most common corruption (truncation, broken headers,
    /// invalid index) at ~1-5ms per file.
    ///
    /// Formats not supported by SharpCompress (.lz4, .cab, .iso) fall back to
    /// returning Valid since magic bytes already passed.
    ///
    /// Note: ArchiveFactory.OpenArchive is synchronous (seek-based I/O + header parsing).
    /// Since the base class pipeline runs consumers on thread pool workers,
    /// this synchronous call is acceptable — same pattern as ATL Track, PdfPig, and ImageSharp.
    /// </remarks>
    protected override ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(filePath);

        // For formats not supported by SharpCompress, fall back to magic-bytes-only result
        if (!FullValidationExtensions.Contains(extension))
        {
            return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
        }

        try
        {
            using var archive = ArchiveFactory.OpenArchive(filePath);

            // Iterate through all entries to force parsing of the archive index.
            // Accessing Key, Size, and CompressedSize validates structural integrity
            // of each entry header without decompressing any data.
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                _ = entry.Key;
                _ = entry.Size;
                _ = entry.CompressedSize;
            }

            return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // SharpCompress throws various exceptions for structural problems:
            // - InvalidOperationException (malformed headers)
            // - InvalidFormatException (corrupt archive structure)
            // - EndOfStreamException (truncated files)
            // - IndexOutOfRangeException (corrupt entry offsets)
            return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
        }
    }
}
