using MongoDB.Bson;
using AccountCore.DAL.Parser.Models;
using System;

namespace AccountCore.Services.Parser.Interfaces
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
