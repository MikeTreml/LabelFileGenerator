# Label File Generator for MS Dynamics 365 for Operations

Generate label files for a Microsoft Dynamics 365 for Operations model in a target
language, using the D365 **Metadata Provider API**. The tool reads the labels of a
model's label files in a source language (en-US by default) and writes the same
labels back for a target language, saving them into the model and layer you specify.

> **Why a target model is required:** labels are identified as `@LabelFile:Id`
> *within a model*, and only models you own can be written to. Sealed Microsoft
> models (ApplicationSuite, etc.) cannot be modified, so generation is scoped to a
> single model that you control.

You are more than welcome to contribute!

# Requirements

- A machine with the D365 F&O developer toolchain (the `PackagesLocalDirectory`
  must be present on disk).
- The build references the metadata assemblies from the AOSService `Bin` folder:
  `Microsoft.Dynamics.AX.Metadata.dll`, `Microsoft.Dynamics.AX.Metadata.Core.dll`
  and `Microsoft.Dynamics.AX.Metadata.Storage.dll`. The reference path is driven by
  the MSBuild property `PackagesBinDir`, which defaults to
  `K:\AosService\PackagesLocalDirectory\Bin`. Override it for a different drive:

  ```
  msbuild /p:PackagesBinDir=C:\AosService\PackagesLocalDirectory\Bin
  ```

# Running from the command line

```
-l or --lang          (required) Target language to generate. For example: pt-BR.

-m or --model         (required) Target model whose label files will be generated.
                      Must be a model you own.

-s or --source-lang   Source language to copy label ids/text from. Default: en-US.

-y or --layer         Layer to save the generated labels into (usr, var, cus, ...).
                      Default: usr.

-f or --folder        The AOSService folder path. For example: K:\AosService\.
                      (if not specified, the first one found on a fixed drive is used)

-v or --verbose       Display the processed label files during the run.

--help                Display the help screen.

--version             Display version information.
```

For example:

```
LabelFileGenerator.exe -l pt-BR -m MyTranslations -v
```

The command line above reads the en-US labels of every label file in the
`MyTranslations` model and writes Brazilian Portuguese (`pt-BR`) labels back into
that same model, with the verbose flag on.
