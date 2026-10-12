var builder = DistributedApplication.CreateBuilder(args);

// The orders API keeps its data in memory, so the sample runs without Docker/Podman.
var api = builder.AddProject<Projects.AspireSample_Api>("api")
    .WithHttpHealthCheck("/health");

// Labcoat (Labcoat.Aspire.Hosting): a web UI for Microsoft.Testing.Platform tests, with dashboard commands to run all /
// failed tests and toggle watch / coverage. With Labcoat.Vitest referenced it runs Vitest projects too; WithRunners picks
// the runners per instance (default: dotnet plus every runner package referenced). The instances of the demo:
//   tests-passing:  the all-green suites (AspireSample.PassingTests on MSTest, AspireSample.TUnitTests on TUnit,
//                   AspireSample.NUnitTests on NUnit, AspireSample.XUnitTests on xUnit v3) - Run all ends in confetti.
//   tests-failing:  the full MSTest suite incl. an intentionally failing test (AspireSample.Tests); .NET only.
//   tests-vitest:   the Vitest tests of the front end (AspireSample.Web; run `npm install` there once); Vitest only.
//   tests-combined: both of the above in one Labcoat.
//
// UI settings set here are defaults: a choice made in the browser (Labcoat's Settings page) wins, unless the setting
// is locked; locked settings show "Set by the AppHost (Program.cs)" there. They can also come from appsettings.json
// (section "Labcoat") or a .labcoat.json in the repository (see docs/configuration.md).
builder.AddLabcoat("tests-passing", labcoat =>
{
    labcoat.TestProjects.Add("../AspireSample.PassingTests");
    labcoat.TestProjects.Add("../AspireSample.TUnitTests");
    labcoat.TestProjects.Add("../AspireSample.NUnitTests");
    labcoat.TestProjects.Add("../AspireSample.XUnitTests");
    labcoat.Ui.Confetti = true;               // the all-green suite celebrates Run all
});

builder.AddLabcoat("tests-failing")
       .WithTestProject("../AspireSample.Tests")
       .WithRunners("dotnet")
       .WithUiSettings(ui => ui.OpenOutputOnError = true);

builder.AddLabcoat("tests-vitest")
       .WithTestProject("../AspireSample.Web")
       .WithRunners("vitest");

builder.AddLabcoat("tests-combined")
       .WithTestProject("../AspireSample.Tests")
       .WithTestProject("../AspireSample.Web")
       .WithRunners("dotnet", "vitest");

builder.Build().Run();
