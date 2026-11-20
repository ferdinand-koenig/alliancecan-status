using System;
using System.IO;

namespace AllianceStatus.Services;

/// <summary>Tiny size-capped log at %LOCALAPPDATA%\AllianceStatus\log.txt.</summary>
public static class Log
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(StateStore.Folder, "log.txt");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(StateStore.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 256 * 1024) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
