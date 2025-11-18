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

    // 术语表缓存
    static Dictionary<string, string> Glossary = new();

    static async Task Main(string[] args)
    {
        Console.WriteLine($"Working dir: {RootDir}");

        // 模式选择
        if (args.Length > 0 && args[0].Equals("scan-cs", StringComparison.OrdinalIgnoreCase))
        {
            ScanHardcodedEnglish();
            return;
        }

        // 默认模式 = 自动翻译 + 统计进度
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            Console.WriteLine("OPENAI_API_KEY 未设置，退出。");
            return;
        }

        LoadGlossary();

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        // 1. 自动翻译
        await AutoTranslateResxAsync(http);

        // 2. 统计进度
        GenerateI18nStats();
    }

    #region 1. 自动翻译 RESX

    static async Task AutoTranslateResxAsync(HttpClient http)
    {
        var resxFiles = Directory.GetFiles(RootDir, "*.resx", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".zh-CN.resx", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!resxFiles.Any())
        {
            Console.WriteLine("未找到任何 .resx 文件。");
            return;
        }

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
                    zh = ApplyGlossaryPostProcess(zh);
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

        Console.WriteLine($"\n自动翻译完成。本次共翻译 {totalTranslated} 条。");
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
        // 把术语表内容拼进 system prompt
        var glossaryLines = Glossary.Select(kv => $"{kv.Key} = {kv.Value}");
        var glossaryText = string.Join("\n", glossaryLines);

        var systemPrompt = "You are a translation engine. " +
                           "Translate English UI/game text into Simplified Chinese. " +
                           "Use the following FIXED glossary when possible:\n" +
                           glossaryText +
                           "\nOnly output the translated Chinese text, no explanations.";

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

    #endregion

    #region 2. 汉化进度统计

    static void GenerateI18nStats()
    {
        var resxFiles = Directory.GetFiles(RootDir, "*.resx", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".zh-CN.resx", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int totalKeys = 0;
        int translatedKeys = 0;

        foreach (var enPath in resxFiles)
        {
            var zhPath = GetZhCnResxPath(enPath);

            var enDict = LoadResx(enPath);
            var zhDict = File.Exists(zhPath) ? LoadResx(zhPath) : new Dictionary<string, string>();

            totalKeys += enDict.Count;

            foreach (var kv in enDict)
            {
                if (zhDict.TryGetValue(kv.Key, out var zhVal) && !string.IsNullOrWhiteSpace(zhVal))
                {
                    translatedKeys++;
                }
            }
        }

        double percent = totalKeys == 0 ? 100.0 : (translatedKeys * 100.0 / totalKeys);

        var lines = new List<string>
        {
            "# Zircon 汉化进度统计",
            "",
            $"- 统计时间：{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} (UTC)",
            $"- 总条目数：{totalKeys}",
            $"- 已翻译条目：{translatedKeys}",
            $"- 未翻译条目：{totalKeys - translatedKeys}",
            $"- 完成度：{percent:F2}%",
            "",
            "（本文件由 ResxAutoTranslate 工具自动生成）"
        };

        var outDir = Path.Combine(RootDir, "Localization");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "i18n_stats.md");
        File.WriteAllLines(outPath, lines, Encoding.UTF8);

        Console.WriteLine($"\n生成汉化统计：{outPath}");
    }

    #endregion

    #region 3. 术语表加载 & 后处理

    static void LoadGlossary()
    {
        var glossaryPath = Path.Combine(RootDir, "Tools", "ResxAutoTranslate", "Glossary.json");
        if (!File.Exists(glossaryPath))
        {
            Console.WriteLine("未找到 Glossary.json，将使用默认空术语表。");
            InitDefaultGlossary(glossaryPath);
        }

        try
        {
            var json = File.ReadAllText(glossaryPath, Encoding.UTF8);
            Glossary = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            Console.WriteLine($"已加载术语表 {Glossary.Count} 条。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载 Glossary.json 失败：{ex.Message}");
            Glossary = new();
        }
    }

    static void InitDefaultGlossary(string path)
    {
        var dict = new Dictionary<string, string>
        {
            { "HP", "生命值" },
            { "MP", "魔法值" },
            { "Health", "生命值" },
            { "Mana", "魔法值" },
            { "Gold", "金币" },
            { "Game Gold", "元宝" },
            { "Monster", "怪物" },
            { "Boss", "首领" },
            { "Potion", "药水" },
            { "Red Potion", "红药" },
            { "Blue Potion", "蓝药" },
            { "Accuracy", "命中" },
            { "Attack Speed", "攻击速度" },
            { "Drop Rate", "爆率" },
            { "Drop", "掉落" },
            { "Experience", "经验" },
            { "PK", "PK" },
        };

        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json, Encoding.UTF8);

        Console.WriteLine($"已创建默认 Glossary.json：{path}");
    }

    // 翻译结果的二次替换，确保术语表生效
    static string ApplyGlossaryPostProcess(string text)
    {
        foreach (var kv in Glossary)
        {
            // 简单处理：如果翻译结果里还带英文关键字，用对应中文替换
            if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
            {
                text = text.Replace(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase);
            }
        }

        return text;
    }

    #endregion

    #region 4. 扫描 .cs 硬编码英文

    static void ScanHardcodedEnglish()
    {
        var csFiles = Directory.GetFiles(RootDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains("Tools/ResxAutoTranslate")) // 排除工具自身
            .ToList();

        Console.WriteLine($"扫描 C# 文件数量：{csFiles.Count}");

        var results = new List<string>();
        var strRegex = new Regex("\"(.*?)\"", RegexOptions.Compiled);

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                foreach (Match m in strRegex.Matches(line))
                {
                    var content = m.Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    // 如果包含英文字母且不是明显路径/格式串，就认为是候选
                    if (Regex.IsMatch(content, "[A-Za-z]") &&
                        !content.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                        !content.Contains("{0") && !content.Contains("{1"))
                    {
                        results.Add($"{file}({i + 1}): \"{content}\"");
                    }
                }
            }
        }

        var outDir = Path.Combine(RootDir, "Localization");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "UnlocalizedStrings.txt");
        File.WriteAllLines(outPath, results, Encoding.UTF8);

        Console.WriteLine($"扫描完成，共发现疑似未本地化字符串 {results.Count} 条。");
        Console.WriteLine($"结果已写入：{outPath}");
    }

    #endregion
}
