# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET Framework 4.5.2 console application that generates Microsoft Dynamics 365
for Operations (D365FO) label files for a target language. It reads the labels
already installed on a D365FO box (English source) and writes the equivalent
`.label.txt` + `.xml` label files for the requested language across a fixed set
of core models, so labels become searchable in that language.

## Build

- Open `LabelFileGenerator.sln` in Visual Studio, or build with MSBuild
  (`msbuild LabelFileGenerator.sln /p:Configuration=Release`). There is no
  cross-platform / `dotnet build` support — it targets .NET Framework 4.5.2.
- **Critical dependency:** the project references
  `Microsoft.Dynamics.AX.Xpp.AxShared.dll` via a hardcoded HintPath
  (`K:\AosService\PackagesLocalDirectory\Bin\...` in `LabelFileGenerator.csproj`).
  Building requires a D365FO environment with that DLL at that path. The
  `LabelHelper` API used throughout `Program.cs`
  (`GetAllLabels`, `GetInstalledLanguages`) comes from this assembly.
- The only NuGet dependency is CommandLineParser 2.3.0, vendored under
  `packages/` and wired through `packages.config` (classic, not PackageReference).

There are no tests, no linter config, and no CI in this repo.

## Run

```
LabelFileGenerator.exe -l pt-BR -v       # language + verbose
LabelFileGenerator.exe -f K:\AosService\ # explicit AOSService folder
```

- With no `-l`, the app prompts for a language interactively and validates it
  against the installed languages.
- The target language can also be encoded in the executable name, e.g.
  `LabelFileGenerator_pt-BR.exe` — double-clicking generates that language
  directly (`GetLanguageFromUserInput` parses the process name).
- With no `-f`, it auto-discovers the AOSService folder by scanning fixed drives
  for `AOSService\PackagesLocalDirectory\ApplicationSuite\Foundation\...`.

## Architecture (all in `LabelFileGenerator/Program.cs`)

The pipeline (`Program.Run` -> `GenerateLabelFiles`):

1. `GetLabelFiles()` walks the hardcoded `ModelsToProcess` allowlist (~34 core
   models). For each model it finds the English source files
   (`*_en-US.xml`) and builds a `LabelFile` per label file id, loading the
   target-language strings via `LabelHelper.GetAllLabels`.
2. **Segmentation:** if a `LabelFile` has more than `LabelFileSegment.SegmentSize`
   (900) labels, it is split into `LabelFileSegment` chunks (900 each). 900 was
   the empirically best per-thread size.
3. **Parallel write:** each `LabelFile` / `LabelFileSegment` becomes a `Task`;
   all tasks start and `Task.WaitAll` blocks until done. Each writes an `.xml`
   descriptor and a `.label.txt` body.
4. **Merge:** segments do not write straight to disk — `LabelFileSegment`
   overrides `CreateFile` to capture content into a `LabelFileInfo`. After the
   tasks finish, `WriteSegmentedFiles` -> `LabelFileSegment.Merge` concatenates
   the segment `.txt` bodies back into a single file per label file id and
   writes it.

Key types: `LabelFile` (base, direct write), `LabelFileSegment : LabelFile`
(captures into `LabelFileInfo` instead of writing, then merged), `LabelFileInfo`
(holds merged xml/txt paths + content), `Options` (CommandLineParser args),
`Program` (discovery, validation, orchestration).

Output layout per model: the `.xml` descriptor goes in the model's
`AxLabelFile\` folder; the `.label.txt` goes in
`AxLabelFile\LabelResources\<language>\`.

## Gotchas

- `ModelsToProcess` is a hardcoded list and drifts as Microsoft adds/renames
  modules — update it there if a model is missing from output.
- Generation is driven entirely off the installed labels on the host; there is
  no network/translation step. The tool surfaces existing localized labels.
