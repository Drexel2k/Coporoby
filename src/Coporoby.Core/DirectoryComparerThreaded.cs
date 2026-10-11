using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.ExceptionServices;

namespace Coporoby.Core
{
    public class DirectoryComparerThreaded
    {
        private readonly string _sourceDirectory;
        private readonly string _targetDirectory;
        private readonly int _concurrentComparers;
        private readonly TimeSpan _fileModifiedTolerance;

        // Directories still to compare. Consumed by the source comparing threads.
        private readonly BlockingCollection<DirectoryWorkItem> _sourceDirectoryQueue = [];
        // Directory listings requested by source comparing threads (used for the concurrent target listing)
        private readonly BlockingCollection<DirectoryListRequest> _targetDirectoryQueue = [];
        private readonly ConcurrentBag<FileSystemChange> _fileSystemChanges = [];
        private readonly TaskCompletionSource<IReadOnlyCollection<FileSystemChange>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _sourceDirectoriesPending;   // directories queued or currently being processed
        private int _activeSourceComparing;       // source listing threads still running
        private int _started;              // an instance can only run once

        private static readonly EnumerationOptions enumerationOptions = new()
        {
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,   // don't follow symlinks/junctions
            RecurseSubdirectories = false,
            BufferSize = 64 * 1024                             // fewer round trips on file systems / network shares
        };

        public DirectoryComparerThreaded(string source, string target, int concurrentComparers, TimeSpan? fileModifiedTolerance)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(concurrentComparers, 1);
            _sourceDirectory = Path.GetFullPath(source);
            _targetDirectory = Path.GetFullPath(target);
            _concurrentComparers = concurrentComparers;
            _fileModifiedTolerance = fileModifiedTolerance ?? TimeSpan.Zero;
        }

        public async Task<IReadOnlyCollection<FileSystemChange>> RunAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("This comparer instance has already been run.");

            if (!Directory.Exists(_sourceDirectory))
                throw new DirectoryNotFoundException($"Source not found: {_sourceDirectory}");

            if (!Directory.Exists(_targetDirectory))
                throw new DirectoryNotFoundException($"Target not found or not reachable: {_targetDirectory}");

            EnqueueSourceDirectoryCompare(new DirectoryWorkItem(string.Empty, true));   // root = empty relative path

            // Dedicated threads for listing source and target directories
            // We use thread and not taks here, because in the library we don't want to use the thread pool
            _activeSourceComparing = _concurrentComparers;

            for (int i = 0; i < _concurrentComparers; i++)
                StartThread($"TargetLister-{i}", DequeueTargetDirectoryList);

            for (int i = 0; i < _concurrentComparers; i++)
                StartThread($"SourceLister-{i}", () => DequeueSourceDirectoryCompare(cancellationToken));

            return await _completion.Task.ConfigureAwait(false);
        }

        private static void StartThread(string name, ThreadStart body)
        {
            new Thread(body) { IsBackground = true, Name = name }.Start();
        }

        private void EnqueueSourceDirectoryCompare(DirectoryWorkItem workItem)
        {
            Interlocked.Increment(ref _sourceDirectoriesPending);   // increment before adding so it can't hit 0 early
            _sourceDirectoryQueue.Add(workItem);
        }

        private void DequeueSourceDirectoryCompare(CancellationToken cancellationToken)
        {
            try
            {
                foreach (DirectoryWorkItem workItem in _sourceDirectoryQueue.GetConsumingEnumerable(cancellationToken))
                {
                    try
                    {
                        CompareDirectory(workItem, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Todo: Log / output
                    }
                    finally
                    {
                        // Subdirectories were enqueued (and counted) in CompareDirectory already
                        if (Interlocked.Decrement(ref _sourceDirectoriesPending) == 0)
                        {
                            _sourceDirectoryQueue.CompleteAdding();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Reported via Finish() through the token.
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeSourceComparing) == 0)
                {
                    Finish(cancellationToken);
                }
            }
        }

        // Called by the last comparer thread to exit.
        private void Finish(CancellationToken cancellationToken)
        {
            _targetDirectoryQueue.CompleteAdding();   // lets the target listing threads exit

            if (cancellationToken.IsCancellationRequested)
                _completion.TrySetCanceled(cancellationToken);
            else
                _completion.TrySetResult(_fileSystemChanges.ToArray());
        }

        private void DequeueTargetDirectoryList()
        {
            foreach (DirectoryListRequest request in _targetDirectoryQueue.GetConsumingEnumerable())
            {
                try
                {
                    request.Result = ListDirectory(request.Path);
                }
                catch (Exception ex)
                {
                    request.Error = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    request.Done.Set();
                }
            }
        }

        private void CompareDirectory(DirectoryWorkItem workItem, CancellationToken cancellationToken)
        {
            string sourcePath = Path.Combine(_sourceDirectory, workItem.RelativeDir);
            string targetPath = Path.Combine(_targetDirectory, workItem.RelativeDir);

            // One enumeration per side, but on target only when it exists.
            // Target is listed on a listing thread while this thread lists the source.
            DirectoryListRequest? targetRequest = null;
            if (workItem.TargetExists)
            {
                targetRequest = new DirectoryListRequest(targetPath);
                _targetDirectoryQueue.Add(targetRequest, cancellationToken);
            }

            Dictionary<string, FileSystemObject> sourceEntries;
            Dictionary<string, FileSystemObject> targetEntries;

            try
            {
                sourceEntries = ListDirectory(sourcePath);
            }
            catch (Exception)
            {
                // Todo: Log / output
                return;
            }

            bool targetListed = true;
            try
            {
                targetEntries = targetRequest is null ? [] : targetRequest.Wait(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                targetEntries = [];
                targetListed = false;
                // Todo: Log / output
            }

            // 1. Check everything in source directory for matches in target directory.
            foreach ((string name, FileSystemObject sourceEntry) in sourceEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = Path.Combine(workItem.RelativeDir, name);

                if (!targetListed)
                {
                    // Target state unknown: report nothing here, but keep descending.
                    // TargetExists = true, so the child tries its own target listing.
                    if (sourceEntry.IsDirectory)
                        EnqueueSourceDirectoryCompare(new DirectoryWorkItem(relativePath, true));
                    continue;
                }

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
                    EnqueueSourceDirectoryCompare(new DirectoryWorkItem(relativePath, hasMatch));
                    continue;
                }

                // We are on a file here.
                if (!hasMatch)
                {
                    _fileSystemChanges.Add(new FileSystemChange(ChangeType.Create, relativePath,
                        $"(source new {sourceEntry.LastWriteUtc:u})", false));
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

            if (!targetListed)
                return;

            // 2. Check everything in target directory for no matches in source directory.
            //    A directory not in source but in target is reported once and not descended into
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
        private static Dictionary<string, FileSystemObject> ListDirectory(string path)
        {
            Dictionary<string, FileSystemObject> result = [];

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