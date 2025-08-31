namespace AccountCore.DAL.Parser.Models
{
    public class AccountStatement
    {
        public string AccountNumber { get; set; } = ""; // "22-04584827/3 (ARS)", "4049044-1 028-3 (USD)", etc.
        public List<Transaction> Transactions { get; set; } = new();

        public decimal? OpeningBalance { get; set; }   // Saldo inicial del período
        public decimal? ClosingBalance { get; set; }   // Saldo final del período
        
        /// <summary>
        /// Moneda de la cuenta: "ARS", "USD", etc.
        /// </summary>
        public string Currency { get; set; } = "ARS";
    }
}
