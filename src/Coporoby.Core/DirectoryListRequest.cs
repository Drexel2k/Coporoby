using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Coporoby.Core
{
    public class DirectoryListRequest
    {
        private readonly string _path;
        private Dictionary<string, FileSystemObject>? _result;
        private readonly ManualResetEventSlim _done = new(false);
        private ExceptionDispatchInfo? _error;
        public string Path
        {
            get => _path;
        }
        public Dictionary<string, FileSystemObject>? Result
        {
            set => _result = value;
            get => _result;

        }
        public ExceptionDispatchInfo? Error
        {
            set => _error = value;
            get => _error;
        }
        public ManualResetEventSlim Done
        {
            get => _done;
        }
        public DirectoryListRequest(string path) => _path = path;

        public Dictionary<string, FileSystemObject> Wait(CancellationToken cancellationToken)
        {
            _done.Wait(cancellationToken);
            _error?.Throw();
            return _result!;
        }
    }
}
