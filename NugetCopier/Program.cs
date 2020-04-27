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

        static async Task Main(string[] args)
        {
            var sourcePath = args?.Length == 1 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
            _ = sourcePath ?? throw new InvalidOperationException("sourcePath is null");
            if (!Directory.Exists(sourcePath))
            {
                throw new InvalidOperationException($"Directory does not exist: {sourcePath}");
            }

            var destinationPath = Path.Combine(sourcePath, "Nuget");
            if (!Directory.Exists(destinationPath))
            {
                Directory.CreateDirectory(destinationPath);
            }

            var csprojFiles = Directory.EnumerateFiles(sourcePath, "*.csproj", SearchOption.AllDirectories);
            var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nugetCacheRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

            static string GetFullName(string name, string version)
            {
                return $"{name}.{version}";
            }

            foreach (var csprojFilePath in csprojFiles)
            {
                using var stream = File.OpenRead(csprojFilePath);
                var xml = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None).ConfigureAwait(false);

                IEnumerable<(string Name, string Version)> GetScarPackageReferences()
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

                var scarPackageReferences = GetScarPackageReferences();
                foreach (var packageReference in scarPackageReferences)
                {
                    void ClonePackageIfExistsInCache()
                    {
                        _ = packages ?? throw new InvalidOperationException("packages is null");
                        _ = nugetCacheRootPath ?? throw new InvalidOperationException("nugetCacheRootPath is null");
                        _ = destinationPath ?? throw new InvalidOperationException("destinationPath is null");

                        var (name, version) = packageReference;
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

                    ClonePackageIfExistsInCache();
                }
            }

            void DeleteNonExistingPackages()
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

            DeleteNonExistingPackages();

            var fileNameComparer = new WinStringComparer();

            void DeleteOutdatedPackagesFromCache(string basePath)
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

            DeleteOutdatedPackagesFromCache(nugetCacheRootPath);
            DeleteOutdatedPackagesFromCache(ScarLocalNugetPath);
        }
    }
}
