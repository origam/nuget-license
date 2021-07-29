using System;
using Newtonsoft.Json;

namespace NugetUtility
{
    public class LibraryInfo
    {
        public string PackageName { get; set; }
        public string PackageVersion { get; set; }
        public string PackageUrl { get; set; }
        public string Copyright { get; set; }
        public string [] Authors { get; set; }
        public string[] Owners { get; set; }
        public string Description { get; set; }
        public string LicenseUrl { get; set; }
        public string LicenseText { get; private set; }
        public string LicenseTextSource { get; private set; }
        public string LicenseType { get; set; }
        public string Projects { get; set; }
        public Repository Repository { get; set; }
        public Metadata SourceData { get; set; }

        public string Source =>
            JsonConvert.SerializeObject(SourceData, Formatting.Indented);
        public string LicenseTextHtml { get; set; }

        public override string ToString()
        {
            return $"{PackageName} v{PackageVersion}";
        }

        public void SetLicenseText(string licenseText, string licenseTextSource)
        {
            LicenseText = licenseText;
            LicenseTextSource = licenseTextSource;
        }
        
    }
}