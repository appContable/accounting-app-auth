using System;
using System.Collections.Generic;

namespace AuthDAL.Models
{
    public partial class Function
    {
        public string Id { get; set; }
        public string FunctionKey { get; set; } = null!;
        public string Description { get; set; } = null!;
    }
}
