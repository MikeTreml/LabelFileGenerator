using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Storage;
using Microsoft.Dynamics.AX.Metadata.Storage.DiskProvider;
using Microsoft.Dynamics.AX.Metadata.Providers;

namespace LabelFileGenerator
{
    /// <summary>
    /// Encapsulates every call into the D365 Metadata Provider API so the rest of
    /// the program is independent of the provider surface. The disk provider reads
    /// and writes model metadata straight from PackagesLocalDirectory on disk - no
    /// running AOS kernel (and therefore no Microsoft.Dynamics.Ax.Xpp /
    /// AxShared.dll reference) is required.
    ///
    /// NOTE: the exact provider member names below could not be verified against
    /// Microsoft Learn (the API reference pages returned HTTP 403). They are the
    /// best reconstruction from the metadata-provider community samples and are
    /// flagged with "VERIFY" where they should be confirmed against the actual
    /// Microsoft.Dynamics.AX.Metadata assemblies on a D365 dev box.
    /// </summary>
    class MetadataLabelService
    {
        private readonly IMetadataProvider provider;
        private readonly ModelSaveInfo saveInfo;
        private readonly string modelName;

        public MetadataLabelService(string packagesLocalDirectory, string targetModel)
        {
            if (!Directory.Exists(packagesLocalDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"PackagesLocalDirectory not found: {packagesLocalDirectory}");
            }

            var diskConfig = new DiskProviderConfiguration();
            diskConfig.AddMetadataPath(packagesLocalDirectory);

            // VERIFY: factory + CreateDiskProvider(DiskProviderConfiguration).
            provider = new MetadataProviderFactory().CreateDiskProvider(diskConfig);

            // A model is bound to a single layer, so resolving its save info once and
            // reusing it for every label file is both correct and avoids repeating the
            // manifest lookup per file.
            // VERIFY: ModelManifest.Find(name) -> ModelInfo.
            ModelInfo modelInfo = provider.ModelManifest.Find(targetModel).FirstOrDefault();
            if (modelInfo == null)
            {
                throw new InvalidOperationException(
                    $"Target model '{targetModel}' was not found in the metadata store.");
            }

            modelName = targetModel;
            saveInfo = new ModelSaveInfo(modelInfo);
        }

        /// <summary>
        /// The label file ids that belong to the target model. Only models you own
        /// can be written to, so generation is scoped to a single target model.
        /// </summary>
        public IEnumerable<string> ListLabelFiles()
        {
            // VERIFY: ListObjectsForModel exists on ILabelProvider; otherwise list
            // all label files and filter by the model's element membership.
            return provider.Labels.ListObjectsForModel(modelName);
        }

        /// <summary>
        /// Copies every <paramref name="sourceLanguage"/> label in the file into
        /// <paramref name="targetLanguage"/> and saves it. When <paramref name="overwrite"/>
        /// is false, labels that already exist in the target language are left
        /// untouched, so re-runs fill gaps without clobbering translations that were
        /// edited by hand; when true, every source label is (re)written. Returns the
        /// number of labels written (0 when the file is missing or nothing changed).
        /// Performs one provider read and at most one provider write per file.
        /// </summary>
        public int CopyLanguage(string labelFileId, string sourceLanguage, string targetLanguage, bool overwrite)
        {
            // VERIFY: Read returns the AxLabelFile metamodel object.
            AxLabelFile labelFile = provider.Labels.Read(labelFileId);
            if (labelFile == null)
            {
                return 0;
            }

            // VERIFY: per-language label content access. The label text values live
            // in the .txt resource of the file for the requested language; adjust
            // GetLabelContents / SetLabel to the actual content API exposed by
            // AxLabelFile.
            var existingTarget = overwrite
                ? new HashSet<string>()
                : new HashSet<string>(labelFile.GetLabelContents(targetLanguage).Select(e => e.Key));

            int written = 0;
            foreach (var entry in labelFile.GetLabelContents(sourceLanguage))
            {
                if (existingTarget.Contains(entry.Key))
                {
                    continue;
                }

                labelFile.SetLabel(targetLanguage, entry.Key, entry.Value, string.Empty);
                written++;
            }

            if (written > 0)
            {
                provider.Labels.Update(labelFile, saveInfo);
            }

            return written;
        }
    }

    class Options
    {
        [Option('l', "lang", Required = true,
            HelpText = "Target language to generate, e.g. pt-BR.")]
        public string Language { get; set; }

        [Option('m', "model", Required = true,
            HelpText = "Target model whose label files will be (re)generated. " +
                       "Must be a model you own - sealed Microsoft models cannot be written.")]
        public string Model { get; set; }

        [Option('s', "source-lang", Required = false, Default = "en-US",
            HelpText = "Source language to copy label ids/text from. Default: en-US.")]
        public string SourceLanguage { get; set; }

        [Option('f', "folder", Required = false,
            HelpText = "The AOSService folder path, e.g. K:\\AosService\\. " +
                       "If not specified, the first one found on a fixed drive is used.")]
        public string AOSServiceFolder { get; set; }

        [Option('o', "overwrite", Required = false,
            HelpText = "Overwrite labels that already exist in the target language. " +
                       "By default existing target-language labels are kept (gaps only).")]
        public bool Overwrite { get; set; }

        [Option('v', "verbose", Required = false,
            HelpText = "Display the processed label files during the run.")]
        public bool Verbose { get; set; }
    }

    class Program
    {
        public static string AOSServicePath = @"AOSService\";
        public static string PackagesLocalDirectoryPath = @"PackagesLocalDirectory\";
        // A folder every PackagesLocalDirectory has, used to recognise an AOSService root.
        public static string PackagesProbePath = PackagesLocalDirectoryPath + @"ApplicationPlatform\";

        public Options Arguments;
        public List<string> AvailableAOSServiceFolders = new List<string>();

        static void Main(string[] args)
        {
            Console.WriteLine();

            Program program = new Program();

            var parser = new Parser(config => config.HelpWriter = Console.Out);
            parser.ParseArguments<Options>(args).WithParsed(o => { program.Arguments = o; });

            if (program.Arguments != null)
            {
                var assembly = Assembly.GetExecutingAssembly();
                var assemblyTitle = assembly.GetCustomAttributes(typeof(AssemblyTitleAttribute), false)[0] as AssemblyTitleAttribute;
                var assemblyVersion = assembly.GetCustomAttributes(typeof(AssemblyFileVersionAttribute), false)[0] as AssemblyFileVersionAttribute;
                var assemblyCopyright = assembly.GetCustomAttributes(typeof(AssemblyCopyrightAttribute), false)[0] as AssemblyCopyrightAttribute;

                Console.WriteLine($"{assemblyTitle.Title} {assemblyVersion.Version}");
                Console.WriteLine(assemblyCopyright.Copyright);

                program.Run();
            }

            Console.WriteLine();
            Console.WriteLine("Press enter to continue...");
            Console.ReadLine();
        }

        public void Run()
        {
            if (!init())
            {
                return;
            }

            string packagesLocalDirectory = Arguments.AOSServiceFolder + PackagesLocalDirectoryPath;

            Console.WriteLine();
            Console.WriteLine($"AOSService folder: {Arguments.AOSServiceFolder}");
            Console.WriteLine($"Target model:      {Arguments.Model}");
            Console.WriteLine($"Source language:   {Arguments.SourceLanguage}");
            Console.WriteLine($"Target language:   {Arguments.Language}");
            Console.WriteLine();

            try
            {
                var processStart = DateTime.Now;

                GenerateLabelFiles(packagesLocalDirectory);

                var processEnd = DateTime.Now;

                Console.WriteLine();
                Console.WriteLine("Done!");
                Console.WriteLine();
                Console.WriteLine("Duration: " + (processEnd - processStart).ToString());
            }
            catch (Exception e)
            {
                LogError(e);
            }
        }

        public bool init()
        {
            if (string.Equals(Arguments.SourceLanguage, Arguments.Language, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Source and target language are both '{Arguments.Language}'; nothing to generate.");
                return false;
            }

            initAvailableAOSServiceFolders();

            if (AvailableAOSServiceFolders.Count() == 0)
            {
                Console.WriteLine("Failed! No AOSService folder found.");
                return false;
            }

            if (string.IsNullOrEmpty(Arguments.AOSServiceFolder))
            {
                Arguments.AOSServiceFolder = AvailableAOSServiceFolders[0];
            }
            else if (!ValidateAOSServiceFolder(Arguments.AOSServiceFolder))
            {
                return false;
            }

            return true;
        }

        public void initAvailableAOSServiceFolders()
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed
                    && Directory.Exists(drive.Name + AOSServicePath + PackagesProbePath))
                {
                    AvailableAOSServiceFolders.Add(drive.Name + AOSServicePath);
                }
            }
        }

        /// <summary>
        /// Reads every label file in the target model for the source language and
        /// writes the same labels back for the target language, through the
        /// Metadata Provider API. A failure on one file is reported and the run
        /// continues with the rest.
        /// </summary>
        public void GenerateLabelFiles(string packagesLocalDirectory)
        {
            Console.WriteLine("Connecting to the metadata store, please wait...");

            var service = new MetadataLabelService(packagesLocalDirectory, Arguments.Model);

            var labelFileIds = service.ListLabelFiles().ToList();

            if (labelFileIds.Count == 0)
            {
                Console.WriteLine($"No label files found in model '{Arguments.Model}'.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"Generating {Arguments.Language} labels for {labelFileIds.Count} label file(s)...");
            Console.WriteLine();

            int filesWritten = 0;
            int filesSkipped = 0;
            int filesFailed = 0;
            int totalLabels = 0;

            foreach (var labelFileId in labelFileIds)
            {
                try
                {
                    int written = service.CopyLanguage(labelFileId, Arguments.SourceLanguage, Arguments.Language, Arguments.Overwrite);

                    if (written == 0)
                    {
                        filesSkipped++;
                        if (Arguments.Verbose)
                        {
                            Console.WriteLine($"Skipped (nothing to add): {labelFileId}");
                        }
                    }
                    else
                    {
                        filesWritten++;
                        totalLabels += written;
                        if (Arguments.Verbose)
                        {
                            Console.WriteLine($"Wrote {written} label(s): {labelFileId} [{Arguments.Language}]");
                        }
                    }
                }
                catch (Exception e)
                {
                    filesFailed++;
                    Console.WriteLine($"Failed: {labelFileId} - {e.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine(
                $"{totalLabels} label(s) written across {filesWritten} file(s); " +
                $"{filesSkipped} skipped; {filesFailed} failed.");
        }

        bool ValidateAOSServiceFolder(string aosServiceFolder)
        {
            if (!AvailableAOSServiceFolders.Contains(aosServiceFolder))
            {
                Console.WriteLine($"AOSService folder {aosServiceFolder} is invalid.");
                Console.WriteLine("Available AOSService folders:");
                foreach (string folder in AvailableAOSServiceFolders)
                {
                    Console.WriteLine(folder);
                }
                Console.WriteLine();
                return false;
            }

            return true;
        }

        public static void LogError(Exception e)
        {
            string msgDelimiter = "----------";
            string emptyLine = Environment.NewLine + Environment.NewLine;

            string msg = "Execution failed!" + emptyLine;

            if (e is UnauthorizedAccessException)
            {
                msg += e.Message
                    + emptyLine
                    + "Try to run this as Administrator."
                    + Environment.NewLine
                    + "You may also try to close Visual Studio or to restart the computer.";
            }
            else
            {
                msg += e.ToString();
            }

            msg = msgDelimiter + Environment.NewLine + msg + Environment.NewLine + msgDelimiter + Environment.NewLine;

            Console.WriteLine(msg);
        }
    }
}
