using System;
using System.Configuration;
using System.Windows.Forms;

namespace Scar.ProjectCloner
{
    public static class FolderBrowserDialogExtensions
    {
        public static DialogResult ShowDialogWithLastChosenValue(this FolderBrowserDialog dialog, ApplicationSettingsBase settings, string key)
        {
            _ = key ?? throw new ArgumentNullException(nameof(key));
            _ = settings ?? throw new ArgumentNullException(nameof(settings));
            _ = dialog ?? throw new ArgumentNullException(nameof(dialog));

            return dialog.ShowDialogWithLastChosenValue(
                settings[key].ToString(),
                path =>
                {
                    settings[key] = dialog.SelectedPath;
                    settings.Save();
                });
        }

        public static DialogResult ShowDialogWithLastChosenValue(this FolderBrowserDialog dialog, string? defaultValue, Action<string?> onAccept)
        {
            _ = onAccept ?? throw new ArgumentNullException(nameof(onAccept));
            _ = dialog ?? throw new ArgumentNullException(nameof(dialog));

            dialog.SelectedPath = defaultValue;
            var result = dialog.ShowDialog();
            if (result == DialogResult.OK)
            {
                // check for null, if needed
                onAccept(dialog.SelectedPath);
            }

            return result;
        }
    }
}
