using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using AccountCore.DTO.Auth.Validation;

namespace AccountCore.DTO.Auth.Entities
{
    public class AuthenticationDTO : IValidatableObject
    {
        /// <summary>
        /// User login (email or CUIT)
        /// </summary>
        [Required(ErrorMessage = "Login is required")]
        [JsonPropertyName("login")]
        public string? Login { get; set; }

        /// <summary>
        /// User Password
        /// </summary>
        [Required(ErrorMessage = "Password is required")]
        public string? Password { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (string.IsNullOrWhiteSpace(Login))
            {
                yield return new ValidationResult("Login is required", new[] { nameof(Login) });
                yield break;
            }

            var normalizedLogin = Login.Trim();
            if (!normalizedLogin.IsValidEmail() && !normalizedLogin.IsValidCuit())
            {
                yield return new ValidationResult("Login must be a valid email or CUIT", new[] { nameof(Login) });
            }
        }
    }
}
