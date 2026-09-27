namespace Gm8Builder.Tree;

/// <summary>
/// File access for a tree made on Windows, where names are case-insensitive
/// and directories list in name order. On a case-sensitive file system a path
/// that does not exist as written is looked up ignoring case, one segment at a
/// time; listings are sorted as NTFS returns them (which is the order gmksplit,
/// via Java, saw).
/// </summary>
public static class TreePath
{
    public static string Combine(params string[] parts) => Resolve(System.IO.Path.Combine(parts));

    public static string Resolve(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return path;
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var segment in full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var exact = System.IO.Path.Combine(current, segment);
            if (File.Exists(exact) || Directory.Exists(exact))
            {
                current = exact;
                continue;
            }
            if (!Directory.Exists(current)) return path;
            var match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(e => string.Equals(System.IO.Path.GetFileName(e), segment, StringComparison.OrdinalIgnoreCase));
            if (match == null) return path;
            current = match;
        }
        return current;
    }

    /// <summary>Files matching `pattern`, in NTFS order (case-insensitive by name).</summary>
    public static IEnumerable<string> Files(string dir, string pattern) =>
        Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
            .Order(StringComparer.OrdinalIgnoreCase);
}
