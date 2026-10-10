using System;
using System.Collections.Generic;
using System.Text;

namespace Coporoby.Core
{
    // A work item knows whether the target directory is known to exist,
    // so new directories never cost a single call against the target.
    public class DirectoryWorkItem
    {
        private string _relativeDir;
        private bool _targetExists;

        public DirectoryWorkItem(string relativeDir, bool targetExists)
        {
            _relativeDir = relativeDir;
            _targetExists = targetExists;
        }

        public string RelativeDir
        {
            get { return _relativeDir; }
            set { _relativeDir = value; }
        }

        public bool TargetExists
        {
            get { return _targetExists; }
            set { _targetExists = value; }
        }
    }
}
