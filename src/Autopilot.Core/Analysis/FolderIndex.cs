namespace Autopilot.Core.Analysis;

/// <summary>One file or folder of the analyzed folder.</summary>
/// <param name="Path">Relative to the analyzed folder, forward slashes, no trailing slash.</param>
public sealed record IndexEntry(string Path, bool IsDirectory, long Length)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>The folder that holds the entry; "." for the top folder.</summary>
    public string Parent => Path.Contains('/', StringComparison.Ordinal) ? Path[..Path.LastIndexOf('/')] : ".";

    public bool NameIs(string name) => Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    public bool HasExtension(string extension) => Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A read-only, bounded listing of a folder: build output, dependency folders and most dot folders are
/// left out, links are not followed, and the walk stops after <see cref="MaxEntries"/> entries.
/// </summary>
public sealed class FolderIndex
{
    public const int MaxEntries = 30_000;
    public const int MaxDepth = 5;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "artifacts", "TestResults", "dist", "target", "__pycache__",
        "venv", "env", "site-packages", "coverage", "vendor",
    };

    /// <summary>Dot folders that hold something the analyzer cares about; every other one is skipped.</summary>
    private static readonly HashSet<string> KeptDotFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".github", ".claude", ".azuredevops", ".pipelines", ".circleci", ".gitlab", ".devcontainer",
    };

    private FolderIndex(string root, List<IndexEntry> entries, bool truncated)
    {
        Root = root;
        Entries = entries;
        Truncated = truncated;
    }

    public string Root { get; }

    public IReadOnlyList<IndexEntry> Entries { get; }

    /// <summary>True when the walk stopped early because the folder is very large.</summary>
    public bool Truncated { get; }

    public IEnumerable<IndexEntry> Files => Entries.Where(e => !e.IsDirectory);

    public IEnumerable<IndexEntry> Directories => Entries.Where(e => e.IsDirectory);

    public static FolderIndex Build(string root)
    {
        var entries = new List<IndexEntry>();
        var truncated = false;
        var pending = new Queue<(string Folder, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0 && !truncated)
        {
            var (folder, depth) = pending.Dequeue();
            List<FileSystemInfo> children;
            try
            {
                children = [.. new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.Ordinal)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (entries.Count >= MaxEntries)
                {
                    truncated = true;
                    break;
                }

                if (child.LinkTarget is not null)
                {
                    continue;
                }

                var relative = System.IO.Path.GetRelativePath(root, child.FullName).Replace('\\', '/');
                if (child is DirectoryInfo)
                {
                    var name = child.Name;
                    if (SkippedFolders.Contains(name) || name == ".git" || (name.StartsWith('.') && !KeptDotFolders.Contains(name)))
                    {
                        continue;
                    }

                    entries.Add(new IndexEntry(relative, true, 0));
                    if (depth + 1 < MaxDepth)
                    {
                        pending.Enqueue((child.FullName, depth + 1));
                    }
                }
                else if (child is FileInfo file)
                {
                    entries.Add(new IndexEntry(relative, false, file.Length));
                }
            }
        }

        return new FolderIndex(root, entries, truncated);
    }

    public bool DirectoryExists(string path) => Entries.Any(e => e.IsDirectory && e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    public bool FileExists(string path) => Entries.Any(e => !e.IsDirectory && e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Files directly in <paramref name="folder"/> whose name satisfies <paramref name="match"/>.</summary>
    public IEnumerable<IndexEntry> FilesIn(string folder, Func<IndexEntry, bool> match) =>
        Files.Where(e => e.Parent == folder && match(e));

    /// <summary>The first <paramref name="maxChars"/> characters of a file; empty when it cannot be read.</summary>
    public string Read(string relativePath, int maxChars = 200_000)
    {
        try
        {
            using var reader = new StreamReader(System.IO.Path.Combine(Root, relativePath));
            var buffer = new char[maxChars];
            var count = reader.ReadBlock(buffer, 0, maxChars);
            return new string(buffer, 0, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
