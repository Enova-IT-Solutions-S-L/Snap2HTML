using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Snap2HTML.Core.Models;
using Snap2HTML.Core.Utilities;
using Snap2HTML.Infrastructure.FileSystem;
using Snap2HTML.Infrastructure.Prerequisites;
using Snap2HTML.Infrastructure.Prerequisites.SqlLocalDb;
using Snap2HTML.Presenters;
using Snap2HTML.Services.CommandLine;
using Snap2HTML.Services.Diagnostics;
using Snap2HTML.Services.Generation;
using Snap2HTML.Services.Scanning;
using Snap2HTML.Services.Validation;

namespace Snap2HTML.Views;

public partial class frmMain : Form, IMainFormView
{
    private bool _initDone;
    private bool _runningAutomated;
    private MainFormPresenter? _presenter;
    private IPrerequisiteManager? _prerequisiteManager;
    private IntegrityValidatorAggregator? _validatorAggregator;
    private ILogger<frmMain> _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<frmMain>.Instance;

    public frmMain()
    {
        InitializeComponent();
        InitializeIntegrityLevelComboBox();
        InitializePresenter();
        _logger = Program.AppLoggerFactory.LoggerFactory.CreateLogger<frmMain>();
        InitializeDiagnosticsTab();
    }

    private void InitializeIntegrityLevelComboBox()
    {
        // Set default selection to "None"
        cmbIntegrityLevel.SelectedIndex = 0;
    }

    private void InitializePresenter()
    {
        var loggerFactory = Program.AppLoggerFactory.LoggerFactory;
        var fileSystem = new FileSystemAbstraction();
        var applicationPath = Path.GetDirectoryName(Application.ExecutablePath) ?? string.Empty;
        var templateProvider = new TemplateProvider(fileSystem, applicationPath);
        var htmlGenerator = new HtmlGenerator(templateProvider, fileSystem, loggerFactory);

        var localDbPrerequisite = new SqlLocalDbPrerequisite(loggerFactory);
        _prerequisiteManager = new PrerequisiteManager(localDbPrerequisite);
        _validatorAggregator = IntegrityValidatorAggregator.CreateDefault(_prerequisiteManager);
        var folderScannerWithValidator = new FolderScanner(fileSystem, _validatorAggregator, loggerFactory);

        _presenter = new MainFormPresenter(folderScannerWithValidator, htmlGenerator, this, loggerFactory);
    }

    #region IMainFormView Implementation

    public void UpdateProgress(MainFormProgress progress)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => UpdateProgress(progress)));
            return;
        }

        toolStripStatusLabel1.Text = progress.StatusMessage;
    }

    public void ShowError(string title, string message)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => ShowError(title, message)));
            return;
        }

        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public void SetBusyState(bool isBusy)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => SetBusyState(isBusy)));
            return;
        }

        if (isBusy)
        {
            Cursor.Current = Cursors.WaitCursor;
            Text = "Snap2HTML (Working... Press Escape to Cancel)";
            tabControl1.Enabled = false;
        }
        else
        {
            Cursor.Current = Cursors.Default;
            tabControl1.Enabled = true;
            Text = "Snap2HTML";

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    #endregion

    private void frmMain_Load(object sender, EventArgs e)
    {
        Text = $"{Application.ProductName} (Press F1 for Help)";
        var versionParts = Application.ProductVersion.Split('.');
        labelAboutVersion.Text = $"version {versionParts[0]}.{versionParts[1]}";

        // Initialize some settings
        var left = Properties.Settings.Default.WindowLeft;
        var top = Properties.Settings.Default.WindowTop;

        if (left >= 0) Left = left;
        if (top >= 0) Top = top;

        if (Directory.Exists(txtRoot.Text))
        {
            SetRootPath(txtRoot.Text, true);
        }
        else
        {
            SetRootPath("", false);
        }

        txtLinkRoot.Enabled = chkLinkFiles.Checked;

        // Setup drag & drop handlers
        tabPage1.DragDrop += DragDropHandler;
        tabPage1.DragEnter += DragEnterHandler;
        tabPage1.AllowDrop = true;

        foreach (Control cnt in tabPage1.Controls)
        {
            cnt.DragDrop += DragDropHandler;
            cnt.DragEnter += DragEnterHandler;
            cnt.AllowDrop = true;
        }

        Opacity = 0; // For silent mode

        _initDone = true;
    }

    private void frmMain_Shown(object sender, EventArgs e)
    {
        // Parse command line
        var commandLine = Environment.CommandLine;
        commandLine = commandLine.Replace("-output:", "-outfile:"); // Correct wrong parameter to avoid confusion
        var splitCommandLine = Arguments.SplitCommandLine(commandLine);
        var arguments = new Arguments(splitCommandLine);

        // First test for single argument (ie path only)
        if (splitCommandLine.Length == 2 && !arguments.Exists("path"))
        {
            if (Directory.Exists(splitCommandLine[1]))
            {
                SetRootPath(splitCommandLine[1]);
            }
        }

        var settings = new SnapSettings();

        if (arguments.Exists("path") && arguments.Exists("outfile"))
        {
            _runningAutomated = true;

            settings.RootFolder = arguments.Single("path") ?? string.Empty;
            settings.OutputFile = arguments.Single("outfile") ?? string.Empty;

            // First validate paths
            if (!Directory.Exists(settings.RootFolder))
            {
                if (!arguments.Exists("silent"))
                {
                    MessageBox.Show($"Input path does not exist: {settings.RootFolder}", "Automation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Application.Exit();
            }

            var outputDir = Path.GetDirectoryName(settings.OutputFile);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                if (!arguments.Exists("silent"))
                {
                    MessageBox.Show($"Output path does not exist: {outputDir}", "Automation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Application.Exit();
            }

            // Rest of settings
            settings.SkipHiddenItems = !arguments.Exists("hidden");
            settings.SkipSystemItems = !arguments.Exists("system");
            settings.OpenInBrowser = false;
            settings.EnableHashing = arguments.Exists("hash");

            settings.LinkFiles = false;
            if (arguments.Exists("link"))
            {
                settings.LinkFiles = true;
                settings.LinkRoot = arguments.Single("link") ?? string.Empty;
            }

            settings.Title = $"Snapshot of {settings.RootFolder}";
            if (arguments.Exists("title"))
            {
                settings.Title = arguments.Single("title") ?? settings.Title;
            }
        }

        // Keep window hidden in silent mode
        if (arguments.IsTrue("silent") && _runningAutomated)
        {
            Visible = false;
        }
        else
        {
            Opacity = 100;
        }

        if (_runningAutomated)
        {
            StartProcessing(settings);
        }

        // Run prerequisite checks in the background immediately after the form is shown
        _ = RunPrerequisiteChecksAsync();
    }

    private async Task RunPrerequisiteChecksAsync()
    {
        if (_prerequisiteManager is null) return;

        // Show "Checking..." in the tab before kicking off the async checks
        PopulatePrerequisitesTab();

        await _prerequisiteManager.CheckAllAsync();

        // Refresh the tab with the real statuses
        PopulatePrerequisitesTab();
    }

    private void frmMain_FormClosing(object sender, FormClosingEventArgs e)
    {
        if (_presenter?.IsProcessing == true) e.Cancel = true;

        if (!_runningAutomated) // Don't save settings when automated through command line
        {
            Properties.Settings.Default.WindowLeft = Left;
            Properties.Settings.Default.WindowTop = Top;
            Properties.Settings.Default.Save();
        }
    }

    private void cmdBrowse_Click(object sender, EventArgs e)
    {
        folderBrowserDialog1.RootFolder = Environment.SpecialFolder.Desktop; // This makes it possible to select network paths too
        folderBrowserDialog1.SelectedPath = txtRoot.Text;

        if (folderBrowserDialog1.ShowDialog() == DialogResult.OK)
        {
            try
            {
                SetRootPath(folderBrowserDialog1.SelectedPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not select folder:\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetRootPath("", false);
            }
        }
    }

    private void cmdCreate_Click(object sender, EventArgs e)
    {
        // Ask for output file
        var fileName = new DirectoryInfo(txtRoot.Text + @"\").Name;
        var invalid = Path.GetInvalidFileNameChars();

        foreach (var c in invalid)
        {
            fileName = fileName.Replace(c.ToString(), "");
        }

        saveFileDialog1.DefaultExt = "html";
        if (!fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) fileName += ".html";
        saveFileDialog1.FileName = fileName;
        saveFileDialog1.Filter = "HTML files (*.html)|*.html|All files (*.*)|*.*";
        saveFileDialog1.InitialDirectory = Path.GetDirectoryName(txtRoot.Text);
        saveFileDialog1.CheckPathExists = true;

        if (saveFileDialog1.ShowDialog() != DialogResult.OK) return;

        if (!saveFileDialog1.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            saveFileDialog1.FileName += ".html";

        // Map combo index to IntegrityValidationLevel
        var integrityLevel = cmbIntegrityLevel.SelectedIndex switch
        {
            1 => IntegrityValidationLevel.MagicBytesOnly,
            2 => IntegrityValidationLevel.FullDecode,
            _ => IntegrityValidationLevel.None
        };

        // Begin generating html
        var settings = new SnapSettings
        {
            RootFolder = txtRoot.Text,
            Title = txtTitle.Text,
            OutputFile = saveFileDialog1.FileName,
            SkipHiddenItems = !chkHidden.Checked,
            SkipSystemItems = !chkSystem.Checked,
            OpenInBrowser = chkOpenOutput.Checked,
            LinkFiles = chkLinkFiles.Checked,
            LinkRoot = txtLinkRoot.Text,
            EnableHashing = chkEnableHash.Checked,
            IntegrityLevel = integrityLevel,
        };

        StartProcessing(settings);
    }

    private async void StartProcessing(SnapSettings settings)
    {
        // Ensure source path format
        settings.RootFolder = Path.GetFullPath(settings.RootFolder);

        if (settings.RootFolder.EndsWith(@"\"))
            settings.RootFolder = settings.RootFolder[..^1];

        // Add backslash to path if only letter and colon eg "c:"
        if (StringUtils.IsWildcardMatch("?:", settings.RootFolder, false))
            settings.RootFolder += @"\";

        // Add slash or backslash to end of link (in cases where it is clear that we can)
        if (settings.LinkFiles)
        {
            if (!settings.LinkRoot.EndsWith(@"/"))
            {
                if (settings.LinkRoot.StartsWith("http", StringComparison.OrdinalIgnoreCase)) // Web site
                {
                    settings.LinkRoot += @"/";
                }

                if (StringUtils.IsWildcardMatch("?:*", settings.LinkRoot, false)) // Local disk
                {
                    settings.LinkRoot += @"\";
                }

                if (settings.LinkRoot.StartsWith(@"\\")) // UNC path
                {
                    settings.LinkRoot += @"\";
                }
            }
        }

        if (_presenter == null) return;

        _ = await _presenter.CreateSnapshotAsync(
            settings,
            Application.ProductName ?? "Lazarus Technology",
            Application.ProductVersion ?? "1.0.0");

        // Quit when finished if automated via command line
        if (_runningAutomated)
        {
            Application.Exit();
        }
    }

    private void chkLinkFiles_CheckedChanged(object sender, EventArgs e)
    {
        txtLinkRoot.Enabled = chkLinkFiles.Checked;
    }

    private void chkEnableHash_CheckedChanged(object sender, EventArgs e)
    {
        if (!_initDone || _runningAutomated || !chkEnableHash.Checked) return;

        var result = MessageBox.Show(
            "Generating SHA-256 hashes for every file can significantly increase scanning time " +
            "and the size of the generated HTML report, especially for large folder trees.\n\n" +
            "Do you want to enable file hashing?",
            "Enable File Hashing",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes)
        {
            chkEnableHash.Checked = false;
        }
    }

    private void lnkSupportedFormats_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        var aggregator = _validatorAggregator
            ?? (_prerequisiteManager is not null
                ? IntegrityValidatorAggregator.CreateDefault(_prerequisiteManager)
                : null);

        if (aggregator is null) return;

        var formats = aggregator.GetSupportedFormats();
        using var dialog = new frmSupportedFormats(formats);
        dialog.ShowDialog(this);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Prerequisites tab
    // ─────────────────────────────────────────────────────────────────────────

    private void PopulatePrerequisitesTab()
    {
        if (InvokeRequired)
        {
            Invoke(PopulatePrerequisitesTab);
            return;
        }

        pnlPrerequisites.SuspendLayout();
        pnlPrerequisites.Controls.Clear();

        if (_prerequisiteManager is null)
        {
            pnlPrerequisites.ResumeLayout();
            return;
        }

        var y = 6;
        const int rowHeight = 112;
        const int padding = 6;
        const int btnW = 70;
        const int btnH = 24;
        const int rightMargin = 8;

        foreach (var prerequisite in _prerequisiteManager.GetAll())
        {
            var rowWidth = pnlPrerequisites.ClientSize.Width - padding * 2;

            var row = new Panel
            {
                Left = padding,
                Top = y,
                Width = rowWidth,
                Height = rowHeight,
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            };

            // ── Right-side buttons (anchored to top-right) ──────────────────

            // "Check" button — always visible
            var btnCheck = new Button
            {
                Text = "Check",
                Left = rowWidth - rightMargin - btnW,
                Top = 6,
                Width = btnW,
                Height = btnH,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            row.Controls.Add(btnCheck);

            // "Install" button — only when installable and not yet installed
            Button? btnInstall = null;
            if (prerequisite.CanInstall && prerequisite.Status != PrerequisiteStatus.Installed)
            {
                btnInstall = new Button
                {
                    Text = "Install",
                    Left = rowWidth - rightMargin - btnW,
                    Top = 36,
                    Width = btnW,
                    Height = btnH,
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                };
                row.Controls.Add(btnInstall);
            }

            // ── Left-side info labels ────────────────────────────────────────

            var leftWidth = rowWidth - rightMargin - btnW - 12;

            // Name
            var lblName = new Label
            {
                Text = prerequisite.Name,
                Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold),
                Left = 8,
                Top = 8,
                Width = leftWidth,
                Height = 18,
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            row.Controls.Add(lblName);

            // Status (coloured, below name on the left side)
            var (statusText, statusColor) = GetStatusDisplay(prerequisite.Status);
            var lblStatus = new Label
            {
                Text = statusText,
                ForeColor = statusColor,
                Left = 8,
                Top = 28,
                Width = leftWidth,
                Height = 16,
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            row.Controls.Add(lblStatus);

            // Description
            var lblDesc = new Label
            {
                Text = prerequisite.Description,
                Left = 8,
                Top = 46,
                Width = leftWidth,
                Height = 24,
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            row.Controls.Add(lblDesc);

            // ── Progress text area (full width, bottom of row) ───────────────
            var txtProgress = new TextBox
            {
                Left = 8,
                Top = 74,
                Width = rowWidth - 16,
                Height = 30,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = System.Drawing.SystemColors.Control,
                ForeColor = System.Drawing.SystemColors.GrayText,
                Font = new System.Drawing.Font(Font.FontFamily, Font.Size - 0.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = GetStatusHint(prerequisite.Status),
                TabStop = false,
            };
            row.Controls.Add(txtProgress);

            // ── Button event handlers ────────────────────────────────────────

            var localPrereq = prerequisite;

            btnCheck.Click += async (_, _) =>
            {
                if (IsDisposed || !IsHandleCreated) return;

                btnCheck.Enabled = false;
                if (btnInstall is not null) btnInstall.Enabled = false;

                lblStatus.Text = "Checking…";
                lblStatus.ForeColor = System.Drawing.SystemColors.GrayText;
                txtProgress.Text = "Running check…";

                _logger.LogInformation("User-initiated check for prerequisite '{Id}'", localPrereq.Id);

                await localPrereq.CheckAsync();

                if (IsDisposed || !IsHandleCreated) return;

                _logger.LogInformation(
                    "Check result for prerequisite '{Id}': {Status}", localPrereq.Id, localPrereq.Status);

                var (newText, newColor) = GetStatusDisplay(localPrereq.Status);
                lblStatus.Text = newText;
                lblStatus.ForeColor = newColor;
                txtProgress.Text = GetStatusHint(localPrereq.Status);

                // Rebuild so Install button appears/disappears as needed
                PopulatePrerequisitesTab();
            };

            if (btnInstall is not null)
            {
                var localBtnInstall = btnInstall;

                localBtnInstall.Click += async (_, _) =>
                {
                    if (IsDisposed || !IsHandleCreated) return;

                    localBtnInstall.Enabled = false;
                    btnCheck.Enabled = false;
                    lblStatus.Text = "Installing…";
                    lblStatus.ForeColor = System.Drawing.Color.DarkOrange;
                    txtProgress.ForeColor = System.Drawing.SystemColors.WindowText;
                    txtProgress.Text = string.Empty;

                    _logger.LogInformation("User-initiated install for prerequisite '{Id}'", localPrereq.Id);

                    var progress = new Progress<string>(msg =>
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        Invoke(() =>
                        {
                            txtProgress.AppendText((txtProgress.TextLength > 0 ? Environment.NewLine : string.Empty) + msg);
                            txtProgress.ScrollToCaret();
                        });
                    });

                    await localPrereq.InstallAsync(progress);

                    // Re-check to confirm real post-install status
                    await localPrereq.CheckAsync();

                    _logger.LogInformation(
                        "Post-install check for prerequisite '{Id}': {Status}", localPrereq.Id, localPrereq.Status);

                    // Rebuild the tab (removes Install button on success, re-enables Check)
                    PopulatePrerequisitesTab();
                };
            }

            pnlPrerequisites.Controls.Add(row);
            y += rowHeight + 4;
        }

        pnlPrerequisites.ResumeLayout();
    }

    private static string GetStatusHint(PrerequisiteStatus status) => status switch
    {
        PrerequisiteStatus.Installed    => "All checks passed. Ready to use.",
        PrerequisiteStatus.NotInstalled => "Not found. Click Install to set it up.",
        PrerequisiteStatus.InstallFailed => "Installation failed. Check the log files for details.",
        PrerequisiteStatus.Checking     => "Running check…",
        PrerequisiteStatus.Installing   => "Installation in progress…",
        _                               => string.Empty,
    };

    private static (string text, System.Drawing.Color color) GetStatusDisplay(PrerequisiteStatus status)
        => status switch
        {
            PrerequisiteStatus.Installed    => ("Installed",     System.Drawing.Color.Green),
            PrerequisiteStatus.NotInstalled => ("Not installed", System.Drawing.Color.Red),
            PrerequisiteStatus.Installing   => ("Installing...", System.Drawing.Color.DarkOrange),
            PrerequisiteStatus.InstallFailed => ("Install failed", System.Drawing.Color.DarkRed),
            PrerequisiteStatus.Checking     => ("Checking...",   System.Drawing.Color.Gray),
            _                               => ("Unknown",       System.Drawing.Color.Gray),
        };

    // Link Label handlers
    private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = @"http://www.rlvision.com",
            UseShellExecute = true
        });
    }

    private void linkLabel3_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = @"https://rlvision.com/exif/about.php",
            UseShellExecute = true
        });
    }

    private void linkLabel2_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = @"http://www.rlvision.com/flashren/about.php",
            UseShellExecute = true
        });
    }

    private void linkLabel4_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        var templatePath = Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath) ?? string.Empty,
            "template.html");

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = templatePath,
            UseShellExecute = true
        });
    }

    private void linkLabel5_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = @"http://www.rlvision.com/contact.php",
            UseShellExecute = true
        });
    }

    private void pictureBoxDonate_Click(object sender, EventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = @"https://www.paypal.com/cgi-bin/webscr?cmd=_donations&business=U3E4HE8HMY9Q4&item_name=Snap2HTML&currency_code=USD&source=url",
            UseShellExecute = true
        });
    }

    // Drag & Drop handlers
    private void DragEnterHandler(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void DragDropHandler(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files
                && files.Length == 1
                && Directory.Exists(files[0]))
            {
                SetRootPath(files[0]);
            }
        }
    }

    // Escape to cancel
    private void frmMain_KeyUp(object sender, KeyEventArgs e)
    {
        if (_presenter?.IsProcessing == true)
        {
            if (e.KeyCode == Keys.Escape)
            {
                _presenter.CancelOperation();
            }
        }
        else
        {
            if (e.KeyCode == Keys.F1)
            {
                var readmePath = Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath) ?? string.Empty,
                    "ReadMe.txt");

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = readmePath,
                    UseShellExecute = true
                });
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Diagnostics tab
    // ─────────────────────────────────────────────────────────────────────────

    private void InitializeDiagnosticsTab()
    {
        var settings = Properties.Settings.Default;

        // Populate controls from saved settings (suppress events during init)
        chkLoggingEnabled.CheckedChanged -= ChkLoggingEnabled_CheckedChanged;
        cmbLogLevel.SelectedIndexChanged -= CmbLogLevel_SelectedIndexChanged;

        chkLoggingEnabled.Checked = settings.LoggingEnabled;
        cmbLogLevel.SelectedItem = settings.LogLevel;
        if (cmbLogLevel.SelectedIndex < 0) cmbLogLevel.SelectedIndex = 2; // Information

        chkLoggingEnabled.CheckedChanged += ChkLoggingEnabled_CheckedChanged;
        cmbLogLevel.SelectedIndexChanged += CmbLogLevel_SelectedIndexChanged;

        btnOpenLogFolder.Click += BtnOpenLogFolder_Click;
        btnGenerateReport.Click += BtnGenerateReport_Click;

        RefreshDiagnosticsControlState();
    }

    private void RefreshDiagnosticsControlState()
    {
        var enabled = chkLoggingEnabled.Checked;
        cmbLogLevel.Enabled = enabled;
        btnOpenLogFolder.Enabled = enabled;
        btnGenerateReport.Enabled = enabled;
        lblLogFolder.Text = Program.AppLoggerFactory.LogDirectory;
    }

    private void ChkLoggingEnabled_CheckedChanged(object? sender, EventArgs e)
    {
        var settings = Properties.Settings.Default;
        settings.LoggingEnabled = chkLoggingEnabled.Checked;
        settings.Save();

        var level = cmbLogLevel.SelectedItem?.ToString() ?? "Information";
        Program.AppLoggerFactory.Configure(chkLoggingEnabled.Checked, level);

        _logger.LogInformation("Logging {State}. Level={Level}",
            chkLoggingEnabled.Checked ? "enabled" : "disabled", level);

        RefreshDiagnosticsControlState();
    }

    private void CmbLogLevel_SelectedIndexChanged(object? sender, EventArgs e)
    {
        var level = cmbLogLevel.SelectedItem?.ToString() ?? "Information";
        var settings = Properties.Settings.Default;
        settings.LogLevel = level;
        settings.Save();

        if (chkLoggingEnabled.Checked)
        {
            Program.AppLoggerFactory.Configure(true, level);
            _logger.LogInformation("Log level changed to {Level}", level);
        }
    }

    private void BtnOpenLogFolder_Click(object? sender, EventArgs e)
    {
        var dir = Program.AppLoggerFactory.LogDirectory;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    private void BtnGenerateReport_Click(object? sender, EventArgs e)
    {
        // Flush so the current log file is complete
        Program.AppLoggerFactory.Flush();

        using var dlg = new SaveFileDialog
        {
            Title = "Save diagnostic report",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"snap2html-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            DefaultExt = "zip",
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _logger.LogInformation("Generating diagnostic report to {Path}", dlg.FileName);

        try
        {
            var logCount = DiagnosticReportService.GenerateReport(
                Program.AppLoggerFactory.LogDirectory,
                dlg.FileName);

            _logger.LogInformation("Diagnostic report saved. LogFiles={Count}, Path={Path}", logCount, dlg.FileName);

            MessageBox.Show(
                $"Report saved to:\n{dlg.FileName}\n\n{logCount} log file(s) included.",
                "Diagnostic Report",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate diagnostic report to {Path}", dlg.FileName);
            MessageBox.Show(
                $"Failed to generate report:\n{ex.Message}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // Sets the root path input box and makes related gui parts ready to use
    private void SetRootPath(string path, bool pathIsValid = true)
    {
        if (pathIsValid)
        {
            txtRoot.Text = path;
            cmdCreate.Enabled = true;
            toolStripStatusLabel1.Text = "";

            if (_initDone)
            {
                txtLinkRoot.Text = txtRoot.Text;
                txtTitle.Text = $"Snapshot of {txtRoot.Text}";
            }
        }
        else
        {
            txtRoot.Text = "";
            cmdCreate.Enabled = false;
            toolStripStatusLabel1.Text = "";

            if (_initDone)
            {
                txtLinkRoot.Text = txtRoot.Text;
                txtTitle.Text = "";
            }
        }
    }
}
