using System.ComponentModel.DataAnnotations;

namespace AccountCore.DTO.Auth.Configuration
{
    public class EmailSettings
    {
        [Required]
        public string BaseUrl { get; set; } = string.Empty;

        [Required]
        public string ApiKey { get; set; } = string.Empty;

        [Required]
        public string FromEmail { get; set; } = string.Empty;

        public string FromName { get; set; } = string.Empty;

        public string WelcomeSubject { get; set; } = "Bienvenido";

        public string ResetPasswordSubject { get; set; } = "Restablecé tu contraseña";

        public string WelcomeTemplateId { get; set; } = string.Empty;

        public string ResetPasswordTemplateId { get; set; } = string.Empty;

        public string UiBaseUrl { get; set; } = string.Empty;
    }
}
