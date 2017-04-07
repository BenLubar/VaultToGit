using System;
using System.Collections.Generic;

namespace SplitGitRepo
{
    partial class Config
    {
        public static Config Instance { get; private set; }

        public string MainRepo { get; set; }
        public IEnumerable<MergedRepo> Merge { get; set; }
        public IEnumerable<SharedRepo> Shared { get; set; }
        public Func<string, string> Origin { get; set; }
        public Func<string, string> MapEmail { get; set; }
    }
}
