using System;
using System.Collections.Generic;
using System.Text;

namespace Coporoby.Core
{
    public class FileSystemChange
    {
        private ChangeType _type;
        private string _relativePath;
        private string _detail;
        private bool _isDirectory;

        public FileSystemChange(ChangeType type, string relativePath, string detail, bool isDirectory)
        {
            _type = type;
            _relativePath = relativePath;
            _detail = detail;
            _isDirectory = isDirectory;
        }

        public ChangeType Type
        {
            get { return _type; }
            set { _type = value; }
        }

        public string RelativePath
        {
            get { return _relativePath; }
            set { _relativePath = value; }
        }

        public string Detail
        {
            get { return _detail; }
            set { _detail = value; }
        }

        public bool IsDirectory
        {
            get { return _isDirectory; }
            set { _isDirectory = value; }
        }
    }
}
