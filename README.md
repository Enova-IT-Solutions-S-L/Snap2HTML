# Snap2HTML

This application takes a "snapshot" of the folder structure on your hard drive and saves it as an HTML file. What's unique about Snap2HTML is that the HTML file uses modern techniques to make it feel like a "real" application, displaying a treeview with folders that you can navigate to view the files contained within. There is also a built-in file search and ability to export data as plain text, CSV, or JSON. Everything is contained in a single HTML file that you can easily store or distribute.

Originally created by **RL Vision** — [rlvision.com/snap2html](http://www.rlvision.com/snap2html)  
Maintained and extended by **Enova IT Solutions S.L.** — [enovait.es](https://www.enovait.es)

## What's New in v3.0

- **Modernized to .NET 8.0** with SDK-style project, nullable references, and latest C# features
- **Layered architecture** (MVP pattern: Core, Infrastructure, Services, Presenters, Views)
- **File integrity validation** — magic bytes / full decode for 8 format families:
  - Images (JPEG, PNG, GIF, BMP, WebP, TIFF) — header + full validation via ImageSharp
  - PDF — header validation
  - Video (MP4, AVI, MKV, WebM, FLV, WMV, MPEG, etc.) — header validation
  - Audio (MP3, WAV, FLAC, AAC, OGG, WMA, M4A, AIFF, etc.) — header validation
  - Archives (ZIP, RAR, 7z, GZIP, TAR, BZ2, XZ, ZSTD, LZ4, CAB, ISO) — header validation
  - Documents (Office OLE2/OOXML, OpenDocument, RTF) — header validation
  - SQLite databases — header validation
  - SQL Server data files (MDF/NDF) — header validation
- **SHA-256 file hashing** with parallel computation and ArrayPool-based buffering
- **High-performance scanning** — async pipeline, `Parallel.ForEachAsync`, Channel-based producer/consumer, `ConcurrentDictionary` with lock-free counters
- **Supported formats dialog** — DataGridView table showing validation capabilities per extension
- **GitHub Actions CI/CD** — manual workflow to build single-file executables and create releases

## Screenshots

WinForms application for generating the directory listings:

<img src="http://www.rlvision.com/snap2html/screenshot.png">

The finished HTML app:

<img src="http://www.rlvision.com/snap2html/example.png">

## Building

```bash
dotnet build Snap2HTML.sln -c Release
```

Requires .NET 8.0 SDK. Target platform: Windows (WinForms).

To publish a self-contained single-file executable:

```bash
dotnet publish Snap2HTML/Snap2HTML.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Contributors

| Contributor | Role |
|---|---|
| [rlv-dan](https://github.com/rlv-dan) (RL Vision) | Original author (v1.0 – v2.14) |
| [rpopeescu](https://github.com/DenixJG) (Rafael Popescu, Enova IT Solutions S.L.) | Maintainer, .NET 8 modernization, architecture refactor, integrity validation, performance optimization |

## License

This project is licensed under the **GNU General Public License v3.0** — see the [LICENSE](LICENSE) file for details.

## Original Project

Forked from [rlv-dan/Snap2HTML](https://github.com/rlv-dan/Snap2HTML). Original homepage: [rlvision.com/snap2html](http://www.rlvision.com/snap2html)
