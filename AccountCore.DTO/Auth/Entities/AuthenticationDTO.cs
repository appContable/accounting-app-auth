using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using AccountCore.DTO.Auth.Validation;

namespace AccountCore.DTO.Auth.Entities
{
    public class AuthenticationDTO
    {
        /// <summary>
        /// User login (email or CUIT)
        /// </summary>
        [JsonPropertyName("login")]
        public string? Login { get; set; }

        /// <summary>
        /// User Password
        /// </summary>
        [JsonPropertyName("password")]
        public string? Password { get; set; }
    }
}
