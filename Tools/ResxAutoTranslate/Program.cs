using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

class Program
{
    // 仓库根目录，CI 中默认就是工作目录
    static string RootDir = Directory.GetCurrentDirectory();

    // 从环境变量读取 OpenAI API Key
    static string? ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    static async Task Main(string[] args)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            Console.WriteLine("OPENAI_API_KEY 未设置，退出。");
            return;
        }

        Console.WriteLine($"ResxAutoTranslate running in: {RootDir}");

        // 找到所有英文 resx（排除 zh-CN）
        var resxFiles = Directory.GetFiles(RootDir, "*.resx", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".zh-CN.resx", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!resxFiles.Any())
        {
            Console.WriteLine("未找到任何 .resx 文件。");
            return;
        }

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

            // 逐条翻译（量不大，简单写）
            var translatedDict = new Dictionary<string, string>();
            foreach (var kv in toTranslate)
            {
                var key = kv.Key;
                var en = kv.Value;

                Console.WriteLine($"    翻译 [{key}] = {en}");

                try
                {
                    var zh = await TranslateTextAsync(http, en);
                    translatedDict[key] = zh;
                    Console.WriteLine($"      -> {zh}");
                    totalTranslated++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      翻译失败：{ex.Message}");
                }
            }

            // 写入 zh-CN resx
            if (translatedDict.Count > 0)
            {
                UpdateZhResx(enResxPath, zhResxPath, translatedDict);
                Console.WriteLine($"  写入 {translatedDict.Count} 条到 {zhResxPath}");
            }
        }

        Console.WriteLine($"\n完成。本次共翻译 {totalTranslated} 条。");
    }

    /// <summary>
    /// 生成对应的 zh-CN 文件路径
    /// </summary>
    static string GetZhCnResxPath(string enResxPath)
    {
        var dir = Path.GetDirectoryName(enResxPath)!;
        var file = Path.GetFileNameWithoutExtension(enResxPath); // e.g. Strings
        var zhFile = file + ".zh-CN.resx";
        return Path.Combine(dir, zhFile);
    }

    /// <summary>
    /// 读取英文和中文 resx，找出“需要翻译”的 key：中文不存在 或 中文为空
    /// </summary>
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

    /// <summary>
    /// 读取 resx 为字典
    /// </summary>
    static Dictionary<string, string> LoadResx(string path)
    {
        var dict = new Dictionary<string, string>();

        using var reader = new ResXResourceReader(path);
        reader.UseResXDataNodes = true;

        foreach (System.Collections.DictionaryEntry entry in reader)
        {
            var node = (ResXDataNode)entry.Value;
            var value = node.GetValue((ITypeResolutionService?)null)?.ToString() ?? "";
            dict[entry.Key.ToString()!] = value;
        }

        return dict;
    }

    /// <summary>
    /// 把翻译结果写回 zh-CN resx（保持其他键不变）
    /// </summary>
    static void UpdateZhResx(string enResxPath, string zhResxPath, Dictionary<string, string> newTranslations)
    {
        // 如果 zh 不存在，则先复制一份 en 的结构
        if (!File.Exists(zhResxPath))
        {
            File.Copy(enResxPath, zhResxPath);
        }

        var xdoc = XDocument.Load(zhResxPath);
        var root = xdoc.Root ?? throw new Exception("Invalid resx file");

        // data 元素是 <data name="Key"><value>Text</value></data>
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

    /// <summary>
    /// 调用 OpenAI Chat Completions 接口，把英文翻译成简体中文
    /// </summary>
    static async Task<string> TranslateTextAsync(HttpClient http, string text)
    {
        var req = new
        {
            model = "gpt-4.1-mini",
            messages = new[]
            {
                new { role = "system", content = "You are a translation engine. Translate English UI/game text into Simplified Chinese. Output ONLY the translated text, no explanations." },
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
}
