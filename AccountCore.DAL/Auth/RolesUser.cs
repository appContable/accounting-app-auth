
namespace AccountCore.DAL.Auth.Models
{
    public partial class RoleUser
    {
        public string RoleId { get; set; }

        public string RoleKey { get; set; }

        public string RoleName { get; set; }

        public DateTime  CreationDate { get; set; }

        public bool Enable { get; set; }
    }
}
