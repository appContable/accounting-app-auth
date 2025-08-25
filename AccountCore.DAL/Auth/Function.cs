using System;
using System.Collections.Generic;

namespace AccountCore.DAL.Auth.Models
{
    public partial class Function
    {
        public string Id { get; set; }
        public string FunctionKey { get; set; } = null!;
        public string Description { get; set; } = null!;
    }
}
