namespace AuthDTO.Entities.User
{
    public partial class UserDTO
    {
        public string? Id { get; set; }

        public string? FirstName { get; set; }

        public string? LastName { get; set; }

        public string Email { get; set; } = null!;
        public string? RefreshToken { get; set; }
        public DateTime? RefreshTokenExpiryTime { get; set; }

        public bool IsLock { get; set; } = false;

        public bool? IsActive { get; set; } = false;

        public DateTime CreationDate { get; set; }

        public List<RoleUserDTO>? Roles { get; set; } = null;
    }
}
