
namespace AccountCore.DAL.Auth.Models
{
    public partial class RoleUser
    {
        public string RoleId { get; set; } = null!;

        public string RoleKey { get; set; } = null!;

        public string RoleName { get; set; } = null!;

        public DateTime  CreationDate { get; set; }

        public bool Enable { get; set; }
    }
}
