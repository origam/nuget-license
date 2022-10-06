using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NugetUtility;

namespace OrigamAttributionsGenerator
{
    class Program
    {
        public static async Task<int> Main(string[] args)
        {
            AttributionsConfig config = GetConfig();

            string attributionsFileName = "Attributions.txt";
            string attributionsCsFileName = "Attributions_cs.txt";
            string attributionsJsFileName = "Attributions_js.txt";
           
            await ExportCsAttributionsToFile(config, attributionsCsFileName);
            Console.WriteLine("C# attributions generated");
            
            await ExportJavascriptAttributionsToFile(config, attributionsJsFileName);
            Console.WriteLine("javascript attributions generated");

            MergeFiles(attributionsCsFileName, attributionsJsFileName,
                attributionsFileName);
            
            var destinations = new[]
            {
                Path.Combine(config.PathToOrigamBackEnd, "OrigamArchitect",
                    attributionsFileName),
                Path.Combine(config.PathToOrigamFrontEnd, "public",
                    attributionsFileName)
            };
            
            foreach (string destination in destinations)
            {
                File.Copy(attributionsFileName, destination, true);
                Console.WriteLine("Attributions file copied to: " + destination);
            }
            
            Console.WriteLine("Done!");
            return 0;
        }

        private static void MergeFiles(string attributionsCsFileName,
            string attributionsJsFileName, string attributionsFileName)
        {
            string csAttributions = File.ReadAllText(attributionsCsFileName);
            var javascriptAttributions = CleanJsAttributionsFile(attributionsJsFileName);
            
            string finalAttributions =
                javascriptAttributions + "\r\n\r\n" + csAttributions;
            
            File.WriteAllText(attributionsFileName, finalAttributions);
        }

        private static async Task ExportCsAttributionsToFile(AttributionsConfig config,
            string attributionsFileName)
        {
            PackageOptions packageOptions = new PackageOptions
            {
                ProjectDirectory =
                    Path.Combine(config.PathToOrigamBackEnd,
                        "Origam.sln"),
                OutputFileName = attributionsFileName,
                GitHubAuthToken = config.GitHubAPIKey
            };

            await CsAttributionCollector.Execute(packageOptions);
        }

        private static async Task ExportJavascriptAttributionsToFile(AttributionsConfig config, string attributionsJsFileName){
            
            var process = new Process
            {
                StartInfo =
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true, 
                    ErrorDialog = false, 
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true
                }
            };
            string pathToAttributionsFile = Path.Combine(Directory.GetCurrentDirectory(), attributionsJsFileName);
            string generateAttributionsCommand = $"yarn licenses generate-disclaimer > {pathToAttributionsFile}";
            
            bool yarnCommandRuns = false;
            process.OutputDataReceived += (object sender, DataReceivedEventArgs e) =>
            {
                string line = e.Data ?? "";

                if (!yarnCommandRuns)
                {
                    yarnCommandRuns = line.Contains(generateAttributionsCommand);
                }
                else
                {
                    // will be hit on the next method call after the generateAttributionsCommand 
                    process.Kill();
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            
            process.StandardInput.WriteLine("cd " + config.PathToOrigamFrontEnd);
            process.StandardInput.WriteLine(generateAttributionsCommand);
            await process.WaitForExitAsync();
        }

        private static string CleanJsAttributionsFile(string attributionsJsFileName)
        {
            bool yarnOutPutBegun = false;
            string licenseOutput = "";
            foreach (string line in File.ReadLines(attributionsJsFileName))
            {
                if (yarnOutPutBegun)
                {
                    licenseOutput += line + "\r\n";
                }
                else
                {
                    if (line.Contains("THE FOLLOWING SETS FORTH ATTRIBUTION NOTICES"))
                    {
                        licenseOutput += line + "\r\n";
                        yarnOutPutBegun = true;
                    }
                }
            }

            return licenseOutput;
        }

        private static AttributionsConfig GetConfig()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false);

            IConfiguration config = builder.Build();
            var attributionsConfig = config
                .GetSection("AttributionsConfig")
                .Get<AttributionsConfig>();
            
            
            var validator = new DataAnnotationsValidator.DataAnnotationsValidator();
            var validationResults = new List<ValidationResult>();
            validator.TryValidateObjectRecursive(attributionsConfig, validationResults);
            if (validationResults.Count > 0)
            {
                throw new ArgumentException(string.Join("\n", validationResults));
            }

            return attributionsConfig;
        }
    }
}