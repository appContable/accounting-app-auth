using System.ComponentModel.DataAnnotations;

namespace AccountCore.API.Configuration
{
    public class ApiSettings
    {
        [Required]
        public string Version { get; set; } = "1.0.0";
        
        public string? UiUrlBase { get; set; }
        
        public int? HttpsPort { get; set; }
        
        [Range(1, int.MaxValue)]
        public int DefaultPageSize { get; set; } = 20;
        
        [Range(1, 1000)]
        public int MaxPageSize { get; set; } = 100;
    }
}