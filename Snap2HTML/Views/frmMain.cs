using System.Windows.Forms;
using Snap2HTML.Core.Models;
using Snap2HTML.Core.Utilities;
using Snap2HTML.Infrastructure.FileSystem;
using Snap2HTML.Presenters;
using Snap2HTML.Services.CommandLine;
using Snap2HTML.Services.Generation;
using Snap2HTML.Services.Scanning;
using Snap2HTML.Services.Validation;

namespace Snap2HTML.Views;

public partial class frmMain : Form, IMainFormView
{
    private bool _initDone;
    private bool _runningAutomated;
    private MainFormPresenter? _presenter;

    public frmMain()
    {
        InitializeComponent();
        InitializeIntegrityLevelComboBox();
        InitializePresenter();
    }

    private void InitializeIntegrityLevelComboBox()
    {
        // Set default selection to "None"
        cmbIntegrityLevel.SelectedIndex = 0;
    }

    private void InitializePresenter()
    {
        var fileSystem = new FileSystemAbstraction();
        var applicationPath = Path.GetDirectoryName(Application.ExecutablePath) ?? string.Empty;
        var templateProvider = new TemplateProvider(fileSystem, applicationPath);
        var folderScanner = new FolderScanner(fileSystem);
        var htmlGenerator = new HtmlGenerator(templateProvider, fileSystem);

        _presenter = new MainFormPresenter(folderScanner, htmlGenerator, this);
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
            Application.ProductName ?? "Snap2HTML",
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
        var formats = IntegrityValidatorAggregator.CreateDefault().GetSupportedFormats();
        using var dialog = new frmSupportedFormats(formats);
        dialog.ShowDialog(this);
    }

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
