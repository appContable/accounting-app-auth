using System.ComponentModel.DataAnnotations;

namespace AccountCore.DTO.Auth.Entities
{
    public class AuthenticationDTO
    {
        /// <summary>
        /// User mail
        /// </summary>
        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        public string? Email { get; set; }

        /// <summary>
        /// User Password
        /// </summary>
        [Required(ErrorMessage = "Password is required")]
        public string? Password { get; set; }
    }
}
