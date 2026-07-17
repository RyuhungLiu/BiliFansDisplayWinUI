using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BiliFansDisplay;

internal sealed class AppSettings
{
    private const string AppDataFolderName = "BiliFansDisplay";

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppDataFolderName,
        "settings.ini");

    public string BilibiliUrl { get; set; } = string.Empty;

    public string YouTubeUrl { get; set; } = string.Empty;

    public static AppSettings LoadOrCreate(long? legacyUid)
    {
        if (!File.Exists(SettingsPath))
        {
            var newSettings = new AppSettings
            {
                BilibiliUrl = legacyUid is > 0
                    ? $"https://space.bilibili.com/{legacyUid.Value.ToString(CultureInfo.InvariantCulture)}"
                    : string.Empty
            };
            newSettings.Save();
            return newSettings;
        }

        try
        {
            var settings = new AppSettings();
            string section = string.Empty;

            foreach (string rawLine in File.ReadLines(SettingsPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                int separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = line[..separatorIndex].Trim();
                string value = line[(separatorIndex + 1)..].Trim();
                if (!key.Equals("Url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (section.Equals("Bilibili", StringComparison.OrdinalIgnoreCase))
                {
                    settings.BilibiliUrl = value;
                }
                else if (section.Equals("YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    settings.YouTubeUrl = value;
                }
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string content = $"""
            ; 修改链接并保存，然后在托盘菜单中选择“重新加载配置”。
            [Bilibili]
            Url={Sanitize(BilibiliUrl)}

            [YouTube]
            Url={Sanitize(YouTubeUrl)}
            """;
        File.WriteAllText(SettingsPath, content, new UTF8Encoding(false));
    }

    public static bool TryGetBilibiliUid(string value, out long uid)
    {
        uid = 0;
        string input = value.Trim();
        if (long.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out uid) && uid > 0)
        {
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri)
            || !uri.Host.Equals("space.bilibili.com", StringComparison.OrdinalIgnoreCase))
        {
            uid = 0;
            return false;
        }

        foreach (string segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out uid) && uid > 0)
            {
                return true;
            }
        }

        uid = 0;
        return false;
    }

    public static string? NormalizeYouTubeUrl(string value)
    {
        string input = value.Trim();
        if (input.StartsWith('@'))
        {
            input = $"https://www.youtube.com/{input}";
        }
        else if (!input.Contains("://", StringComparison.Ordinal) && input.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            input = $"https://{input}";
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        bool isYouTubeHost = uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
        if (!isYouTubeHost)
        {
            return null;
        }
        string path = uri.AbsolutePath.TrimEnd('/');
        bool isChannelPath = path.StartsWith("/@", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/c/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/user/", StringComparison.OrdinalIgnoreCase);

        if (!isChannelPath)
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string Sanitize(string value)
    {
        return value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
    }
}
