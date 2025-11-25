using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using AccountCore.DTO.Auth.Validation;

namespace AccountCore.DTO.Auth.Entities
{
    public class ResetPasswordRequest : IValidatableObject
    {
        [Required(ErrorMessage = "Email is required")]
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (string.IsNullOrWhiteSpace(Email))
            {
                yield return new ValidationResult("Email is required", new[] { nameof(Email) });
                yield break;
            }

            if (!Email.IsValidEmail())
            {
                yield return new ValidationResult("Email must be valid", new[] { nameof(Email) });
            }
        }
    }
}
