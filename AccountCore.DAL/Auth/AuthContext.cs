using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;
using Microsoft.Extensions.Configuration;

namespace AccountCore.DAL.Auth.Models
{
    public partial class AuthContext : DbContext
    {
        private readonly IConfiguration? _configuration;

        public AuthContext(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public AuthContext(DbContextOptions<AuthContext> options)
            : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                if (_configuration == null)
                {
                    throw new InvalidOperationException("Configuration is required to configure MongoDB.");
                }

                var connectionString = _configuration["MongoDB:ConnectionString"];
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException("MongoDB:ConnectionString configuration is missing or empty.");
                }

                var databaseName = _configuration["MongoDB:Database"];
                if (string.IsNullOrWhiteSpace(databaseName))
                {
                    throw new InvalidOperationException("MongoDB:Database configuration is missing or empty.");
                }

                optionsBuilder.UseMongoDB(connectionString, databaseName: databaseName);
            }
        }

        public virtual DbSet<Role> Roles { get; set; } = null!;

        public virtual DbSet<User> Users { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().HasNoDiscriminator()
                .ToCollection("Users");

            modelBuilder.Entity<Role>().HasNoDiscriminator()
                .ToCollection("Roles");

          
        }

    }
}
