using CommandLine;
using ResxScanner;

public class Program
{


    public static void Main(string[] args)
    {
        var res = Parser.Default.ParseArguments<Options>(args).WithParsed(async options =>
        {
            await Scanner.ScanAsync(options);
        });
    }
}