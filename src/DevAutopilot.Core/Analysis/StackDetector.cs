using System.Text.Json;

namespace DevAutopilot.Core.Analysis;

/// <summary>Finds the stacks in a folder and the build and test commands, and writable paths that fit them.</summary>
internal static class StackDetector
{
    private static readonly string[] KindOrder = ["dotnet", "node", "python", "go", "rust", "maven", "gradle"];

    private static readonly string[] SourceFolders = ["src", "lib", "app", "apps", "packages", "components", "pages", "cmd", "internal", "pkg"];

    private static readonly string[] TestFolders = ["tests", "test", "__tests__", "spec", "e2e"];

    /// <summary>The commands of one stack. Null means that stack has no such command.</summary>
    private sealed record StackCommands(Detected? Build, Detected? Test);

    public static IReadOnlyList<DetectedStack> Detect(FolderIndex index)
    {
        var stacks = new List<DetectedStack>();

        var solutions = index.Files.Where(f => f.HasExtension(".sln") || f.HasExtension(".slnx")).ToList();
        if (solutions.Count > 0)
        {
            AddTopmost(stacks, "dotnet", solutions);
        }
        else
        {
            var projects = index.Files.Where(IsDotnetProject).ToList();
            if (projects.Count > 0)
            {
                stacks.Add(new DetectedStack("dotnet", ".", $"{projects.Count} project file(s) such as {projects[0].Path}, no solution file"));
            }
        }

        AddTopmost(stacks, "node", [.. index.Files.Where(f => f.NameIs("package.json"))]);
        AddTopmost(stacks, "python", [.. index.Files.Where(f => f.NameIs("pyproject.toml") || f.NameIs("setup.py") || f.NameIs("setup.cfg") || IsRequirements(f))]);
        AddTopmost(stacks, "go", [.. index.Files.Where(f => f.NameIs("go.mod"))]);
        AddTopmost(stacks, "rust", [.. index.Files.Where(f => f.NameIs("Cargo.toml"))]);
        AddTopmost(stacks, "maven", [.. index.Files.Where(f => f.NameIs("pom.xml"))]);
        AddTopmost(stacks, "gradle", [.. index.Files.Where(f => f.NameIs("build.gradle") || f.NameIs("build.gradle.kts"))]);

        return [.. stacks.OrderBy(s => s.Path == "." ? 0 : 1).ThenBy(s => Array.IndexOf(KindOrder, s.Kind)).ThenBy(s => s.Path, StringComparer.Ordinal)];
    }

    /// <summary>Build and test commands: those of the primary stack, with the other stacks' commands as alternatives.</summary>
    public static (Detected? Build, Detected? Test) Commands(FolderIndex index, IReadOnlyList<DetectedStack> stacks, List<string> warnings)
    {
        if (stacks.Count == 0)
        {
            return (null, null);
        }

        var all = stacks.Select(s => (Stack: s, Commands: For(index, s, warnings))).ToList();
        var primary = all[0];
        var mixed = all.Count > 1;
        Detected? Finish(Detected? chosen, Func<StackCommands, Detected?> pick)
        {
            if (chosen is null)
            {
                return null;
            }

            var others = all.Skip(1).Select(a => pick(a.Commands)).Where(d => d is not null).Select(d => d!.Value).Distinct().ToList();
            var confidence = mixed && chosen.Confidence == Confidence.High ? Confidence.Medium : chosen.Confidence;
            var evidence = mixed ? $"{chosen.Evidence}. This folder has more than one stack, so this is the {primary.Stack.Kind} one in {Display(primary.Stack.Path)}" : chosen.Evidence;
            return chosen with { Evidence = evidence, Confidence = confidence, Alternatives = others };
        }

        return (Finish(primary.Commands.Build, c => c.Build), Finish(primary.Commands.Test, c => c.Test));
    }

    /// <summary>Source and test folders for every stack.</summary>
    public static IReadOnlyList<Detected> WritablePaths(FolderIndex index, IReadOnlyList<DetectedStack> stacks)
    {
        var found = new Dictionary<string, Detected>(StringComparer.OrdinalIgnoreCase);
        void Add(string pattern, string evidence, Confidence confidence) => found.TryAdd(pattern, new Detected(pattern, evidence, confidence));

        foreach (var stack in stacks)
        {
            var d = stack.Path;
            foreach (var folder in SourceFolders.Concat(TestFolders))
            {
                if (index.DirectoryExists(Rel(d, folder)) && AppliesTo(stack.Kind, folder))
                {
                    var kind = TestFolders.Contains(folder) ? "test" : "source";
                    Add($"{Rel(d, folder)}/**", $"Conventional {kind} folder {Rel(d, folder)}/ in the {stack.Kind} stack", Confidence.High);
                }
            }

            if (stack.Kind == "dotnet")
            {
                if (!found.Keys.Any(k => k.StartsWith(Rel(d, "src") + "/", StringComparison.OrdinalIgnoreCase) || k.StartsWith(Rel(d, "tests") + "/", StringComparison.OrdinalIgnoreCase) || k.StartsWith(Rel(d, "test") + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    AddProjectFolders(index, d, Add);
                }
            }
            else if (stack.Kind == "python")
            {
                foreach (var init in index.Files.Where(f => f.NameIs("__init__.py") && f.Parent != "." && ParentFolder(f.Parent) == d))
                {
                    if (!SourceFolders.Concat(TestFolders).Contains(init.Parent[(init.Parent.LastIndexOf('/') + 1)..]))
                    {
                        Add($"{init.Parent}/**", $"Python package folder ({init.Path} exists)", Confidence.High);
                    }
                }
            }
        }

        return [.. found.Values];
    }

    private static bool AppliesTo(string kind, string folder) => kind switch
    {
        // "packages", "components" and "pages" only mean something to a node project; "cmd", "internal" and "pkg" to Go.
        "node" => true,
        "go" => folder is "cmd" or "internal" or "pkg" or "tests" or "test",
        "dotnet" or "rust" or "maven" or "gradle" => folder is "src" or "tests" or "test",
        _ => folder is "src" or "lib" or "app" or "tests" or "test" or "spec" or "e2e" or "__tests__",
    };

    /// <summary>When a .NET repository has no src/ or tests/ folder, the folders that hold a project file.</summary>
    private static void AddProjectFolders(FolderIndex index, string stackFolder, Action<string, string, Confidence> add)
    {
        var scope = stackFolder == "." ? string.Empty : stackFolder + "/";
        var projects = index.Files.Where(f => IsDotnetProject(f) && f.Path.StartsWith(scope, StringComparison.Ordinal) && f.Parent != stackFolder).Select(f => f.Parent).Distinct().ToList();
        foreach (var folder in projects.Where(p => !projects.Any(o => o != p && p.StartsWith(o + "/", StringComparison.Ordinal))))
        {
            var top = folder[scope.Length..].Split('/')[0];
            if (top is "src" or "tests" or "test")
            {
                continue;
            }

            add($"{folder}/**", $"Folder with a project file ({folder}/)", Confidence.Medium);
        }
    }

    private static StackCommands For(FolderIndex index, DetectedStack stack, List<string> warnings) => stack.Kind switch
    {
        "dotnet" => Dotnet(index, stack.Path),
        "node" => Node(index, stack.Path),
        "python" => Python(index, stack.Path, warnings),
        "go" => Simple(stack, "go.mod", d => d == "." ? "go build ./..." : $"go -C {d} build ./...", d => d == "." ? "go test ./..." : $"go -C {d} test ./..."),
        "rust" => Simple(stack, "Cargo.toml", d => d == "." ? "cargo build" : $"cargo build --manifest-path {d}/Cargo.toml", d => d == "." ? "cargo test" : $"cargo test --manifest-path {d}/Cargo.toml"),
        "maven" => Simple(stack, "pom.xml", d => d == "." ? "mvn -B -DskipTests package" : $"mvn -B -f {d}/pom.xml -DskipTests package", d => d == "." ? "mvn -B test" : $"mvn -B -f {d}/pom.xml test", Confidence.Medium),
        "gradle" => Simple(stack, "the Gradle build file", d => d == "." ? "gradle build -x test" : $"gradle -p {d} build -x test", d => d == "." ? "gradle test" : $"gradle -p {d} test", Confidence.Medium),
        _ => new StackCommands(null, null),
    };

    private static StackCommands Simple(DetectedStack stack, string file, Func<string, string> build, Func<string, string> test, Confidence confidence = Confidence.High)
    {
        var evidence = $"{file} in {Display(stack.Path)} (standard {stack.Kind} commands)";
        return new StackCommands(new Detected(build(stack.Path), evidence, confidence), new Detected(test(stack.Path), evidence, confidence));
    }

    private static StackCommands Dotnet(FolderIndex index, string d)
    {
        var solutions = index.FilesIn(d, f => f.HasExtension(".sln") || f.HasExtension(".slnx")).OrderBy(f => f.HasExtension(".slnx") ? 0 : 1).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        if (solutions.Count > 0)
        {
            var folderName = d == "." ? System.IO.Path.GetFileName(index.Root.TrimEnd('\\', '/')) : d[(d.LastIndexOf('/') + 1)..];
            var chosen = solutions.FirstOrDefault(s => System.IO.Path.GetFileNameWithoutExtension(s.Name).Equals(folderName, StringComparison.OrdinalIgnoreCase)) ?? solutions[0];
            var confidence = solutions.Count == 1 ? Confidence.High : Confidence.Medium;
            var evidence = solutions.Count == 1
                ? $"Solution file {chosen.Path}"
                : $"Solution file {chosen.Path}, picked from {solutions.Count} solution files ({string.Join(", ", solutions.Select(s => s.Name))})";
            return new StackCommands(new Detected($"dotnet build \"{chosen.Path}\"", evidence, confidence), new Detected($"dotnet test \"{chosen.Path}\"", evidence, confidence));
        }

        var scope = d == "." ? string.Empty : d + "/";
        var projects = index.Files.Where(f => IsDotnetProject(f) && f.Path.StartsWith(scope, StringComparison.Ordinal)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var tests = projects.Where(IsTestProject).ToList();
        var sources = projects.Except(tests).ToList();
        var build = sources.Count > 0 ? new Detected($"dotnet build \"{sources[0].Path}\"", $"Project file {sources[0].Path} (there is no solution file)", sources.Count == 1 ? Confidence.Medium : Confidence.Low) : null;
        var test = tests.Count > 0 ? new Detected($"dotnet test \"{tests[0].Path}\"", $"Test project {tests[0].Path} (there is no solution file)", tests.Count == 1 ? Confidence.Medium : Confidence.Low) : null;
        return new StackCommands(build, test);
    }

    private static StackCommands Node(FolderIndex index, string d)
    {
        var manifest = Rel(d, "package.json");
        string? buildScript = null;
        string? testScript = null;
        try
        {
            using var doc = JsonDocument.Parse(index.Read(manifest), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
            {
                buildScript = scripts.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                testScript = scripts.TryGetProperty("test", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            }
        }
        catch (JsonException)
        {
            return new StackCommands(null, null);
        }

        var manager = index.FileExists(Rel(d, "pnpm-lock.yaml")) || index.FileExists("pnpm-lock.yaml") ? "pnpm"
            : index.FileExists(Rel(d, "yarn.lock")) || index.FileExists("yarn.lock") ? "yarn"
            : "npm";
        var hasLock = manager != "npm" || index.FileExists(Rel(d, "package-lock.json")) || index.FileExists("package-lock.json");
        var (install, run) = manager switch
        {
            "pnpm" => (d == "." ? "pnpm install --frozen-lockfile" : $"pnpm --dir {d} install --frozen-lockfile", (Func<string, string>)(s => d == "." ? $"pnpm run {s}" : $"pnpm --dir {d} run {s}")),
            "yarn" => (d == "." ? "yarn install --frozen-lockfile" : $"yarn --cwd {d} install --frozen-lockfile", s => d == "." ? $"yarn {s}" : $"yarn --cwd {d} {s}"),
            _ => (d == "." ? (hasLock ? "npm ci" : "npm install") : $"npm {(hasLock ? "ci" : "install")} --prefix {d}", s => d == "." ? $"npm run {s}" : $"npm run {s} --prefix {d}"),
        };

        var why = $"{manifest} defines the \"{{0}}\" script; {manager} installs dependencies first because a clean checkout has none";
        Detected? build = string.IsNullOrWhiteSpace(buildScript) ? null : new Detected($"{install} && {run("build")}", string.Format(System.Globalization.CultureInfo.InvariantCulture, why, "build"), Confidence.High);
        var hasTest = !string.IsNullOrWhiteSpace(testScript) && !testScript.Contains("no test specified", StringComparison.OrdinalIgnoreCase);
        Detected? test = hasTest ? new Detected($"{install} && {(d == "." ? manager + " test" : manager == "npm" ? $"npm test --prefix {d}" : run("test"))}", string.Format(System.Globalization.CultureInfo.InvariantCulture, why, "test"), Confidence.High) : null;
        return new StackCommands(build, test);
    }

    private static StackCommands Python(FolderIndex index, string d, List<string> warnings)
    {
        var scope = d == "." ? string.Empty : d + "/";
        var pytest = false;
        var evidence = string.Empty;
        foreach (var name in new[] { "pyproject.toml", "setup.cfg", "tox.ini", "pytest.ini", "conftest.py" })
        {
            var path = Rel(d, name);
            if (!index.FileExists(path))
            {
                continue;
            }

            if (name is "pytest.ini" or "conftest.py" || index.Read(path).Contains("pytest", StringComparison.OrdinalIgnoreCase))
            {
                pytest = true;
                evidence = $"{path} mentions pytest";
                break;
            }
        }

        if (!pytest)
        {
            var requirement = index.Files.FirstOrDefault(f => f.Path.StartsWith(scope, StringComparison.Ordinal) && IsRequirements(f) && index.Read(f.Path).Contains("pytest", StringComparison.OrdinalIgnoreCase));
            if (requirement is not null)
            {
                pytest = true;
                evidence = $"{requirement.Path} lists pytest";
            }
        }

        var hasTests = TestFolders.Any(t => index.DirectoryExists(Rel(d, t)));
        var target = d == "." ? string.Empty : " " + d;
        Detected? test = pytest
            ? new Detected($"python -m pytest{target}", evidence, Confidence.High)
            : hasTests ? new Detected($"python -m unittest discover{(d == "." ? string.Empty : " -s " + d)}", $"A test folder exists in {Display(d)} but nothing says which runner; unittest is the standard one", Confidence.Low) : null;
        var source = index.DirectoryExists(Rel(d, "src")) ? Rel(d, "src") : d;
        var build = new Detected($"python -m compileall -q {source}", "Python has no build step; this only checks that the code compiles. Replace it if the project has a real one", Confidence.Low);
        warnings.Add("Python: the build and test commands run on a clean checkout, so the dependencies must already be installed for the interpreter that runs them (a virtual environment on PATH, or an install step in the command).");
        return new StackCommands(build, test);
    }

    private static void AddTopmost(List<DetectedStack> stacks, string kind, List<IndexEntry> files)
    {
        var folders = files.GroupBy(f => f.Parent).OrderBy(g => g.Key == "." ? 0 : g.Key.Count(c => c == '/') + 1).ThenBy(g => g.Key, StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var group in folders)
        {
            if (kept.Any(k => k == "." || group.Key.StartsWith(k + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            kept.Add(group.Key);
            stacks.Add(new DetectedStack(kind, group.Key, string.Join(", ", group.Select(f => f.Name).Distinct().Take(4))));
        }
    }

    private static bool IsDotnetProject(IndexEntry f) => f.HasExtension(".csproj") || f.HasExtension(".fsproj");

    private static bool IsRequirements(IndexEntry f) => f.Name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && f.HasExtension(".txt");

    private static bool IsTestProject(IndexEntry f)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(f.Name);
        return name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
            || f.Path.Split('/').Any(s => s.Equals("tests", StringComparison.OrdinalIgnoreCase) || s.Equals("test", StringComparison.OrdinalIgnoreCase));
    }

    private static string ParentFolder(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : ".";

    private static string Rel(string folder, string name) => folder == "." ? name : folder + "/" + name;

    private static string Display(string folder) => folder == "." ? "the top folder" : folder + "/";
}
