using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scar.Common.Comparers;
using Scar.Common.IO;
using Scar.Common.Processes;
using Scar.Utilities;

namespace Scar.NugetCopier
{
    class Program
    {
        const string ScarLocalNugetPath = "D:\\OneDrive\\Projects\\Nuget";
        const string ProjectBuiltinNugetDirectoryName = "Nuget";

        static async Task Main(string[] args)
        {
            var host = HostUtilities.BuildAndRunHost(args, x => x.AddSingleton<IProcessUtility, ProcessUtility>().AddSingleton<IComparer<string>, WinStringComparer>());

            var logger = host.Services.GetService<ILogger<Program>>();
            var sourcePath = args?.Length == 1 ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory;
            _ = sourcePath ?? throw new InvalidOperationException("sourcePath is null");
            if (!Directory.Exists(sourcePath))
            {
                throw new InvalidOperationException($"Directory does not exist: {sourcePath}");
            }

            var destinationPath = Path.Combine(sourcePath, ProjectBuiltinNugetDirectoryName);
            if (!Directory.Exists(destinationPath))
            {
                Directory.CreateDirectory(destinationPath);
            }

            var csprojFiles = Directory.EnumerateFiles(sourcePath, "*.csproj", SearchOption.AllDirectories);
            var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nugetCacheRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            var processUtility = host.Services.GetService<IProcessUtility>();
            var tempPath = PathExtensions.CreateTempDirectory();
            logger.LogTrace("Using temp path {TempPath}...", tempPath);

            async Task CopyAllPackageDependenciesAsync(string packageFilePath)
            {
                await NugetUtilities.ExtractPackageAndApplyActionAsync(
                        packageFilePath,
                        processUtility ?? throw new InvalidOperationException("processUtility is null"),
                        tempPath ?? throw new InvalidOperationException("tempPath is null"),
                        async (dllFilePath, nuspecFilePath) =>
                        {
                            var dependencies = await GetScarPackageReferencesFromNuspecFileAsync(nuspecFilePath).ConfigureAwait(false);
                            foreach (var nugetPackageInfo in dependencies)
                            {
                                logger.LogTrace("Trying to clone {PackageName} which is a dependency of {DependentPackageName}...", nugetPackageInfo.ToString(), NugetUtilities.ParseNugetPackageInfoForPath(packageFilePath)?.ToString());
                                await ClonePackageIfExistsInCacheAsync(
                                        logger ?? throw new InvalidOperationException("logger is null"),
                                        packages ?? throw new InvalidOperationException("packages is null"),
                                        nugetPackageInfo,
                                        nugetCacheRootPath ?? throw new InvalidOperationException("nugetCacheRootPath is null"),
                                        destinationPath ?? throw new InvalidOperationException("destinationPath is null"),
                                        CopyAllPackageDependenciesAsync)
                                    .ConfigureAwait(false);
                            }
                        })
                    .ConfigureAwait(false);
            }

            var tasks = csprojFiles.Select(
                async csprojFilePath =>
                {
                    var scarPackageReferences = await GetScarPackageReferencesFromProjectFileAsync(csprojFilePath).ConfigureAwait(false);
                    foreach (var nugetPackageInfo in scarPackageReferences)
                    {
                        logger.LogTrace("Trying to clone {PackageName}...", nugetPackageInfo.ToString());
                        await ClonePackageIfExistsInCacheAsync(logger, packages, nugetPackageInfo, nugetCacheRootPath, destinationPath, CopyAllPackageDependenciesAsync).ConfigureAwait(false);
                    }
                });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            DeleteNonExistingPackages(logger, packages, destinationPath);

            var fileNameComparer = host.Services.GetService<IComparer<string>>();

            DeleteOutdatedPackagesFromCache(logger, nugetCacheRootPath, fileNameComparer);
            DeleteOutdatedPackagesFromCache(logger, ScarLocalNugetPath, fileNameComparer);
            Directory.Delete(tempPath, true);
            logger.LogInformation("Deleted {TempPath}", tempPath);
        }

        static async Task<IEnumerable<NugetPackageInfo>> GetScarPackageReferencesFromProjectFileAsync(string filePath)
        {
            return await GetScarPackageReferencesFromFileAsync(filePath, "//PackageReference", "Include", "Version").ConfigureAwait(false);
        }

        static async Task<IEnumerable<NugetPackageInfo>> GetScarPackageReferencesFromNuspecFileAsync(string filePath)
        {
            return await GetScarPackageReferencesFromFileAsync(filePath, "//*[local-name()='dependency']", "id", "version").ConfigureAwait(false);
        }

        static async Task<IEnumerable<NugetPackageInfo>> GetScarPackageReferencesFromFileAsync(string filePath, string packageNode, string idAttribute, string versionAttribute)
        {
            await using var stream = File.OpenRead(filePath);
            var xml = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None).ConfigureAwait(false);
            return GetScarPackageReferencesFromXml(xml, packageNode, idAttribute, versionAttribute);
        }

        static IEnumerable<NugetPackageInfo> GetScarPackageReferencesFromXml(XNode xml, string packageNodeXPath, string idAttribute, string versionAttribute)
        {
            var packageInfos = xml.XPathSelectElements(packageNodeXPath)
                .Select(
                    pr =>
                    {
                        var name = pr.Attribute(idAttribute);
                        var version = pr.Attribute(versionAttribute);
                        if (name == null || version == null)
                        {
                            return null;
                        }

                        return new NugetPackageInfo(name.Value, new Version(version.Value));
                    })
                .Where(x => x != null && x.Name.StartsWith("Scar", StringComparison.OrdinalIgnoreCase));
            return packageInfos!;
        }

        static void DeleteOutdatedPackagesFromCache(
            ILogger logger, string basePath, IComparer<string> fileNameComparer)
        {
            if (!Directory.Exists(basePath))
            {
                logger.LogWarning("{NugetCacheDirectoryPath} Nuget cache does not exist", basePath);
                return;
            }

            var scarPackagesDirectories = Directory.EnumerateDirectories(basePath, "scar.*");
            foreach (var packageDirectoryPath in scarPackagesDirectories)
            {
                var versionSubDirectories = Directory.EnumerateDirectories(packageDirectoryPath).OrderByDescending(x => x, fileNameComparer);
                foreach (var versionDirectoryPath in versionSubDirectories.Skip(1))
                {
                    Directory.Delete(versionDirectoryPath, true);
                    logger.LogInformation("Deleted outdated package {PackagePath}", versionDirectoryPath);
                }
            }
        }

        static void DeleteNonExistingPackages(
            ILogger logger, ISet<string> packages, string destinationPath)
        {
            var nupkgRegex = new Regex("^(.*?)\\.((?:\\.?[0-9]+){3,}(?:[-a-z]+)?)\\.nupkg$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var destinationNugetFiles = Directory.EnumerateFiles(destinationPath, "*.nupkg", SearchOption.AllDirectories);
            foreach (var nugetFilePath in destinationNugetFiles)
            {
                var fileName = Path.GetFileName(nugetFilePath);
                var matches = nupkgRegex.Matches(fileName);
                if (matches.Count != 1)
                {
                    continue;
                }

                var match = matches.Single();

                if (match.Groups.Count != 3)
                {
                    continue;
                }

                var name = match.Groups[1].Value;
                var version = match.Groups[2].Value;
                var fullName = new NugetPackageInfo(name, new Version(version)).ToString();

                lock (packages)
                {
                    if (!packages.Contains(fullName))
                    {
                        File.Delete(nugetFilePath);
                        logger.LogInformation("Deleted {PackagePath} from {DestinationDirectoryPath}", fullName, destinationPath);
                    }
                }
            }
        }

        static async Task ClonePackageIfExistsInCacheAsync(
            ILogger logger,
            ISet<string> packages,
            NugetPackageInfo nugetPackageInfo,
            string nugetCacheRootPath,
            string destinationPath,
            Func<string, Task> processPackageAsync)
        {
            var fullName = nugetPackageInfo.ToString();
            lock (packages)
            {
                if (packages.Contains(fullName))
                {
                    logger.LogInformation("Package {PackageName} is already processed", fullName);
                    return;
                }

                packages.Add(fullName);
            }

            var packageFileName = $"{fullName}.nupkg";
            var destinationFilePath = Path.Combine(destinationPath, packageFileName);
            if (File.Exists(destinationFilePath))
            {
                logger.LogInformation("Package {PackageName} already exists in destination directory {DestinationDirectoryPath}", fullName, destinationPath);
                return;
            }

            var packageNugetCacheFilePath = Path.Combine(nugetCacheRootPath, nugetPackageInfo.Name, nugetPackageInfo.Version.ToString(), packageFileName);
            if (File.Exists(packageNugetCacheFilePath))
            {
                File.Copy(packageNugetCacheFilePath, destinationFilePath);
                logger.LogInformation("Copied {PackageName} from cache to {DestinationDirectoryPath}", fullName, destinationPath);

                await processPackageAsync(packageNugetCacheFilePath).ConfigureAwait(false);
            }
            else
            {
                logger.LogWarning("Package {PackageName} does not exist in the cache {NugetCacheDirectoryPath}", fullName, nugetCacheRootPath);
            }
        }
    }
}
