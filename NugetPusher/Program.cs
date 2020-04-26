using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scar.Common;
using Scar.Common.Comparers;
using Scar.Common.Cryptography;
using Scar.Common.Processes;

namespace Scar.NugetPusher
{
    class Program
    {
        const string NugetServerApiKey = "NugetServer";
        static readonly Uri NugetServerUrl = new Uri("http://localhost:5533/");

        static string AssemblyDirectory
        {
            get
            {
                var codeBase = Assembly.GetExecutingAssembly().CodeBase ?? throw new InvalidOperationException("CodeBase location cannot be detected");
                var uri = new UriBuilder(codeBase);
                var path = Uri.UnescapeDataString(uri.Path);
                return Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Assembly directory cannot be detected");
            }
        }

        static async Task Main(string[] args)
        {
            var host = BuildAndRunHost(args);

            var sourceDirectoryPath = args?.Length == 1 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
            var logger = host.Services.GetService<ILogger<Program>>();

            logger.LogInformation("Source directory is {0}", sourceDirectoryPath);
            _ = sourceDirectoryPath ?? throw new InvalidOperationException("sourcePath is null");
            if (!Directory.Exists(sourceDirectoryPath))
            {
                throw new InvalidOperationException($"Directory {sourceDirectoryPath} does not exist");
            }

            var nupkgFiles = Directory.GetFiles(sourceDirectoryPath, "*.nupkg", SearchOption.AllDirectories).OrderByDescending(x => x, new WinStringComparer()).ToArray();
            if (nupkgFiles.Length >= 2)
            {
                var lastTwoPackages = nupkgFiles.Take(2).ToArray();
                var lastPackagePath = lastTwoPackages.First();
                var previousPackagePath = lastTwoPackages.Last();
                var processUtility = host.Services.GetService<IProcessUtility>();
                var fileHasher = host.Services.GetService<IFileHasher>();
                var lastPackageHash = await GetPackageHashAsync(lastPackagePath, processUtility, fileHasher, sourceDirectoryPath).ConfigureAwait(false);
                var previousPackageHash = await GetPackageHashAsync(previousPackagePath, processUtility, fileHasher, sourceDirectoryPath).ConfigureAwait(false);
                var areFilesEqual = lastPackageHash.SequenceEqual(previousPackageHash);
                if (!areFilesEqual)
                {
                    await PushNugetAsync(processUtility, lastPackagePath).ConfigureAwait(false);
                }
                else
                {
                    logger.LogInformation("Nuget file is identical to previous. Push is skipped");
                }

                DeleteFiles(nupkgFiles.Skip(1), logger);
            }
            else if (nupkgFiles.Length == 1)
            {
                var lastPackagePath = nupkgFiles.Single();
                var processUtility = host.Services.GetService<IProcessUtility>();
                await PushNugetAsync(processUtility, lastPackagePath).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine("Nothing to compare");
            }
        }

        static async Task PushNugetAsync(IProcessUtility processUtility, string lastPackagePath)
        {
            await processUtility.ExecuteCommandAsync("dotnet", $"nuget push \"{lastPackagePath}\" -s {NugetServerUrl} -k {NugetServerApiKey}", CancellationToken.None).ConfigureAwait(false);
        }

        static void DeleteFiles(IEnumerable<string> nupkgFiles, ILogger logger)
        {
            foreach (var nupkgFilePath in nupkgFiles)
            {
                File.Delete(nupkgFilePath);
                logger.LogInformation("Deleted {0}", nupkgFilePath);
                var snupkgFilePath = nupkgFilePath.Replace(".nupkg", ".snupkg", StringComparison.OrdinalIgnoreCase);
                if (File.Exists(snupkgFilePath))
                {
                    File.Delete(snupkgFilePath);
                    logger.LogInformation("Deleted {0}", snupkgFilePath);
                }
            }
        }

        static async Task<byte[]> GetPackageHashAsync(string lastPackagePath, IProcessUtility processUtility, IFileHasher fileHasher, string sourcePath)
        {
            var packageInfo = NugetHelper.ParseNugetPackageInfoForPath(lastPackagePath) ?? throw new InvalidOperationException("Package info is null");

            var versionDirectoryPath = Path.Combine(sourcePath, packageInfo.Version.ToString());
            if (!Directory.Exists(versionDirectoryPath))
            {
                Directory.CreateDirectory(versionDirectoryPath);
            }

            var fileName = $"{packageInfo.Name}.dll";
            await processUtility.ExecuteCommandAsync(
                    Path.Combine(AssemblyDirectory, "7za.exe"),
                    $"e \"{lastPackagePath}\" -o\"{versionDirectoryPath}\" {fileName} -r -y",
                    CancellationToken.None)
                .ConfigureAwait(false);
            var hash = fileHasher.GetSha512Hash(Path.Combine(versionDirectoryPath, fileName));

            Directory.Delete(versionDirectoryPath, true);
            return hash;
        }

        static IHost BuildAndRunHost(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices(x => x.AddSingleton<IProcessUtility, ProcessUtility>().AddSingleton<IFileHasher, FileHasher>())
                .ConfigureLogging(
                    logging =>
                    {
                        logging.ClearProviders().
                        AddConsole().
                        SetMinimumLevel(LogLevel.Trace);
                    })
                .Build();
            host.RunAsync();
            return host;
        }
    }
}
