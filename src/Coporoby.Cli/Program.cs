using Coporoby.Core;
using System.Diagnostics;

namespace Coporoby.Cli
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Stopwatch stopwatch = new Stopwatch();
            //args = ["C:\\source", "\\\\ip\\target"];
            if (args.Length >= 2)
            {
                string source = args[0];
                string target = args[1];
                int concurrentComparers = args.Length > 2 && int.TryParse(args[2], out int concurrentComparersInternal) ? concurrentComparersInternal : 4;
                TimeSpan fileModifiedTolerance = TimeSpan.FromMilliseconds(args.Length > 3 && double.TryParse(args[3], out double timeSpan) ? timeSpan : 0);

                using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellationTokenSource.Cancel(); };


                try
                {
                    Console.WriteLine($"DirectoryComparerThreaded");
                    Console.WriteLine();
                    stopwatch.Start();
                    DirectoryComparerThreaded comparer = new DirectoryComparerThreaded(source, target, concurrentComparers, fileModifiedTolerance);
                    IReadOnlyCollection<FileSystemChange> changes = await comparer.RunAsync(cancellationTokenSource.Token);
                    Console.WriteLine(stopwatch.Elapsed);

                    foreach (FileSystemChange fileSystemChange in changes.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        string marker;
                        if (fileSystemChange.IsDirectory)
                        {
                            marker = fileSystemChange.Type switch
                            {
                                ChangeType.Create => "D+",
                                ChangeType.Update => "D~",
                                ChangeType.Delete => "D-",
                                _ => "D?"
                            };
                        }
                        else
                        {
                            marker = fileSystemChange.Type switch
                            {
                                ChangeType.Create => "F+",
                                ChangeType.Update => "F~",
                                ChangeType.Delete => "F-",
                                _ => "F?"
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
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception {ex}.");
                }

            }
            else
            {
                Console.WriteLine("Usage: <sourceFolder> <targetFolder> [comparers=4] [toleranceMilliSeconds=0]");
            }

            Console.ReadKey();
        }
    }
}
