using System;
using System.Collections.Generic;
using System.Security.Claims;

namespace AuthDAL.Models
{
    public partial class Role
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string RoleKey { get; set; }

        public bool IsEnabled { get; set; } = true;
    }
}
