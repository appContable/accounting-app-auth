namespace AccountCore.DTO.Auth.Entities.User
{
    public class UserPostDTO
    {
        public string? FirstName { get; set; }

        public string? LastName { get; set; }

        public string Email { get; set; } = null!;

        public string[]? RoleIds { get; set; }
    }
}
