namespace SessionMemory
{
    /// <summary>Lists the files the clients read, the way the original Python bridge did; shared by every client.</summary>
    internal static class SourceFiles
    {
        // Same order as Python 3.12 Path.rglob: the root, then the children of each directory walked depth-first.
        public static IEnumerable<string> Rglob(string root, string pattern)
        {
            if (!Directory.Exists(root)) yield break;
            foreach (var directory in WalkDirectories(root))
            {
                foreach (var file in Files(directory, pattern)) yield return file;
            }
        }

        private static IEnumerable<string> WalkDirectories(string root)
        {
            yield return root;
            var pending = new Stack<string>([root]);
            while (pending.Count > 0)
            {
                var children = SubDirectories(pending.Pop());
                foreach (var child in children) yield return child;
                for (var index = children.Count - 1; index >= 0; index--) pending.Push(children[index]);
            }
        }

        public static List<string> SubDirectories(string directory) =>
            Listing(() => new DirectoryInfo(directory).EnumerateDirectories()
                .Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(child => child.FullName));

        public static List<string> Files(string directory, string pattern) => Listing(() => Directory.EnumerateFiles(directory, pattern));

        // A directory that vanished or cannot be read lists nothing, so the walk goes on without it.
        // NTFS lists names alphabetically but ext4 and APFS do not, so sorting keeps the order, and the corpus built from it, the same everywhere.
        private static List<string> Listing(Func<IEnumerable<string>> entries) =>
            Attempt.Io(() => entries().Order(StringComparer.OrdinalIgnoreCase).ToList()).GetValueOrDefault([]);

        public static string[] DatabaseFiles(string path) => [path, path + "-wal"];
    }
}
