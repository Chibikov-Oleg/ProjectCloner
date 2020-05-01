using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scar.Common.Comparers;
using Scar.Common.Cryptography;
using Scar.Common.Processes;
using Scar.Utilities;

namespace Scar.NugetPusher
{
    class Program
    {
        const string NugetServerApiKey = "NugetServer";
        static readonly Uri NugetServerUrl = new Uri("http://localhost:5533/");

        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.SpacingRules", "SA1011:Closing square brackets should be spaced correctly", Justification = "byte[]?")]
        static async Task Main(string[] args)
        {
            var host = HostUtilities.BuildAndRunHost(
                args,
                x => x.AddSingleton<IProcessUtility, ProcessUtility>().AddSingleton<IFileHasher, FileHasher>().AddSingleton<IComparer<string>, WinStringComparer>());

            var sourceDirectoryPath = args?.Length == 1 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
            var logger = host.Services.GetService<ILogger<Program>>();

            logger.LogTrace("Processing packages in {SourceDirectoryPath}...", sourceDirectoryPath);
            _ = sourceDirectoryPath ?? throw new InvalidOperationException("sourcePath is null");
            if (!Directory.Exists(sourceDirectoryPath))
            {
                throw new InvalidOperationException($"Directory {sourceDirectoryPath} does not exist");
            }

            var orderedNupkgFiles = Directory.GetFiles(sourceDirectoryPath, "*.nupkg", SearchOption.AllDirectories).OrderByDescending(x => x, host.Services.GetService<IComparer<string>>()).ToArray();
            if (orderedNupkgFiles.Length >= 2)
            {
                var lastTwoPackages = orderedNupkgFiles.Take(2).ToArray();
                var lastPackagePath = lastTwoPackages[0];
                var previousPackagePath = lastTwoPackages[1];
                var processUtility = host.Services.GetService<IProcessUtility>();
                var fileHasher = host.Services.GetService<IFileHasher>();
                string? previousPackageNuspecText = null, lastPackageNuspecText = null;
                byte[]? lastPackageDllHash = null, previousPackageDllHash = null;

                async Task ExtractLastPackageDetails(string dllFilePath, string nuspecFilePath)
                {
                    var (dllHash, nuspecText) = await ExtractPackageHashAndNuspecTextWithoutVersionAsync(
                            fileHasher ?? throw new InvalidOperationException(nameof(fileHasher)),
                            dllFilePath,
                            nuspecFilePath)
                        .ConfigureAwait(false);
                    lastPackageDllHash = dllHash;
                    lastPackageNuspecText = nuspecText;
                }

                async Task ExtractPreviousPackageDetails(string dllFilePath, string nuspecFilePath)
                {
                    var (dllHash, nuspecText) = await ExtractPackageHashAndNuspecTextWithoutVersionAsync(
                            fileHasher ?? throw new InvalidOperationException(nameof(fileHasher)),
                            dllFilePath,
                            nuspecFilePath)
                        .ConfigureAwait(false);
                    previousPackageDllHash = dllHash;
                    previousPackageNuspecText = nuspecText;
                }

                await Task.WhenAll(
                        NugetUtilities.ExtractPackageAndApplyActionAsync(lastPackagePath, processUtility, sourceDirectoryPath, ExtractLastPackageDetails),
                        NugetUtilities.ExtractPackageAndApplyActionAsync(previousPackagePath, processUtility, sourceDirectoryPath, ExtractPreviousPackageDetails))
                    .ConfigureAwait(false);

                var lastPackageName = NugetUtilities.ParseNugetPackageInfoForPath(lastPackagePath)?.ToString();
                var previousPackageName = NugetUtilities.ParseNugetPackageInfoForPath(previousPackagePath)?.ToString();

                var dllHashesAreEqual = lastPackageDllHash.SequenceEqual(previousPackageDllHash);
                var nuspecDifference = CompareStrings(lastPackageNuspecText, previousPackageNuspecText);

                var packagesAreEqual = dllHashesAreEqual && nuspecDifference == 0;
                if (!packagesAreEqual)
                {
                    logger.LogInformation(
                        @"Nuget packages {LastPackageName} is different from previous {PreviousPackageName} Nuspec: {NuspecDifference}, Dll: {DllsAreEqual}
Current dll hash: {CurrentDllHash}
Previous dll hash: {PreviousDllHash}
Current nuspec: {CurrentNuspec}
Previous nuspec: {PreviousNuspec}",
                        lastPackageName,
                        previousPackageName,
                        nuspecDifference,
                        !dllHashesAreEqual,
                        lastPackageDllHash?.GetHashString(),
                        previousPackageDllHash?.GetHashString(),
                        lastPackageNuspecText,
                        previousPackageNuspecText);
                    await PushNugetAsync(logger, processUtility, lastPackagePath).ConfigureAwait(false);
                }
                else
                {
                    logger.LogInformation("Nuget package {LastPackageName} is identical to previous {PreviousPackageName}. Push is skipped", lastPackageName, previousPackageName);
                }

                // Leave 2 last files
                DeleteFiles(orderedNupkgFiles.Skip(2), logger);
            }
            else if (orderedNupkgFiles.Length == 1)
            {
                var lastPackagePath = orderedNupkgFiles.Single();
                logger.LogInformation("There is only one nuget package {LastPackageName}", NugetUtilities.ParseNugetPackageInfoForPath(lastPackagePath)?.ToString());
                var processUtility = host.Services.GetService<IProcessUtility>();
                await PushNugetAsync(logger, processUtility, lastPackagePath).ConfigureAwait(false);
            }
            else
            {
                logger.LogInformation("Nothing to compare");
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1309:Use ordinal stringcomparison", Justification = "Already done, analyzer bug")]
        static int CompareStrings(string? lastPackageNuspecText, string? previousPackageNuspecText) =>
            string.Compare(
                lastPackageNuspecText,
                previousPackageNuspecText,
                CultureInfo.InvariantCulture,
                CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth | CompareOptions.IgnoreSymbols);

        static async Task<(byte[] DllHash, string NuspecText)> ExtractPackageHashAndNuspecTextWithoutVersionAsync(IFileHasher fileHasher, string dllFilePath, string nuspecFilePath)
        {
            var dllHash = fileHasher.GetSha512Hash(dllFilePath);

            // As version might differ but the rest stay the same we need to remove Version to do a proper comparison
            var nuspecTextWithoutVersion = await RemoveVersionTagAsync(nuspecFilePath).ConfigureAwait(false);
            return (dllHash, nuspecTextWithoutVersion);
        }

        static async Task PushNugetAsync(ILogger logger, IProcessUtility processUtility, string lastPackagePath)
        {
            await processUtility.ExecuteCommandAsync("dotnet", $"nuget push \"{lastPackagePath}\" -s {NugetServerUrl} -k {NugetServerApiKey}", CancellationToken.None).ConfigureAwait(false);

            logger.LogInformation("Nuget file {PackageName} was pushed to {NugetServerUrl}", NugetUtilities.ParseNugetPackageInfoForPath(lastPackagePath)?.ToString(), NugetServerUrl);
        }

        static void DeleteFiles(IEnumerable<string> nupkgFiles, ILogger logger)
        {
            foreach (var nupkgFilePath in nupkgFiles)
            {
                File.Delete(nupkgFilePath);
                logger.LogInformation("Deleted {PackagePath}", nupkgFilePath);
                var snupkgFilePath = nupkgFilePath.Replace(".nupkg", ".snupkg", StringComparison.OrdinalIgnoreCase);
                if (File.Exists(snupkgFilePath))
                {
                    File.Delete(snupkgFilePath);
                    logger.LogInformation("Deleted {SymbolsPackagePath}", snupkgFilePath);
                }
            }
        }

        static async Task<string> RemoveVersionTagAsync(string nuspecFilePath)
        {
            var text = await File.ReadAllTextAsync(nuspecFilePath).ConfigureAwait(false);
            return Regex.Replace(text, @"\<Version\>.*\<\/Version\>", string.Empty, RegexOptions.IgnoreCase);
        }
    }
}
