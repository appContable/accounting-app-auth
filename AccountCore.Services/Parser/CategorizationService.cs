using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

using AccountCore.DAL.Parser.Models;                 // ParseResult, AccountStatement, Transaction, RulePatternType, UserCategoryRule, BankCategoryRule
using AccountCore.Services.Parser.Interfaces;        // ICategorizationService, IUserCategoryRuleRepository, IBankCategoryRuleRepository
namespace AccountCore.Services.Parser
{
    public class CategorizationService : ICategorizationService
    {
        private readonly IUserCategoryRuleRepository _userRepo;
        private readonly IBankCategoryRuleRepository _bankRepo;

        public CategorizationService(
            IUserCategoryRuleRepository userRepo,
            IBankCategoryRuleRepository bankRepo)
        {
            _userRepo = userRepo;
            _bankRepo = bankRepo;
        }

        public async Task ApplyAsync(ParseResult result, string bank, string userId, CancellationToken cancellationToken)
        {
            var bankKey = (bank ?? string.Empty).Trim();

            // 1) Reglas de banco desde Mongo (OJO: Enabled)
            var bankRulesAll = await _bankRepo.GetByBankAsync(bankKey, cancellationToken);
            var bankRules = (bankRulesAll ?? new List<BankCategoryRule>())
                            .Where(r => r.Enabled)
                            .ToList();

            // 2) Reglas de usuario desde Mongo (OJO: Active)
            var userRulesAll = await _userRepo.GetByUserAndBankAsync(userId, bankKey, cancellationToken);
            var userRules = (userRulesAll ?? new List<UserCategoryRule>())
                            .Where(r => r.Active)
                            .ToList();

            // 3) Unimos y ordenamos por prioridad (menor = más fuerte)
            var compiled = new List<(Guid? id, string pattern, RulePatternType type, string category, string? sub, int pri, string src)>();

            compiled.AddRange(bankRules.Select(r =>
                ((Guid?)null, r.Pattern, r.PatternType, r.Category, r.Subcategory, r.Priority, "BankRule")));

            compiled.AddRange(userRules.Select(r =>
                (r.Id == Guid.Empty ? (Guid?)null : r.Id, r.Pattern, r.PatternType, r.Category, r.Subcategory, r.Priority, "UserLearned")));

            var ordered = compiled.OrderBy(x => x.pri).ToList();

            // 4) Aplicar
            foreach (var acc in result.Statement.Accounts ?? Enumerable.Empty<AccountStatement>())
                foreach (var t in acc.Transactions ?? Enumerable.Empty<Transaction>())
                {
                    var text = Normalize(t.Description);
                    foreach (var rule in ordered)
                    {
                        if (Matches(text, rule.pattern, rule.type))
                        {
                            t.Category = rule.category;
                            t.Subcategory = rule.sub;
                            t.CategorySource = rule.src;
                            // t.CategoryRuleId = rule.id; // si tu modelo lo expone como Guid?
                            break; // primera coincidencia gana
                        }
                    }
                }
        }

        public async Task<UserCategoryRule> LearnAsync(AccountCore.DTO.Parser.Parameters.LearnRuleRequest req, CancellationToken ct = default)
        {
            var type = Enum.TryParse<RulePatternType>(req.PatternType, true, out var parsed)
                ? parsed : RulePatternType.Contains;

            var rule = new UserCategoryRule
            {
                UserId = req.UserId.Trim(),
                Bank = req.Bank.Trim(),
                Pattern = req.Pattern.Trim(),
                PatternType = type,
                Category = req.Category.Trim(),
                Subcategory = string.IsNullOrWhiteSpace(req.Subcategory) ? null : req.Subcategory.Trim(),
                Priority = req.Priority ?? 100,
                Origin = RuleOrigin.Manual,
                Active = true,
                UpdatedAt = DateTime.UtcNow
            };

            await _userRepo.UpsertAsync(rule, ct);
            return rule;
        }

        private static string Normalize(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var formD = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (var ch in formD)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString()
                     .Normalize(NormalizationForm.FormC)
                     .ToUpperInvariant()
                     .Trim();
        }

        private static bool Matches(string text, string pattern, RulePatternType type) => type switch
        {
            RulePatternType.Equals => text == Normalize(pattern),
            RulePatternType.StartsWith => text.StartsWith(Normalize(pattern)),
            RulePatternType.EndsWith => text.EndsWith(Normalize(pattern)),
            RulePatternType.Regex => SafeRegex(text, pattern),
            _ => text.Contains(Normalize(pattern)),
        };

        private static bool SafeRegex(string text, string pat)
        {
            try { return Regex.IsMatch(text, pat, RegexOptions.IgnoreCase); }
            catch { return false; }
        }
    }
}
