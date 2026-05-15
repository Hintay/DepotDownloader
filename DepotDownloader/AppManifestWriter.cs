// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DepotDownloader
{
    sealed class SteamAppManifestDepot(uint depotId, ulong manifestId)
    {
        public uint DepotId { get; } = depotId;
        public ulong ManifestId { get; } = manifestId;
    }

    sealed class SteamAppManifest(
        uint appId,
        string name,
        string installDir,
        uint buildId,
        string language,
        IReadOnlyCollection<SteamAppManifestDepot> depots)
    {
        public uint AppId { get; } = appId;
        public string Name { get; } = name;
        public string InstallDir { get; } = installDir;
        public uint BuildId { get; } = buildId;
        public string Language { get; } = language;
        public IReadOnlyCollection<SteamAppManifestDepot> Depots { get; } = depots;
    }

    static class AppManifestWriter
    {
        public static void WriteToFile(string path, SteamAppManifest manifest)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, BuildContent(manifest), Encoding.UTF8);
        }

        static string BuildContent(SteamAppManifest manifest)
        {
            var builder = new StringBuilder();

            builder.AppendLine("\"AppState\"");
            builder.AppendLine("{");
            AppendKeyValue(builder, 1, "appid", manifest.AppId.ToString());
            AppendKeyValue(builder, 1, "universe", "1");
            AppendKeyValue(builder, 1, "name", manifest.Name);
            AppendKeyValue(builder, 1, "installdir", manifest.InstallDir);

            if (manifest.BuildId != 0)
            {
                AppendKeyValue(builder, 1, "buildid", manifest.BuildId.ToString());
                AppendKeyValue(builder, 1, "TargetBuildID", manifest.BuildId.ToString());
            }

            AppendBlockStart(builder, 1, "InstalledDepots");

            foreach (var depot in manifest.Depots.OrderBy(depot => depot.DepotId))
            {
                AppendBlockStart(builder, 2, depot.DepotId.ToString());
                AppendKeyValue(builder, 3, "manifest", depot.ManifestId.ToString());
                AppendBlockEnd(builder, 2);
            }

            AppendBlockEnd(builder, 1);
            // AppendLanguageBlock(builder, "UserConfig", manifest.Language);
            // AppendLanguageBlock(builder, "MountedConfig", manifest.Language);
            builder.AppendLine("}");

            return builder.ToString();
        }

        static void AppendLanguageBlock(StringBuilder builder, string name, string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return;
            }

            AppendBlockStart(builder, 1, name);
            AppendKeyValue(builder, 2, "language", language);
            AppendBlockEnd(builder, 1);
        }

        static void AppendBlockStart(StringBuilder builder, int indent, string name)
        {
            AppendIndent(builder, indent);
            builder.Append('"').Append(Escape(name)).AppendLine("\"");
            AppendIndent(builder, indent);
            builder.AppendLine("{");
        }

        static void AppendBlockEnd(StringBuilder builder, int indent)
        {
            AppendIndent(builder, indent);
            builder.AppendLine("}");
        }

        static void AppendKeyValue(StringBuilder builder, int indent, string key, string value)
        {
            AppendIndent(builder, indent);
            builder.Append('"').Append(Escape(key)).Append("\"\t\t\"").Append(Escape(value ?? string.Empty)).AppendLine("\"");
        }

        static void AppendIndent(StringBuilder builder, int indent)
        {
            builder.Append('\t', indent);
        }

        static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
