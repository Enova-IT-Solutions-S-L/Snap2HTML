using System.Diagnostics;
using System.Text;
using Snap2HTML.Core.Models;
using Snap2HTML.Core.Utilities;
using Snap2HTML.Infrastructure.FileSystem;
using Snap2HTML.Services.Scanning;

namespace Snap2HTML.Services.Generation;

/// <summary>
/// Implementation of IHtmlGenerator that creates HTML output from scanned folder content.
/// </summary>
public class HtmlGenerator : IHtmlGenerator
{
    private readonly ITemplateProvider _templateProvider;
    private readonly IFileSystemAbstraction _fileSystem;

    public HtmlGenerator(ITemplateProvider templateProvider, IFileSystemAbstraction fileSystem)
    {
        _templateProvider = templateProvider;
        _fileSystem = fileSystem;
    }

    public async Task<HtmlGenerationResult> GenerateAsync(
        ScanResult scanResult,
        HtmlGenerationOptions options,
        IProgress<HtmlGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new HtmlGenerationResult();

        try
        {
            progress?.Report(new HtmlGenerationProgress
            {
                StatusMessage = "Generating HTML file...",
                PercentComplete = 0
            });

            // Load template
            string templateContent;
            try
            {
                templateContent = await _templateProvider.LoadTemplateAsync(null, cancellationToken);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to open 'Template.html' for reading: {ex.Message}";
                return result;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                result.WasCancelled = true;
                return result;
            }

            // Build HTML
            var sbTemplate = new StringBuilder(templateContent);
            ApplyTemplateReplacements(sbTemplate, scanResult, options);

            // Write output file
            try
            {
                using var writer = _fileSystem.CreateStreamWriter(options.OutputFile);

                var template = sbTemplate.ToString();
                var startOfData = template.IndexOf("[DIR DATA]");

                await writer.WriteAsync(template[..startOfData]);

                if (cancellationToken.IsCancellationRequested)
                {
                    result.WasCancelled = true;
                    return result;
                }

                await BuildJavascriptContentArrayAsync(scanResult.Folders, 0, writer, progress, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    result.WasCancelled = true;
                    return result;
                }

                await writer.WriteAsync(template[(startOfData + 10)..]);

                result.Success = true;
                result.OutputPath = options.OutputFile;

                if (options.OpenInBrowser)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = options.OutputFile,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to open file for writing: {ex.Message}";
                return result;
            }

            progress?.Report(new HtmlGenerationProgress
            {
                StatusMessage = "Ready!",
                PercentComplete = 100
            });
        }
        catch (OperationCanceledException)
        {
            result.WasCancelled = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private static void ApplyTemplateReplacements(
        StringBuilder sbTemplate,
        ScanResult scanResult,
        HtmlGenerationOptions options)
    {
        sbTemplate.Replace("[TITLE]", options.Title);
        sbTemplate.Replace("[APP LINK]", "http://www.rlvision.com");
        sbTemplate.Replace("[APP NAME]", options.AppName);

        var versionParts = options.AppVersion.Split('.');
        var displayVersion = versionParts.Length >= 2
            ? $"{versionParts[0]}.{versionParts[1]}"
            : options.AppVersion;
        sbTemplate.Replace("[APP VER]", displayVersion);

        sbTemplate.Replace("[GEN TIME]", DateTime.Now.ToString("t"));
        sbTemplate.Replace("[GEN DATE]", DateTime.Now.ToString("d"));
        sbTemplate.Replace("[NUM FILES]", scanResult.TotalFiles.ToString());
        sbTemplate.Replace("[NUM DIRS]", scanResult.TotalDirectories.ToString());
        sbTemplate.Replace("[TOT SIZE]", scanResult.TotalSize.ToString());

        if (options.LinkFiles)
        {
            sbTemplate.Replace("[LINK FILES]", "true");
            sbTemplate.Replace("[LINK ROOT]", options.LinkRoot.Replace(@"\", "/"));
            sbTemplate.Replace("[SOURCE ROOT]", options.RootFolder.Replace(@"\", "/"));

            var linkRoot = options.LinkRoot.Replace(@"\", "/");

            // "file://" is needed in the browser if path begins with drive letter, else it should not be used
            if (StringUtils.IsWildcardMatch(@"?:/*", linkRoot, false))
            {
                sbTemplate.Replace("[LINK PROTOCOL]", @"file://");
            }
            else if (linkRoot.StartsWith("//")) // For UNC paths e.g. \\server\path
            {
                sbTemplate.Replace("[LINK PROTOCOL]", @"file://///");
            }
            else
            {
                sbTemplate.Replace("[LINK PROTOCOL]", "");
            }
        }
        else
        {
            sbTemplate.Replace("[LINK FILES]", "false");
            sbTemplate.Replace("[LINK PROTOCOL]", "");
            sbTemplate.Replace("[LINK ROOT]", "");
            sbTemplate.Replace("[SOURCE ROOT]", options.RootFolder.Replace(@"\", "/"));
        }
    }

    private static async Task BuildJavascriptContentArrayAsync(
        List<SnappedFolder> content,
        int startIndex,
        StreamWriter writer,
        IProgress<HtmlGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        //  Data format:
        //    Each index in "dirs" array is an array representing a directory:
        //      First item in array: "directory path*always 0*directory modified date"
        //        Note that forward slashes are used instead of (Windows style) backslashes
        //      Then, for each each file in the directory: "filename*size of file*file modified date*hash*integrityStatus"
        //        The hash field is always present (empty string if hashing disabled)
        //        The integrityStatus field is always present (0=Unknown, 1=Valid, 2=InvalidMagicBytes, 3=DecodingFailed, 4=NotSupported)
        //      Second to last item in array tells the total size of directory content
        //      Last item in array references IDs to all subdirectories of this dir (if any).
        //        ID is the item index in dirs array.
        //    Note: Modified date is in UNIX format

        var lineBreakSymbol = ""; // Could be set to \n to make the html output more readable

        // Assign an ID to each folder
        var dirIndexes = new Dictionary<string, string>();
        for (var i = 0; i < content.Count; i++)
        {
            dirIndexes.Add(content[i].GetFullPath(), (i + startIndex).ToString());
        }

        // Build a lookup table with subfolder IDs for each folder
        var subdirs = new Dictionary<string, List<string>>();

        foreach (var dir in content)
        {
            subdirs.Add(dir.GetFullPath(), []);
        }

        if (!subdirs.ContainsKey(content[0].Path) && content[0].Name != "")
        {
            subdirs.Add(content[0].Path, []);
        }

        foreach (var dir in content)
        {
            if (dir.Name != "")
            {
                try
                {
                    subdirs[dir.Path].Add(dirIndexes[dir.GetFullPath()]);
                }
                catch
                {
                    // Orphan file or folder?
                }
            }
        }

        // Generate the data array
        var result = new StringBuilder();
        var processedCount = 0;

        foreach (var currentDir in content)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            result.Append($"D.p([{lineBreakSymbol}");

            var sDirWithForwardSlash = currentDir.GetFullPath().Replace(@"\", "/");
            result.Append($"\"{StringUtils.MakeCleanJsString(sDirWithForwardSlash)}*0*{currentDir.GetProp("Modified")}\",{lineBreakSymbol}");

            long dirSize = 0;

            foreach (var currentFile in currentDir.Files)
            {
                // Include hash and integrityStatus fields: "filename*size*modified*hash*integrityStatus"
                var hash = currentFile.GetProp("Hash");
                var integrityStatus = currentFile.GetProp("IntegrityStatus");
                result.Append($"\"{StringUtils.MakeCleanJsString(currentFile.Name)}*{currentFile.GetProp("Size")}*{currentFile.GetProp("Modified")}*{hash}*{integrityStatus}\",{lineBreakSymbol}");
                dirSize += StringUtils.ParseLong(currentFile.GetProp("Size"));
            }

            result.Append($"{dirSize},{lineBreakSymbol}");
            result.Append($"\"{string.Join("*", subdirs[currentDir.GetFullPath()])}\"{lineBreakSymbol}");
            result.Append("])");
            result.Append('\n');

            // Write result in chunks to limit memory consumption
            if (result.Length > 10240)
            {
                await writer.WriteAsync(result.ToString());
                result.Clear();
            }

            processedCount++;
            if (processedCount % 100 == 0)
            {
                var percent = (int)((double)processedCount / content.Count * 100);
                progress?.Report(new HtmlGenerationProgress
                {
                    StatusMessage = $"Writing output... {processedCount}/{content.Count}",
                    PercentComplete = percent
                });
            }
        }

        await writer.WriteAsync(result.ToString());
    }
}
