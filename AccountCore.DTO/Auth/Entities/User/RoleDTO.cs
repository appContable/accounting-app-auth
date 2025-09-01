
namespace AccountCore.DTO.Auth.Entities
{
    public partial class RoleDTO
    {
        public string? Id { get; set; }
        public string Name { get; set; } = null!;
        public string Desctiption { get; set; } = null!;
        public string RoleKey { get; set; } = null!;

    }
}
