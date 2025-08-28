// AccountCore.DAL.Parser/Models/BankStatement.cs
using System;
using System.Collections.Generic;

namespace AccountCore.DAL.Parser.Models
{
    public class AccountStatement
    {
        public string AccountNumber { get; set; } = ""; // "22-04584827/3", "4049044-1 028-3", etc.
        public List<Transaction> Transactions { get; set; } = new();
    }
}
