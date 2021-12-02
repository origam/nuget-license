using System.ComponentModel.DataAnnotations;

namespace OrigamAttributionsGenerator
{
    public class AttributionsConfig
    {
        [Required(AllowEmptyStrings = false) ]
        public string PathToOrigamFrontEnd { get; set; }
        
        [Required(AllowEmptyStrings = false) ]
        public string PathToOrigamBackEnd { get; set; }
        
        [Required(AllowEmptyStrings = false) ]
        public string GitHubAPIKey { get; set; }
    }
}