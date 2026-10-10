using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace Coporoby.Core
{
    public class DirectoryComparerThreaded
    {
        private readonly string _sourceDirectory;
        private readonly string _targetDirectory;
        private readonly int _concurrentScanners;
        private readonly TimeSpan _fileModifiedTolerance;

        private readonly Channel<DirectoryWorkItem> _directoryChannel = Channel.CreateUnbounded<DirectoryWorkItem>();
        private readonly ConcurrentBag<FileSystemChange> _fileSystemChanges = new();
        private int _directoriesPending;   // directories queued OR currently being processed

        private static readonly EnumerationOptions enumerationOptions = new()
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,   // don't follow symlinks/junctions
            RecurseSubdirectories = false,
            BufferSize = 64 * 1024                             // fewer round trips on file systems / network shares
        };

        public DirectoryComparerThreaded(string source, string target, int concurrentScanners, TimeSpan? fileModifiedTolerance)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(concurrentScanners, 1);
            _sourceDirectory = Path.GetFullPath(source);
            _targetDirectory = Path.GetFullPath(target);
            _concurrentScanners = concurrentScanners;
            _fileModifiedTolerance = fileModifiedTolerance ?? TimeSpan.Zero;
        }

        public async Task<IReadOnlyCollection<FileSystemChange>> RunAsync(CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(_sourceDirectory))
                throw new DirectoryNotFoundException($"Source not found: {_sourceDirectory}");

            if (!Directory.Exists(_targetDirectory))
                throw new DirectoryNotFoundException($"Target not found or not reachable: {_targetDirectory}");

            // Source listing blocks on synchronous I/O (and each may use one more thread for the
            // concurrent target listing, therefore _consumerCount * 2), so let the pool grow
            // immediately instead of slowly. + 4 as buffer for other workl in the process.
            ThreadPool.GetMinThreads(out int minWorker, out int minIo);
            int minNeededWorker = _concurrentScanners * 2 + 4;
            if (minWorker < minNeededWorker)
                ThreadPool.SetMinThreads(minNeededWorker, minIo);

            EnqueuDirectoryScan(new DirectoryWorkItem(string.Empty, true));   // root = empty relative path

            Task[] tasks = Enumerable.Range(0, _concurrentScanners)
                .Select(_ => Task.Run(() => DequeueDirectoryScanAsync(cancellationToken), cancellationToken))
                .ToArray();
            await Task.WhenAll(tasks);

            return _fileSystemChanges.ToArray();
        }

        private void EnqueuDirectoryScan(DirectoryWorkItem workItem)
        {
            Interlocked.Increment(ref _directoriesPending);   // count before writing so it can't hit 0 early
            if (!_directoryChannel.Writer.TryWrite(workItem))
            {
                throw new InvalidOperationException("Adding directory to queue failed.");
            }
        }

        private async Task DequeueDirectoryScanAsync(CancellationToken cancellationToken)
        {
            await foreach (DirectoryWorkItem workItem in _directoryChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    CompareDirectory(workItem, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Todo: Logger / output instead.
                    Console.WriteLine($"Error in '{workItem.RelativeDir}': {ex.Message}");
                }
                finally
                {
                    // Subdirectories were enqueued (and counted) in CompareDirectory already
                    if (Interlocked.Decrement(ref _directoriesPending) == 0)
                    {
                        _directoryChannel.Writer.TryComplete();
                    }
                }
            }
        }

        private void CompareDirectory(DirectoryWorkItem workItem, CancellationToken cancellationToken)
        {
            string sourcePath = Path.Combine(_sourceDirectory, workItem.RelativeDir);
            string targetPath = Path.Combine(_targetDirectory, workItem.RelativeDir);

            // One enumeration per side, but on target only when it exists. Source and target are listed concurrently.
            Task<Dictionary<string, FileSystemObject>>? targetTask = workItem.TargetExists
                ? Task.Run(() => ListDirectory(targetPath), cancellationToken)
                : null;

            Dictionary<string, FileSystemObject> sourceEntries = ListDirectory(sourcePath);
            Dictionary<string, FileSystemObject> targetEntries = targetTask is null
                ? new Dictionary<string, FileSystemObject>(StringComparer.OrdinalIgnoreCase)
                : targetTask.GetAwaiter().GetResult();

            // 1. Check everything in source directory for matches in target directory.
            foreach ((string name, FileSystemObject sourceEntry) in sourceEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = Path.Combine(workItem.RelativeDir, name);

                // Same name and same kind on the other side?
                bool hasMatch = targetEntries.TryGetValue(name, out FileSystemObject? targetEntry) && targetEntry.IsDirectory == sourceEntry.IsDirectory;

                if (sourceEntry.IsDirectory)
                {
                    if (!hasMatch)
                    {
                        _fileSystemChanges.Add(new FileSystemChange(ChangeType.Create, relativePath, "(directory)", true));
                    }

                    // Always descend: for a new directory this lists its content as New files/dirs.
                    // If the target directory is missing, the children never touch the target.
                    EnqueuDirectoryScan(new DirectoryWorkItem(relativePath, hasMatch));
                    continue;
                }

                // We are on a file here.
                if (!hasMatch)
                {
                    _fileSystemChanges.Add(new FileSystemChange(ChangeType.Create, relativePath,
                        $"(source modified {sourceEntry.LastWriteUtc:u})", false));
                    continue;
                }

                // If we have a match targetEntry cannot be null.
                TimeSpan diff = sourceEntry.LastWriteUtc - targetEntry!.LastWriteUtc;
                if (diff.Duration() > _fileModifiedTolerance)
                {
                    string direction = diff > TimeSpan.Zero ? "source newer" : "target newer";
                    _fileSystemChanges.Add(new FileSystemChange(ChangeType.Update, relativePath,
                        $"({direction}: {sourceEntry.LastWriteUtc:u} vs {targetEntry.LastWriteUtc:u})", false));
                }
                else if (sourceEntry.Length != targetEntry.Length)
                {
                    _fileSystemChanges.Add(new FileSystemChange(ChangeType.Update, relativePath,
                        $"(size differs: {sourceEntry.Length} vs {targetEntry.Length})", false));
                }
            }

            // 2. Check everything in target directory for no matches in source directory.
            //    A directory not in source but in target is reported ONCE and not descended into
            //    (it is never enqueued), so its content is not reported file by file.
            foreach ((string name, FileSystemObject targetEntry) in targetEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sourceEntries.TryGetValue(name, out FileSystemObject? sourceEntry) && sourceEntry.IsDirectory == targetEntry.IsDirectory)
                {
                    continue;
                }

                string relativePath = Path.Combine(workItem.RelativeDir, name);

                if (targetEntry.IsDirectory)
                {
                    _fileSystemChanges.Add(new FileSystemChange(ChangeType.Delete, relativePath,
                        "(directory only in target)", true));
                }
                else
                {
                    _fileSystemChanges.Add(new FileSystemChange(ChangeType.Delete, relativePath,
                        $"(only in target, modified {targetEntry.LastWriteUtc:u})", false));
                }
            }
        }

        // One enumeration returns files and directories together.
        // Case-insensitive like Windows/SMB
        private static Dictionary<string, FileSystemObject> ListDirectory(string path)
        {
            Dictionary<string, FileSystemObject> result = new Dictionary<string, FileSystemObject>(StringComparer.OrdinalIgnoreCase);

            foreach ((string name, FileSystemObject entry) in new FileSystemEnumerable<(string Name, FileSystemObject)>(path,
                (ref FileSystemEntry e) => (
                    e.FileName.ToString(),
                    new FileSystemObject(e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcDateTime)),
                enumerationOptions))

                result[name] = entry;


            return result;
        }
    }
}