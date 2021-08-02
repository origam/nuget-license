using System;

namespace NugetUtility
{
    class GitHubUrlParser
    {
        private readonly string repositoryUrl;
        private bool parsed = false;
        private string user;
        private string repositoryName;
        public string User {
            get
            {
                if (!parsed)
                {
                    Parse();
                }
                return user;
            }
        }
        public string RepositoryName {
            get
            {
                if (!parsed)
                {
                    Parse();
                } 
                return repositoryName;
            }
        }
        
        public GitHubUrlParser(string repositoryUrl)
        {
            this.repositoryUrl = repositoryUrl;
        }

        private void Parse()
        {
            if (!IsValid())
            {
                throw new Exception($"Cannot Parse {repositoryUrl} to repositoryUrl");
            }

            string[] splitUrl = repositoryUrl.Split("/");
            user = splitUrl[3];
            repositoryName = splitUrl[4].EndsWith(".git")
                ? splitUrl[4].Substring(0, splitUrl[4].Length-4) 
                : splitUrl[4];
        }

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(repositoryUrl) && 
                   repositoryUrl.ToLower().Contains("github") && 
                   (repositoryUrl.StartsWith("http") || repositoryUrl.StartsWith("git://"));
        }
    }
}