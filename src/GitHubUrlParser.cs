﻿using System;

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
                !repositoryUrl.StartsWith("http"))
            {
                throw new Exception(
                    $"Cannot Parse {repositoryUrl} to repositoryUrl");
            }

            string[] splitUrl = repositoryUrl.Split("/");
            User = splitUrl[3];
            RepositoryName = splitUrl[4];
        }
    }
}