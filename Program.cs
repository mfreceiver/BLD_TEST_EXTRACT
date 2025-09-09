using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

public class AppConfig
{
    public int PollingIntervalSeconds { get; set; }
    public string SourceDir { get; set; }
    public string ArchiveDir { get; set; }
    public string LogDir { get; set; }
    public string ResultCsv { get; set; }
}

public static class RegexPatterns
{
    public const string Barcode = @"P\|1\|\|(.*?)\|";
    public const string SendTime = @"H\|.*\|\|(\d{14})";
    public const string ResultTime = @"O\|1\|\|.*?\|.*?\|.*?\|(\d{14})";
    public const string TestTypeAbScreening = @"Result\^(\w+)\^Ab\.screening";
    public const string ResultAbScreening = @"Result\^CN15B\^.*?\|\^\^(.+?)\^";
    public const string TestTypeBloodgroup = @"Result\^(\w+)\^Bloodgr";
    public const string ResultBloodgroup = @"Result\^MO31X\^.*?\|(.*?)\^(.+?)\^";
}

public class Program
{
    private static AppConfig _config;
    private static Timer _timer;

    public static void Main(string[] args)
    {
        Console.WriteLine("文件处理应用已启动...");
        Console.WriteLine("输入 'exit' 或 'quit' 来退出程序。");

        if (!LoadConfig())
        {
            Console.WriteLine("无法读取配置，应用将退出。");
            Console.WriteLine("按任意键退出...");
            Console.ReadKey();
            return;
        }

        CheckDirectories();

        _timer = new Timer(DoWork, null, 0, _config.PollingIntervalSeconds * 1000);

        Console.WriteLine("程序正在运行中，输入 'exit' 或 'quit' 来退出程序。");

        // 循环等待用户输入退出命令
        while (true)
        {
            string input = Console.ReadLine();
            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        // 停止计时器
        _timer.Dispose();
        Console.WriteLine("程序已退出。");
    }

    private static void DoWork(object state)
    {
        try
        {
            Log($"[{DateTime.Now:HH:mm:ss}] 开始轮询...");

            string[] files = Directory.GetFiles(_config.SourceDir, "*.upl");
            Log($"[{DateTime.Now:HH:mm:ss}] 发现 {files.Length} 个文件。");

            foreach (string file in files)
            {
                ProcessFile(file);
            }

            Log($"[{DateTime.Now:HH:mm:ss}] 本次轮询完成，将在 {_config.PollingIntervalSeconds} 秒后再次轮询。");
        }
        catch (Exception ex)
        {
            Log($"[{DateTime.Now:HH:mm:ss}] 发生错误: {ex.Message}");
        }
    }

    private static void ProcessFile(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        DateTime startTime = DateTime.Now;
        Log($"[{DateTime.Now:HH:mm:ss}] 开始解析文件: {fileName}");

        try
        {
            string fileContent = File.ReadAllText(filePath);

            string barcode = GetMatch(fileContent, RegexPatterns.Barcode);
            string sendTime = GetMatch(fileContent, RegexPatterns.SendTime);
            string resultTime = GetMatch(fileContent, RegexPatterns.ResultTime);

            EnsureCsvHeader();

            List<string> commonInfo = new List<string>
            {
                fileName,
                barcode,
                sendTime,
                resultTime
            };

            // 处理抗体筛查结果
            string testTypeAb = GetMatch(fileContent, RegexPatterns.TestTypeAbScreening);
            if (!string.IsNullOrEmpty(testTypeAb))
            {
                string abResult = GetMatch(fileContent, RegexPatterns.ResultAbScreening);
                string resultValue = string.IsNullOrEmpty(abResult) ? "NA" : abResult;
                string csvLine = $"{string.Join(",", commonInfo)},{testTypeAb},{resultValue}";
                File.AppendAllText(_config.ResultCsv, csvLine + Environment.NewLine);
            }

            // 处理血型结果
            string testTypeBg = GetMatch(fileContent, RegexPatterns.TestTypeBloodgroup);
            if (!string.IsNullOrEmpty(testTypeBg))
            {
                Match match = Regex.Match(fileContent, RegexPatterns.ResultBloodgroup);
                string resultValue = "NA";
                if (match.Success && match.Groups.Count >= 3)
                {
                    resultValue = $"{match.Groups[1].Value}|{match.Groups[2].Value}";
                }
                string csvLine = $"{string.Join(",", commonInfo)},{testTypeBg},{resultValue}";
                File.AppendAllText(_config.ResultCsv, csvLine + Environment.NewLine);
            }

            // 如果都没有，也需要写入一行NA
            if (string.IsNullOrEmpty(testTypeAb) && string.IsNullOrEmpty(testTypeBg))
            {
                string csvLine = $"{string.Join(",", commonInfo)},NA,NA";
                File.AppendAllText(_config.ResultCsv, csvLine + Environment.NewLine);
            }

            string destPath = Path.Combine(_config.ArchiveDir, fileName);
            File.Move(filePath, destPath);

            DateTime endTime = DateTime.Now;
            Log($"[{DateTime.Now:HH:mm:ss}] 完成解析文件: {fileName}, 耗时: {(endTime - startTime).TotalSeconds:F2}秒");
        }
        catch (Exception ex)
        {
            Log($"[{DateTime.Now:HH:mm:ss}] 解析文件 {fileName} 失败: {ex.Message}");
        }
    }

    private static void EnsureCsvHeader()
    {
        if (!File.Exists(_config.ResultCsv))
        {
            string header = "FILENAME,REQ_NO,SEND_TIME,RESULT_TIME,TEST_NAME,TEST_RESULT";
            File.WriteAllText(_config.ResultCsv, header + Environment.NewLine);
        }
    }


    private static string GetMatch(string text, string pattern)
    {
        Match match = Regex.Match(text, pattern, RegexOptions.Singleline);
        return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value : string.Empty;
    }

    private static bool LoadConfig()
    {
        _config = new AppConfig();
        try
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            
            if (!File.Exists(configPath))
            {
                string defaultConfig = "[CONFIG]\r\nPOLLING_INTERVAL_SECONDS=60\r\nSOURCE_DIR=./SOURCE_DIR\r\nARCHIVE_DIR=./ARCHIVE_DIR\r\nLOG_DIR=./LOG_DIR\r\nRESULT_CSV=./BLD_RESULT.csv";
                File.WriteAllText(configPath, defaultConfig);
                Console.WriteLine($"未找到 config.ini 文件，已自动创建默认配置文件。");
            }

            string[] lines = File.ReadAllLines(configPath);
            foreach (var line in lines)
            {
                if (line.StartsWith("POLLING_INTERVAL_SECONDS")) _config.PollingIntervalSeconds = int.Parse(line.Split('=')[1].Trim());
                if (line.StartsWith("SOURCE_DIR")) _config.SourceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, line.Split('=')[1].Trim().Replace("./", ""));
                if (line.StartsWith("ARCHIVE_DIR")) _config.ArchiveDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, line.Split('=')[1].Trim().Replace("./", ""));
                if (line.StartsWith("LOG_DIR")) _config.LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, line.Split('=')[1].Trim().Replace("./", ""));
                if (line.StartsWith("RESULT_CSV")) _config.ResultCsv = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, line.Split('=')[1].Trim().Replace("./", ""));
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"读取config.ini失败: {ex.Message}");
            return false;
        }
    }

    private static void CheckDirectories()
    {
        if (!Directory.Exists(_config.SourceDir)) Directory.CreateDirectory(_config.SourceDir);
        if (!Directory.Exists(_config.ArchiveDir)) Directory.CreateDirectory(_config.ArchiveDir);
        if (!Directory.Exists(_config.LogDir)) Directory.CreateDirectory(_config.LogDir);
    }

    private static void Log(string message)
    {
        string logFileName = Path.Combine(_config.LogDir, $"log_{DateTime.Now:yyyy-MM-dd}.log");
        File.AppendAllText(logFileName, message + Environment.NewLine);
        Console.WriteLine(message);
    }
}