using System.Text.RegularExpressions;

namespace Autopilot.Core.Impact;

public enum EdgeKind
{
    Import,
    TestOf,
}

/// <summary>"From" depends on (imports / tests) "To". Paths are project-relative with forward slashes.</summary>
public sealed record ReferenceEdge(string From, string To, EdgeKind Kind);

public sealed record SkippedFile(string Path, string Reason);

public sealed record BlastRadius(
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> DirectDependents,
    IReadOnlyList<string> TransitiveDependents,
    IReadOnlyList<string> CoveringTests,
    IReadOnlyList<SkippedFile> Skipped);

public sealed class ReferenceGraph
{
    private readonly HashSet<string> _files;
    private readonly List<ReferenceEdge> _edges;
    private readonly Dictionary<string, List<ReferenceEdge>> _incoming = new(StringComparer.Ordinal);

    public ReferenceGraph(IEnumerable<string> files, IEnumerable<ReferenceEdge> edges, IEnumerable<SkippedFile> skipped)
    {
        _files = new HashSet<string>(files, StringComparer.Ordinal);
        _edges = edges.Distinct().ToList();
        Skipped = skipped.ToList();
        foreach (var e in _edges)
        {
            if (!_incoming.TryGetValue(e.To, out var list))
                _incoming[e.To] = list = new List<ReferenceEdge>();
            list.Add(e);
        }
    }

    public IReadOnlyCollection<string> Files => _files;
    public IReadOnlyList<ReferenceEdge> Edges => _edges;
    public IReadOnlyList<SkippedFile> Skipped { get; }

    public static readonly string[] Extensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts"];
    private static readonly string[] IgnoredDirs = ["node_modules", ".git", ".autopilot", "dist", "build", "bin", "obj", "coverage"];

    private static readonly Regex ImportRegex = new(
        @"(?:\bimport\s*(?:[^'""();]*?\bfrom\s*)?|\bexport\s+[^'"";]*?\bfrom\s*|\brequire\s*\(\s*|\bimport\s*\(\s*)['""](?<spec>[^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static bool IsTestFile(string path)
    {
        var p = path.Replace('\\', '/');
        var name = p[(p.LastIndexOf('/') + 1)..];
        return name.Contains(".test.", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".spec.", StringComparison.OrdinalIgnoreCase)
            || p.Split('/').Any(s => s == "__tests__");
    }

    /// <summary>Builds the TS/JS import graph for a project directory. Never throws for bad files.</summary>
    public static ReferenceGraph Build(string root)
    {
        var skipped = new List<SkippedFile>();
        var files = new List<string>();
        if (!Directory.Exists(root))
        {
            skipped.Add(new SkippedFile(".", "project root not found"));
            return new ReferenceGraph([], [], skipped);
        }

        Collect(root, root, files, skipped);
        var fileSet = new HashSet<string>(files, StringComparer.Ordinal);
        var edges = new List<ReferenceEdge>();
        var parsed = new List<string>();

        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(root, file));
            }
            catch (Exception ex)
            {
                skipped.Add(new SkippedFile(file, "unreadable: " + ex.Message));
                continue;
            }
            if (text.Contains('\0'))
            {
                skipped.Add(new SkippedFile(file, "unparseable: binary content"));
                continue;
            }
            parsed.Add(file);
            var isTest = IsTestFile(file);
            foreach (Match m in ImportRegex.Matches(text))
            {
                var target = Resolve(file, m.Groups["spec"].Value, fileSet);
                if (target is null || target == file) continue;
                edges.Add(new ReferenceEdge(file, target, isTest ? EdgeKind.TestOf : EdgeKind.Import));
            }
        }

        // Naming convention: foo.test.ts / foo.spec.ts / __tests__/foo.ts cover foo.*
        var byBase = fileSet.Where(f => !IsTestFile(f))
            .GroupBy(f => (Dir: DirOf(f), Base: BaseName(f)))
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var test in parsed.Where(IsTestFile))
        {
            var dir = DirOf(test);
            var b = BaseName(test);
            var dirs = new List<string> { dir };
            if (dir.EndsWith("__tests__", StringComparison.Ordinal) || dir == "__tests__")
                dirs.Add(DirOf(dir));
            foreach (var d in dirs)
                if (byBase.TryGetValue((d, b), out var targets))
                    foreach (var t in targets)
                        edges.Add(new ReferenceEdge(test, t, EdgeKind.TestOf));
        }

        return new ReferenceGraph(files, edges, skipped);
    }

    private static void Collect(string root, string dir, List<string> files, List<SkippedFile> skipped)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
                if (Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    files.Add(Path.GetRelativePath(root, f).Replace('\\', '/'));
            foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
                if (!IgnoredDirs.Contains(Path.GetFileName(d)))
                    Collect(root, d, files, skipped);
        }
        catch (Exception ex)
        {
            skipped.Add(new SkippedFile(Path.GetRelativePath(root, dir).Replace('\\', '/'), "unreadable directory: " + ex.Message));
        }
    }

    private static string DirOf(string p)
    {
        var i = p.LastIndexOf('/');
        return i < 0 ? "" : p[..i];
    }

    private static string BaseName(string p)
    {
        var name = p[(p.LastIndexOf('/') + 1)..];
        var parts = name.Split('.');
        return parts[0];
    }

    private static string? Resolve(string from, string spec, HashSet<string> files)
    {
        if (!spec.StartsWith('.')) return null; // external package
        var parts = new List<string>();
        var dir = DirOf(from);
        if (dir.Length > 0) parts.AddRange(dir.Split('/'));
        foreach (var seg in spec.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(seg);
        }
        var basePath = string.Join('/', parts);
        var candidates = new List<string> { basePath };
        var ext = Path.GetExtension(basePath);
        if (ext is ".js" or ".jsx" or ".mjs" or ".cjs")
            candidates.Add(basePath[..^ext.Length]);
        foreach (var c in candidates.ToList())
        {
            foreach (var e in Extensions) candidates.Add(c + e);
            foreach (var e in Extensions) candidates.Add(c + "/index" + e);
        }
        return candidates.FirstOrDefault(files.Contains);
    }

    /// <summary>Direct and transitive dependents and covering tests of the changed files.</summary>
    public BlastRadius ComputeBlastRadius(IEnumerable<string> changedFiles)
    {
        var changed = changedFiles.Select(f => f.Replace('\\', '/')).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var skipped = new List<SkippedFile>(Skipped.Where(s => changed.Contains(s.Path)));
        var known = new List<string>();
        foreach (var c in changed)
        {
            if (_files.Contains(c)) known.Add(c);
            else if (!skipped.Any(s => s.Path == c))
                skipped.Add(new SkippedFile(c, "not in reference graph"));
        }

        var seen = new HashSet<string>(known, StringComparer.Ordinal);
        var direct = new List<string>();
        var transitive = new List<string>();
        var frontier = known.ToList();
        var depth = 0;
        while (frontier.Count > 0)
        {
            depth++;
            var next = new List<string>();
            foreach (var f in frontier)
            {
                if (!_incoming.TryGetValue(f, out var ins)) continue;
                foreach (var e in ins)
                {
                    if (e.Kind != EdgeKind.Import || !seen.Add(e.From)) continue;
                    (depth == 1 ? direct : transitive).Add(e.From);
                    next.Add(e.From);
                }
            }
            frontier = next;
        }

        var covered = new HashSet<string>(known, StringComparer.Ordinal);
        covered.UnionWith(direct);
        covered.UnionWith(transitive);
        var tests = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in covered)
            if (_incoming.TryGetValue(f, out var ins))
                foreach (var e in ins)
                    if (e.Kind == EdgeKind.TestOf && !changed.Contains(e.From) && !seen.Contains(e.From))
                        tests.Add(e.From);

        direct.Sort(StringComparer.Ordinal);
        transitive.Sort(StringComparer.Ordinal);
        return new BlastRadius(changed, direct, transitive, tests.ToList(), skipped);
    }
}
