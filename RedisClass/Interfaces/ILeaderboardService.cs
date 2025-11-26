using RedisClass.Models;

namespace RedisClass.Interfaces
{
    public interface ILeaderboardService
    {
        Task<double> SetPlayerScoreAsync(string playerId, double score);
        Task<double> IncrementPlayerScoreAsync(string playerId, double increment);
        Task<double?> GetPlayerScoreAsync(string playerId);
        Task<long?> GetPlayerRankAsync(string playerId);
        Task<List<LeaderboardEntry>> GetTopPlayersAsync(int count = 10);
        Task<long> GetPlayerCountAsync();
    }
}
