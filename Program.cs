using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

public class AppConfig
{
    public int PollingIntervalSeconds { get; set; }
    public string[] SourceDirs { get; set; }
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
    // TODO: 请将以下连接字符串替换为你的Oracle数据库信息
    private const string _oracleConnStr = "Data Source=YourDataSource;User Id=YourUserId;Password=YourPassword;";

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

            int totalFilesProcessed = 0;
            foreach (var sourceDir in _config.SourceDirs)
            {
                string[] files = Directory.GetFiles(sourceDir, "*.upl");
                totalFilesProcessed += files.Length;

                foreach (string file in files)
                {
                    ProcessFile(file);
                }
            }

            Log($"[{DateTime.Now:HH:mm:ss}] 本次轮询处理了 {totalFilesProcessed} 个文件，将在 {_config.PollingIntervalSeconds} 秒后再次轮询。");
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
                WriteToOracle(fileName, barcode, sendTime, resultTime, testTypeAb, resultValue);
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
                WriteToOracle(fileName, barcode, sendTime, resultTime, testTypeBg, resultValue);
            }

            // 如果都没有，也需要写入一行NA
            if (string.IsNullOrEmpty(testTypeAb) && string.IsNullOrEmpty(testTypeBg))
            {
                string csvLine = $"{string.Join(",", commonInfo)},NA,NA";
                File.AppendAllText(_config.ResultCsv, csvLine + Environment.NewLine);
                WriteToOracle(fileName, barcode, sendTime, resultTime, "NA", "NA");
            }

            string todayDir = DateTime.Now.ToString("yyyy-MM-dd");
            string destDir = Path.Combine(_config.ArchiveDir, todayDir);
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }
            string destPath = Path.Combine(destDir, fileName);
            File.Move(filePath, destPath);

            DateTime endTime = DateTime.Now;
            Log($"[{DateTime.Now:HH:mm:ss}] 完成解析文件: {fileName}, 耗时: {(endTime - startTime).TotalSeconds:F2}秒");
        }
        catch (Exception ex)
        {
            Log($"[{DateTime.Now:HH:mm:ss}] 解析文件 {fileName} 失败: {ex.Message}");
        }
    }

    private static void WriteToOracle(string fileName, string reqNo, string sendTime, string resultTime, string testName, string testResult)
    {
        using (OracleConnection conn = new OracleConnection(_oracleConnStr))
        {
            try
            {
                conn.Open();
                string sql = "INSERT INTO zshis.bld_check_result (FILENAME, REQ_NO, SEND_TIME, RESULT_TIME, TEST_NAME, TEST_RESULT, CREATE_TIME) VALUES (:filename, :req_no, :send_time, :result_time, :test_name, :test_result, :create_time)";
                using (OracleCommand cmd = new OracleCommand(sql, conn))
                {
                    cmd.Parameters.Add(new OracleParameter("filename", fileName));
                    cmd.Parameters.Add(new OracleParameter("req_no", reqNo));
                    cmd.Parameters.Add(new OracleParameter("send_time", ParseToOracleTimestamp(sendTime)));
                    cmd.Parameters.Add(new OracleParameter("result_time", ParseToOracleTimestamp(resultTime)));
                    cmd.Parameters.Add(new OracleParameter("test_name", testName));
                    cmd.Parameters.Add(new OracleParameter("test_result", testResult));
                    cmd.Parameters.Add(new OracleParameter("create_time", DateTime.Now));
                    cmd.ExecuteNonQuery();
                }
                Log($"[{DateTime.Now:HH:mm:ss}] 数据已成功写入 Oracle 数据库。");
            }
            catch (Exception ex)
            {
                Log($"[{DateTime.Now:HH:mm:ss}] 写入 Oracle 数据库失败: {ex.Message}");
            }
        }
    }

    private static DateTime ParseToOracleTimestamp(string timestampStr)
    {
        // 格式为 YYYYMMDDHHMMSS
        return DateTime.ParseExact(timestampStr, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
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
                string defaultConfig = "[CONFIG]\r\nPOLLING_INTERVAL_SECONDS=60\r\nSOURCE_DIRS=./SOURCE_DIR\r\nARCHIVE_DIR=./ARCHIVE_DIR\r\nLOG_DIR=./LOG_DIR\r\nRESULT_CSV=./BLD_RESULT.csv";
                File.WriteAllText(configPath, defaultConfig);
                Console.WriteLine($"未找到 config.ini 文件，已自动创建默认配置文件。");
            }

            string[] lines = File.ReadAllLines(configPath);
            foreach (var line in lines)
            {
                if (line.StartsWith("POLLING_INTERVAL_SECONDS")) _config.PollingIntervalSeconds = int.Parse(line.Split('=')[1].Trim());
                if (line.StartsWith("SOURCE_DIRS"))
                {
                    _config.SourceDirs = line.Split('=')[1].Trim().Split(',');
                    for (int i = 0; i < _config.SourceDirs.Length; i++)
                    {
                        _config.SourceDirs[i] = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.SourceDirs[i].Trim().Replace("./", ""));
                    }
                }
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
        foreach (var sourceDir in _config.SourceDirs)
        {
            if (!Directory.Exists(sourceDir)) Directory.CreateDirectory(sourceDir);
        }
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