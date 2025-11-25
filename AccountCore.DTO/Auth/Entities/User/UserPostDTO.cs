using System.ComponentModel.DataAnnotations;

namespace AccountCore.DTO.Auth.Entities.User
{
    public class UserPostDTO
    {
        [Required(ErrorMessage = "First name is required")]
        [StringLength(100, ErrorMessage = "First name cannot exceed 100 characters")]
        public string? FirstName { get; set; }

        [Required(ErrorMessage = "Last name is required")]
        [StringLength(100, ErrorMessage = "Last name cannot exceed 100 characters")]
        public string? LastName { get; set; }

        [Required(ErrorMessage = "Cuit is required")]
        [RegularExpression("^[0-9]{11}$", ErrorMessage = "Cuit must be exactly 11 digits")]
        public string Cuit { get; set; } = null!;

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        [StringLength(256, ErrorMessage = "Email cannot exceed 256 characters")]
        public string Email { get; set; } = null!;

        [Required(ErrorMessage = "At least one role is required")]
        [MinLength(1, ErrorMessage = "At least one role must be assigned")]
        public string[]? RoleIds { get; set; }
    }
}
