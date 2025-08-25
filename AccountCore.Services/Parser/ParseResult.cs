using System.Collections.Generic;
using AccountCore.DAL.Parser.Models;

namespace AccountCore.Services.Parser
{
    public class ParseResult
    {
        public BankStatement Statement { get; set; } = new BankStatement();
        public List<string> Warnings { get; set; } = new();
    }
}
