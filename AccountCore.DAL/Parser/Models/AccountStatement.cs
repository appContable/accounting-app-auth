namespace AccountCore.DAL.Parser.Models
{
    public class AccountStatement
    {
        public string AccountNumber { get; set; } = ""; // "22-04584827/3", "4049044-1 028-3", etc.
        public List<Transaction> Transactions { get; set; } = new();

        public decimal? OpeningBalance { get; set; }   // Saldo inicial del período
        public decimal? ClosingBalance { get; set; }   // Saldo final del período
    }
}
