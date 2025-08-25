// ParserDAL/Models/BankStatement.cs
using System;
using System.Collections.Generic;

namespace ParserDAL.Models
{
    public class ParseResult
    {
        public BankStatement Statement { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
