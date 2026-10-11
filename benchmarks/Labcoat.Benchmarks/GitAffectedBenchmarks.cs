using System.Text;
using BenchmarkDotNet.Attributes;

namespace Labcoat.Benchmarks;

/// <summary>
/// "Run affected" diff parsing (GitAffected.cs): <c>git diff --name-status -z</c> (<see cref="GitRepo.ParseNameStatus"/>)
/// and the changed line ranges of a <c>git diff -U0</c> patch (<see cref="GitRepo.ParseHunks"/>). A branch with
/// <see cref="Files"/> changed files: mostly modified (with 1-12 hunks of 1-6 lines each), some added, deleted, renamed
/// and a quoted path. The git process itself is not part of it.
/// </summary>
[MemoryDiagnoser]
public class GitAffectedBenchmarks
{
    [Params(50, 1_000)]
    public int Files { get; set; }

    private string _nameStatus = "";
    private string _patch = "";

    [GlobalSetup]
    public void Setup()
    {
        var ns = new StringBuilder();
        var patch = new StringBuilder();
        for (var i = 0; i < Files; i++)
        {
            var path = $"src/Contoso.Billing/Domain/Handlers/Order{(i % 7 == 6 ? " copy" : "")}Handler{i}.cs";
            var kind = i % 20;
            if (kind == 0)
            {
                ns.Append("A\0").Append(path).Append('\0');
                patch.Append($"diff --git a/{path} b/{path}\nnew file mode 100644\nindex 0000000..1234567\n--- /dev/null\n+++ b/{path}\n@@ -0,0 +1,40 @@\n");
                for (var l = 0; l < 40; l++) patch.Append("+    line ").Append(l).Append('\n');
                continue;
            }
            if (kind == 1)
            {
                ns.Append("D\0").Append(path).Append('\0');
                patch.Append($"diff --git a/{path} b/{path}\ndeleted file mode 100644\nindex 1234567..0000000\n--- a/{path}\n+++ /dev/null\n@@ -1,3 +0,0 @@\n-a\n-b\n-c\n");
                continue;
            }
            if (kind == 2)
            {
                var old = path.Replace("Handler", "OldHandler", StringComparison.Ordinal);
                ns.Append("R087\0").Append(old).Append('\0').Append(path).Append('\0');
                patch.Append($"diff --git a/{old} b/{path}\nsimilarity index 87%\nrename from {old}\nrename to {path}\nindex 1234567..89abcde 100644\n--- a/{old}\n+++ b/{path}\n@@ -12 +12 @@\n-old\n+new\n");
                continue;
            }
            var quoted = kind == 3;
            var shown = quoted ? $"\"src/Contoso.Billing/Domain/caf\\303\\251{i}.cs\"" : path;
            ns.Append("M\0").Append(quoted ? $"src/Contoso.Billing/Domain/café{i}.cs" : path).Append('\0');
            patch.Append(quoted
                ? $"diff --git \"a/src/Contoso.Billing/Domain/caf\\303\\251{i}.cs\" {shown.Replace("\"src", "\"b/src", StringComparison.Ordinal)}\nindex 1234567..89abcde 100644\n--- \"a/src/Contoso.Billing/Domain/caf\\303\\251{i}.cs\"\n+++ \"b/src/Contoso.Billing/Domain/caf\\303\\251{i}.cs\"\n"
                : $"diff --git a/{path} b/{path}\nindex 1234567..89abcde 100644\n--- a/{path}\n+++ b/{path}\n");
            var hunks = 1 + i % 12;
            var line = 5;
            for (var h = 0; h < hunks; h++)
            {
                var removed = h % 3;
                var added = 1 + (h + i) % 6;
                line += 7 + h * 3;
                patch.Append($"@@ -{line}{(removed == 1 ? "" : "," + removed)} +{line}{(added == 1 ? "" : "," + added)} @@ public int Method{h}(int value)\n");
                for (var r = 0; r < removed; r++) patch.Append("-        value = value * 31 + ").Append(r).Append(";\n");
                for (var a = 0; a < added; a++) patch.Append("+        value = value * 37 + ").Append(a).Append(";\n");
            }
        }
        _nameStatus = ns.ToString();
        _patch = patch.ToString();
    }

    [Benchmark]
    public int ParseNameStatus() => GitRepo.ParseNameStatus(_nameStatus).Count;

    [Benchmark]
    public int ParseHunks() => GitRepo.ParseHunks(_patch).Count;
}
