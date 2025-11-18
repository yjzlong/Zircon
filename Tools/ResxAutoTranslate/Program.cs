using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

class Program
{
    static string RootDir = Directory.GetCurrentDirectory();
    static string? ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    // 简单术语表：英文 => 中文固定说法
    static readonly Dictionary<string, string> Glossary = new(StringComparer.OrdinalIgnoreCase)
    {
        { "HP", "生命值" },
        { "MP", "魔法值" },
        { "Health", "生命值" },
        { "Mana", "魔法值" },
        { "Attack", "攻击" },
        { "Defense", "防御" },
        { "Accuracy", "命中" },
        { "Evasion", "闪避" },
        { "Critical", "暴击" },
        { "Drop rate", "爆率" },
        { "Drop", "掉落" },
        { "Gold", "金币" },
        { "Storage", "仓库" },
        { "Potion", "药水" },
        { "Red Potion", "红药" },
        { "Blue Potion", "蓝药" },
        { "Skill", "技能" },
        { "Quest", "任务" },
        { "Party", "组队" },
        { "Guild", "行会" },
        { "PK", "PK" },
        { "Respawn", "复活" },
        { "Cooldown", "冷却时间" }
    };

    static async Task Main(string[] args)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            Console.WriteLine("OPENAI_API_KEY 未设置，退出。");
            return;
        }

        Console.WriteLine($"ResxAutoTranslate running in: {RootDir}");

        var resxFiles = Directory.GetFiles(RootDir, "*.resx", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".zh-CN.resx", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!resxFiles.Any())
        {
            Console.WriteLine("未找到任何 .resx 文件。");
        }
        else
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            int totalTranslated = 0;

            foreach (var enResxPath in resxFiles)
            {
                var zhResxPath = GetZhCnResxPath(enResxPath);

                Console.WriteLine($"\n处理：{enResxPath}");
                Console.WriteLine($"对应中文：{zhResxPath}");

                var toTranslate = LoadDiff(enResxPath, zhResxPath);

                if (toTranslate.Count == 0)
                {
                    Console.WriteLine("  没有需要翻译的新键，跳过。");
                    continue;
                }

                Console.WriteLine($"  需要翻译 {toTranslate.Count} 条。");

                var translatedDict = new Dictionary<string, string>();

                foreach (var kv in toTranslate)
                {
                    var key = kv.Key;
                    var en = kv.Value;

                    Console.WriteLine($"    翻译 [{key}] = {en}");

                    try
                    {
                        var zh = await TranslateTextAsync(http, en);
                        zh = ApplyGlossary(zh);
                        translatedDict[key] = zh;
                        Console.WriteLine($"      -> {zh}");
                        totalTranslated++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"      翻译失败：{ex.Message}");
                    }
                }

                if (translatedDict.Count > 0)
                {
                    UpdateZhResx(enResxPath, zhResxPath, translatedDict);
                    Console.WriteLine($"  写入 {translatedDict.Count} 条到 {zhResxPath}");
                }
            }

            Console.WriteLine($"\n翻译完成，本次共翻译 {totalTranslated} 条。");
        }

        // ① 统计汉化进度
        try
        {
            ComputeTranslationStats();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"统计汉化进度时出错：{ex.Message}");
        }

        // ② 扫描硬编码英文字符串
        try
        {
            ScanHardcodedEnglish();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"扫描硬编码英文时出错：{ex.Message}");
        }

        Console.WriteLine("\n全部任务执行完毕。");
    }

    static string GetZhCnResxPath(string enResxPath)
    {
        var dir = Path.GetDirectoryName(enResxPath)!;
        var file = Path.GetFileNameWithoutExtension(enResxPath);
        var zhFile = file + ".zh-CN.resx";
        return Path.Combine(dir, zhFile);
    }

    static Dictionary<string, string> LoadDiff(string enResxPath, string zhResxPath)
    {
        var result = new Dictionary<string, string>();

        var enDict = LoadResx(enResxPath);
        var zhDict = File.Exists(zhResxPath) ? LoadResx(zhResxPath) : new Dictionary<string, string>();

        foreach (var kv in enDict)
        {
            var key = kv.Key;
            var enVal = kv.Value?.Trim();

            if (string.IsNullOrEmpty(enVal)) continue;

            if (!zhDict.TryGetValue(key, out var zhVal) || string.IsNullOrWhiteSpace(zhVal))
            {
                result[key] = enVal;
            }
        }

        return result;
    }

    static Dictionary<string, string> LoadResx(string path)
    {
        var dict = new Dictionary<string, string>();

        var xdoc = XDocument.Load(path);
        var root = xdoc.Root;
        if (root == null) return dict;

        foreach (var data in root.Elements("data"))
        {
            var nameAttr = data.Attribute("name");
            if (nameAttr == null) continue;

            var valueElem = data.Element("value");
            if (valueElem == null) continue;

            dict[nameAttr.Value] = valueElem.Value ?? "";
        }

        return dict;
    }

    static void UpdateZhResx(string enResxPath, string zhResxPath, Dictionary<string, string> newTranslations)
    {
        if (!File.Exists(zhResxPath))
        {
            File.Copy(enResxPath, zhResxPath);
        }

        var xdoc = XDocument.Load(zhResxPath);
        var root = xdoc.Root ?? throw new Exception("Invalid resx file");

        foreach (var kv in newTranslations)
        {
            var key = kv.Key;
            var zhVal = kv.Value;

            var dataElem = root.Elements("data")
                .FirstOrDefault(e => (string?)e.Attribute("name") == key);

            if (dataElem == null)
            {
                dataElem = new XElement("data",
                    new XAttribute("name", key),
                    new XAttribute(XNamespace.Xml + "space", "preserve"),
                    new XElement("value", zhVal)
                );
                root.Add(dataElem);
            }
            else
            {
                var valueElem = dataElem.Element("value");
                if (valueElem == null)
                {
                    valueElem = new XElement("value", zhVal);
                    dataElem.Add(valueElem);
                }
                else
                {
                    valueElem.Value = zhVal;
                }
            }
        }

        xdoc.Save(zhResxPath);
    }

    static async Task<string> TranslateTextAsync(HttpClient http, string text)
    {
        // 把术语表写进提示词，尽量遵守固定翻译
        var glossaryLines = string.Join("\n", Glossary.Select(kv => $"{kv.Key} -> {kv.Value}"));
        var systemPrompt =
            "You are a translation engine for a Chinese Legend of Mir 3 game client.\n" +
            "Translate the given English UI/game strings into Simplified Chinese.\n" +
            "Rules:\n" +
            "- Output ONLY the translated Chinese text, no explanations.\n" +
            "- Keep placeholders / variables (like {0}, {1}, %d, %s, {Name}) unchanged.\n" +
            "- Do not add extra punctuation that is not in the original unless needed for Chinese grammar.\n" +
            "- Use the following glossary strictly when applicable:\n" +
            glossaryLines;

        var req = new
        {
            model = "gpt-4.1-mini",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = text }
            }
        };

        var json = JsonSerializer.Serialize(req);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await http.PostAsync("https://api.openai.com/v1/chat/completions", content);
        resp.EnsureSuccessStatusCode();

        var respJson = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(respJson);
        var root = doc.RootElement;

        var msg = root.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        return msg.Trim();
    }

    static string ApplyGlossary(string zh)
    {
        // 简单替换，保证术语统一（防止模型偶尔翻成别的词）
        foreach (var kv in Glossary)
        {
            // 如果中文里不包含目标词，但包含可能的英文/简写，可以按需替换
            // 这里为了安全，只对目标中文再做一次“规范化”，避免未来脚本重命名用
            // （现在先保留为占位，方便以后扩展）
        }
        return zh;
    }

    /// <summary>
    /// ① 统计所有 resx 的汉化进度，输出到 docs/i18n-status.md
    /// </summary>
    static void ComputeTranslationStats()
    {
        Console.WriteLine("\n开始统计汉化进度……");

        var allEnResx = Directory.GetFiles(RootDir, "*.resx", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".zh-CN.resx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p)
            .ToList();

        int totalKeys = 0;
        int translatedKeys = 0;

        var lines = new List<string>
        {
            "# Zircon 汉化进度报告",
            "",
            $"生成时间：{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} (UTC)",
            "",
            "| 文件 | 总条目 | 已翻译 | 完成度 |",
            "|------|--------|--------|--------|"
        };

        foreach (var enResx in allEnResx)
        {
            var zhResx = GetZhCnResxPath(enResx);

            var enDict = LoadResx(enResx);
            var zhDict = File.Exists(zhResx) ? LoadResx(zhResx) : new Dictionary<string, string>();

            int fileTotal = enDict.Count;
            int fileTranslated = enDict.Count(kv =>
                zhDict.TryGetValue(kv.Key, out var zhVal) &&
                !string.IsNullOrWhiteSpace(zhVal));

            totalKeys += fileTotal;
            translatedKeys += fileTranslated;

            double percent = fileTotal == 0 ? 100.0 : fileTranslated * 100.0 / fileTotal;
            var shortName = GetRepoRelativePath(enResx);

            lines.Add($"| `{shortName}` | {fileTotal} | {fileTranslated} | {percent:F1}% |");
        }

        lines.Add("");
        double totalPercent = totalKeys == 0 ? 100.0 : translatedKeys * 100.0 / totalKeys;
        lines.Add($"**总计：** {translatedKeys}/{totalKeys} （完成度 {totalPercent:F1}%）");

        var docsDir = Path.Combine(RootDir, "docs");
        Directory.CreateDirectory(docsDir);
        var outPath = Path.Combine(docsDir, "i18n-status.md");
        File.WriteAllLines(outPath, lines, Encoding.UTF8);

        Console.WriteLine($"汉化进度已写入：{outPath}");
    }

    /// <summary>
    /// ② 扫描 .cs 中的硬编码英文字符串，输出到 docs/i18n-hardcoded-strings.txt
    /// </summary>
    static void ScanHardcodedEnglish()
    {
        Console.WriteLine("\n开始扫描硬编码英文字符串……");

        var csFiles = Directory.GetFiles(RootDir, "*.cs", SearchOption.AllDirectories)
            .Where(p =>
                !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                !p.Contains(Path.DirectorySeparatorChar + "Tools" + Path.DirectorySeparatorChar))
            .OrderBy(p => p)
            .ToList();

        var stringLiteralRegex = new Regex("\"([^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"", RegexOptions.Compiled);
        var report = new List<string>();
        int hitCount = 0;
        const int MaxHits = 500;

        foreach (var file in csFiles)
        {
            var relPath = GetRepoRelativePath(file);
            var lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                foreach (Match m in stringLiteralRegex.Matches(line))
                {
                    var text = m.Groups[1].Value;

                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (!HasAsciiLetters(text)) continue;
                    if (ContainsChinese(text)) continue;
                    if (text.Length < 3) continue; // 太短就忽略

                    report.Add($"{relPath}({i + 1}): \"{text}\"");
                    hitCount++;

                    if (hitCount >= MaxHits)
                        goto Done;
                }
            }
        }

    Done:
        var docsDir = Path.Combine(RootDir, "docs");
        Directory.CreateDirectory(docsDir);
        var outPath = Path.Combine(docsDir, "i18n-hardcoded-strings.txt");
        if (report.Count == 0)
        {
            report.Add("未检测到明显的硬编码英文字符串。（仅简单规则，可能有漏检）");
        }
        File.WriteAllLines(outPath, report, Encoding.UTF8);

        Console.WriteLine($"硬编码英文扫描结果已写入：{outPath}");
    }

    static bool HasAsciiLetters(string s) =>
        s.Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));

    static bool ContainsChinese(string s) =>
        s.Any(c => c >= '\u4e00' && c <= '\u9fff');

    static string GetRepoRelativePath(string fullPath)
    {
        if (!fullPath.StartsWith(RootDir))
            return fullPath;
        var rel = fullPath.Substring(RootDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
