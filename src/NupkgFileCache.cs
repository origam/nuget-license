using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NugetUtility
{
    class NupkgFileCache
    {
        private readonly string _fallbackPackageUrl;
        private readonly HttpClient _httpClient;
        private readonly DirectoryInfo _cacheDirectory = new DirectoryInfo("NugetFilesCache");
        private readonly TimeSpan _maxCacheAge = new TimeSpan(100, 0,0,0);

        public NupkgFileCache(string fallbackPackageUrl, HttpClient httpClient)
        {
            _fallbackPackageUrl = fallbackPackageUrl;
            _httpClient = httpClient;
            CleanUp();
        }

        private void CleanUp()
        {
            if (!_cacheDirectory.Exists)
            {
                return;
            }

            foreach (FileInfo file in _cacheDirectory.GetFiles())
            {
                if (DateTime.Now - file.CreationTime > _maxCacheAge)
                {
                    file.Delete();
                }
            }
        }

        public async Task<string> GetPath(string package, string version)
        {
            _cacheDirectory.Create();
            var pathToNupkgFile = Path.Combine(_cacheDirectory.FullName, $"{package}_{version}.nupkg.zip");
            if (!File.Exists(pathToNupkgFile))
            {
                await DownloadNupkgFile(package, version, pathToNupkgFile);
            }

            return pathToNupkgFile;
        }
            
        private async Task DownloadNupkgFile(string package, string version,
            string pathToNupkgFile)
        {
            var nupkgEndpoint =
                new Uri(string.Format(_fallbackPackageUrl, package, version));
            using var packageRequest =
                new HttpRequestMessage(HttpMethod.Get, nupkgEndpoint);
            using var packageResponse =
                await _httpClient.SendAsync(packageRequest, CancellationToken.None);

            if (!packageResponse.IsSuccessStatusCode)
            {
                throw new Exception(packageResponse.ReasonPhrase);
            }


            await using (var fileStream = File.OpenWrite(pathToNupkgFile))
            {
                await packageResponse.Content.CopyToAsync(fileStream);
            }
        }
    }
}