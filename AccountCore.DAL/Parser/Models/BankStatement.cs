namespace AccountCore.DAL.Parser.Models
{
    public class BankStatement
    {
        public string Bank { get; set; } = "";
        public DateTime? PeriodStart { get; set; }
        public DateTime? PeriodEnd { get; set; }

        // Multi-cuenta: un item por cuenta
        public List<AccountStatement> Accounts { get; set; } = new();
    }
}
