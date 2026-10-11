# Samples

Small test projects to try Labcoat on. They contain deliberate failures and skipped tests, so there is something to
look at.

| Project | Framework | Tests | Failed | Skipped |
| --- | --- | --- | --- | --- |
| [SampleTests](SampleTests) | MSTest 4 | 17 | 7 | 1 |
| [NUnitSample](NUnitSample) | NUnit 5 | 6 | 1 | 1 |
| [XUnitV3Sample](XUnitV3Sample) | xUnit v3 | 6 | 1 | 1 |
| [TUnitSample](TUnitSample) | TUnit 1 | 6 | 1 | 1 |

All four test [SampleTests.Lib](SampleTests.Lib), a small product library, so the coverage view has real code to show.
They are in [Samples.slnx](Samples.slnx):

```bash
labcoat samples/Samples.slnx             # or: dnx Labcoat samples/Samples.slnx
dotnet run --project samples/NUnitSample # run one sample without Labcoat
```

SampleTests/Snapshots has three [Verify](https://github.com/VerifyTests/Verify) snapshot tests: one that passes,
one whose committed `.verified.txt` no longer matches and one without a snapshot yet. Labcoat shows the snapshot diff
with **Accept** / **Reject** (see [docs/features.md](../docs/features.md#snapshot-testing)). Accepting changes files in
your working tree; `git checkout samples` restores them.

Each project file is standalone (package versions included), so you can copy it as a starting point. CI runs this
solution through the packaged tool and checks the counts above.

[AspireSample](AspireSample) is an Aspire app with integration tests and two Labcoat resources in its dashboard; see
its README.
