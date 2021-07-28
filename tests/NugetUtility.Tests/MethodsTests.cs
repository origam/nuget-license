using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommandLine.Text;
using FluentAssertions;
using NUnit.Framework;

namespace NugetUtility.Tests {
    [ExcludeFromCodeCoverage]
    [TestFixture]
    public class MethodTests {
        private string _projectPath;
        private Methods _methods;

        [SetUp]
        public void Setup () {
            _projectPath = @"../../../";
            _methods = new Methods (new PackageOptions { ProjectDirectory = _projectPath });
        }

        [Test]
        public void GetProjectExtension_Should_Be_CsprojOrFsProj () {
            _methods.GetProjectExtensions ().Should ().Contain (".csproj", ".fsproj");
        }

        [Test]
        public void GetProjectReferences_Should_Resolve_Projects () {
            var packages = _methods.GetProjectReferences (_projectPath);

            packages.Should ().NotBeEmpty ();
        }

        [Test]
        public async Task GetNugetInformationAsync_Should_Resolve_Projects () {
            var packages = _methods.GetProjectReferences (_projectPath);
            var referencedpackages = packages.Select (p => { var split = p.Split (","); return new PackageNameAndVersion { Name = split[0], Version = split[1] }; });
            var information = await _methods.GetNugetInformationAsync (_projectPath, referencedpackages);

            packages.Select (x => x.Split (',') [0].ToLower ())
                .Should ()
                .BeEquivalentTo (information.Select (x => x.Value.Metadata.Id.ToLower ()));
        }

        [TestCase ("FluentValidation,5.1.0.0")]
        [Test]
        public async Task GetNugetInformationAsync_Should_Resolve_Missing_NuSpec_File (string package) {
            var packages = package.Split (';', System.StringSplitOptions.RemoveEmptyEntries);
            var referencedpackages = packages.Select (p => { var split = p.Split (","); return new PackageNameAndVersion { Name = split[0], Version = split[1] }; });
            var information = await _methods.GetNugetInformationAsync (_projectPath, referencedpackages);

            packages.Select (x => x.Split (',') [0])
                .Should ()
                .BeEquivalentTo (information.Select (x => x.Value.Metadata.Id));
        }

        [TestCase ("FluentValidation", "5.1.0.0")]
        [TestCase ("System.Linq", "(4.1.0,)")]
        [TestCase ("System.Linq", "[4.1.0]")]
        [TestCase ("System.Linq", "(,4.1.0]")]
        [TestCase ("System.Linq", "(,4.1.0)")]
        [TestCase ("System.Linq", "[4.1.0,4.3.0]")]
        [TestCase ("System.Linq", "(4.1.0,4.3.0)")]
        [TestCase ("System.Linq", "[4.1.0,4.3.0)")]
        [TestCase ("BCrypt.Net-Next", "2.1.3")]
        [Test]
        public async Task GetNugetInformationAsync_Should_Properly_TreatAllAllowedNuSpecReferenceTypes (string package,
            string version) {
            var referencedpackages = new PackageNameAndVersion[] { new PackageNameAndVersion { Name = package, Version = version } };
            var information = await _methods.GetNugetInformationAsync (_projectPath, referencedpackages);

            var expectation = version.Trim (new char[] { '[', '(', ']', ')' })
                .Split (",", System.StringSplitOptions.RemoveEmptyEntries).Select (v => $"{package},{v}");
            expectation.Should ().BeEquivalentTo (information.Select (x => x.Key));
            expectation.Should ()
                .BeEquivalentTo (information.Select (x => $"{x.Value.Metadata.Id},{x.Value.Metadata.Version}"));
        }

     
        [Test]
        public async Task GetPackages_ProjectsFilter_Should_Remove_Test_Projects () {
            var methods = new Methods (new PackageOptions {
                ProjectsFilterOption = @"../../../SampleProjectFilters.json",
                    ProjectDirectory = TestSetup.ThisProjectSolutionPath
            });

            var result = await methods.GetPackages ();

            result.Should ()
                .HaveCount (1)
                .And.Match (kvp => kvp.First ().Key.EndsWith ("NugetUtility.csproj"));
        }

        [Test]
        public async Task GetPackages_PackagesFilter_Should_Remove_CommandLineParser () {
            var methods = new Methods (new PackageOptions {
                PackagesFilterOption = @"../../../SamplePackagesFilters.json",
                    ProjectDirectory = TestSetup.ThisProjectSolutionPath
            });

            var result = await methods.GetPackages ();

            result.Should ().NotBeEmpty ()
                .And.NotContainKey ("CommandLineParser");
        }

        [Test]
        public async Task GetPackages_RegexPackagesFilter_Should_Remove_CommandLineParser()
        {
            var methods = new Methods(new PackageOptions
            {
                PackagesFilterOption = "/CommandLine*/",
                ProjectDirectory = TestSetup.ThisProjectSolutionPath
            });

            var result = await methods.GetPackages();

            result.Should().NotBeEmpty()
                .And.NotContainKey("CommandLineParser");
        }

        [Test]
        public async Task GetPackages_AllowedLicenses_Should_Throw_On_MIT () {
            var methods = new Methods (new PackageOptions {
                ProjectsFilterOption = @"../../../SampleProjectFilters.json",
                    AllowedLicenseTypesOption = @"../../../SampleAllowedLicenses.json",
                    ProjectDirectory = TestSetup.ThisProjectSolutionPath
            });

            var result = await methods.GetPackages ();
            var validationResult = methods.ValidateLicenses (result);

            result.Should ().HaveCount (1);
            validationResult.IsValid.Should ().BeFalse ();
            validationResult.InvalidPackages.Count.Should ().Be (3);
        }

        [Test]
        public async Task GetPackages_InputJson_Should_OnlyParseGivenProjects () {
            var methods = new Methods (new PackageOptions {
                AllowedLicenseTypesOption = @"../../../SampleAllowedLicenses.json",
                    ProjectDirectory = @"../../../SampleAllowedProjects.json"
            });

            var result = await methods.GetPackages ();
            var validationResult = methods.ValidateLicenses (result);

            result.Should ().HaveCount (1);
            validationResult.IsValid.Should ().BeFalse ();
            validationResult.InvalidPackages.Count.Should ().Be (3);
        }

        [Test]
        public async Task GetProjectReferencesFromAssetsFile_Should_Resolve_Transitive_Assets()
        {
            var methods = new Methods(new PackageOptions
            {
                UseProjectAssetsJson = true,
                IncludeTransitive = true,
                ProjectDirectory = @"../../../",
            });

            var packages = await methods.GetPackages();
            packages.Should().ContainKey("../../../NugetUtility.Tests.csproj");
            packages.Should().HaveCount(1);
            var list = packages.Values.First();

            // Just look for a few expected packages. First-order refs:
            list.Should().ContainKey($"dotnet-project-licenses,{typeof(Methods).Assembly.GetName().Version.ToString(3)}");
            list.Should().ContainKey($"NUnit,{typeof(TestAttribute).Assembly.GetName().Version.ToString(3)}");

            // Some second-order refs:
            list.Should().ContainKey($"CommandLineParser,{typeof(UsageAttribute).Assembly.GetName().Version.ToString(3)}");
            list.Should().ContainKey("System.IO.Compression,4.3.0");

            // Some third-order refs:
            list.Should().ContainKey("System.Buffers,4.3.0");
        }
        
        // [TestCase("BenchmarkDotNet", "License.txt", "10.12.1")]
        // [Test]
        // public async Task GetLicenceFromNpkgFile_Should_Return_False(string packageName, string licenseFile, string packageVersion)
        // {
        //     var methods = new Methods(new PackageOptions
        //     {
        //         ExportLicenseTexts = true,
        //     });
        //
        //     var result = await methods.GetLicenceFromNpkgFile(packageName, licenseFile, packageVersion);
        //     Assert.False(result);
        // }

        [Test]
        public void HttpClient_IgnoreSslError_CallbackTest()
        {
            Assert.True(Methods.IgnoreSslCertificateErrorCallback(null, null, null, System.Net.Security.SslPolicyErrors.None));
        }

        [TestCase("System.Linq", "(4.1.0,)")]
        [TestCase("BCrypt.Net-Next", "2.1.3")]
        [Test]
        public void HttpClient_IgnoreSslError_GetNugetInformationAsync(string package, string version)
        {
            var methods = new Methods(new PackageOptions { ProjectDirectory = _projectPath, IgnoreSslCertificateErrors = true });

            var referencedpackages = new PackageNameAndVersion[] { new PackageNameAndVersion { Name = package, Version = version } };

            Assert.DoesNotThrowAsync(async () => await _methods.GetNugetInformationAsync(_projectPath, referencedpackages));
        }
    }
}