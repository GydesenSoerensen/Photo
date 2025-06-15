using Extension.Utilities;
using Microsoft.Extensions.DependencyInjection;
using PhotoGallery.Data;
using PhotoGallery.Model;
using PhotoGallery.Workers;
using Serilog;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PhotoGallery
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<ThumbnailItem> Thumbnails { get; } = new();

        private ThumbnailFetchWorker? _fetchWorker;
        private readonly IPhotoRepository _repo;
        private int _currentColumns = 5;
        private const double CellMargin = 10;
        private string _currentFolderPath = string.Empty;
        private CancellationTokenSource? _scanCancellation;

        // Test folder path - update this to your actual photo folder
        private readonly string _defaultTestPath = @"D:\test2";

        public MainWindow()
            : this(App.Services.GetRequiredService<IPhotoRepository>())
        {
        }

        public MainWindow(IPhotoRepository repo)
        {
            InitializeComponent();
            DataContext = this;

            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _fetchWorker = new ThumbnailFetchWorker(_repo, Dispatcher, Thumbnails);

            InitializeFolderTree();

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            SizeChanged += (_, __) => RefreshLazyImageWidths();
            ColumnSelector.SelectionChanged += ColumnSelector_SelectionChanged;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshLazyImageWidths();

            // Load default folder if it exists
            if (Directory.Exists(_defaultTestPath))
            {
                LoadImagesFromFolder(_defaultTestPath);
                StartMetadataScan(_defaultTestPath);
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _fetchWorker?.Dispose();
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
        }

        private void InitializeFolderTree()
        {
            try
            {
                // Add common root folders
                var roots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    _defaultTestPath
                }.Where(Directory.Exists).Distinct();

                foreach (var root in roots)
                {
                    var rootItem = new TreeViewItem
                    {
                        Header = Path.GetFileName(root) ?? root,
                        Tag = root
                    };
                    rootItem.Expanded += Folder_Expanded;
                    rootItem.Selected += FolderTree_SelectedItemChanged;
                    rootItem.Items.Add(null); // Placeholder for lazy loading
                    FolderTree.Items.Add(rootItem);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize folder tree");
            }
        }

        private void StartMetadataScan(string folder)
        {
            if (MetadataScanner.IsScanning)
            {
                Log.Information("Metadata scan already in progress, skipping new scan");
                return;
            }

            _scanCancellation?.Cancel();
            _scanCancellation = new CancellationTokenSource();

            // Create progress handler
            var progress = new Progress<ScanProgress>(scanProgress =>
            {
                Dispatcher.Invoke(() =>
                {
                    Title = scanProgress.IsComplete
                        ? "Photo Gallery"
                        : $"Photo Gallery - Scanning: {scanProgress.ProgressPercentage:F0}% ({scanProgress.ProcessedCount}/{scanProgress.TotalCount})";
                });
            });

            // Run scan in background
            Task.Run(async () =>
            {
                try
                {
                    await MetadataScanner.RunParallelMetadataExtractionAsync(
                        folder,
                        maxConcurrency: Environment.ProcessorCount,
                        progress: progress,
                        cancellationToken: _scanCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Information("Metadata scan was cancelled");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Metadata scan failed for folder: {Folder}", folder);
                    Dispatcher.Invoke(() =>
                        MessageBox.Show(this, $"Metadata scan failed:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error));
                }
                finally
                {
                    Dispatcher.Invoke(() => Title = "Photo Gallery");
                }
            }, _scanCancellation.Token);
        }

        private void FolderTree_SelectedItemChanged(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem selectedItem && selectedItem.Tag is string path)
            {
                LoadImagesFromFolder(path);
                StartMetadataScan(path);
            }
        }

        private void Folder_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is not TreeViewItem item ||
                item.Items.Count != 1 ||
                item.Items[0] != null ||
                item.Tag is not string folderPath)
                return;

            item.Items.Clear();

            try
            {
                var dir = new DirectoryInfo(folderPath);
                var subDirs = dir.GetDirectories()
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden | FileAttributes.System))
                    .OrderBy(d => d.Name);

                foreach (var subDir in subDirs)
                {
                    var subItem = new TreeViewItem
                    {
                        Header = subDir.Name,
                        Tag = subDir.FullName
                    };
                    subItem.Expanded += Folder_Expanded;
                    subItem.Selected += FolderTree_SelectedItemChanged;

                    // Add placeholder if directory has subdirectories
                    if (subDir.GetDirectories().Any())
                        subItem.Items.Add(null);

                    item.Items.Add(subItem);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to expand folder: {Path}", folderPath);
            }
        }

        private void LoadImagesFromFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return;

            _currentFolderPath = folderPath;
            Log.Information("Loading images from folder: {Folder}", folderPath);

            try
            {
                // Stop current fetch worker
                _fetchWorker?.Stop();

                // Start new worker for this folder
                _fetchWorker = new ThumbnailFetchWorker(_repo, Dispatcher, Thumbnails);
                _fetchWorker.Start(folderPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load images from folder: {Folder}", folderPath);
                MessageBox.Show(this, $"Failed to load folder:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ReloadThumbnails_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentFolderPath))
            {
                LoadImagesFromFolder(_currentFolderPath);
            }
            else if (Directory.Exists(_defaultTestPath))
            {
                LoadImagesFromFolder(_defaultTestPath);
            }
            else
            {
                MessageBox.Show(this, "No folder selected. Please select a folder from the tree first.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ColumnSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColumnSelector.SelectedItem is ComboBoxItem cbi &&
                int.TryParse(cbi.Content?.ToString(), out int cols) &&
                cols > 0)
            {
                _currentColumns = cols;
                var grid = FindVisualChild<UniformGrid>(ThumbnailList);
                if (grid != null)
                    grid.Columns = cols;

                RefreshLazyImageWidths();
            }
        }

        private void RefreshLazyImageWidths()
        {
            if (_currentColumns <= 0 || ThumbnailList == null) return;

            double usableWidth = ThumbnailList.ActualWidth;
            if (usableWidth <= 0 || double.IsNaN(usableWidth)) return;

            double cellWidth = (usableWidth / _currentColumns) - CellMargin;
            if (cellWidth < 60) cellWidth = 60;

            foreach (var lazyImage in FindVisualChildren<Controls.LazyImage>(ThumbnailList))
                lazyImage.Width = cellWidth;
        }

        private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;

                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
            where T : DependencyObject
        {
            if (parent == null) yield break;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) yield return typed;

                foreach (var descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }
    }
}