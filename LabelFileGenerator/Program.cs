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

        public MetadataLabelService(string packagesLocalDirectory)
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
        }

        /// <summary>
        /// The label file ids that belong to the given model. Only models you own
        /// can be written to, so generation is scoped to a single target model.
        /// </summary>
        public IEnumerable<string> ListLabelFilesForModel(string model)
        {
            // VERIFY: ListObjectsForModel exists on ILabelProvider; otherwise list
            // all label files and filter by the model's element membership.
            return provider.Labels.ListObjectsForModel(model);
        }

        /// <summary>
        /// Reads the (label id -> text) pairs of a label file for one language.
        /// </summary>
        public IDictionary<string, string> ReadLabels(string labelFileId, string language)
        {
            var labels = new Dictionary<string, string>();

            // VERIFY: Read returns the AxLabelFile metamodel object.
            AxLabelFile labelFile = provider.Labels.Read(labelFileId);
            if (labelFile == null)
            {
                return labels;
            }

            // VERIFY: per-language label content access. The label text values live
            // in the .txt resource of the file for the requested language; adjust
            // the enumeration below to the actual content collection exposed by
            // AxLabelFile (e.g. LabelContents / a language-keyed entry list).
            foreach (var entry in labelFile.GetLabelContents(language))
            {
                labels[entry.Key] = entry.Value;
            }

            return labels;
        }

        /// <summary>
        /// Writes/merges a set of (id -> text) labels into <paramref name="labelFileId"/>
        /// for <paramref name="language"/>, saving into the given model and layer.
        /// </summary>
        public void WriteLabels(
            string labelFileId,
            string language,
            IDictionary<string, string> labels,
            string targetModel,
            string layer)
        {
            // VERIFY: resolve the ModelInfo for the target model so the save is
            // routed into the correct model + layer.
            ModelInfo modelInfo = provider.ModelManifest
                .Find(targetModel)
                .FirstOrDefault();

            if (modelInfo == null)
            {
                throw new InvalidOperationException(
                    $"Target model '{targetModel}' was not found in the metadata store.");
            }

            if (!string.IsNullOrEmpty(layer))
            {
                modelInfo.Layer = layer;
            }

            var saveInfo = new ModelSaveInfo(modelInfo);

            // VERIFY: read-existing-or-create. Existing files are updated so we add a
            // language rather than overwrite the file's other languages.
            AxLabelFile labelFile = provider.Labels.Read(labelFileId);
            bool isNew = labelFile == null;
            if (isNew)
            {
                labelFile = new AxLabelFile { Name = labelFileId };
            }

            // VERIFY: set the language content. Mirrors LabelEditorController.Insert
            // (id, text, description) used by the in-VS metadata API.
            foreach (var label in labels)
            {
                labelFile.SetLabel(language, label.Key, label.Value, string.Empty);
            }

            if (isNew)
            {
                provider.Labels.Create(labelFile, saveInfo);
            }
            else
            {
                provider.Labels.Update(labelFile, saveInfo);
            }
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

        [Option('y', "layer", Required = false, Default = "usr",
            HelpText = "Layer to save the generated labels into (usr, var, cus, ...). Default: usr.")]
        public string Layer { get; set; }

        [Option('f', "folder", Required = false,
            HelpText = "The AOSService folder path, e.g. K:\\AosService\\. " +
                       "If not specified, the first one found on a fixed drive is used.")]
        public string AOSServiceFolder { get; set; }

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
            Console.WriteLine($"Layer:             {Arguments.Layer}");
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
        /// Metadata Provider API.
        /// </summary>
        public void GenerateLabelFiles(string packagesLocalDirectory)
        {
            Console.WriteLine("Connecting to the metadata store, please wait...");

            var service = new MetadataLabelService(packagesLocalDirectory);

            var labelFileIds = service.ListLabelFilesForModel(Arguments.Model).ToList();

            if (!labelFileIds.Any())
            {
                Console.WriteLine($"No label files found in model '{Arguments.Model}'.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"Generating {Arguments.Language} labels for {labelFileIds.Count} label file(s)...");
            Console.WriteLine();

            foreach (var labelFileId in labelFileIds)
            {
                var sourceLabels = service.ReadLabels(labelFileId, Arguments.SourceLanguage);

                if (sourceLabels.Count == 0)
                {
                    if (Arguments.Verbose)
                    {
                        Console.WriteLine($"Skipped (no {Arguments.SourceLanguage} labels): {labelFileId}");
                    }
                    continue;
                }

                service.WriteLabels(
                    labelFileId,
                    Arguments.Language,
                    sourceLabels,
                    Arguments.Model,
                    Arguments.Layer);

                if (Arguments.Verbose)
                {
                    Console.WriteLine($"Wrote {sourceLabels.Count} label(s): {labelFileId} [{Arguments.Language}]");
                }
            }
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
