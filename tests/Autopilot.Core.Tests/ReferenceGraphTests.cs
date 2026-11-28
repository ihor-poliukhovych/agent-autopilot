using Autopilot.Core.Impact;

namespace Autopilot.Core.Tests;

public sealed class ReferenceGraphTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "refgraph-" + Guid.NewGuid().ToString("N"));

    public ReferenceGraphTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private void BuildFixture()
    {
        Write("src/sleep.ts", "export const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));");
        Write("src/retry.ts", "import { sleep } from './sleep';\nexport async function retry(f: () => Promise<void>) { await sleep(1); await f(); }");
        Write("src/apiClient.ts", "import { retry } from './retry.js';\nexport const get = () => retry(async () => {});");
        Write("src/jobA.ts", "import { retry } from './retry';\nexport const a = retry;");
        Write("src/jobB.ts", "const { get } = require('./apiClient');\nexport const b = get;");
        Write("src/main.ts", "import './jobB';\nimport fs from 'fs';");
        Write("src/other.ts", "export const other = 1;");
        Write("src/retry.test.ts", "import { retry } from './retry';");
        Write("src/__tests__/jobA.ts", "// covers jobA by naming only\n");
        Write("src/other.spec.ts", "import { other } from './other';");
        Write("src/cycleA.ts", "import { b } from './cycleB';\nexport const a = 1;");
        Write("src/cycleB.ts", "import { a } from './cycleA';\nexport const b = 1;");
        Write("node_modules/x/index.js", "import '../../src/retry';");
    }

    [Fact]
    public void Edges_have_import_and_test_of_kinds()
    {
        BuildFixture();
        var g = ReferenceGraph.Build(_root);
        Assert.Contains(new ReferenceEdge("src/retry.ts", "src/sleep.ts", EdgeKind.Import), g.Edges);
        Assert.Contains(new ReferenceEdge("src/apiClient.ts", "src/retry.ts", EdgeKind.Import), g.Edges);
        Assert.Contains(new ReferenceEdge("src/jobB.ts", "src/apiClient.ts", EdgeKind.Import), g.Edges);
        Assert.Contains(new ReferenceEdge("src/retry.test.ts", "src/retry.ts", EdgeKind.TestOf), g.Edges);
        Assert.DoesNotContain(g.Files, f => f.StartsWith("node_modules"));
    }

    [Fact]
    public void Changing_helper_lists_exactly_callers_and_tests()
    {
        BuildFixture();
        var r = ReferenceGraph.Build(_root).ComputeBlastRadius(["src/retry.ts"]);
        Assert.Equal(["src/apiClient.ts", "src/jobA.ts"], r.DirectDependents);
        Assert.Equal(["src/jobB.ts", "src/main.ts"], r.TransitiveDependents);
        Assert.Equal(["src/__tests__/jobA.ts", "src/retry.test.ts"], r.CoveringTests);
    }

    [Fact]
    public void Tests_found_by_naming_convention_without_import()
    {
        BuildFixture();
        var r = ReferenceGraph.Build(_root).ComputeBlastRadius(["src/jobA.ts"]);
        Assert.Equal(["src/__tests__/jobA.ts"], r.CoveringTests);
    }

    [Fact]
    public void Tests_found_by_import_graph()
    {
        BuildFixture();
        var r = ReferenceGraph.Build(_root).ComputeBlastRadius(["src/sleep.ts"]);
        Assert.Equal(["src/retry.ts"], r.DirectDependents);
        Assert.Contains("src/retry.test.ts", r.CoveringTests);
        Assert.DoesNotContain("src/other.spec.ts", r.CoveringTests);
    }

    [Fact]
    public void File_without_dependents_gives_empty_radius()
    {
        BuildFixture();
        var r = ReferenceGraph.Build(_root).ComputeBlastRadius(["src/main.ts"]);
        Assert.Empty(r.DirectDependents);
        Assert.Empty(r.TransitiveDependents);
        Assert.Empty(r.CoveringTests);
        Assert.Empty(r.Skipped);
    }

    [Fact]
    public void Import_cycles_terminate()
    {
        BuildFixture();
        var r = ReferenceGraph.Build(_root).ComputeBlastRadius(["src/cycleA.ts"]);
        Assert.Equal(["src/cycleB.ts"], r.DirectDependents);
        Assert.Empty(r.TransitiveDependents);
    }

    [Fact]
    public void Unparseable_and_missing_files_are_reported_not_thrown()
    {
        BuildFixture();
        Write("src/bin.ts", "abc\0def");
        var g = ReferenceGraph.Build(_root);
        Assert.Contains(g.Skipped, s => s.Path == "src/bin.ts");
        var r = g.ComputeBlastRadius(["src/missing.ts", "src/bin.ts"]);
        Assert.Contains(r.Skipped, s => s.Path == "src/missing.ts");
        Assert.Contains(r.Skipped, s => s.Path == "src/bin.ts");
        Assert.Empty(r.DirectDependents);
    }

    [Fact]
    public void Missing_root_is_reported()
    {
        var g = ReferenceGraph.Build(Path.Combine(_root, "nope"));
        Assert.Empty(g.Files);
        Assert.Single(g.Skipped);
    }
}
