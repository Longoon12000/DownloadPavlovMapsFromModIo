using System;
using System.Data.Common;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ModDownloader
{
    class ProgressBar
    {
        private const int progressBarWidth = 50;
        private readonly long total;

        public ProgressBar(long total)
        {
            this.total = total;
        }

        public void Update(long progress)
        {
            double percentage = (double)progress / total;
            int completed = (int)(percentage * progressBarWidth);

            Console.Write("\r[" + new string('#', completed) + new string(' ', progressBarWidth - completed) + $"] {percentage:P}");
        }

        public void Finish()
        {
            Console.WriteLine();
        }
    }

    class Program
    {
        private const string settingsPath = "./settings.json";
        private const string pavlovSettingsDirectoryBasePath = "%localappdata%\\Pavlov\\Saved";
        private const string modIoBaseUrl = "https://api.mod.io/v1";
        private const int limit = 100;

        record Settings(string AccessToken, string PavlovModsDirectory);
        record Mod(string Id, string LatestVersion, string Name, bool Exists, bool Download);

        private Settings? settings = null;

        private bool directDownload = false;
        private bool subscribedOnly = false;
        private bool skipFailedDownloads = false;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Pavlov VR Mod Downloader version 8");

            bool directDownload = false;
            bool subscribedOnly = false;
            bool skipFailedDownloads = false;

            foreach (string arg in args)
            {
                switch (arg.ToLower().Trim())
                {
                    case "--yes":
                        directDownload = true;

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Direct download enabled. No confirmation will be asked before downloading mods. Program will automatically exit after download is complete.");
                        Console.ResetColor();

                        break;
                    case "--subscribedonly":
                        subscribedOnly = true;

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Subscribed only enabled. Only subscribed mods will be downloaded.");
                        Console.ResetColor();

                        break;
                    case "--skipfaileddownloads":
                        skipFailedDownloads = true;

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Skip failed downloads enabled. Failed downloads will be skipped.");
                        Console.ResetColor();

                        break;
                    default:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Unknown argument: {arg}");
                        Console.ResetColor();
                        break;
                }
            }

            try
            {
                await new Program(directDownload, subscribedOnly, skipFailedDownloads).execute();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(ex);
                Console.ResetColor();
            }

            if (!directDownload)
            {
                Console.WriteLine();
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
            }
        }

        public Program(bool directDownload, bool subscribedOnly, bool skipFailedDownloads)
        {
            this.directDownload = directDownload;
            this.subscribedOnly = subscribedOnly;
            this.skipFailedDownloads = skipFailedDownloads;
        }

        private static DateTime lastRequest = DateTime.MinValue;
        private static readonly int requestDelay = 150;

        private static HttpClient createClient(string accessToken)
        {
            int waitTime = requestDelay - (int)(DateTime.Now - lastRequest).TotalMilliseconds;
            if (waitTime > 0)
            {
                Task.Delay(waitTime).Wait();
            }
            HttpClient client = new();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return client;
        }

        private static async Task<HttpResponseMessage> get(string endpoint, string accessToken)
        {
            HttpClient client = createClient(accessToken);
            return await client.GetAsync($"{modIoBaseUrl}{endpoint}");
        }

        private static async Task<string> getString(string endpoint, string accessToken)
        {
            HttpResponseMessage response = await get(endpoint, accessToken);
            return await response.Content.ReadAsStringAsync();
        }

        private async Task download(string url, string destination, string accessToken)
        {
            long totalBytes = -1;
            long receivedBytes = 0;

            using (HttpClient client = createClient(accessToken))
            {
                while (true)
                {
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            request.Headers.Range = new RangeHeaderValue(receivedBytes, null);

                            using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                            {
                                response.EnsureSuccessStatusCode();

                                if (totalBytes == -1)
                                {
                                    totalBytes = response.Content.Headers.ContentRange?.Length ?? -1;
                                }

                                using (var contentStream = await response.Content.ReadAsStreamAsync())
                                using (var fileStream = new FileStream(destination, FileMode.Append, FileAccess.Write))
                                {
                                    var buffer = new byte[1048576]; // 1MB buffer
                                    int bytesRead;

                                    var progressBar = new ProgressBar(totalBytes);

                                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                    {
                                        await fileStream.WriteAsync(buffer, 0, bytesRead);

                                        receivedBytes += bytesRead;
                                        progressBar.Update(receivedBytes);
                                    }

                                    progressBar.Finish();
                                }
                            }
                        }

                        break;
                    }
                    catch (Exception ex)
                    {
                        if (skipFailedDownloads)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"Error: {ex.Message}. Skipping download...");
                            Console.ResetColor();
                            return;
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"Error: {ex.Message}. Retrying and resuming download...");
                            Console.ResetColor();
                            await Task.Delay(1000);
                        }
                    }
                }
            }
        }

        private async Task execute()
        {
            if (File.Exists(settingsPath))
            {
                this.settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(settingsPath));
            }

            if (this.settings == null)
            {
                string? accessToken = null;

                while (accessToken == null)
                {
                    Console.Write("Enter your Mod.io OAuth access token (mod.io/me/access): ");
                    accessToken = Console.ReadLine()!;

                    if (accessToken.Length < 500)
                    {
                        Console.WriteLine("Access token is very short. Please make sure that you generate an OAuth token and press the + button on the right and then copy the token. NOT the API key!");
                        accessToken = null;
                        continue;
                    }

                    HttpResponseMessage response = await get("/me", accessToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("Failed to get user data from Mod.io. Make sure your token is correct and has read permissions.");
                        accessToken = null;
                        continue;
                    }
                }

                string pavlovSettingsDirectory = Environment.ExpandEnvironmentVariables(pavlovSettingsDirectoryBasePath);
                string pavlovModsDirectory = string.Empty;
                if (!Directory.Exists(pavlovSettingsDirectory))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Could not find Pavlov VR settings directory at {pavlovSettingsDirectory}. Make sure you have Pavlov VR installed.");
                    Console.ResetColor();
                    Console.WriteLine("To continue you will have to provide the Pavlov VR mods directory manually in the next step.");
                }
                else
                {
                    pavlovModsDirectory = File.ReadAllLines(Path.Combine(pavlovSettingsDirectory, "Config", "Windows", "GameUserSettings.ini")).FirstOrDefault(l => l.ToLower().StartsWith("moddirectory="))?.Split('=').Skip(1).FirstOrDefault() ?? string.Empty;

                    if (string.IsNullOrEmpty(pavlovModsDirectory))
                    {
                        pavlovModsDirectory = Path.Combine(pavlovSettingsDirectory, "Mods");

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"Could not find overwrite for Pavlov VR mods directory in {Path.Combine(pavlovSettingsDirectory, "Config", "Windows", "GameUserSettings.ini")}. Therefore default mods location {pavlovModsDirectory} is assumed to be the correct one.");
                        Console.ResetColor();
                    }
                }

                bool requestPavlovModsDirectory = false;
                if (Directory.Exists(pavlovModsDirectory))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Pavlov VR mods directory found at {pavlovModsDirectory}.");
                    Console.ResetColor();
                    Console.Write("Do you want to use this directory? (Y/n): ");
                    string? pavlovModsDirectoryOption = Console.ReadLine();

                    if (pavlovModsDirectoryOption?.Trim().ToLower() != "y" && !string.IsNullOrEmpty(pavlovModsDirectoryOption?.Trim()))
                    {
                        requestPavlovModsDirectory = true;
                    }
                }
                else
                {
                    requestPavlovModsDirectory = true;

                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Could not find Pavlov VR mods directory.");
                    Console.ResetColor();
                }

                if (requestPavlovModsDirectory)
                {
                    Console.Write("Enter the path to your Pavlov VR mods directory: ");
                    do
                    {
                        pavlovModsDirectory = Console.ReadLine()!;
                    } while (!Directory.Exists(pavlovModsDirectory));
                }

                this.settings = new(accessToken, pavlovModsDirectory);

                File.WriteAllText(settingsPath, JsonConvert.SerializeObject(this.settings));
            }

            string? subscribedModsJson = null;
            List<JObject>? subscribedMods = new();

            int offset = 0;
            int total = 0;
            bool allPages = false;

            while (!allPages)
            {

                try
                {
                    subscribedModsJson = await getString($"/me/subscribed?game_id=3959&_limit={limit}&_offset={offset}", this.settings.AccessToken);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(ex.Message);
                    Console.WriteLine("Failed to get subscribed mods from Mod.io. Make sure your token is correct and has read permissions.");
                    Console.ResetColor();
                    return;
                }

                try
                {
                    JObject jsonData = JObject.Parse(subscribedModsJson);

                    if (jsonData["result_count"]?.Value<int>() is int resultCount && jsonData["result_total"]?.Value<int>() is int resultTotal)
                    {
                        total += resultCount;

                        if (resultTotal > total)
                        {
                            offset += limit;
                        }
                        else
                        {
                            allPages = true;
                        }
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Could not determine if all pages have been loaded. Assuming all pages have been loaded.");
                        Console.ResetColor();
                        allPages = true;
                    }

                    JArray mods = (JArray)jsonData["data"]!;
                    subscribedMods.AddRange(extractModsByGameId(mods, 3959));
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(ex.Message);
                    Console.WriteLine("Failed to extract Pavlov VR mods from subscribed mods. Make sure you are subscribed to Pavlov VR mods and that your token is correct.");
                    Console.ResetColor();
                    if (subscribedOnly)
                    {
                        return;
                    }
                }
            }

            Console.WriteLine($"Found {subscribedMods.Count} subscribed Pavlov VR mods.");

            if (subscribedMods.Count == 0 && subscribedOnly)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("No Pavlov VR mods found. Make sure you are subscribed to Pavlov VR mods or run without --subscribedOnly.");
                Console.ResetColor();
                return;
            }

            if (!subscribedOnly)
            {
                string[] installedMods = Directory.GetDirectories(this.settings.PavlovModsDirectory).Where(m => !subscribedMods.Any(s => m.EndsWith($"UGC{s["id"]}"))).ToArray();

                Console.WriteLine($"Found {installedMods.Length} installed but not subscribed Pavlov VR mods.");
                Console.WriteLine("Checking for updates...");

                foreach (string modDirectory in installedMods)
                {
                    string? modId = modDirectory.Split("UGC").Skip(1).FirstOrDefault();

                    if (string.IsNullOrEmpty(modId))
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"The mod directory {modDirectory} does not seem to be a valid Pavlov VR mod. Skipping.");
                        Console.ResetColor();
                        continue;
                    }

                    try
                    {
                        JObject mod = JObject.Parse(await getString($"/games/3959/mods/{modId}", this.settings.AccessToken));
                        if (mod["error"] is not null)
                        {
                            throw new Exception(mod["error"]?["message"]?.ToString() ?? $"Unknown error ({mod})");
                        }
                        subscribedMods.Add(mod);
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(ex.Message);
                        Console.WriteLine($"Failed to get mod information for {modDirectory}. Skipping.");
                        Console.ResetColor();
                    }
                }
            }

            List<Mod> modsToDownload = new();

            foreach (var mod in subscribedMods)
            {
                string? latestVersion = null;

                foreach (var platform in mod["platforms"])
                {
                    if (platform["platform"]?.ToString() == "windows")
                    {
                        latestVersion = platform["modfile_live"].ToString();
                        break;
                    }
                }

                if (latestVersion == null)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Could not find a Windows version of this mod. Skipping.");
                    Console.ResetColor();
                    continue;
                }

                bool exists = Directory.Exists(Path.Combine(settings.PavlovModsDirectory, $"UGC{mod["id"]}"));
                bool download = true;

                if (exists)
                {
                    string currentVersion = string.Empty;
                    try
                    {
                        currentVersion = File.ReadAllText(Path.Combine(settings.PavlovModsDirectory, $"UGC{mod["id"]}", "taint"));
                    }
                    catch (Exception ex)
                    {
                        download = true;
                        continue;
                    }

                    if (currentVersion == latestVersion)
                    {
                        download = false;
                    }
                }

                modsToDownload.Add(new(mod["id"].ToString(), latestVersion, mod["name"].ToString(), exists, download));
            }

            int longestName = modsToDownload.Max(m => m.Name.Length);
            if (longestName > 60)
            {
                longestName = 60;
            }

            foreach (Mod mod in modsToDownload.OrderBy(m => m.Download).OrderByDescending(m => m.Exists))
            {
                Console.WriteLine($"{mod.Name[..Math.Min(mod.Name.Length, 60)].PadRight(longestName)} | {(mod.Exists ? "Exists" : "New"),-6} | {(mod.Download ? "Update required" : "Up to date")}");
            }

            if (modsToDownload.Count(m => m.Download) == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("No updates required.");
                Console.ResetColor();
                return;
            }

            if (!directDownload)
            {
                Console.Write("Do you want to continue? (Y/n): ");
                string? continueOption = Console.ReadLine();

                if (continueOption?.Trim().ToLower() != "y" && !string.IsNullOrEmpty(continueOption?.Trim()))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Download canceled.");
                    Console.ResetColor();
                    return;
                }
            }

            foreach (Mod mod in modsToDownload.Where(m => m.Download).OrderByDescending(m => m.Exists))
            {
                string modFilesJson = await getString($"/games/3959/mods/{mod.Id}/files/{mod.LatestVersion}", settings.AccessToken);

                JObject jsonData = JObject.Parse(modFilesJson);

                string downloadUrl = jsonData["download"]["binary_url"].ToString();

                Console.WriteLine($"Downloading: {mod.Name}");

                try
                {
                    await downloadAndExtractMod(downloadUrl, settings, mod);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(ex.Message);
                    Console.WriteLine($"Failed to download and extract {mod.Name}. Skipping.");
                    Console.ResetColor();
                }
            }

            Console.WriteLine("Done.");
        }

        private static List<JObject> extractModsByGameId(JArray mods, int gameId)
        {
            List<JObject> selectedMods = new();

            foreach (JObject mod in mods)
            {
                int modGameId = mod["game_id"]!.Value<int>();
                if (modGameId == gameId)
                {
                    selectedMods.Add(mod);
                }
            }

            return selectedMods;
        }

        private async Task downloadAndExtractMod(string downloadUrl, Settings settings, Mod mod)
        {
            if (!mod.Download)
            {
                return;
            }

            string modDirectory = Path.Combine(settings.PavlovModsDirectory, $"UGC{mod.Id}");

            if (mod.Exists)
            {
                Directory.Delete(modDirectory, true);
            }

            string tempZipFile = Path.GetTempFileName();
            string tempExtractDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            try
            {
                await download(downloadUrl, tempZipFile, settings.AccessToken);
            }
            catch (Exception ex)
            {
                try
                {
                    File.Delete(tempZipFile);
                }
                catch { }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(ex.Message);
                Console.WriteLine($"Failed to download {mod.Name}. Skipping.");
                Console.ResetColor();
                throw ex;
            }

            Console.WriteLine("Extracting mod...");

            try
            {
                ZipFile.ExtractToDirectory(tempZipFile, tempExtractDirectory);
            }
            catch (Exception ex)
            {
                try
                {
                    File.Delete(tempZipFile);
                }
                catch { }

                try
                {
                    Directory.Delete(tempExtractDirectory, true);
                }
                catch { }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(ex.Message);
                Console.WriteLine($"Failed to extract {mod.Name}. Skipping.");
                Console.ResetColor();
                throw ex;
            }

            try
            {
                Directory.CreateDirectory(modDirectory);
            }
            catch (Exception ex)
            {
                try
                {
                    File.Delete(tempZipFile);
                }
                catch { }

                try
                {
                    Directory.Delete(tempExtractDirectory, true);
                }
                catch { }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(ex.Message);
                Console.WriteLine($"Failed to create mod directory {modDirectory}. Skipping.");
                Console.ResetColor();
                throw ex;
            }

            string modDataDirectory = Path.Combine(modDirectory, "Data");

            try
            {
                MoveDirectory(tempExtractDirectory, modDataDirectory);
            }
            catch (Exception ex)
            {
                try
                {
                    File.Delete(tempZipFile);
                }
                catch { }

                try
                {
                    Directory.Delete(tempExtractDirectory, true);
                }
                catch { }

                try
                {
                    Directory.Delete(modDirectory, true);
                }
                catch { }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(ex.Message);
                Console.WriteLine($"Failed to move mod files to {modDataDirectory}. Skipping.");
                Console.ResetColor();
                throw ex;
            }

            try
            {
                File.WriteAllText(Path.Combine(modDirectory, "taint"), mod.LatestVersion);
            }
            catch (Exception ex)
            {
                try
                {
                    File.Delete(tempZipFile);
                }
                catch { }

                try
                {
                    Directory.Delete(tempExtractDirectory, true);
                }
                catch { }

                try
                {
                    Directory.Delete(modDirectory, true);
                }
                catch { }

                try
                {
                    File.Delete(Path.Combine(modDirectory, "taint"));
                }
                catch { }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(ex.Message);
                Console.WriteLine($"Failed to create taint file for {mod.Name}. Skipping.");
                Console.ResetColor();
                throw ex;
            }

            try
            {
                File.Delete(tempZipFile);
            }
            catch { }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Mod downloaded and extracted successfully.");
            Console.ResetColor();
        }

        static void MoveDirectory(string sourceDir, string targetDir)
        {
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string[] files = Directory.GetFiles(sourceDir);

            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                string targetPath = Path.Combine(targetDir, fileName);
                File.Copy(file, targetPath, true);
            }

            string[] subDirectories = Directory.GetDirectories(sourceDir);

            foreach (string subDir in subDirectories)
            {
                string targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
                MoveDirectory(subDir, targetSubDir);
            }

            Directory.Delete(sourceDir, true);
        }
    }
}
