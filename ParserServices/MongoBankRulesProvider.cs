using System.Collections.Generic;
using System.Linq;
using ParserServices.Interfaces;     // IBankRulesProvider, IBankCategoryRuleRepository
using DAL = ParserDAL.Models;        // BankCategoryRule

namespace ParserServices
{
    /// <summary>
    /// Implementación de IBankRulesProvider que trae reglas globales desde Mongo
    /// y las adapta al shape dinámico que hoy consume tu CategorizationService.
    /// </summary>
    public class MongoBankRulesProvider : IBankRulesProvider
    {
        private readonly IBankCategoryRuleRepository _repo;

        public MongoBankRulesProvider(IBankCategoryRuleRepository repo)
        {
            _repo = repo;
        }

        public IReadOnlyList<BankRule> GetForBank(string bank)
        {
            // NOTA: La interfaz es síncrona, así que bloqueamos el async del repo.
            var rules = _repo.GetByBankAsync(bank).GetAwaiter().GetResult()
                        ?? new List<DAL.BankCategoryRule>();

            // Solo reglas habilitadas
            rules = rules.Where(r => r.Enabled).ToList();

            // Adaptamos al shape que espera tu CategorizationService actual:
            // { Pattern, PatternType, Category, Subcategory, Priority }
            var bankRules = rules.Select(r => new BankRule(
                r.Pattern,
                r.PatternType,
                r.Category,
                r.Subcategory,
                r.Priority
            )).ToList();

            return bankRules;
        }
    }
}
