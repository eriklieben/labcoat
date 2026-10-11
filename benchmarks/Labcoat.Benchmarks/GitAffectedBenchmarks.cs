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
            switch (i % 20)
            {
                case 0: AppendAdded(ns, patch, path); break;
                case 1: AppendDeleted(ns, patch, path); break;
                case 2: AppendRenamed(ns, patch, path); break;
                case 3: AppendModifiedQuoted(ns, patch, i); break;
                default: AppendModified(ns, patch, path, i); break;
            }
        }
        _nameStatus = ns.ToString();
        _patch = patch.ToString();
    }

    private static void AppendAdded(StringBuilder ns, StringBuilder patch, string path)
    {
        ns.Append("A\0").Append(path).Append('\0');
        patch.Append($"diff --git a/{path} b/{path}\nnew file mode 100644\nindex 0000000..1234567\n--- /dev/null\n+++ b/{path}\n@@ -0,0 +1,40 @@\n");
        for (var l = 0; l < 40; l++) patch.Append("+    line ").Append(l).Append('\n');
    }

    private static void AppendDeleted(StringBuilder ns, StringBuilder patch, string path)
    {
        ns.Append("D\0").Append(path).Append('\0');
        patch.Append($"diff --git a/{path} b/{path}\ndeleted file mode 100644\nindex 1234567..0000000\n--- a/{path}\n+++ /dev/null\n@@ -1,3 +0,0 @@\n-a\n-b\n-c\n");
    }

    private static void AppendRenamed(StringBuilder ns, StringBuilder patch, string path)
    {
        var old = path.Replace("Handler", "OldHandler", StringComparison.Ordinal);
        ns.Append("R087\0").Append(old).Append('\0').Append(path).Append('\0');
        patch.Append($"diff --git a/{old} b/{path}\nsimilarity index 87%\nrename from {old}\nrename to {path}\nindex 1234567..89abcde 100644\n--- a/{old}\n+++ b/{path}\n@@ -12 +12 @@\n-old\n+new\n");
    }

    /// <summary>A modified file with a non-ASCII name, which git quotes (octal escapes) in the patch.</summary>
    private static void AppendModifiedQuoted(StringBuilder ns, StringBuilder patch, int i)
    {
        var shown = $"\"src/Contoso.Billing/Domain/caf\\303\\251{i}.cs\"";
        ns.Append("M\0").Append($"src/Contoso.Billing/Domain/café{i}.cs").Append('\0');
        patch.Append($"diff --git \"a/src/Contoso.Billing/Domain/caf\\303\\251{i}.cs\" {shown.Replace("\"src", "\"b/src", StringComparison.Ordinal)}\nindex 1234567..89abcde 100644\n--- \"a/src/Contoso.Billing/Domain/caf\\303\\251{i}.cs\"\n+++ \"b/src/Contoso.Billing/Domain/caf\\303\\251{i}.cs\"\n");
        AppendHunks(patch, i);
    }

    private static void AppendModified(StringBuilder ns, StringBuilder patch, string path, int i)
    {
        ns.Append("M\0").Append(path).Append('\0');
        patch.Append($"diff --git a/{path} b/{path}\nindex 1234567..89abcde 100644\n--- a/{path}\n+++ b/{path}\n");
        AppendHunks(patch, i);
    }

    /// <summary>1-12 hunks, each removing 0-2 and adding 1-6 lines.</summary>
    private static void AppendHunks(StringBuilder patch, int i)
    {
        var hunks = 1 + i % 12;
        var line = 5;
        for (var h = 0; h < hunks; h++)
        {
            var removed = h % 3;
            var added = 1 + (h + i) % 6;
            line += 7 + h * 3;
            patch.Append($"@@ -{line}{RangeLength(removed)} +{line}{RangeLength(added)} @@ public int Method{h}(int value)\n");
            for (var r = 0; r < removed; r++) patch.Append("-        value = value * 31 + ").Append(r).Append(";\n");
            for (var a = 0; a < added; a++) patch.Append("+        value = value * 37 + ").Append(a).Append(";\n");
        }
    }

    /// <summary>The <c>,count</c> of a hunk range; git leaves it out for a count of 1.</summary>
    private static string RangeLength(int count) => count == 1 ? "" : "," + count;

    [Benchmark]
    public int ParseNameStatus() => GitRepo.ParseNameStatus(_nameStatus).Count;

    [Benchmark]
    public int ParseHunks() => GitRepo.ParseHunks(_patch).Count;
}
