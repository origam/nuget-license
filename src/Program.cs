﻿using CommandLine;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NugetUtility
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var result = Parser.Default.ParseArguments<PackageOptions>(args);
            return await result.MapResult(
                options => Execute(options),
                errors => Task.FromResult(1));
        }

        private static async Task<int> Execute(PackageOptions options)
        {
            DateTime startTime = DateTime.Now;
            if (string.IsNullOrWhiteSpace(options.ProjectDirectory))
            {
                Console.WriteLine("ERROR(S):");
                Console.WriteLine("-i\tInput the Directory Path (csproj or fsproj file)");

                return 1;
            }

            if (options.UseProjectAssetsJson && !options.IncludeTransitive)
            {
                Console.WriteLine("ERROR(S):");
                Console.WriteLine("--use-project-assets-json\tThis option always includes transitive references, so you must also provide the -t option.");

                return 1;
            }

            try
            {
                Methods methods = new Methods(options);
                var projectsWithPackages = await methods.GetPackages();
                var mappedLibraryInfo =
                    methods.MapPackagesToLibraryInfo(projectsWithPackages);
                HandleInvalidLicenses(methods, mappedLibraryInfo,
                    options.AllowedLicenseType);

                await methods.AddLicenseTexts(mappedLibraryInfo);

                mappedLibraryInfo =
                    methods.HandleDeprecateMSFTLicense(mappedLibraryInfo);

                if (options.Print == true)
                {
                    Console.WriteLine();
                    Console.WriteLine("Project Reference(s) Analysis...");
                    methods.PrintLicenses(mappedLibraryInfo);
                }
                
                methods.SaveAsTextFile(mappedLibraryInfo);
                methods.PersistRunInfo(mappedLibraryInfo);
                    
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return -1;
            }
            finally
            {
                DateTime endTime = DateTime.Now;
                Console.WriteLine($"Time elapsed: {endTime - startTime}");
            }
        }



        private static void HandleInvalidLicenses(Methods methods, List<LibraryInfo> libraries, ICollection<string> allowedLicenseType)
        {
            var invalidPackages = methods.ValidateLicenses(libraries);

            if (!invalidPackages.IsValid)
            {
                throw new InvalidLicensesException<LibraryInfo>(invalidPackages, allowedLicenseType);
            }
        }
    }
}
