namespace PdfOcrRenamer;

public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string message, Exception exc);
}

public class ConsoleLogger : ILogger
{
    public void Info(string message) => Console.WriteLine(message);
    public void Warn(string message) => Console.WriteLine(message);
    public void Error(string message) => Console.WriteLine(message);
    public void Error(string message, Exception exc) => Console.WriteLine($"{message}: {exc}");
}

public sealed class FileLogger : ILogger
{
    private readonly object _lock = new();
    private readonly string _path;

    public FileLogger(string path)
    {
        _path = path;
    }

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            using var writer = new StreamWriter(_path, append: true);
            writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} main: {message}");
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARNING", message);
    public void Error(string message) => Write("ERROR", message);
    public void Error(string message, Exception exc) => Write("ERROR", $"{message}: {exc}");
}
