using System.ComponentModel.DataAnnotations;

namespace AccountCore.DTO.Auth.Entities.User
{
    public class SetPasswordDTO
    {
        [Required(ErrorMessage = "Password is required")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be between 8 and 100 characters")]
        public string? Password { get; set; }

        [Required(ErrorMessage = "Password confirmation is required")]
        [Compare("Password", ErrorMessage = "Password and confirmation do not match")]
        public string? ConfirmPassword { get; set; }
    }
}
