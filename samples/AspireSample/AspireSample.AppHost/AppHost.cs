var builder = DistributedApplication.CreateBuilder(args);

// The orders API keeps its data in memory, so the sample runs without Docker/Podman.
var api = builder.AddProject<Projects.AspireSample_Api>("api")
    .WithHttpHealthCheck("/health");

// Labcoat (Labcoat.Aspire.Hosting): a web UI for Microsoft.Testing.Platform tests, with dashboard commands to run all /
// failed tests and toggle watch / coverage. Two instances for the demo:
//   tests-passing: the all-green suites (AspireSample.PassingTests on MSTest, AspireSample.TUnitTests on TUnit,
//                  AspireSample.NUnitTests on NUnit, AspireSample.XUnitTests on xUnit v3) - Run all ends in confetti.
//   tests-failing: the full suite incl. an intentionally failing test (AspireSample.Tests).
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
       .WithUiSettings(ui => ui.OpenOutputOnError = true);

builder.Build().Run();
