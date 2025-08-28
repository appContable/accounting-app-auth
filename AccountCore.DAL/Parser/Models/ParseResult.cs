// AccountCore.DAL.Parser/Models/BankStatement.cs
using System;
using System.Collections.Generic;

namespace AccountCore.DAL.Parser.Models
{
    public class ParseResult
    {
        public BankStatement Statement { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
