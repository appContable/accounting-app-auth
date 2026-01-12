using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using AccountCore.DTO.Auth.Validation;

namespace AccountCore.DTO.Auth.Entities
{
    public class AuthenticationDTO
    {
        private string? _login;

        /// <summary>
        /// User login (email or CUIT)
        /// </summary>
        [JsonPropertyName("login")]
        public string? Login
        {
            get => _login;
            set => _login = value;
        }

        /// <summary>
        /// Alias para compatibilidad con clientes que envían "email" en lugar de "login".
        /// </summary>
        [JsonPropertyName("email")]
        public string? Email
        {
            get => _login;
            set => _login = value;
        }

        /// <summary>
        /// User Password
        /// </summary>
        [JsonPropertyName("password")]
        public string? Password { get; set; }
    }
}
