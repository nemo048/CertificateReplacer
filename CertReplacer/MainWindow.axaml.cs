using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CertReplacer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LogEntry> _logEntries = new();
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();

        LogItems.ItemsSource = _logEntries;

        BrowseRootButton.Click += async (_, _) => await BrowseRootAsync();
        BrowseCertButton.Click += async (_, _) => await BrowseCertAsync();
        RunButton.Click += async (_, _) => await RunAsync();
        CancelButton.Click += (_, _) => _cts?.Cancel();
        ClearLogButton.Click += (_, _) => _logEntries.Clear();
    }

    private async Task BrowseRootAsync()
    {
        var provider = GetTopLevel(this)!.StorageProvider;
        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select root folder",
            AllowMultiple = false
        });

        var folder = result.FirstOrDefault();
        if (folder?.TryGetLocalPath() is { } path)
        {
            RootDirectoryBox.Text = path;
        }
    }

    private async Task BrowseCertAsync()
    {
        var provider = GetTopLevel(this)!.StorageProvider;
        var result = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select new certificate file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Certificate files")
                {
                    Patterns = new[] { "*.pfx", "*.p12", "*.cer", "*.crt", "*.pem" }
                },
                FilePickerFileTypes.All
            }
        });

        var file = result.FirstOrDefault();
        if (file?.TryGetLocalPath() is { } path)
        {
            CertificatePathBox.Text = path;
        }
    }

    private async Task RunAsync()
    {
        var root = RootDirectoryBox.Text?.Trim() ?? string.Empty;
        var certPath = CertificatePathBox.Text?.Trim() ?? string.Empty;
        var dryRun = DryRunCheckBox.IsChecked == true;

        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(certPath))
        {
            AppendLog(LogKind.Error, "Please select both a root folder and a certificate file.");
            return;
        }

        var patterns = SplitList(PatternsBox.Text);
        if (patterns.Length == 0)
        {
            patterns = new[] { "*.pfx", "*.p12", "*.cer*", "*.crt", "*.pem" };
        }
        var excludeFolders = SplitList(ExcludeFoldersBox.Text);

        if (!dryRun)
        {
            var confirmed = await ConfirmDialog.ShowAsync(this,
                "Confirm certificate replacement",
                $"This will permanently delete matching certificate files under:\n{root}\n\n" +
                $"and replace them with:\n{certPath}\n\n" +
                "This cannot be undone. Continue?");

            if (!confirmed) return;
        }

        SetRunning(true);
        _cts = new CancellationTokenSource();

        var options = new ReplaceOptions
        {
            RootDirectory = root,
            NewCertificatePath = certPath,
            CertificatePatterns = patterns,
            ExcludeFolders = excludeFolders,
            IncludeRoot = IncludeRootCheckBox.IsChecked == true,
            DryRun = dryRun
        };

        try
        {
            var token = _cts.Token;
            var result = await Task.Run(() =>
                CertificateReplacer.Run(options, (kind, message) => AppendLog(kind, message), token), token);

            StatusText.Text = $"Processed: {result.Processed}, skipped: {result.Skipped}";
        }
        catch (OperationCanceledException)
        {
            AppendLog(LogKind.Error, "Cancelled by user.");
            StatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLog(LogKind.Error, $"Error: {ex.Message}");
            StatusText.Text = "Failed";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        RootDirectoryBox.IsEnabled = !running;
        CertificatePathBox.IsEnabled = !running;
        PatternsBox.IsEnabled = !running;
        ExcludeFoldersBox.IsEnabled = !running;
        IncludeRootCheckBox.IsEnabled = !running;
        DryRunCheckBox.IsEnabled = !running;
        BrowseRootButton.IsEnabled = !running;
        BrowseCertButton.IsEnabled = !running;
    }

    private static string[] SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private void AppendLog(LogKind kind, string message)
    {
        var brush = kind switch
        {
            LogKind.Removed => Brushes.DarkOrange,
            LogKind.Installed => Brushes.SeaGreen,
            LogKind.Skipped => Brushes.Gray,
            LogKind.Done => Brushes.SteelBlue,
            LogKind.Error => Brushes.Crimson,
            _ => Brushes.Black
        };

        void Add()
        {
            _logEntries.Add(new LogEntry(message, brush));
            LogScrollViewer.ScrollToEnd();
        }

        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }
}

public sealed record LogEntry(string Text, IBrush Brush);
