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
