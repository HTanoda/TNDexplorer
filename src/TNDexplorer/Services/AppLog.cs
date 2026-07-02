using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace TNDexplorer.Services
{
    /// <summary>
    /// アプリケーション共通のログ出力。
    /// 出力先: %LOCALAPPDATA%\TNDexplorer\logs\app-YYYYMMDD.log
    /// 書式: YYYY-MM-DD HH:MM:SS [LEVEL] module: メッセージ
    ///
    /// パスワードやトークンなどの機密情報はログに書き込まないこと。
    /// </summary>
    public static class AppLog
    {
        private static readonly object _lock = new object();
        private static readonly string _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TNDexplorer", "logs");

        public static void Info(string module, string message)
        {
            Write("INFO", module, message);
        }

        public static void Warn(string module, string message)
        {
            Write("WARN", module, message);
        }

        public static void Error(string module, string message)
        {
            Write("ERROR", module, message);
        }

        public static void Error(string module, string message, Exception ex)
        {
            // ex.ToString() は型名・メッセージ・InnerException・スタックトレースを全て含む
            string detail = ex != null ? message + " | " + ex : message;
            Write("ERROR", module, detail);
        }

        private static void Write(string level, string module, string message)
        {
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {module}: {message}{Environment.NewLine}";
                lock (_lock)
                {
                    if (!Directory.Exists(_logDir))
                        Directory.CreateDirectory(_logDir);

                    string path = Path.Combine(_logDir, $"app-{DateTime.Now:yyyyMMdd}.log");
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// logs フォルダ内の app-*.log のうち、14日より古いものを削除する。
        /// 起動時に Task.Run で1回呼び出す。失敗は無視する。
        /// </summary>
        public static void CleanupOldLogs()
        {
            try
            {
                if (!Directory.Exists(_logDir))
                    return;

                DateTime threshold = DateTime.Now.AddDays(-14);
                foreach (string file in Directory.GetFiles(_logDir, "app-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < threshold)
                            File.Delete(file);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
    }
}
