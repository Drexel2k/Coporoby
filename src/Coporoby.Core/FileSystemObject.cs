using System;
using System.Collections.Generic;
using System.Text;

namespace Coporoby.Core
{
    public class FileSystemObject
    {
        private bool _isDirectory;
        private long _length;
        private DateTime _lastWriteUtc;

        public FileSystemObject(bool isDirectory, long length, DateTime lastWriteUtc)
        {
            _isDirectory = isDirectory;
            _length = length;
            _lastWriteUtc = lastWriteUtc;
        }

        public bool IsDirectory
        {
            get { return _isDirectory; }
            set { _isDirectory = value; }
        }

        public long Length
        {
            get { return _length; }
            set { _length = value; }
        }

        public DateTime LastWriteUtc
        {
            get { return _lastWriteUtc; }
            set { _lastWriteUtc = value; }
        }
    }
}
