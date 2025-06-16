using Extension.Utilities;
using PhotoGallery.Imaging;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace PhotoGallery.Workers
{
    /// <summary>
    /// Handles parallel metadata extraction and caching for media files.
    /// </summary>
    public static class MetadataScanner
    {
        private static readonly object _scanLock = new object();
        private static volatile bool _isScanning = false;

        /// <summary>
        /// Runs parallel metadata extraction for all supported files in a folder.
        /// </summary>
        /// <param name="folder">Root folder to scan</param>
        /// <param name="maxConcurrency">Maximum parallel operations (default: processor count)</param>
        /// <param name="progress">Optional progress callback</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public static async Task RunParallelMetadataExtractionAsync(
            string folder,
            int? maxConcurrency = null,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            lock (_scanLock)
            {
                if (_isScanning)
                {
                    Log.Warning("Metadata scan already in progress, skipping new scan request");
                    return;
                }
                _isScanning = true;
            }

            try
            {
                await RunMetadataExtractionInternal(folder, maxConcurrency, progress, cancellationToken);
            }
            finally
            {
                lock (_scanLock)
                {
                    _isScanning = false;
                }
            }
        }

        /// <summary>
        /// Synchronous version for backward compatibility.
        /// </summary>
        public static void RunParallelMetadataExtraction(string folder)
        {
            try
            {
                RunParallelMetadataExtractionAsync(folder).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Synchronous metadata extraction failed for folder: {Folder}", folder);
                throw;
            }
        }

        private static async Task RunMetadataExtractionInternal(
            string folder,
            int? maxConcurrency,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                Log.Error("Invalid or non-existent folder: {Folder}", folder);
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            Log.Information("Starting metadata extraction for folder: {Folder}", folder);

            // FIXED: Get files asynchronously to avoid blocking
            var imageFiles = await Helper.GetImageFilesRecursiveAsync(folder).ConfigureAwait(false);
            var videoFiles = await Helper.GetVideoFilesRecursiveAsync(folder).ConfigureAwait(false);
            var allFiles = imageFiles.Concat(videoFiles).ToList();

            if (allFiles.Count == 0)
            {
                Log.Warning("No supported media files found in folder: {Folder}", folder);
                progress?.Report(new ScanProgress(0, 0, "No files found"));
                return;
            }

            Log.Information("Discovered {Count} media files. Starting extraction...", allFiles.Count);
            progress?.Report(new ScanProgress(0, allFiles.Count, "Starting scan..."));

            // FIXED: Reduced concurrency to prevent overwhelming system
            var concurrency = Math.Min(maxConcurrency ?? Environment.ProcessorCount, 3);
            var semaphore = new SemaphoreSlim(concurrency, concurrency);
            var processedCount = 0;
            var errorCount = 0;
            var skippedCount = 0;

            var results = new ConcurrentBag<ProcessResult>();

            // FIXED: Process files in smaller batches to reduce memory pressure
            const int batchSize = 50;
            var batches = allFiles.Chunk(batchSize);

            foreach (var batch in batches)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Process batch with limited concurrency
                var tasks = batch.Select(async filePath =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var result = await ProcessFileAsync(filePath, cancellationToken).ConfigureAwait(false);
                        results.Add(result);

                        var currentProcessed = Interlocked.Increment(ref processedCount);
                        if (result.Success)
                        {
                            Log.Debug("✓ Metadata extracted: {Path}", filePath);
                        }
                        else if (result.Skipped)
                        {
                            Interlocked.Increment(ref skippedCount);
                            Log.Debug("⊘ Skipped (already exists): {Path}", filePath);
                        }
                        else
                        {
                            Interlocked.Increment(ref errorCount);
                            Log.Warning("✗ Failed: {Path} - {Error}", filePath, result.Error);
                        }

                        // Report progress every 5 files or on last file
                        if (currentProcessed % 5 == 0 || currentProcessed == allFiles.Count)
                        {
                            progress?.Report(new ScanProgress(
                                currentProcessed,
                                allFiles.Count,
                                $"Processed {currentProcessed}/{allFiles.Count} files"));
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Small delay between batches to prevent overwhelming the system
                if (cancellationToken.IsCancellationRequested)
                    break;

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            var successCount = results.Count(r => r.Success);

            Log.Information(
                "✓ Metadata extraction complete: {Success} success, {Skipped} skipped, {Errors} errors, {Total} total files in {Duration:F1}s",
                successCount, skippedCount, errorCount, allFiles.Count, stopwatch.Elapsed.TotalSeconds);

            progress?.Report(new ScanProgress(
                allFiles.Count,
                allFiles.Count,
                $"Complete: {successCount} processed, {skippedCount} skipped, {errorCount} errors"));
        }

        private static async Task<ProcessResult> ProcessFileAsync(string filePath, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip if already processed (optional optimization)
                if (PhotoDb.Exists(filePath))
                {
                    return new ProcessResult { Skipped = true, FilePath = filePath };
                }

                // Use async EXIF reading if available
                var reader = new GdiExifReader();
                var exifInfo = await reader.ReadAsync(filePath);

                // Store in database
                PhotoDb.UpsertPhoto(exifInfo);

                return new ProcessResult { Success = true, FilePath = filePath };
            }
            catch (OperationCanceledException)
            {
                Log.Information("Metadata extraction cancelled for: {Path}", filePath);
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to process file: {Path}", filePath);
                return new ProcessResult
                {
                    Success = false,
                    FilePath = filePath,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Gets scanning status.
        /// </summary>
        public static bool IsScanning => _isScanning;

        /// <summary>
        /// Cancellable metadata extraction with progress reporting.
        /// </summary>
        public static async Task ScanFolderWithProgressAsync(
            string folder,
            IProgress<ScanProgress> progress,
            CancellationToken cancellationToken = default)
        {
            await RunParallelMetadataExtractionAsync(folder, null, progress, cancellationToken);
        }
    }

    /// <summary>
    /// Progress information for metadata scanning.
    /// </summary>
    public record ScanProgress(int ProcessedCount, int TotalCount, string StatusMessage)
    {
        public double ProgressPercentage => TotalCount > 0 ? (double)ProcessedCount / TotalCount * 100 : 0;
        public bool IsComplete => ProcessedCount >= TotalCount;
    }

    /// <summary>
    /// Result of processing a single file.
    /// </summary>
    internal record ProcessResult
    {
        public bool Success { get; init; }
        public bool Skipped { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public string? Error { get; init; }
    }
}