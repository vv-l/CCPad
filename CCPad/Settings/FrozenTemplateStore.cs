using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace CCPad.Settings
{
    /// <summary>
    /// Persistent in-app frozen-template library. Files are ordinary
    /// .ccpad-template documents under the per-user CCPad data directory, so an
    /// item can also be exported/imported without conversion.
    /// </summary>
    public static class FrozenTemplateStore
    {
        private static readonly string Dir = AppPaths.Sub("templates");

        public sealed class Item
        {
            public string Path { get; init; } = "";
            public string Name { get; init; } = "";
            public int Order { get; init; }
            public DateTime UpdatedAt { get; init; }
            public WorkspaceEntry Entry { get; init; } = new();
        }

        private static T WithLock<T>(Func<T> body, T fallback)
        {
            Mutex? mutex = null;
            bool owned = false;
            try
            {
                mutex = new Mutex(false, @"Local\CCPad.FrozenTemplates");
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
                catch (AbandonedMutexException) { owned = true; }
                return body();
            }
            catch { return fallback; }
            finally
            {
                try { if (owned) mutex?.ReleaseMutex(); } catch { }
                mutex?.Dispose();
            }
        }

        public static List<Item> List() => WithLock(ListCore, new List<Item>());

        private static List<Item> ListCore()
        {
            var result = new List<Item>();
            if (!Directory.Exists(Dir)) return result;

            foreach (var path in Directory.GetFiles(Dir, "*" + WorkspaceConfig.TemplateExtension))
            {
                var entry = WorkspaceConfig.LoadFromFile(path);
                if (entry?.Layout == null) continue;
                var name = string.IsNullOrWhiteSpace(entry.TemplateName)
                    ? System.IO.Path.GetFileNameWithoutExtension(path)
                    : entry.TemplateName.Trim();
                result.Add(new Item
                {
                    Path = path,
                    Name = name,
                    Order = entry.TemplateOrder,
                    UpdatedAt = entry.TemplateUpdatedAt ?? File.GetLastWriteTime(path),
                    Entry = entry
                });
            }

            return result.OrderBy(x => x.Order <= 0 ? int.MaxValue : x.Order)
                         .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                         .ToList();
        }

        public static Item? SaveNew(WorkspaceEntry entry, string defaultNamePrefix)
            => WithLock<Item?>(() =>
            {
                Directory.CreateDirectory(Dir);
                var existing = ListCore();
                int order = existing.Count == 0 ? 1 : existing.Max(x => x.Order) + 1;
                if (order <= 0) order = existing.Count + 1;
                string name = MakeUniqueName($"{defaultNamePrefix} {order}", existing, null);
                var now = DateTime.Now;
                entry.TemplateId = Guid.NewGuid().ToString("N");
                entry.TemplateName = name;
                entry.TemplateOrder = order;
                entry.TemplateCreatedAt = now;
                entry.TemplateUpdatedAt = now;
                WorkspaceConfig.MarkAllTabsFrozen(entry);
                var path = System.IO.Path.Combine(
                    Dir, $"template-{entry.TemplateId}{WorkspaceConfig.TemplateExtension}");
                if (!WorkspaceConfig.SaveToFile(path, entry)) return null;
                return new Item { Path = path, Name = name, Order = order, UpdatedAt = now, Entry = entry };
            }, null);

        public static bool Overwrite(Item item, WorkspaceEntry entry)
            => WithLock(() =>
            {
                var now = DateTime.Now;
                entry.TemplateId = string.IsNullOrWhiteSpace(item.Entry.TemplateId)
                    ? Guid.NewGuid().ToString("N")
                    : item.Entry.TemplateId;
                entry.TemplateName = item.Name;
                entry.TemplateOrder = item.Order;
                entry.TemplateCreatedAt = item.Entry.TemplateCreatedAt ?? now;
                entry.TemplateUpdatedAt = now;
                WorkspaceConfig.MarkAllTabsFrozen(entry);
                return WorkspaceConfig.SaveToFile(item.Path, entry);
            }, false);

        public static bool Rename(Item item, string requestedName)
            => WithLock(() =>
            {
                var name = requestedName.Trim();
                if (name.Length == 0) return false;
                var existing = ListCore();
                item.Entry.TemplateName = MakeUniqueName(name, existing, item.Path);
                item.Entry.TemplateUpdatedAt = DateTime.Now;
                return WorkspaceConfig.SaveToFile(item.Path, item.Entry);
            }, false);

        public static bool Delete(Item item)
            => WithLock(() =>
            {
                if (File.Exists(item.Path)) File.Delete(item.Path);
                return true;
            }, false);

        public static Item? Import(string sourcePath, string fallbackName)
            => WithLock<Item?>(() =>
            {
                var entry = WorkspaceConfig.LoadFromFile(sourcePath);
                if (entry?.Layout == null) return null;
                Directory.CreateDirectory(Dir);
                var existing = ListCore();
                int order = existing.Count == 0 ? 1 : existing.Max(x => x.Order) + 1;
                if (order <= 0) order = existing.Count + 1;
                var baseName = string.IsNullOrWhiteSpace(entry.TemplateName)
                    ? fallbackName
                    : entry.TemplateName.Trim();
                var now = DateTime.Now;
                entry.TemplateId = Guid.NewGuid().ToString("N");
                entry.TemplateName = MakeUniqueName(baseName, existing, null);
                entry.TemplateOrder = order;
                entry.TemplateCreatedAt = now;
                entry.TemplateUpdatedAt = now;
                WorkspaceConfig.MarkAllTabsFrozen(entry);
                var path = System.IO.Path.Combine(
                    Dir, $"template-{entry.TemplateId}{WorkspaceConfig.TemplateExtension}");
                if (!WorkspaceConfig.SaveToFile(path, entry)) return null;
                return new Item
                {
                    Path = path,
                    Name = entry.TemplateName,
                    Order = order,
                    UpdatedAt = now,
                    Entry = entry
                };
            }, null);

        public static bool Export(Item item, string destinationPath)
            => WorkspaceConfig.SaveToFile(destinationPath, item.Entry);

        private static string MakeUniqueName(string requested, List<Item> existing, string? exceptPath)
        {
            var used = new HashSet<string>(
                existing.Where(x => !string.Equals(x.Path, exceptPath, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Name),
                StringComparer.CurrentCultureIgnoreCase);
            if (!used.Contains(requested)) return requested;
            for (int i = 2; ; i++)
            {
                var candidate = $"{requested} ({i})";
                if (!used.Contains(candidate)) return candidate;
            }
        }
    }
}
