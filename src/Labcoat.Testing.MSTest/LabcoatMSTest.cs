using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Labcoat.Testing;

/// <summary>
/// MSTest adapter of <see cref="LabcoatTracing"/>: per-test traces from MSTest's test hooks.
/// </summary>
/// <remarks>
/// Call <see cref="StartTest"/> / <see cref="EndTest"/> from <c>[GlobalTestInitialize]</c> / <c>[GlobalTestCleanup]</c>
/// to cover the whole assembly, or derive test classes from <see cref="LabcoatTracedTest"/>:
/// <code>
/// [TestClass]
/// public static class LabcoatHooks
/// {
///     [GlobalTestInitialize] public static void Start(TestContext c) => LabcoatMSTest.StartTest(c);
///     [GlobalTestCleanup]    public static void End(TestContext c)   => LabcoatMSTest.EndTest(c);
/// }
/// </code>
/// No-ops unless <see cref="LabcoatTracing.IsEnabled"/> (tests run under Labcoat with test tracing on).
/// </remarks>
public static class LabcoatMSTest
{
    /// <summary>The <c>test.framework</c> tag value.</summary>
    public const string Framework = "mstest";

    // The span of the test running in this execution context: MSTest runs [GlobalTestInitialize], the test and
    // [GlobalTestCleanup] in one flowing ExecutionContext.
    private static readonly AsyncLocal<LabcoatTestSpan?> s_current = new();

    // Categories, traits and default trace filter per test method (class and method name): MSTest's TestContext does
    // not expose them, so they are read from the attributes once.
    private static readonly ConcurrentDictionary<(string Class, string Method), TestAttributes> s_attributes = new();

    /// <summary>
    /// Starts the span for the current test and records its trace id in the test output (the activity flows into the
    /// test method). Call from <c>[TestInitialize]</c> or <c>[GlobalTestInitialize]</c>, paired with <see cref="EndTest"/>.
    /// Calling it again for a test that already has a span (e.g. both hooks and <see cref="LabcoatTracedTest"/>) does nothing.
    /// A test that does not match the trace filter (Labcoat's, else a <see cref="LabcoatTracingAttribute.Filter"/> on the
    /// class or assembly) gets no span.
    /// </summary>
    public static void StartTest(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!LabcoatTracing.IsEnabled) return;
        var test = ToLabcoatTest(context);
        if (s_current.Value is { IsEnded: false } running && running.Test == test) return;
        s_current.Value = LabcoatTracing.ShouldTrace(test, AttributesOf(context).Filter)
            ? LabcoatTracing.BeginTest(test, Console.WriteLine)
            : null;
    }

    /// <summary>Ends the span started by <see cref="StartTest"/>, tagging it with the test outcome.</summary>
    public static void EndTest(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var span = s_current.Value;
        if (span is null) return;
        s_current.Value = null;
        var outcome = context.CurrentTestOutcome;
        LabcoatTracing.EndTest(span, ToOutcome(outcome), outcome.ToString());
    }

    /// <summary>
    /// The test as Labcoat.Testing describes it (MSTest does not expose the test node uid, so no <see cref="LabcoatTest.Id"/>).
    /// <see cref="LabcoatTest.Categories"/> are the <c>[TestCategory]</c>s of the method, its class (and base classes) and
    /// the assembly; <see cref="LabcoatTest.Traits"/> the <c>[TestProperty]</c>s of the method and class, <c>[Owner]</c>,
    /// <c>[Priority]</c> and <c>[Description]</c>: the traits MSTest reports to Labcoat.
    /// </summary>
    public static LabcoatTest ToLabcoatTest(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var attributes = AttributesOf(context);
        return new LabcoatTest(context.TestDisplayName ?? context.TestName ?? "test")
        {
            ClassName = context.FullyQualifiedTestClassName,
            MethodName = context.TestName,
            Framework = Framework,
            Categories = attributes.Categories,
            Traits = attributes.Traits,
        };
    }

    /// <summary>The categories, traits and default trace filter of a test, by its class and method name.</summary>
    private static TestAttributes AttributesOf(TestContext context) =>
        context.FullyQualifiedTestClassName is { Length: > 0 } className && context.TestName is { Length: > 0 } methodName
            ? s_attributes.GetOrAdd((className, methodName), key => TestAttributes.Read(FindType(key.Class), key.Method))
            : TestAttributes.None;

    /// <summary>The test class: in the test application's assembly, else any loaded assembly; null when not found.</summary>
    internal static Type? FindType(string className)
    {
        var entry = Assembly.GetEntryAssembly();
        var assemblies = entry is null ? AppDomain.CurrentDomain.GetAssemblies() : [entry, .. AppDomain.CurrentDomain.GetAssemblies()];
        foreach (var assembly in assemblies)
        {
            try
            {
                if (assembly.GetType(className, throwOnError: false) is { } type) return type;
            }
            catch (Exception ex) when (ex is ArgumentException or FileLoadException or FileNotFoundException or BadImageFormatException)
            {
                // Not a valid type name for this assembly, or one of its dependencies can't be loaded: try the next one.
            }
        }
        return null;
    }

    /// <summary>What the attributes of a test method say: its categories, traits and default trace filter.</summary>
    internal sealed record TestAttributes(IReadOnlyList<string> Categories, IReadOnlyList<KeyValuePair<string, string>> Traits, string? Filter)
    {
        public static readonly TestAttributes None = new([], [], null);

        /// <summary>Reads the attributes of the methods named <paramref name="methodName"/> of <paramref name="type"/>.</summary>
        public static TestAttributes Read(Type? type, string methodName)
        {
            if (type is null) return None;
            // Public instance methods only: MSTest doesn't run a static or non-public [TestMethod] (UTA007).
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == methodName).ToList();
            var categories = methods.SelectMany(m => m.GetCustomAttributes<TestCategoryBaseAttribute>(inherit: true))
                .Concat(type.GetCustomAttributes<TestCategoryBaseAttribute>(inherit: true))
                .Concat(type.Assembly.GetCustomAttributes<TestCategoryBaseAttribute>())
                .SelectMany(c => c.TestCategories)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            // [Owner], [Priority] and [Description] are TestPropertyAttributes too (Owner=..., Priority=..., Description=...).
            var traits = type.GetCustomAttributes<TestPropertyAttribute>(inherit: true)
                .Concat(methods.SelectMany(m => m.GetCustomAttributes<TestPropertyAttribute>(inherit: true)))
                .Select(p => new KeyValuePair<string, string>(p.Name, p.Value))
                .ToList();
            var filter = type.GetCustomAttribute<LabcoatTracingAttribute>(inherit: true)?.Filter
                ?? type.Assembly.GetCustomAttribute<LabcoatTracingAttribute>()?.Filter;
            return new TestAttributes(categories, traits, filter);
        }
    }

    /// <summary>Maps MSTest's outcome to the span outcome.</summary>
    public static LabcoatTestOutcome ToOutcome(UnitTestOutcome outcome) => outcome switch
    {
        UnitTestOutcome.Passed => LabcoatTestOutcome.Passed,
        UnitTestOutcome.Failed => LabcoatTestOutcome.Failed,
        UnitTestOutcome.Error => LabcoatTestOutcome.Error,
        UnitTestOutcome.Timeout => LabcoatTestOutcome.TimedOut,
        UnitTestOutcome.Aborted => LabcoatTestOutcome.Cancelled,
        UnitTestOutcome.Inconclusive => LabcoatTestOutcome.Inconclusive,
        UnitTestOutcome.NotRunnable or UnitTestOutcome.Ignored => LabcoatTestOutcome.Skipped,
        _ => LabcoatTestOutcome.Unknown,
    };
}

/// <summary>
/// The default trace filter of MSTest tests: <c>[assembly: LabcoatTracing(Filter = "cat:Integration")]</c>, or on a test
/// class (it wins over the assembly's). Used when Labcoat passes no filter of its own (<c>WithTestTracing(filter: ...)</c>
/// in the AppHost wins). The hooks (<see cref="LabcoatMSTest"/>, <see cref="LabcoatTracedTest"/>) still do the tracing;
/// this only narrows it. The package adds it to the assembly from the MSBuild property
/// <c>&lt;LabcoatTracingFilter&gt;cat:Integration&lt;/LabcoatTracingFilter&gt;</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class LabcoatTracingAttribute : Attribute
{
    /// <summary>
    /// Only tests matching this filter are traced, in the Labcoat UI's search syntax: <c>cat:Integration</c>,
    /// <c>trait:Kind=Slow</c>, name text; every term must match. Null: every test.
    /// </summary>
    public string? Filter { get; set; }
}

/// <summary>
/// Optional base class: gives every test in the derived class its own trace (see <see cref="LabcoatMSTest"/>).
/// Alternatively call <see cref="LabcoatMSTest.StartTest"/> / <see cref="LabcoatMSTest.EndTest"/> from
/// <c>[GlobalTestInitialize]</c> / <c>[GlobalTestCleanup]</c> to cover the whole assembly.
/// </summary>
public abstract class LabcoatTracedTest
{
    /// <summary>The MSTest context of the running test.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Starts the test's span.</summary>
    [TestInitialize]
    public void LabcoatStartTrace() => LabcoatMSTest.StartTest(TestContext);

    /// <summary>Ends the test's span.</summary>
    [TestCleanup]
    public void LabcoatEndTrace() => LabcoatMSTest.EndTest(TestContext);
}
