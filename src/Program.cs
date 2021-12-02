using CommandLine;
using System.Threading.Tasks;

namespace NugetUtility
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var result = Parser.Default.ParseArguments<PackageOptions>(args);
            return await result.MapResult(
                CsAttributionCollector.Execute,
                errors => Task.FromResult(1));
        }
    }
}
