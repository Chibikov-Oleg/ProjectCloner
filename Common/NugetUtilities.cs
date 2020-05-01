using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Scar.Common.Processes;

namespace Scar.Utilities
{
    public static class NugetUtilities
    {
        static readonly Regex NupkgRegex = new Regex("^(.*?)\\.((?:\\.?[0-9]+){3,}(?:[-a-z]+)?)\\.nupkg$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        public static async Task ExtractPackageAndApplyActionAsync(string packageFilePath, IProcessUtility processUtility, string extractionTempPath, Func<string, string, Task> processPackageAsync)
        {
            _ = processPackageAsync ?? throw new ArgumentNullException(nameof(processPackageAsync));
            _ = extractionTempPath ?? throw new ArgumentNullException(nameof(extractionTempPath));
            _ = packageFilePath ?? throw new ArgumentNullException(nameof(packageFilePath));
            _ = processUtility ?? throw new ArgumentNullException(nameof(processUtility));

            var packageInfo = ParseNugetPackageInfoForPath(packageFilePath) ?? throw new InvalidOperationException("Package info is null");

            var versionDirectoryPath = Path.Combine(extractionTempPath, packageInfo.Version.ToString());
            if (!Directory.Exists(versionDirectoryPath))
            {
                Directory.CreateDirectory(versionDirectoryPath);
            }

            var dllFileName = $"{packageInfo.Name}.dll";
            var nuspecFileName = $"{packageInfo.Name}.nuspec";
            await ExtractPackageAsync(packageFilePath, processUtility, versionDirectoryPath, dllFileName).ConfigureAwait(false);
            await ExtractPackageAsync(packageFilePath, processUtility, versionDirectoryPath, nuspecFileName).ConfigureAwait(false);
            var dllFilePath = Path.Combine(versionDirectoryPath, dllFileName);
            var nuspecFilePath = Path.Combine(versionDirectoryPath, nuspecFileName);

            await processPackageAsync(dllFilePath, nuspecFilePath).ConfigureAwait(false);

            Directory.Delete(versionDirectoryPath, true);
        }

        public static NugetPackageInfo? ParseNugetPackageInfoForPath(string filePath)
        {
            _ = filePath ?? throw new ArgumentNullException(nameof(filePath));

            var fileName = Path.GetFileName(filePath);
            return ParseNugetPackageInfo(fileName);
        }

        static async Task ExtractPackageAsync(string packageFilePath, IProcessUtility processUtility, string versionDirectoryPath, string fileName)
        {
            await processUtility.ExecuteCommandAsync(Path.Combine(AssemblyDirectory, "7za.exe"), $"e \"{packageFilePath}\" -o\"{versionDirectoryPath}\" {fileName} -r -y", CancellationToken.None)
                .ConfigureAwait(false);
        }

        static NugetPackageInfo? ParseNugetPackageInfo(string fileName)
        {
            _ = fileName ?? throw new ArgumentNullException(nameof(fileName));
            var matches = NupkgRegex.Matches(fileName);
            if (matches.Count != 1)
            {
                return null;
            }

            var match = matches[0];

            if (match.Groups.Count != 3)
            {
                return null;
            }

            var name = match.Groups[1].Value;
            if (!Version.TryParse(match.Groups[2].Value, out var version))
            {
                return null;
            }

            return new NugetPackageInfo(name, version);
        }
    }
}
