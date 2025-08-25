using MongoDB.Bson;
using ParserDAL.Models;
using System;

namespace ParserServices.Interfaces
{
    public interface IParseUsageRepository
    {
        Task<List<ParseUsage>> GetAllAsync();
        Task<ParseUsage?> GetByIdAsync(ObjectId id);
        Task CreateAsync(ParseUsage usage);
        Task UpdateAsync(ParseUsage usage);
        Task DeleteAsync(ObjectId id);
        Task<int> CountByUserAsync(string userId, DateTime start, DateTime end);
    }
}
