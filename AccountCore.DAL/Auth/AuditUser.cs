namespace AccountCore.DAL.Auth.Models
{
    /// <summary>
    /// Mapeo del usuario creador de las entidades.
    /// </summary>
    public class AuditUser
    {
        public string? Id { get; set; }

        public string? FirstName { get; set; }

        public string? LastName { get; set; }

        public string Email { get; set; } = null!;

    }
}
