using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using MongoDB.EntityFrameworkCore.Extensions;

namespace AccountCore.DAL.Auth.Models
{
    public partial class AuthContext : DbContext
    {
        private readonly IConfiguration _configuration;

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
                optionsBuilder.UseMongoDB(_configuration["ConnectionString"], databaseName: _configuration["DatabaseId"]);
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
