using Microsoft.AspNetCore.Mvc;
using RedisClass.Models;
using TaskAPI.Services;

namespace TaskAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly IUserService _userService;
        private readonly ILogger<UsersController> _logger;

        public UsersController(IUserService userService, ILogger<UsersController> logger)
        {
            _userService = userService;
            _logger = logger;
        }

        /// <summary>
        /// Create or update user profile
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<UserProfile>> CreateOrUpdateUser([FromBody] CreateUserRequest request)
        {
            try
            {
                var user = await _userService.CreateOrUpdateUserAsync(
                    request.UserId,
                    request.Name,
                    request.Email
                );
                return Ok(user);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating/updating user");
                return StatusCode(500, "Error creating/updating user");
            }
        }

        /// <summary>
        /// Get user profile.
        /// </summary>
        [HttpGet("{userId}")]
        public async Task<ActionResult<UserProfile>> GetUser(string userId)
        {
            try
            {
                var user = await _userService.GetUserAsync(userId);
                if (user == null)
                {
                    return NotFound($"User {userId} not found");
                }
                return Ok(user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user {UserId}", userId);
                return StatusCode(500, "Error retrieving user");
            }
        }

        /// <summary>
        /// Get all users.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<UserProfile>>> GetAllUsers()
        {
            try
            {
                var users = await _userService.GetAllUsersAsync();
                return Ok(users);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all users");
                return StatusCode(500, "Error retrieving users");
            }
        }

        /// <summary>
        /// Increment tasks created counter.
        /// </summary>
        [HttpPost("{userId}/increment-created")]
        public async Task<IActionResult> IncrementTasksCreated(string userId)
        {
            try
            {
                await _userService.IncrementTasksCreatedAsync(userId);
                return Ok(new { message = "Counter incremented" });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error incrementing counter for user {UserId}", userId);
                return StatusCode(500, "Error incrementing counter");
            }
        }

        /// <summary>
        /// Increment tasks completed counter.
        /// </summary>
        [HttpPost("{userId}/increment-completed")]
        public async Task<IActionResult> IncrementTasksCompleted(string userId)
        {
            try
            {
                await _userService.IncrementTasksCompletedAsync(userId);
                return Ok(new { message = "Counter incremented" });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error incrementing counter for user {UserId}", userId);
                return StatusCode(500, "Error incrementing counter");
            }
        }
    }

    public class CreateUserRequest
    {
        public string UserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}
