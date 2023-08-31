using System.Diagnostics;
using System.Xml;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System.Security.Cryptography;

namespace WwiseExtractor
{
    class Program
    {
        static readonly string unrealPakPath = @"Engine\Binaries\Win64\UnrealPak.exe";
        static readonly string bnkextrPath = @"Engine\bnkextr.exe";
        static readonly string revorbPath = @"Engine\revorb.exe";
        static readonly string ww2oggPath = @"Engine\ww2ogg\ww2ogg.exe";
        static readonly string packedCodebooksPath = @"Engine\ww2ogg\packed_codebooks_aoTuV_603.bin";
        static readonly string configPath = "config.json";
        static readonly string jsonFilePath = "ExtractedAudio.json";

        static void Main(string[] args)
        {
            Console.Title = "Wwise Extractor";
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(@"__      __       _           ___     _               _           ");
            Console.WriteLine(@"\ \    / /_ __ _(_)___ ___  | __|_ _| |_ _ _ __ _ __| |_ ___ _ _ ");
            Console.WriteLine(@" \ \/\/ /\ V  V / (_-</ -_) | _|\ \ /  _| '_/ _` / _|  _/ _ \ '_|");
            Console.WriteLine(@"  \_/\_/  \_/\_/|_/__/\___| |___/_\_\\__|_| \__,_\__|\__\___/_|  ");
            Console.WriteLine(@"                         By BrandonItaly                         ");
            Console.ResetColor();
            Console.WriteLine();

            // Read variables from config.json
            Config config = LoadConfig();
            string? pakMountPoint = config.PakMountPoint;
            string wwiseDirectory = config.WwiseDirectory ?? @"WwiseAudio\Windows";
            string outputDirectory = config.OutputDirectory ?? "Output";
            string unknownDirectory = Path.Combine(outputDirectory, "Wwise Unknown Audio");

            Console.Write("Press Enter to begin audio extraction...");
            Console.ReadLine();

            if (config.PakFilePaths?.Length > 0)
            {
                foreach (string pakFilePath in config.PakFilePaths)
                {
                    string fileName = Path.GetFileName(pakFilePath);
                    Console.WriteLine($"Extracting {fileName}...");
                    ExtractPakChunk(pakFilePath);
                }
            }
            else
            {
                Console.WriteLine("No pak files specified in the config. Skipping pak extraction.");
            }

            // Read audio data from SoundbanksInfo.xml
            Console.WriteLine("Parsing SoundbanksInfo.xml...");
            string soundbanksInfoPath = Path.Combine(wwiseDirectory, "SoundbanksInfo.xml");
            XmlDocument xmlDocument = new();
            xmlDocument.Load(soundbanksInfoPath);

            XmlNodeList soundBankPaths = xmlDocument.SelectNodes("//SoundBank/Path");
            foreach (XmlNode soundBankPath in soundBankPaths)
            {
                string soundBankFilePath = Path.Combine(wwiseDirectory, soundBankPath.InnerText);
                RunCommand(bnkextrPath, $"{soundBankFilePath} /nodir");
            }

            // Read data from ExtractedAudio.json
            string originalExtractedAudio = File.Exists(jsonFilePath) ? File.ReadAllText(jsonFilePath) : string.Empty;
            List<AudioFileInfo> audioFileInfoList = JsonConvert.DeserializeObject<List<AudioFileInfo>>(originalExtractedAudio) ?? new List<AudioFileInfo>();

            Console.WriteLine("Moving WEM files...");
            HashSet<string> processedFiles = new();
            List<AudioFileInfo> audioFiles = new();
            XmlNodeList files = xmlDocument.SelectNodes("//File");
            foreach (XmlNode file in files)
            {
                if (file["Path"] != null)
                {
                    string fileId = file.Attributes["Id"].Value;
                    string filePath = Regex.Replace(file["Path"].InnerText, "_[0-9A-F]+\\.wem$", ".wem");
                    string language = file.Attributes["Language"]?.Value ?? "SFX";
                    string destination = Path.Combine(outputDirectory, filePath);
                    string sourcePath = Path.Combine(wwiseDirectory, language != "SFX" ? language : "", fileId + ".wem");

                    // Check if the file has already been processed
                    if (processedFiles.Contains(filePath))
                    {
                        File.Delete(sourcePath);
                        continue;
                    }

                    AudioFileInfo audioFileInfo = new()
                    {
                        FileId = fileId,
                        FilePath = filePath,
                        FileHash = CalculateFileHash(sourcePath)
                    };

                    audioFiles.Add(audioFileInfo);

                    bool shouldMove = true;

                    // Check if file hashes are identical
                    AudioFileInfo matchingAudioFile = audioFileInfoList.FirstOrDefault(audioFile => audioFile.FilePath == filePath);
                    if (matchingAudioFile != null && matchingAudioFile.FileHash == CalculateFileHash(sourcePath))
                    {
                        shouldMove = false;
                    }

                    // Check if the corresponding .ogg file exists
                    string oggFilePath = Path.ChangeExtension(destination, ".ogg");
                    if (!File.Exists(oggFilePath))
                    {
                        shouldMove = true;
                    }

                    if (shouldMove)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        MoveFile(sourcePath, destination);
                        Console.WriteLine($"Moved {filePath}");
                    }

                    File.Delete(sourcePath);

                    // Add the file to the processed set
                    processedFiles.Add(filePath);
                }
            }

            Directory.CreateDirectory(unknownDirectory);
            foreach (string file in Directory.EnumerateFiles(wwiseDirectory, "*.wem", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(file);
                string destination = Path.Combine(unknownDirectory, fileName);

                File.Move(file, destination);
                Console.WriteLine($"Moved unknown audio file {fileName}");
            }

            ConvertAndCompress(outputDirectory);

            string jsonData = JsonConvert.SerializeObject(audioFiles, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(jsonFilePath, jsonData);
            Console.WriteLine($"{jsonFilePath} created sucessfully.");

            CleanupFiles(outputDirectory, pakMountPoint);

            Console.WriteLine("Sound extraction completed!");
        }

        static string CalculateFileHash(string filePath)
        {
            using (var sha256 = SHA256.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] hashBytes = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
                }
            }
        }

        static Config LoadConfig()
        {
            if (!File.Exists(configPath))
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine("First-time setup: Please provide default values for extraction.");
                Console.ResetColor();

                Config config = new Config
                {
                    PakFilePaths = GetPakFilePaths(),
                    PakMountPoint = GetPakMountPoint(),
                    WwiseDirectory = GetWwiseDirectory(),
                    OutputDirectory = GetOutputDirectory()
                };

                string json = JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(configPath, json);

                Console.WriteLine("Default config values set. You can modify them later in the config.json file.");
                return config;
            }
            else
            {
                string jsonContent = File.ReadAllText(configPath);
                return JsonConvert.DeserializeObject<Config>(jsonContent) ?? new Config();
            }
        }

        static string[] GetPakFilePaths()
        {
            // Prompt the user for pak file paths
            Console.WriteLine("Enter pak file paths (comma-separated):");
            string input = Console.ReadLine();
            return input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(path => path.TrimStart()).ToArray();
        }

        static string GetPakMountPoint()
        {
            // Prompt the user for pak mount point
            Console.WriteLine("Enter pak file mount point (ex. \"DeadByDaylight\"):");
            return Console.ReadLine();
        }

        static string GetWwiseDirectory()
        {
            // Prompt the user for Wwise directory
            Console.WriteLine("Enter WwiseAudio directory (ex. \"DeadByDaylight\\Content\\WwiseAudio\\Windows\"):");
            return Console.ReadLine() ?? @"WwiseAudio\Windows";
        }

        static string GetOutputDirectory()
        {
            // Prompt the user for output directory
            Console.WriteLine("Enter output directory:");
            return Console.ReadLine() ?? "Output";
        }

        static void ExtractPakChunk(string pakFilePath)
        {
            string extractPath = @"../../../";
            RunCommand(unrealPakPath, $"\"{pakFilePath}\" -Extract \"{extractPath}\" -extracttomountpoint");
        }

        static void MoveFile(string source, string destination)
        {
            if (File.Exists(source))
            {
                File.Move(source, destination, true);
            }
        }

        static void ConvertAndCompress(string outputDirectory)
        {
            var wemFiles = Directory.EnumerateFiles(outputDirectory, "*.wem", SearchOption.AllDirectories);
            Parallel.ForEach(wemFiles, file =>
            {
                RunCommand(ww2oggPath, $"\"{file}\" --pcb \"{packedCodebooksPath}\"");
            });

            Parallel.ForEach(wemFiles, file =>
            {
                string oggFilePath = Path.ChangeExtension(file, ".ogg");
                if (File.Exists(oggFilePath))
                {
                    Console.WriteLine($"Compressing {oggFilePath}");
                    RunCommand(revorbPath, $"\"{oggFilePath}\"");
                }
            });
        }

        static void CleanupFiles(string outputDirectory, string pakMountPoint)
        {
            foreach (string file in Directory.EnumerateFiles(outputDirectory, "*.wem", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }

            if (Directory.Exists(pakMountPoint))
            {
                Directory.Delete(pakMountPoint, true);
            }
        }

        static void RunCommand(string filePath, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = filePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrEmpty(output))
                    Console.WriteLine(output);

                if (!string.IsNullOrEmpty(error))
                    Console.WriteLine(error);
            }
        }
    }

    class Config
    {
        public string[]? PakFilePaths { get; set; }
        public string? PakMountPoint { get; set; }
        public string? WwiseDirectory { get; set; }
        public string? OutputDirectory { get; set; }
    }

    class AudioFileInfo
    {
        public string? FileId { get; set; }
        public string? FilePath { get; set; }
        public string? FileHash { get; set; }
    }
}