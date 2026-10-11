// The Labcoat host: command line, workspace discovery, settings, port, banner and the web app all live in
// LabcoatApp.cs, so tests can build the same app in-process (LabcoatApp.Build) and run the whole program (RunAsync).
return await Labcoat.LabcoatApp.RunAsync(args);
