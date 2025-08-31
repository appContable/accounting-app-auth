using System.ComponentModel.DataAnnotations;

namespace AccountCore.DTO.Auth.Configuration
{
    public class JwtSettings
    {
        [Required]
        [MinLength(32, ErrorMessage = "JWT Secret must be at least 32 characters")]
        public string Secret { get; set; } = string.Empty;
        
        public string ValidIssuer { get; set; } = "AccountCore.API";
        public string ValidAudience { get; set; } = "AccountCore.Client";
        
        [Range(1, 1440, ErrorMessage = "Token validity must be between 1 and 1440 minutes")]
        public int TokenValidityInMinutes { get; set; } = 60;
        
        [Range(1, 30, ErrorMessage = "Refresh token validity must be between 1 and 30 days")]
        public int RefreshTokenValidityInDays { get; set; } = 7;
    }
}