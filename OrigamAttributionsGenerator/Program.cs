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

            var attributionsFileName = "Attributions.txt";
            await ExportCsAttributionsToFile(config, attributionsFileName);
            Console.WriteLine("C# attributions generated");
            
            await AddJavascriptAttributionsToFile(config, attributionsFileName);
            Console.WriteLine("javascript attributions generated");
            
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

        private static async Task AddJavascriptAttributionsToFile(
            AttributionsConfig config, string attributionsFileName)
        {
            string javascriptAttributions =
                await GenerateJavascriptAttributions(config.PathToOrigamFrontEnd);

            string csAttributions = await File.ReadAllTextAsync(attributionsFileName);
            string finalAttributions =
                javascriptAttributions + "\r\n\r\n" + csAttributions;

            await File.WriteAllTextAsync(attributionsFileName, finalAttributions);
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

        private static async Task<string> GenerateJavascriptAttributions(string pathToOrigamFrontEnd){
            
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
            string licenseOutput = "";

            var tokenSource = new CancellationTokenSource();
            bool yarnOutPutBegun = false;
            process.OutputDataReceived += (object sender, DataReceivedEventArgs e) =>
            {
                string? line = e.Data ?? "";
                tokenSource.Cancel();

                if (yarnOutPutBegun && !line.Contains(pathToOrigamFrontEnd))
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

                tokenSource = new CancellationTokenSource();
                Task.Run(() =>
                {
                    var token = tokenSource.Token;
                    Thread.Sleep(5000);
                    if (token.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                    process.Kill();
                }, tokenSource.Token);
            };

            process.Start();
            process.BeginOutputReadLine();
            
            process.StandardInput.WriteLine("cd " + pathToOrigamFrontEnd);
            process.StandardInput.WriteLine("yarn licenses generate-disclaimer");
            await process.WaitForExitAsync();

            return licenseOutput;
        }

        private static AttributionsConfig GetConfig()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false);

            IConfiguration config = builder.Build();
            var attributionsConfig = config.GetSection("AttributionsConfig").Get<AttributionsConfig>();
            
            
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