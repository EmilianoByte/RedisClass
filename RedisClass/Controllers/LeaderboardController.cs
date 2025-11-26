using Microsoft.AspNetCore.Mvc;
using RedisClass.Interfaces;
using RedisClass.Models;
using RedisClass.Services;

namespace RedisClass.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LeaderboardController(
        ILeaderboardService leaderboardService,
        ILogger<LeaderboardController> logger) : ControllerBase
    {
        private readonly ILeaderboardService _leaderboardService = leaderboardService;
        private readonly ILogger<LeaderboardController> _logger = logger;

        /// <summary>
        /// Set or update player score
        /// </summary>
        [HttpPost("{playerId}/score")]
        public async Task<ActionResult<double>> SetPlayerScore(string playerId, [FromBody] double score)
        {
            try
            {
                var result = await _leaderboardService.SetPlayerScoreAsync(playerId, score);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting player score for {PlayerId}", playerId);
                return StatusCode(500, "Error setting player score");
            }
        }

        /// <summary>
        /// Increment player score atomically
        /// </summary>
        [HttpPost("{playerId}/score/increment")]
        public async Task<ActionResult<double>> IncrementPlayerScore(
            string playerId,
            [FromBody] double increment)
        {
            try
            {
                var newScore = await _leaderboardService.IncrementPlayerScoreAsync(playerId, increment);
                return Ok(newScore);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error incrementing score for {PlayerId}", playerId);
                return StatusCode(500, "Error incrementing score");
            }
        }

        /// <summary>
        /// Get player score
        /// </summary>
        [HttpGet("{playerId}/score")]
        public async Task<ActionResult<double>> GetPlayerScore(string playerId)
        {
            try
            {
                var score = await _leaderboardService.GetPlayerScoreAsync(playerId);

                if (!score.HasValue)
                    return NotFound($"Player {playerId} not found in leaderboard");

                return Ok(score.Value);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting score for {PlayerId}", playerId);
                return StatusCode(500, "Error getting score");
            }
        }

        /// <summary>
        /// Get player rank (1-based)
        /// </summary>
        [HttpGet("{playerId}/rank")]
        public async Task<ActionResult<long>> GetPlayerRank(string playerId)
        {
            try
            {
                var rank = await _leaderboardService.GetPlayerRankAsync(playerId);

                if (!rank.HasValue)
                    return NotFound($"Player {playerId} not found in leaderboard");

                return Ok(rank.Value);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting rank for {PlayerId}", playerId);
                return StatusCode(500, "Error getting rank");
            }
        }

        /// <summary>
        /// Get top N players from leaderboard
        /// </summary>
        [HttpGet("top")]
        public async Task<ActionResult<List<LeaderboardEntry>>> GetTopPlayers(
            [FromQuery] int count = 10)
        {
            try
            {
                if (count <= 0 || count > 100)
                    return BadRequest("Count must be between 1 and 100");

                var players = await _leaderboardService.GetTopPlayersAsync(count);
                return Ok(players);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting top players");
                return StatusCode(500, "Error getting top players");
            }
        }

        /// <summary>
        /// Get total player count in leaderboard
        /// </summary>
        [HttpGet("count")]
        public async Task<ActionResult<long>> GetPlayerCount()
        {
            try
            {
                var count = await _leaderboardService.GetPlayerCountAsync();
                return Ok(count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting player count");
                return StatusCode(500, "Error getting player count");
            }
        }
    }
}
