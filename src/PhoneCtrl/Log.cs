using System;
using System.IO;

namespace PhoneCtrl
{
    /// <summary>Tiny file logger: %LOCALAPPDATA%\PhoneCtrl\phonectrl.log</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string Folder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneCtrl");

        public static string FilePath { get; } = Path.Combine(Folder, "phonectrl.log");

        public static void Info(string message) => Write("INFO ", message);

        public static void Error(string message, Exception? ex = null) =>
            Write("ERROR", ex == null ? message : $"{message}: {ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 1_000_000)
                        File.Move(FilePath, FilePath + ".old", overwrite: true);
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }
}
