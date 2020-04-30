using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using Scar.Common.Comparers;

namespace Scar.NugetCopier
{
    class Program
    {
        const string ScarLocalNugetPath = "D:\\OneDrive\\Projects\\Nuget";
        const string ProjectBuiltinNugetDirectoryName = "Nuget";

        static async Task Main(string[] args)
        {
            var sourcePath = args?.Length == 1 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
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

            foreach (var csprojFilePath in csprojFiles)
            {
                var scarPackageReferences = await GetScarPackageReferencesFromProjectFileAsync(csprojFilePath).ConfigureAwait(false);
                foreach (var (name, version) in scarPackageReferences)
                {
                    ClonePackageIfExistsInCache(packages, name, version, nugetCacheRootPath, destinationPath);
                }
            }

            DeleteNonExistingPackages(packages, destinationPath);

            var fileNameComparer = new WinStringComparer();

            DeleteOutdatedPackagesFromCache(nugetCacheRootPath, fileNameComparer);
            DeleteOutdatedPackagesFromCache(ScarLocalNugetPath, fileNameComparer);
        }

        static async Task<IEnumerable<(string Name, string Version)>> GetScarPackageReferencesFromProjectFileAsync(string csprojFilePath)
        {
            await using var stream = File.OpenRead(csprojFilePath);
            var xml = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None).ConfigureAwait(false);
            var scarPackageReferences = GetScarPackageReferences(xml);
            return scarPackageReferences;
        }

        static IEnumerable<(string Name, string Version)> GetScarPackageReferences(XNode xml)
        {
            var valueTuples = xml.XPathSelectElements("//PackageReference")
                .Select(
                    pr =>
                    {
                        var name = pr.Attribute("Include");
                        var version = pr.Attribute("Version");
                        if (name == null || version == null)
                        {
                            return default;
                        }

                        return (Name: name.Value, Version: version.Value);
                    })
                .Where(x => x != default && x.Name.StartsWith("Scar", StringComparison.OrdinalIgnoreCase));
            return valueTuples;
        }

        static void DeleteOutdatedPackagesFromCache(string basePath, IComparer<string> fileNameComparer)
        {
            if (!Directory.Exists(basePath))
            {
                Console.WriteLine($"{basePath} Nuget cache does not exist");
                return;
            }

            var scarPackagesDirectories = Directory.EnumerateDirectories(basePath, "scar.*");
            foreach (var packageDirectoryPath in scarPackagesDirectories)
            {
                var versionSubDirectories = Directory.EnumerateDirectories(packageDirectoryPath).OrderByDescending(x => x, fileNameComparer);
                foreach (var versionDirectoryPath in versionSubDirectories.Skip(1))
                {
                    Directory.Delete(versionDirectoryPath, true);
                    Console.WriteLine($"Deleted outdated package {versionDirectoryPath}");
                }
            }
        }

        static void DeleteNonExistingPackages(ISet<string> packages, string destinationPath)
        {
            _ = packages ?? throw new InvalidOperationException("packages is null");

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
                var fullName = GetFullName(name, version);

                if (!packages.Contains(fullName))
                {
                    File.Delete(nugetFilePath);
                    Console.WriteLine($"Deleted {fullName} from {destinationPath}");
                }
            }
        }

        static string GetFullName(string name, string version)
        {
            return $"{name}.{version}";
        }

        static void ClonePackageIfExistsInCache(ISet<string> packages, string name, string version, string nugetCacheRootPath, string destinationPath)
        {
            var fullName = GetFullName(name, version);
            if (!packages.Contains(fullName))
            {
                packages.Add(fullName);
                var packageFileName = $"{fullName}.nupkg";
                var packageNugetCachePath = Path.Combine(nugetCacheRootPath, name, version, packageFileName);
                var destinationFilePath = Path.Combine(destinationPath, packageFileName);
                if (File.Exists(packageNugetCachePath))
                {
                    if (!File.Exists(destinationFilePath))
                    {
                        File.Copy(packageNugetCachePath, destinationFilePath);
                        Console.WriteLine($"Copied {fullName} from cache to {destinationPath}");
                    }
                }
            }
        }
    }
}
