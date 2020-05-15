using System;
using System.IO;
using System.Windows.Forms;

namespace Scar.ProjectCloner
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            var completed = false;
            while (!completed)
            {
                try
                {
                    Console.Write("Enter directory with template project: ");
                    string directoryPath;
                    using (var fbd = new FolderBrowserDialog { AutoUpgradeEnabled = true })
                    {
                        if (fbd.ShowDialogWithLastChosenValue(Settings.Default, nameof(Settings.Default.LastValue)) == DialogResult.OK)
                        {
                            directoryPath = fbd.SelectedPath;
                        }
                        else
                        {
                            return;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                    {
                        throw new InvalidOperationException("Directory does not exist");
                    }

                    Console.WriteLine();
                    Console.WriteLine(directoryPath);

                    var oldName = GetDirectoryName(directoryPath);

                    Console.Write("Enter new name: ");
                    var newName = Console.ReadLine();

                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        throw new InvalidOperationException("New name is empty");
                    }

                    var newDirectoryPath = directoryPath.Replace(oldName, newName, StringComparison.OrdinalIgnoreCase);

                    if (Directory.Exists(newDirectoryPath))
                    {
                        Console.Write($"Directory {newDirectoryPath} exists. Recreate? ");
                        if (!"y".Equals(Console.ReadLine(), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        Directory.Delete(newDirectoryPath, true);
                    }

                    CopyDirectory(directoryPath, newDirectoryPath);

                    RenameDirectory(newDirectoryPath, oldName, newName);
                }
                catch (IOException ex)
                {
                    Console.WriteLine(ex);
                    continue;
                }

                completed = true;
            }

            static void RenameDirectory(string directoryPath, string oldName, string newName, bool renameRoot = false)
            {
                if (renameRoot)
                {
                    var directoryName = GetDirectoryName(directoryPath);
                    if (directoryName.Contains(oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        var newDirectoryName = directoryName.Replace(oldName, newName, StringComparison.OrdinalIgnoreCase);
                        var newDirectoryPath = Path.Combine(Directory.GetParent(directoryPath).FullName, newDirectoryName);
                        Directory.Move(directoryPath, newDirectoryPath);
                        directoryPath = newDirectoryPath;
                    }
                }

                foreach (var subDirectoryPath in Directory.GetDirectories(directoryPath))
                {
                    RenameDirectory(subDirectoryPath, oldName, newName, true);
                }

                foreach (var filePath in Directory.GetFiles(directoryPath))
                {
                    var newFilePath = filePath;
                    if (filePath.Contains(oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        newFilePath = filePath.Replace(oldName, newName, StringComparison.OrdinalIgnoreCase);
                        File.Move(filePath, newFilePath);
                    }

                    ReplaceTextInFile(newFilePath, oldName, newName);
                }
            }

            static void ReplaceTextInFile(string filePath, string oldText, string newText)
            {
                var content = File.ReadAllText(filePath);
                if (content.Contains(oldText, StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(filePath, content.Replace(oldText, newText, StringComparison.OrdinalIgnoreCase));
                }
            }

            static void CopyDirectory(string sourceDirectoryPath, string destDirectoryPath, bool copySubDirs = true)
            {
                var sourceDirectoryName = GetDirectoryName(sourceDirectoryPath);
                if (sourceDirectoryName == "bin" || sourceDirectoryName == ".vs" || sourceDirectoryName == "obj")
                {
                    return;
                }

                // Get the subdirectories for the specified directory.
                var dir = new DirectoryInfo(sourceDirectoryPath);

                if (!dir.Exists)
                {
                    throw new DirectoryNotFoundException(
                        "Source directory does not exist or could not be found: "
                        + sourceDirectoryPath);
                }

                var dirs = dir.GetDirectories();

                // If the destination directory doesn't exist, create it.
                if (!Directory.Exists(destDirectoryPath))
                {
                    Directory.CreateDirectory(destDirectoryPath);
                }

                // Get the files in the directory and copy them to the new location.
                var files = dir.GetFiles();
                foreach (var file in files)
                {
                    var tempPath = Path.Combine(destDirectoryPath, file.Name);
                    file.CopyTo(tempPath, false);
                }

                // If copying subdirectories, copy them and their contents to new location.
                if (copySubDirs)
                {
                    foreach (var subDir in dirs)
                    {
                        var tempPath = Path.Combine(destDirectoryPath, subDir.Name);
                        CopyDirectory(subDir.FullName, tempPath, copySubDirs);
                    }
                }
            }

            static string GetDirectoryName(string fullPath) => Path.GetFileName(Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar));
        }
    }
}
