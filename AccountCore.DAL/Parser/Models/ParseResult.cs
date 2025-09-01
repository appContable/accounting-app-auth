namespace AccountCore.DAL.Parser.Models
{
    public class ParseResult
    {
        public BankStatement Statement { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
