using System;

namespace NugetUtility
{
    class GitHubUrlParser
    {
        public string User { get; }
        public string RepositoryName { get; }

        public string LicenseUrl =>
            $"https://raw.githubusercontent.com/{User}/{RepositoryName}/master/LICENSE";
        public GitHubUrlParser(string repositoryUrl)
        {
            if (string.IsNullOrWhiteSpace(repositoryUrl) ||
                (!repositoryUrl.StartsWith("http") && !repositoryUrl.StartsWith("git://")))
            {
                throw new Exception(
                    $"Cannot Parse {repositoryUrl} to repositoryUrl");
            }

            string[] splitUrl = repositoryUrl.Split("/");
            User = splitUrl[3];
            RepositoryName = splitUrl[4].EndsWith(".git")
                ? splitUrl[4].Substring(0, splitUrl[4].Length-4) 
                : splitUrl[4];
        }
    }
}