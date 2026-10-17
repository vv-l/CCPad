using System;
using System.IO;
using CCPad.Settings;

namespace CCPad.CodexQuota
{
    public enum QuotaToolbarView { Compact, AllAccounts }

    /// <summary>A separate atomic preference, so other windows' app settings cannot overwrite it.</summary>
    public sealed class QuotaToolbarSettings
    {
        private readonly string _file;
        public QuotaToolbarSettings(string? dataDirectory = null)
            => _file = Path.Combine(dataDirectory ?? AppPaths.Root, "quota-toolbar-view.txt");

        public QuotaToolbarView Load()
        {
            try
            {
                return File.ReadAllText(_file).Trim() == "compact"
                    ? QuotaToolbarView.Compact : QuotaToolbarView.AllAccounts;
            }
            catch { return QuotaToolbarView.AllAccounts; }
        }

        public bool TrySave(QuotaToolbarView view)
        {
            if (view != QuotaToolbarView.Compact && view != QuotaToolbarView.AllAccounts) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var temp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, view == QuotaToolbarView.Compact ? "compact" : "all");
                File.Move(temp, _file, overwrite: true);
                return true;
            }
            catch { return false; }
        }
    }
}
