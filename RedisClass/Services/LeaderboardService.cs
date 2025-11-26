using RedisClass.Interfaces;
using RedisClass.Models;
using StackExchange.Redis;

namespace RedisClass.Services
{
    public class LeaderboardService(IConnectionMultiplexer redis, ILogger<LeaderboardService> logger) : ILeaderboardService
    {
        private readonly IDatabase _redis = redis.GetDatabase();
        private readonly ILogger<LeaderboardService> _logger = logger;

        private const string LeaderboardKey = "leaderboard:global";

        /// <summary>
        /// Sets or updates a player's score (ZADD)
        /// </summary>
        public async Task<double> SetPlayerScoreAsync(string playerId, double score)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                throw new ArgumentException("Player ID cannot be empty", nameof(playerId));

            if (score < 0)
                throw new ArgumentException("Score cannot be negative", nameof(score));

            // ZADD: add or update element with score
            await _redis.SortedSetAddAsync(LeaderboardKey, playerId, score);

            _logger.LogInformation("Player {PlayerId} score set to {Score}", playerId, score);

            return score;
        }

        /// <summary>
        /// Increments a player's score atomically (ZINCRBY)
        /// </summary>
        public async Task<double> IncrementPlayerScoreAsync(string playerId, double increment)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                throw new ArgumentException("Player ID cannot be empty", nameof(playerId));

            // ZINCRBY: atomic increment operation
            // No race condition even with concurrent requests
            var newScore = await _redis.SortedSetIncrementAsync(LeaderboardKey, playerId, increment);

            _logger.LogInformation(
                "Player {PlayerId} score incremented by {Increment}, new score: {NewScore}",
                playerId, increment, newScore);

            return newScore;
        }

        /// <summary>
        /// Gets a player's score (ZSCORE - O(1))
        /// </summary>
        public async Task<double?> GetPlayerScoreAsync(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                throw new ArgumentException("Player ID cannot be empty", nameof(playerId));

            // ZSCORE: get score of member, O(1) operation
            var score = await _redis.SortedSetScoreAsync(LeaderboardKey, playerId);

            if (!score.HasValue)
            {
                _logger.LogWarning("Player {PlayerId} not found in leaderboard", playerId);
            }

            return score;
        }

        /// <summary>
        /// Gets a player's rank (1-based, highest score = rank 1)
        /// Uses ZREVRANK for descending order
        /// </summary>
        public async Task<long?> GetPlayerRankAsync(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                throw new ArgumentException("Player ID cannot be empty", nameof(playerId));

            // ZREVRANK: get position in descending order (highest score first)
            // Returns 0-based index, we convert to 1-based rank
            var rank = await _redis.SortedSetRankAsync(
                LeaderboardKey,
                playerId,
                order: Order.Descending);

            if (!rank.HasValue)
            {
                _logger.LogWarning("Player {PlayerId} not found in leaderboard", playerId);
                return null;
            }

            // Convert 0-based to 1-based rank
            return rank.Value + 1;
        }

        /// <summary>
        /// Gets top N players (ZREVRANGE - descending by score)
        /// </summary>
        public async Task<List<LeaderboardEntry>> GetTopPlayersAsync(int count = 10)
        {
            if (count <= 0)
                throw new ArgumentException("Count must be positive", nameof(count));

            // ZREVRANGE WITHSCORE: get elements in descending order by score
            // 0 = highest score, count-1 = Nth highest score
            var entries = await _redis.SortedSetRangeByRankWithScoresAsync(
                LeaderboardKey,
                start: 0,
                stop: count - 1,
                order: Order.Descending);

            var result = new List<LeaderboardEntry>();
            var rank = 1;

            foreach (var entry in entries)
            {
                result.Add(new LeaderboardEntry
                {
                    Rank = rank++,
                    PlayerId = entry.Element.ToString(),
                    Score = entry.Score
                });
            }

            return result;
        }

        /// <summary>
        /// Gets total number of players in leaderboard (ZCARD - O(1))
        /// </summary>
        public async Task<long> GetPlayerCountAsync()
        {
            // ZCARD: count elements in sorted set
            return await _redis.SortedSetLengthAsync(LeaderboardKey);
        }
    }
}
