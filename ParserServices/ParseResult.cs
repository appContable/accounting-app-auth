using System.Collections.Generic;
using ParserDAL.Models;

namespace ParserServices
{
    public class ParseResult
    {
        public BankStatement Statement { get; set; } = new BankStatement();
        public List<string> Warnings { get; set; } = new();
    }
}
