# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET Framework 4.5.2 console application that generates Microsoft Dynamics 365
for Operations (D365FO) labels for a target language using the **D365 Metadata
Provider API**. For a single writable target model, it reads that model's label
files in a source language (en-US by default) and writes the same labels back for
the target language, so labels become searchable in that language.

It does **not** use a running AOS kernel or the X++ runtime. Everything goes
through the disk metadata provider over `PackagesLocalDirectory`.

## Build

- Open `LabelFileGenerator.sln` in Visual Studio, or build with MSBuild
  (`msbuild LabelFileGenerator.sln /p:Configuration=Release`). There is no
  cross-platform / `dotnet build` support — it targets .NET Framework 4.5.2.
- **Critical dependency:** the project references the D365 metadata assemblies
  (`Microsoft.Dynamics.AX.Metadata.dll`, `Microsoft.Dynamics.AX.Metadata.Core.dll`,
  `Microsoft.Dynamics.AX.Metadata.Storage.dll`) from the AOSService `Bin` folder.
  The reference path is driven by the MSBuild property `PackagesBinDir`, which
  defaults to `K:\AosService\PackagesLocalDirectory\Bin`; override it with
  `/p:PackagesBinDir=...`. Building requires a D365FO box where those assemblies
  exist.
- The only NuGet dependency is CommandLineParser 2.3.0, vendored under
  `packages/` and wired through `packages.config` (classic, not PackageReference).

There are no tests, no linter config, and no CI in this repo. A GitHub-hosted
build would always fail because the proprietary metadata assemblies are not
available off-box, so it is intentionally not wired up.

## Run

```
LabelFileGenerator.exe -l pt-BR -m MyTranslations -v   # target lang + model + verbose
LabelFileGenerator.exe -l pt-BR -m MyTranslations -o   # overwrite existing target labels
LabelFileGenerator.exe -l pt-BR -m MyTranslations -f K:\AosService\
```

CLI options (`Options`): `-l/--lang` (required), `-m/--model` (required),
`-s/--source-lang` (default `en-US`), `-f/--folder` (auto-discovered if omitted),
`-o/--overwrite` (off by default), `-v/--verbose`.

- `--model` must be a model you own. Labels are identified as `@LabelFile:Id`
  *within a model*, and sealed Microsoft models cannot be written, so generation
  is scoped to one writable model. The model determines its own layer (there is no
  layer option).
- With no `-f`, the AOSService folder is auto-discovered by scanning fixed drives
  for `AOSService\PackagesLocalDirectory\ApplicationPlatform\`.
- The app only pauses for Enter when launched with **no** arguments (double-click
  from Explorer); run with arguments and it exits cleanly for scripting.

## Architecture (`LabelFileGenerator/Program.cs`)

- **`MetadataLabelService`** isolates every Metadata Provider call. The
  constructor builds a disk provider
  (`MetadataProviderFactory().CreateDiskProvider(DiskProviderConfiguration)`) over
  `PackagesLocalDirectory` and resolves the target model's `ModelSaveInfo` once.
  - `ListLabelFiles()` — label file ids in the target model.
  - `CopyLanguage(labelFileId, sourceLanguage, targetLanguage, overwrite)` — one
    provider read + at most one write per file. Copies source-language labels into
    the target language; non-destructive unless `overwrite` is set.
- **`Program`** handles CLI parsing, AOSService-folder discovery/validation, and
  orchestration. `GenerateLabelFiles` iterates the model's label files, isolates
  per-file failures, and prints a written/skipped/failed summary.

> **VERIFY markers:** the exact provider member names (e.g. `Labels.Read`,
> `Labels.Update`, `AxLabelFile.GetLabelContents`/`SetLabel`,
> `ModelManifest.Find`) could not be verified against Microsoft Learn and are a
> best-effort reconstruction. They are flagged with `// VERIFY` in
> `MetadataLabelService` and should be confirmed against the real assemblies on a
> D365 dev box.

## Gotchas

- Generation is driven entirely off the labels already on the host; there is no
  network/translation step. The tool copies existing source-language text into the
  target-language slot so it becomes searchable — it does not translate.
- Because the build needs proprietary on-box assemblies, changes here cannot be
  compiled in this environment; treat edits as needing on-box verification.
