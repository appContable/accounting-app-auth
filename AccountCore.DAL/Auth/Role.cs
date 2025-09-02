
namespace AccountCore.DAL.Auth.Models
{
    public partial class Role
    {
        public string Id { get; set; } = null!;

        public string Name { get; set; } = null!;

        public string RoleKey { get; set; } = null!;

        public bool IsEnabled { get; set; } = true;
    }
}
