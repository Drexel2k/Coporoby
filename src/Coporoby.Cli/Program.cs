using Coporoby.Core;
using System.Diagnostics;

namespace Coporoby.Cli
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Stopwatch stopwatch = new Stopwatch();
            if (args.Length >= 2)
            {
                string source = args[0];
                string target = args[1];
                int concurrentScanners = args.Length > 2 && int.TryParse(args[2], out int concurrentScannersInternal) ? concurrentScannersInternal : 4;
                TimeSpan fileModifiedTolerance = TimeSpan.FromSeconds(args.Length > 3 && double.TryParse(args[3], out double timeSpan) ? timeSpan : 0);

                using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellationTokenSource.Cancel(); };


                try
                {
                    Console.WriteLine($"DirectoryComparerThreaded");
                    Console.WriteLine();
                    stopwatch.Start();
                    DirectoryComparerThreaded comparer = new DirectoryComparerThreaded(source, target, concurrentScanners, fileModifiedTolerance);
                    IReadOnlyCollection<FileSystemChange> changes = await comparer.RunAsync(cancellationTokenSource.Token);
                    Console.WriteLine(stopwatch.Elapsed);

                    foreach (FileSystemChange fileSystemChange in changes.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        string marker;
                        if (fileSystemChange.IsDirectory)
                        {
                            marker = fileSystemChange.Type switch
                            {
                                ChangeType.Create => "++",
                                ChangeType.Update => "~~",
                                ChangeType.Delete => "--",
                                _ => "??"
                            };
                        }
                        else
                        {
                            marker = fileSystemChange.Type switch
                            {
                                ChangeType.Create => "+",
                                ChangeType.Update => "~",
                                ChangeType.Delete => "-",
                                _ => "?"
                            };
                        }

                        Console.WriteLine($"{marker} {fileSystemChange.RelativePath}  {fileSystemChange.Detail}");
                    }

                    Console.WriteLine();
                    Console.WriteLine($"Create: {changes.Count(x => x.Type == ChangeType.Create)}, " +
                                      $"Update: {changes.Count(x => x.Type == ChangeType.Update)}, " +
                                      $"Delete: {changes.Count(x => x.Type == ChangeType.Delete)}");



                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Cancelled.");
                }

            }
            else
            {
                Console.WriteLine("Usage: <sourceFolder> <targetFolder> [consumers=4] [toleranceSeconds=0]");
            }

            Console.ReadKey();
        }
    }
}
