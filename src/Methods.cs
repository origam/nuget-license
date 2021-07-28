using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Serialization;
using System.Xml.XPath;
using Newtonsoft.Json;
using Octokit;
using static NugetUtility.Utilties;

namespace NugetUtility
{
    public class Methods
    {
        private const string fallbackPackageUrl = "https://www.nuget.org/api/v2/package/{0}/{1}";
        private const string nugetUrl = "https://api.nuget.org/v3-flatcontainer/";
        private const string deprecateNugetLicense = "https://aka.ms/deprecateLicenseUrl";

        private static readonly string licenseDownloadDir = "./LicenseFiles/DirectDownloads";
        private static readonly string licenseOverrideDir = "./LicenseFiles/ManualOverrides";
        private static readonly Dictionary<Tuple<string, string>, Package> _requestCache = new Dictionary<Tuple<string, string>, Package>();
        private static readonly Dictionary<Tuple<string, string>, string> _licenseFileCache = new Dictionary<Tuple<string, string>, string>();
        /// <summary>
        /// See https://aspnetmonsters.com/2016/08/2016-08-27-httpclientwrong/
        /// </summary>
        private static HttpClient _httpClient;

        private const int maxRedirects = 5; // HTTP client max number of redirects allowed
        private const int timeout = 100; // HTTP client timeout in seconds
        private readonly IReadOnlyDictionary<string, string> _licenseMappings;
        private readonly PackageOptions _packageOptions;
        private readonly XmlSerializer _serializer;
        private string _lastRunInfosFile = "LastRunInfos.json";
        private readonly List<LibraryInfo> _lastRunInfos;

        internal static bool IgnoreSslCertificateErrorCallback(HttpRequestMessage message, System.Security.Cryptography.X509Certificates.X509Certificate2 cert, System.Security.Cryptography.X509Certificates.X509Chain chain, System.Net.Security.SslPolicyErrors sslPolicyErrors)
            => true;

        public Methods(PackageOptions packageOptions)
        {
            if (_httpClient is null)
            {
                var httpClientHandler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = maxRedirects
                };
                if (packageOptions.IgnoreSslCertificateErrors)
                {
                    httpClientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => IgnoreSslCertificateErrorCallback(message, cert, chain, sslPolicyErrors);
                }

                _httpClient = new HttpClient(httpClientHandler)
                {
                    BaseAddress = new Uri(nugetUrl),
                    Timeout = TimeSpan.FromSeconds(timeout)
                };
            }

            _serializer = new XmlSerializer(typeof(Package));
            _packageOptions = packageOptions;
            _licenseMappings = packageOptions.LicenseToUrlMappingsDictionary;
            _lastRunInfos = LoadLastRunInfos();
        }

        /// <summary>
        /// Get Nuget References per project
        /// </summary>
        /// <param name="project">project name</param>
        /// <param name="packages">List of projects</param>
        /// <returns></returns>
        public async Task<PackageList> GetNugetInformationAsync(string project, IEnumerable<PackageNameAndVersion> packages)
        {
            WriteOutput(Environment.NewLine + "project:" + project + Environment.NewLine, logLevel: LogLevel.Information);
            var licenses = new PackageList();
            foreach (var packageWithVersion in packages)
            {
                var versions = packageWithVersion.Version.Trim(new char[] { '[', ']', '(', ')' }).Split(",");
                foreach (var version in versions)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(packageWithVersion.Name) || string.IsNullOrWhiteSpace(version))
                        {
                            WriteOutput($"Skipping invalid entry {packageWithVersion}", logLevel: LogLevel.Verbose);
                            continue;
                        }

                        if (_packageOptions.PackageFilter.Any (p => string.Compare (p, packageWithVersion.Name, StringComparison.OrdinalIgnoreCase) == 0) ||
                            _packageOptions.PackageRegex?.IsMatch (packageWithVersion.Name) == true) {
                            WriteOutput (packageWithVersion.Name + " skipped by filter.", logLevel : LogLevel.Verbose);
                            continue;
                        }

                        var lookupKey = Tuple.Create(packageWithVersion.Name, version);

                        if (_requestCache.TryGetValue(lookupKey, out var package))
                        {
                            WriteOutput(packageWithVersion + " obtained from request cache.", logLevel: LogLevel.Information);
                            licenses.TryAdd($"{packageWithVersion.Name},{version}", package);
                            continue;
                        }

                        // Search nuspec in local cache (Fix for linux distro)
                        string userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                        var nuspecPath = CreateNuSpecPath(userDir, version, packageWithVersion.Name);
                        //Linux: package file name could be lowercase
                        if (IsLinux())
                        {
                            //Check package file
                            if(!File.Exists(nuspecPath))
                            {
                                //Try lowercase
                                nuspecPath = CreateNuSpecPath(userDir, version, packageWithVersion.Name?.ToLowerInvariant());
                            }
                        }

                        if (File.Exists(nuspecPath))
                        {
                            try
                            {
                                using var textReader = new StreamReader(nuspecPath);
                                await ReadNuspecFile(project, licenses, packageWithVersion.Name, version, lookupKey, textReader);
                                continue;
                            }
                            catch(Exception exc)
                            {
                                // Ignore errors in local cache, try online call
                                WriteOutput($"ReadNuspecFile error, package '{packageWithVersion.Name}'", exc, LogLevel.Verbose);
                            }
                        }
                        else
                        {
                            WriteOutput($"Package '{packageWithVersion.Name}' not found in local cache ({nuspecPath})..", logLevel: LogLevel.Verbose);
                        }

                        // Try dowload nuspec
                        using var request = new HttpRequestMessage(HttpMethod.Get, $"{packageWithVersion.Name}/{version}/{packageWithVersion.Name}.nuspec");
                        using var response = await _httpClient.SendAsync(request);
                        if (!response.IsSuccessStatusCode)
                        {
                            WriteOutput($"{request.RequestUri} failed due to {response.StatusCode}!", logLevel: LogLevel.Warning);
                            var fallbackResult = await GetNuGetPackageFileResult<Package>(packageWithVersion.Name, version, $"{packageWithVersion.Name}.nuspec");
                            if (fallbackResult is Package)
                            {
                                licenses.Add($"{packageWithVersion.Name},{version}", fallbackResult);
                                await this.AddTransitivePackages(project, licenses, fallbackResult);
                                _requestCache[lookupKey] = fallbackResult;
                                await HandleLicensing(fallbackResult);
                            }
                            else
                            {
                                licenses.Add($"{packageWithVersion.Name},{version}", new Package { Metadata = new Metadata { Version = version, Id = packageWithVersion.Name } });
                            }

                            continue;
                        }

                        WriteOutput($"Successfully received {request.RequestUri}", logLevel: LogLevel.Information);
                        using (var responseText = await response.Content.ReadAsStreamAsync())
                        using (var textReader = new StreamReader(responseText))
                        {
                            try
                            {
                                await ReadNuspecFile(project, licenses, packageWithVersion.Name, version, lookupKey, textReader);
                            }
                            catch (Exception e)
                            {
                                WriteOutput(e.Message, e, LogLevel.Error);
                                throw;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteOutput(ex.Message, ex, LogLevel.Error);
                    }
                }
            }

            return licenses;

            static bool IsLinux()
            {
#if NET5_0_OR_GREATER
                return OperatingSystem.IsLinux();
#else
                return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
#endif
            }

            static string CreateNuSpecPath(string userDir, string version, string packageName)
                => Path.Combine(userDir, ".nuget", "packages", packageName, version, $"{packageName}.nuspec");
        }

        private async Task ReadNuspecFile(string project, PackageList licenses, string package, string version, Tuple<string, string> lookupKey, StreamReader textReader)
        {
            if (_serializer.Deserialize(new NamespaceIgnorantXmlTextReader(textReader)) is Package result)
            {
                licenses.Add($"{package},{version}", result);
                await this.AddTransitivePackages(project, licenses, result);
                _requestCache[lookupKey] = result;
                await HandleLicensing(result);
            }
        }

        private async Task AddTransitivePackages(string project, PackageList licenses, Package result)
        {
            var groups = result.Metadata?.Dependencies?.Group;
            if (_packageOptions.IncludeTransitive && groups != null)
            {
                foreach (var group in groups)
                {
                    var dependant =
                        group
                        .Dependency
                        .Where(e => !licenses.Keys.Contains($"{e.Id},{e.Version}"))
                        .Select(e => new PackageNameAndVersion { Name = e.Id, Version = e.Version });

                    var dependantPackages = await GetNugetInformationAsync(project, dependant);
                    foreach (var dependantPackage in dependantPackages)
                    {
                        if (!licenses.ContainsKey(dependantPackage.Key))
                        {
                            licenses.Add(dependantPackage.Key, dependantPackage.Value);
                        }
                    }
                }
            }
        }

        public async Task<Dictionary<string, PackageList>> GetPackages()
        {
            WriteOutput(() => $"Starting {nameof(GetPackages)}...", logLevel: LogLevel.Verbose);
            var licenses = new Dictionary<string, PackageList>();
            var projectFiles = await GetValidProjects(_packageOptions.ProjectDirectory);
            foreach (var projectFile in projectFiles)
            {
                var references = this.GetProjectReferences(projectFile);
                var referencedPackages = references.Select((package) =>
                {
                    var split = package.Split(',');
                    return new PackageNameAndVersion { Name = split[0], Version = split[1] };
                });
                var currentProjectLicenses = await this.GetNugetInformationAsync(projectFile, referencedPackages);
                licenses[projectFile] = currentProjectLicenses;
            }

            return licenses;
        }

        public string[] GetProjectExtensions(bool withWildcard = false) =>
            withWildcard ?
            new[] { "*.csproj", "*.fsproj" } :
            new[] { ".csproj", ".fsproj" };

        /// <summary>
        /// Retreive the project references from csproj or fsproj file
        /// </summary>
        /// <param name="projectPath">The Project Path</param>
        /// <returns></returns>
        public IEnumerable<string> GetProjectReferences(string projectPath)
        {
            WriteOutput(() => $"Starting {nameof(GetProjectReferences)}...", logLevel: LogLevel.Verbose);
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentNullException(projectPath);
            }

            if (!GetProjectExtensions().Any(projExt => projectPath.EndsWith(projExt)))
            {
                projectPath = GetValidProjects(projectPath).GetAwaiter().GetResult().FirstOrDefault();
            }

            if (projectPath is null)
            {
                throw new FileNotFoundException();
            }

            IEnumerable<string> references = Array.Empty<string>();
         
            // First use project.assets.json, if this option is enabled.
            if (_packageOptions.UseProjectAssetsJson)
            {
                var assetsFile = Path.Combine(Path.GetDirectoryName(projectPath) ?? ".", "obj", "project.assets.json");
                if (!File.Exists(assetsFile))
                {
                    WriteOutput(() => $"Cannot find {assetsFile}", logLevel: LogLevel.Warning);
                }
                else
                {
                    references = GetProjectReferencesFromAssetsFile(assetsFile);
                }
            }

            // Then try to get references from new project file format
            if (!references.Any())
            {
                references = GetProjectReferencesFromNewProjectFile(projectPath);
            }

            // Then if needed from old packages.config
            if (!references.Any())
            {
                references = GetProjectReferencesFromPackagesConfig(projectPath);
            }

            return references ?? Array.Empty<string>();
        }

        /// <summary>
        /// Main function to cleanup
        /// </summary>
        /// <param name="packages"></param>
        /// <returns></returns>
        public List<LibraryInfo> MapPackagesToLibraryInfo(Dictionary<string, PackageList> packages)
        {
            WriteOutput(() => $"Starting {nameof(MapPackagesToLibraryInfo)}...", logLevel: LogLevel.Verbose);
            var libraryInfos = new List<LibraryInfo>(256);
            foreach (var packageList in packages)
            {
                foreach (var item in packageList.Value.Select(p => p.Value))
                {
                    var info = MapPackageToLibraryInfo(item, packageList.Key);
                    libraryInfos.Add(info);
                }
            }

            // merge in missing manual items where there wasn't a package
            var missedManualItems = _packageOptions.ManualInformation.Except(libraryInfos, LibraryNameAndVersionComparer.Default);
            foreach (var missed in missedManualItems)
            {
                libraryInfos.Add(missed);
            }
            
            return libraryInfos
                .OrderBy(p => p.PackageName)
                .ToList();
        }

        private LibraryInfo MapPackageToLibraryInfo(Package item, string projectFile)
        {
            string licenseType = item.Metadata.License?.Text ?? null;
            string licenseUrl = item.Metadata.LicenseUrl ?? null;

            if (licenseUrl is string && string.IsNullOrWhiteSpace(licenseType))
            {
                if (_licenseMappings.TryGetValue(licenseUrl, out var license))
                {
                    licenseType = license;
                }
            }

            var manual = _packageOptions.ManualInformation
                .FirstOrDefault(f => f.PackageName == item.Metadata.Id && f.PackageVersion == item.Metadata.Version);

            return new LibraryInfo
            {
                PackageName = item.Metadata.Id ?? string.Empty,
                PackageVersion = item.Metadata.Version ?? string.Empty,
                PackageUrl = !string.IsNullOrWhiteSpace(manual?.PackageUrl) ?
                    manual.PackageUrl :
                    item.Metadata.ProjectUrl ?? string.Empty,
                Copyright = item.Metadata.Copyright ?? string.Empty,
                Authors = manual?.Authors ?? item.Metadata.Authors?.Split(',') ?? new string[] { },
                Description = !string.IsNullOrWhiteSpace(manual?.Description) ?
                    manual.Description :
                    item.Metadata.Description ?? string.Empty,
                LicenseType = manual?.LicenseType ?? licenseType ?? string.Empty,
                LicenseUrl = manual?.LicenseUrl ?? licenseUrl ?? string.Empty,
                Projects = _packageOptions.IncludeProjectFile ? projectFile : null,
                Repository = item.Metadata.Repository,
                SourceData =  item.Metadata
            };
        }

        public IValidationResult<KeyValuePair<string, Package>> ValidateLicenses(Dictionary<string, PackageList> projectPackages)
        {
            if (_packageOptions.AllowedLicenseType.Count == 0)
            {
                return new ValidationResult<KeyValuePair<string, Package>> { IsValid = true };
            }

            WriteOutput(() => $"Starting {nameof(ValidateLicenses)}...", logLevel: LogLevel.Verbose);
            var invalidPackages = projectPackages
                .SelectMany(kvp => kvp.Value.Select(p => new KeyValuePair<string, Package>(kvp.Key, p.Value)))
                .Where(p => !_packageOptions.AllowedLicenseType.Any(allowed =>
                {
                    if (p.Value.Metadata.LicenseUrl is string licenseUrl)
                    {
                        if (_licenseMappings.TryGetValue(licenseUrl, out var license))
                        {
                            return allowed == license;
                        }

                        if (p.Value.Metadata.LicenseUrl?.Contains(allowed, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            return true;
                        }
                    }

                    if (p.Value.Metadata.License.IsLicenseFile())
                    {
                        var key = Tuple.Create(p.Value.Metadata.Id, p.Value.Metadata.Version);

                        if (_licenseFileCache.TryGetValue(key, out var licenseText))
                        {
                            if (licenseText.Contains(allowed, StringComparison.OrdinalIgnoreCase))
                            {
                                p.Value.Metadata.License.Text = allowed;
                                return true;
                            }
                        }
                    }

                    return allowed == p.Value.Metadata.License?.Text;
                }))
                .ToList();

            return new ValidationResult<KeyValuePair<string, Package>> { IsValid = invalidPackages.Count == 0, InvalidPackages = invalidPackages };
        }

        public ValidationResult<LibraryInfo> ValidateLicenses(List<LibraryInfo> projectPackages)
        {
            if (_packageOptions.AllowedLicenseType.Count == 0)
            {
                return new ValidationResult<LibraryInfo> { IsValid = true };
            }

            WriteOutput(() => $"Starting {nameof(ValidateLicenses)}...", logLevel: LogLevel.Verbose);
            var invalidPackages = projectPackages
                .Where(p => !_packageOptions.AllowedLicenseType.Any(allowed =>
                {
                    if (p.LicenseUrl is string licenseUrl)
                    {
                        if (_licenseMappings.TryGetValue(licenseUrl, out var license))
                        {
                            return allowed == license;
                        }

                        if (p.LicenseUrl?.Contains(allowed, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            return true;
                        }
                    }

                    return allowed == p.LicenseType;
                }))
                .ToList();

            return new ValidationResult<LibraryInfo> { IsValid = invalidPackages.Count == 0, InvalidPackages = invalidPackages };
        }

        private async Task<T> GetNuGetPackageFileResult<T>(string packageName, string versionNumber, string fileInPackage)
        where T : class
        {
            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(versionNumber)) { return await Task.FromResult<T>(null); }
            var fallbackEndpoint = new Uri(string.Format(fallbackPackageUrl, packageName, versionNumber));
            WriteOutput(() => "Attempting to download: " + fallbackEndpoint.ToString(), logLevel: LogLevel.Verbose);
            using var packageRequest = new HttpRequestMessage(HttpMethod.Get, fallbackEndpoint);
            using var packageResponse = await _httpClient.SendAsync(packageRequest, CancellationToken.None);
            if (!packageResponse.IsSuccessStatusCode)
            {
                WriteOutput($"{packageRequest.RequestUri} failed due to {packageResponse.StatusCode}!", logLevel: LogLevel.Warning);
                return null;
            }

            using var fileStream = new MemoryStream();
            await packageResponse.Content.CopyToAsync(fileStream);

            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
            var entry = archive.GetEntry(fileInPackage);
            if (entry is null)
            {
                WriteOutput(() => $"{fileInPackage} was not found in NuGet Package: {packageName}", logLevel: LogLevel.Verbose);
                return null;
            }
            WriteOutput(() => $"Attempting to read: {fileInPackage}", logLevel: LogLevel.Verbose);
            using var entryStream = entry.Open();
            using var textReader = new StreamReader(entryStream);
            var typeT = typeof(T);
            if (typeT == typeof(Package))
            {
                if (_serializer.Deserialize(new NamespaceIgnorantXmlTextReader(textReader)) is T result)
                {
                    return (T)result;
                }
            }
            else if (typeT == typeof(string))
            {
                return await textReader.ReadToEndAsync() as T;
            }

            throw new ArgumentException($"{typeT.FullName} isn't supported!");
        }

        private IEnumerable<string> GetFilteredProjects(IEnumerable<string> projects)
        {
            if (_packageOptions.ProjectFilter.Count == 0)
            {
                return projects;
            }

            var filteredProjects = projects.Where(project => !_packageOptions.ProjectFilter
               .Any(projectToSkip =>
                  project.Contains(projectToSkip, StringComparison.OrdinalIgnoreCase)
               )).ToList();

            WriteOutput(() => $"Filtered Project Files {Environment.NewLine}", logLevel: LogLevel.Verbose);
            WriteOutput(() => string.Join(Environment.NewLine, filteredProjects.ToArray()), logLevel: LogLevel.Verbose);

            return filteredProjects;
        }

        private async Task HandleLicensing(Package package)
        {
            if (package?.Metadata is null) { return; }
            if (package.Metadata.LicenseUrl is string licenseUrl &&
                package.Metadata.License?.Text is null)
            {
                if (_licenseMappings.TryGetValue(licenseUrl, out var mappedLicense))
                {
                    package.Metadata.License = new License { Text = mappedLicense };
                }
            }

            if (!package.Metadata.License.IsLicenseFile() || _packageOptions.AllowedLicenseType.Count == 0) { return; }

            var key = Tuple.Create(package.Metadata.Id, package.Metadata.Version);

            if (_licenseFileCache.TryGetValue(key, out _)) { return; }

            _licenseFileCache[key] = await GetNuGetPackageFileResult<string>(package.Metadata.Id, package.Metadata.Version, package.Metadata.License.Text);
        }

        private string GetOutputFilename (string defaultName) {
            string outputDir = GetExportDirectory ();

            return string.IsNullOrWhiteSpace (_packageOptions.OutputFileName) ?
                Path.Combine (outputDir, defaultName) :
                Path.Combine (outputDir, _packageOptions.OutputFileName);
        }

        public string GetExportDirectory () {
            string outputDirectory = string.Empty;
            if (!string.IsNullOrWhiteSpace (_packageOptions.OutputDirectory)) {
                if (_packageOptions.OutputDirectory.EndsWith ('/')) {
                    outputDirectory = Path.GetDirectoryName (_packageOptions.OutputDirectory);
                } else {
                    outputDirectory = Path.GetDirectoryName (_packageOptions.OutputDirectory + "/");

                }
                if (!Directory.Exists (outputDirectory)) {
                    Directory.CreateDirectory (outputDirectory);
                }
            }

            outputDirectory = string.IsNullOrWhiteSpace (outputDirectory) ? Environment.CurrentDirectory : outputDirectory;

            return outputDirectory;
        }

        /// <summary>
        /// Retreive the project references from new csproj file format
        /// </summary>
        /// <param name="projectPath">The Project Path</param>
        /// <returns></returns>
        private IEnumerable<string> GetProjectReferencesFromNewProjectFile(string projectPath)
        {
            var projDefinition = XDocument.Load(projectPath);

            // Uses an XPath instead of direct navigation (using Elements("…")) as the project file may use xml namespaces
            return projDefinition?
                .XPathSelectElements("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageReference']")?
                .Select(refElem => GetProjectReferenceFromElement(refElem)) ??
                Array.Empty<string>();
        }

        private string GetProjectReferenceFromElement(XElement refElem)
        {
            string version, package = refElem.Attribute("Include")?.Value ?? string.Empty;

            var versionAttribute = refElem.Attribute("Version");

            if (versionAttribute != null)
                version = versionAttribute.Value;
            else // no version attribute, look for child element
                version = refElem.Elements()
                .Where(elem => elem.Name.LocalName == "Version")
                .FirstOrDefault()?.Value ?? string.Empty;

            return $"{package},{version}";
        }

        /// <summary>
        /// Retrieve the project references from old packages.config file
        /// </summary>
        /// <param name="projectPath">The Project Path</param>
        /// <returns></returns>
        private IEnumerable<string> GetProjectReferencesFromPackagesConfig(string projectPath)
        {
            var dir = Path.GetDirectoryName(projectPath);
            var packagesFile = Path.Join(dir, "packages.config");

            if (File.Exists(packagesFile))
            {
                var packagesConfig = XDocument.Load(packagesFile);

                return packagesConfig?
                    .Element("packages")?
                    .Elements("package")?
                    .Select(refElem => (refElem.Attribute("id")?.Value ?? string.Empty) + "," + (refElem.Attribute("version")?.Value ?? string.Empty));
            }

            return Array.Empty<string>();
        }

        /// <summary>
        /// Gets project references from a project.assets.json file.
        /// </summary>
        /// <param name="assetsPath">The assets file</param>
        /// <returns></returns>
        private IEnumerable<string> GetProjectReferencesFromAssetsFile(string assetsPath)
        {
            WriteOutput(() => $"Reading assets file {assetsPath}", logLevel: LogLevel.Verbose);
            using var assetsFileStream = File.OpenRead(assetsPath);
            var doc = JsonDocument.Parse(assetsFileStream);
            assetsFileStream.Dispose();

            if (!doc.RootElement.TryGetProperty("targets", out var targets))
            {
                WriteOutput(() => $"No \"targets\" property found in {assetsPath}", logLevel: LogLevel.Warning);
                yield break;
            }

            foreach (var target in targets.EnumerateObject())
            {
                WriteOutput(() => $"Reading dependencies for target {target.Name}", logLevel: LogLevel.Verbose);
                foreach (var dep in target.Value.EnumerateObject())
                {
                    var depName = dep.Name.Split('/');
                    if (depName.Length != 2)
                    {
                        WriteOutput(() => $"Unexpected package name: {dep.Name}", logLevel: LogLevel.Warning);
                        continue;
                    }

                    yield return string.Join(",", depName);
                }
            }
        }

        private async Task<IEnumerable<string>> GetValidProjects(string projectPath)
        {
            var pathInfo = new FileInfo(projectPath);
            var extensions = GetProjectExtensions();
            IEnumerable<string> validProjects;
            switch (pathInfo.Extension)
            {
                case ".sln":
                    validProjects = (await ParseSolution(pathInfo.FullName))
                        .Select(p => new FileInfo(Path.Combine(pathInfo.Directory.FullName, p)))
                        .Where(p => p.Exists && extensions.Contains(p.Extension))
                        .Select(p => p.FullName);
                    break;
                case ".csproj":
                    validProjects = new string[] { projectPath };
                    break;
                case ".fsproj":
                    validProjects = new string[] { projectPath };
                    break;
                case ".json":
                    validProjects = ReadListFromFile<string>(projectPath)
                        .Select(x => x.EnsureCorrectPathCharacter())
                        .ToList();
                    break;
                default:
                    validProjects =
                        GetProjectExtensions(withWildcard: true)
                        .SelectMany(wildcardExtension =>
                           Directory.EnumerateFiles(projectPath, wildcardExtension, SearchOption.AllDirectories)
                        );
                    break;
            }

            WriteOutput(() => $"Discovered Project Files {Environment.NewLine}", logLevel: LogLevel.Verbose);
            WriteOutput(() => string.Join(Environment.NewLine, validProjects.ToArray()), logLevel: LogLevel.Verbose);

            return GetFilteredProjects(validProjects);
        }

        private async Task<IEnumerable<string>> ParseSolution(string fullName)
        {
            var solutionFile = new FileInfo(fullName);
            if (!solutionFile.Exists) { throw new FileNotFoundException(fullName); }
            var projectFiles = new List<string>(250);

            using (var fileStream = solutionFile.OpenRead())
            using (var streamReader = new StreamReader(fileStream))
            {
                while (await streamReader.ReadLineAsync() is string line)
                {
                    if (!line.StartsWith("Project")) { continue; }
                    var segments = line.Split(',');
                    if (segments.Length < 2) { continue; }
                    projectFiles.Add(segments[1].EnsureCorrectPathCharacter().Trim('"'));
                }
            }

            return projectFiles;
        }


        /// <summary>
        /// Downloads the nuget package file and read the licence file
        /// </summary>
        /// <param name="package"></param>
        /// <param name="licenseFile"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        public async Task<string> GetLicenceFromNpkgFile(string package, string licenseFile, string version)
        {
            var nupkgEndpoint = new Uri(string.Format(fallbackPackageUrl, package, version));
            WriteOutput(() => $"Attempting to download: {nupkgEndpoint}", logLevel: LogLevel.Verbose);
            using var packageRequest = new HttpRequestMessage(HttpMethod.Get, nupkgEndpoint);
            using var packageResponse = await _httpClient.SendAsync(packageRequest, CancellationToken.None);

            if (!packageResponse.IsSuccessStatusCode)
            {
                throw new Exception(packageResponse.ReasonPhrase);
            }

            var outpath =  $"{package}_{version}.nupkg.zip";

            using (var fileStream = File.OpenWrite(outpath))
            {
                await packageResponse.Content.CopyToAsync(fileStream);
            }

            using (ZipArchive archive = ZipFile.OpenRead(outpath))
            {
                var sample = archive.GetEntry(licenseFile);
                if (sample != null)
                {
                    var stream = sample.Open();
                    StreamReader reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                    
                }
            }

            File.Delete(outpath);
            return null;
        }

        /// <summary>
        /// HandleMSFTLicenses handle deprecate MSFT nuget licenses
        /// </summary>
        /// <param name="libraries">List<LibraryInfo></param>
        /// <returns>A List of LibraryInfo</returns>
        public List<LibraryInfo> HandleDeprecateMSFTLicense(List<LibraryInfo> libraries)
        {
            List<LibraryInfo> result = libraries;

            foreach (var item in result)
            {
                if (item.LicenseUrl == deprecateNugetLicense)
                {
                    item.LicenseUrl = string.Format("https://www.nuget.org/packages/{0}/{1}/License", item.PackageName, item.PackageVersion);
                }
            }
            return result;
        }

        public async Task AddLicenseTexts (List<LibraryInfo> infos) {
            foreach (var info in infos)
            {
                AddLicenseTextFromGitHub(info);
                if (!string.IsNullOrEmpty(info.LicenseText))
                {
                    continue;
                }

                await AddLicenseTextFormLicenseUrl(info);
                if (!string.IsNullOrEmpty(info.LicenseText))
                {
                    continue;
                }

                AddLicenseTextFromLocalFile(info);
            }
        }

        private void AddLicenseTextFromLocalFile(LibraryInfo info)
        {
            string fileInOverridesDir = GetLicenseOverrideFile(info);
            if (!File.Exists(fileInOverridesDir))
            {
                throw new Exception(
                    $"Since all other ways of getting the license text have failed we were trying to get the license text from a local file but the file was not found. Please find the license text and save it into {fileInOverridesDir}\nPackage info:\n{info.Source}");
            }

            info.SetLicenseText(File.ReadAllText(fileInOverridesDir), "Local File");
        }

        private static string GetLicenseOverrideFile(LibraryInfo info)
        {
            return Path.Combine(licenseOverrideDir,
                GetLicenseFileName(info));
        }

        private void AddLicenseTextFromGitHub(LibraryInfo info)
        {
            if (info.Repository?.Type?.ToLower() != "git" || string.IsNullOrEmpty(info.Repository.Url))
            {
                return;
            }
            var gitHubUrlParser = new GitHubUrlParser(info.Repository.Url);
            var github = new GitHubClient(new ProductHeaderValue("LicenseGetter"));
            if (!string.IsNullOrWhiteSpace(_packageOptions.GitHubAuthToken))
            {
                var tokenAuth = new Credentials(_packageOptions.GitHubAuthToken);
                github.Credentials = tokenAuth;
            }

            var licenseFilePath = github
                .Repository
                .Content
                .GetAllContents(gitHubUrlParser.User, gitHubUrlParser.RepositoryName).Result
                .FirstOrDefault(item => item.Name.ToLower().Contains("license"))
                ?.Path;
            if (licenseFilePath == null)
            {
                return;
            }

            var licenseText = github
                .Repository
                .Content.GetAllContents(gitHubUrlParser.User, gitHubUrlParser.RepositoryName, licenseFilePath)
                .Result
                .FirstOrDefault()
                ?.Content;
            if (!string.IsNullOrEmpty(licenseText))
            {
                info.SetLicenseText(licenseText, "GitHub");
            }

            ApiInfo lastApiInfo = github.GetLastApiInfo();
            if (lastApiInfo.RateLimit.Remaining < 20)
            {
                WriteOutput(
                    $"GitHub api request limit is running out! Requests left: {lastApiInfo.RateLimit.Remaining}, Limit: {lastApiInfo.RateLimit.Limit}, Resets at: {lastApiInfo.RateLimit.Reset}",
                    logLevel: LogLevel.Warning);
            }

        }

        private async Task AddLicenseTextFormLicenseUrl(LibraryInfo info)
        {
            if (string.IsNullOrEmpty(info.LicenseUrl))
            {
                return;
            }

            var source = info.LicenseUrl;
            if (source == deprecateNugetLicense)
            {
                var licenseText = await GetLicenceFromNpkgFile(info.PackageName,
                    info.LicenseType, info.PackageVersion);
                if (!string.IsNullOrEmpty(licenseText))
                {
                    info.SetLicenseText(licenseText, "LicenseUrl (NpkgFile)");
                    return;
                }
            }

            if (source == "http://go.microsoft.com/fwlink/?LinkId=329770" || source ==
                "https://dotnet.microsoft.com/en/dotnet_library_license.htm")
            {
                var licenseText = await GetLicenceFromNpkgFile(info.PackageName,
                    "dotnet_library_license.txt", info.PackageVersion);
                if (!string.IsNullOrEmpty(licenseText))
                {
                    info.SetLicenseText(licenseText, "LicenseUrl (NpkgFile)");
                    return;
                }
            }

            if (source.StartsWith("https://licenses.nuget.org"))
            {
                var licenseText = await GetLicenceFromNpkgFile(info.PackageName,
                    "License.txt", info.PackageVersion);
                if (!string.IsNullOrEmpty(licenseText))
                {
                    info.SetLicenseText(licenseText, "LicenseUrl (NpkgFile)");
                    return;
                }
            }

            string license = await DownloadFile(source);
            if (IsHtml(license))
            {
                info.LicenseTextHtml = license;
                LibraryInfo previousRunInfo = _lastRunInfos.FirstOrDefault(
                    prevInfo => prevInfo.PackageName == info.PackageName &&
                                prevInfo.PackageVersion == info.PackageVersion);
                if (previousRunInfo != null && 
                    previousRunInfo.LicenseTextSource != info.LicenseTextSource)
                {
                    if (_packageOptions.UpdateCachedHtmlLicenses)
                    {
                        WriteOutput(
                            $"Over writing cached html for LicenseUrl for package {info} because --update-cached-html-licenses is set.",
                            logLevel: LogLevel.Always);
                    }
                    else
                    {
                        string fileInOverridesDir = GetLicenseOverrideFile(info);
                        throw new Exception($"The LicenseUrl of package {info} is pointing to a web page with html, not a file and the page contents has changed since the last run. Please update the local license file {fileInOverridesDir} and run the tool again with option --update-cached-html-licenses");
                    }
                }
            }
            else
            {
                info.SetLicenseText(license, "LicenseUrl");
            }
        }
        
        private bool IsHtml(string maybeHtml)
        {
            var tagRegex = new System.Text.RegularExpressions.Regex(@"<\s*([^ >]+)[^>]*>.*?<\s*/\s*\1\s*>");
            return tagRegex.IsMatch(maybeHtml);
        }
        
        private async Task<string> DownloadFile(string source)
        {
            do
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    WriteOutput(
                        $"{request.RequestUri} failed due to {response.StatusCode}!",
                        logLevel: LogLevel.Error);
                    break;
                }

                // Detect a redirect 302
                if (response.RequestMessage.RequestUri.AbsoluteUri != source)
                {
                    WriteOutput(() => " Redirect detected", logLevel: LogLevel.Verbose);
                    source = response.RequestMessage.RequestUri.AbsoluteUri;
                    continue;
                }

                // Modify the URL if required
                if (CorrectUri(source) != source)
                {
                    WriteOutput(() => " Fixing URL", logLevel: LogLevel.Verbose);
                    source = CorrectUri(source);
                    continue;
                }

                Stream stream = await response.Content.ReadAsStreamAsync();
                StreamReader reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            } while (true);

            return null;
        }

        private static string GetLicenseFileName(LibraryInfo info)
        {
            return $"{info.PackageName}_{info.PackageVersion}.txt";
        }

        private bool IsGithub(string uri)
        {
            return uri.StartsWith("https://github.com", StringComparison.Ordinal);
        }

        /// <summary>
        /// make the appropriate changes to the URI to get the raw text of the license.
        /// </summary>
        /// <param name="uri">URI</param>
        /// <returns>Returns the raw URL to get the raw text of the library</returns>
        private string CorrectUri(string uri)
        {
            if (!IsGithub(uri))
            {
                return uri;
            }

            if (uri.Contains("/blob/", StringComparison.Ordinal))
            {
                uri = uri.Replace("/blob/", "/raw/", StringComparison.Ordinal);
            }

          /*  if (uri.Contains("/dotnet/corefx/", StringComparison.Ordinal))
            {
                uri = uri.Replace("/dotnet/corefx/", "/dotnet/runtime/", StringComparison.Ordinal);
            }*/

            return uri;
        }

        public void PrintLicenses(List<LibraryInfo> libraries)
        {
            if (libraries is null) { throw new ArgumentNullException(nameof(libraries)); }
            if (!libraries.Any()) { return; }

            WriteOutput(Environment.NewLine + "References:", logLevel: LogLevel.Always);
            WriteOutput(libraries.ToStringTable(new[] { "Reference", "Version", "License Type", "LicenseUrl", "LicenseTextSource" },
                                                            a => a.PackageName ?? "---",
                                                            a => a.PackageVersion ?? "---",
                                                            a => a.LicenseType ?? "---",
                                                            a => a.LicenseUrl ?? "---",
                                                            a => a.LicenseTextSource ?? "---"), logLevel: LogLevel.Always);
        }

        public void SaveAsTextFile(List<LibraryInfo> libraries)
        {
            if (!libraries.Any()) { return; }
            StringBuilder builder = new StringBuilder(256);
            foreach (var lib in libraries)
            {
                builder.Append($"The following software may be included in this product: {lib.PackageName}. A copy of the source code may be downloaded from {lib.PackageUrl}. This software contains the following license and notice below:");
                builder.AppendLine();
                builder.AppendLine();
                if (!string.IsNullOrWhiteSpace(lib.Copyright))
                {
                    builder.Append($"{lib.Copyright}");
                }
                else
                {
                    if (lib.Authors == null || lib.Authors.Length == 0)
                    {
                        throw new Exception($"Package {lib.PackageName} has not copyright and author.");
                    }
                    builder.Append($"Authors: {string.Join(", ",lib.Authors)}");
                }
                builder.AppendLine();
                builder.AppendLine();
                if (string.IsNullOrWhiteSpace(lib.LicenseText))
                {
                    throw new Exception($"License of the package {lib.PackageName} was not found.");
                }
                builder.Append($" {lib.LicenseText}");
                builder.AppendLine();
                builder.AppendLine();
            }

            File.WriteAllText(GetOutputFilename("attributions.txt"), builder.ToString());
        }

        private void WriteOutput(Func<string> line, Exception exception = null, LogLevel logLevel = LogLevel.Information)
        {
            if ((int)logLevel < (int)_packageOptions.LogLevelThreshold)
            {
                return;
            }

            Console.WriteLine(line.Invoke());

            if (exception is object)
            {
                Console.WriteLine(exception.ToString());
            }
        }

        private void WriteOutput(string line, Exception exception = null, LogLevel logLevel = LogLevel.Information) =>
            WriteOutput(() => line, exception, logLevel);

        public void PersistRunInfo(List<LibraryInfo> mappedLibraryInfo)
        {
            string serializedInfos = JsonConvert.SerializeObject(
                mappedLibraryInfo, Formatting.Indented);
            File.WriteAllText(_lastRunInfosFile, serializedInfos);
        }

        private List<LibraryInfo> LoadLastRunInfos()
        {
            if (!File.Exists(_lastRunInfosFile))
            {
                return new List<LibraryInfo>();
            }

            string serializedInfos = File.ReadAllText(_lastRunInfosFile);
            return JsonConvert.DeserializeObject<List<LibraryInfo>>(serializedInfos);
        }
    }
}