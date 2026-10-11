using System.Diagnostics;
using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Configurations;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Extensions.TestHost;
using Microsoft.Testing.Platform.Extensions.TestHostControllers;
using Microsoft.Testing.Platform.Logging;
using Microsoft.Testing.Platform.TestHost;
using Microsoft.Testing.Platform.TestHostControllers;

namespace Labcoat.TestingPlatform.Tests;

/// <summary>The parsed command line: option name -> arguments.</summary>
internal sealed class FakeOptions(params (string Name, string[] Args)[] options) : ICommandLineOptions
{
    private readonly Dictionary<string, string[]> _options = options.ToDictionary(o => o.Name, o => o.Args);

    public bool IsOptionSet(string optionName) => _options.ContainsKey(optionName);

    public bool TryGetOptionArgumentList(string optionName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string[]? arguments) => _options.TryGetValue(optionName, out arguments);
}

/// <summary>Records what an extension registers.</summary>
internal sealed class FakeBuilder : ITestApplicationBuilder, ICommandLineManager, ITestHostManager
{
    public List<Func<ICommandLineOptionsProvider>> Providers { get; } = [];
    public List<Func<IServiceProvider, ITestHostApplicationLifetime>> Lifetimes { get; } = [];

    public ICommandLineManager CommandLine => this;
    public ITestHostManager TestHost => this;
    public ITestHostControllersManager TestHostControllers => throw new NotSupportedException();
#pragma warning disable TPEXP // experimental members of the interface; not used by the extension
    public IConfigurationManager Configuration => throw new NotSupportedException();
    public ILoggingManager Logging => throw new NotSupportedException();
#pragma warning restore TPEXP

    public ITestApplicationBuilder RegisterTestFramework(Func<IServiceProvider, ITestFrameworkCapabilities> capabilitiesFactory,
        Func<ITestFrameworkCapabilities, IServiceProvider, ITestFramework> frameworkFactory) => throw new NotSupportedException();
    public Task<ITestApplication> BuildAsync() => throw new NotSupportedException();

    public void AddProvider(Func<ICommandLineOptionsProvider> providerFactory) => Providers.Add(providerFactory);
    public void AddProvider(Func<IServiceProvider, ICommandLineOptionsProvider> providerFactory) => throw new NotSupportedException();

    public void AddTestHostApplicationLifetime(Func<IServiceProvider, ITestHostApplicationLifetime> testHostApplicationLifetime) =>
        Lifetimes.Add(testHostApplicationLifetime);
    public void AddDataConsumer(Func<IServiceProvider, IDataConsumer> dataConsumerFactory) => throw new NotSupportedException();
    public void AddDataConsumer<T>(CompositeExtensionFactory<T> compositeServiceFactory) where T : class, IDataConsumer => throw new NotSupportedException();
    public void AddTestSessionLifetimeHandle(Func<IServiceProvider, ITestSessionLifetimeHandler> testSessionLifetimeHandleFactory) => throw new NotSupportedException();
    public void AddTestSessionLifetimeHandle<T>(CompositeExtensionFactory<T> compositeServiceFactory) where T : class, ITestSessionLifetimeHandler => throw new NotSupportedException();
}

internal sealed class Services(ICommandLineOptions options) : IServiceProvider
{
    public object? GetService(Type serviceType) => serviceType == typeof(ICommandLineOptions) ? options : null;
}

/// <summary>A Labcoat process that exits when told to (or never).</summary>
internal sealed class FakeProcess(int exitCode, bool exitsOnItsOwn = true) : ILabcoatProcess
{
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ProcessStartInfo? StartInfo { get; set; }
    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }
    public bool HasExited => _exited.Task.IsCompleted;
    public int ExitCode => HasExited ? exitCode : throw new InvalidOperationException("still running");

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        exitsOnItsOwn ? Exit() : _exited.Task.WaitAsync(cancellationToken);

    private Task Exit()
    {
        _exited.TrySetResult();
        return Task.CompletedTask;
    }

    public void ExitNow() => _exited.TrySetResult();

    public void Kill() => Killed = true; // a process that ignores the kill (exit code stays unknown)

    public void Dispose() => Disposed = true;
}
