using Microsoft.AspNetCore.Mvc;
using RedisClass.Interfaces;
using RedisClass.Models;

namespace RedisClass.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TasksController(ITaskService taskService, ILogger<TasksController> logger) : ControllerBase
{
    private readonly ITaskService _taskService = taskService;
    private readonly ILogger<TasksController> _logger = logger;

    /// <summary>
    /// Get all tasks.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TaskItem>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TaskItem>>> GetAllTasks()
    {
        try
        {
            // Check rate limit (100 requests per minute).
            var userId = User.Identity?.Name ?? "anonymous";
            var allowed = await _taskService.CheckRateLimitAsync(userId);

            if (!allowed)
            {
                return StatusCode(429, new { error = "Rate limit exceeded. Try again later." });
            }

            var tasks = await _taskService.GetAllTasksAsync();
            return Ok(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all tasks");
            return StatusCode(500, "Error retrieving tasks from Redis");
        }       
    }

    /// <summary>
    /// Get a specific task by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(TaskItem), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskItem>> GetTask(string id)
    {
        try
        {
            var task = await _taskService.GetTaskAsync(id);

            if (task == null)
            {
                return NotFound($"Task with ID '{id}' not found");
            }

            return Ok(task);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving task {TaskId}", id);
            return StatusCode(500, "Error retrieving task from Redis");
        }
    }

    /// <summary>
    /// Create a new task.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TaskItem), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TaskItem>> CreateTask([FromBody] TaskItem task)
    {
        try
        {
            var createdTask = await _taskService.CreateTaskAsync(task);
            return CreatedAtAction(
                nameof(GetTask),
                new { id = createdTask.Id },
                createdTask);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating task");
            return StatusCode(500, "Error creating task in Redis");
        }
    }

    /// <summary>
    /// Update an existing task.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> UpdateTask(string id, [FromBody] TaskItem task)
    {
        if (id != task.Id)
        {
            return BadRequest("ID in URL does not match ID in request body");
        }

        try
        {
            var success = await _taskService.UpdateTaskAsync(task);

            if (!success)
            {
                return NotFound($"Task with ID '{id}' not found");
            }

            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating task {TaskId}", id);
            return StatusCode(500, "Error updating task in Redis");
        }
    }

    /// <summary>
    /// Delete a task.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteTask(string id)
    {
        try
        {
            var success = await _taskService.DeleteTaskAsync(id);

            if (!success)
            {
                return NotFound($"Task with ID '{id}' not found");
            }

            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting task {TaskId}", id);
            return StatusCode(500, "Error deleting task from Redis");
        }
    }

    /// <summary>
    /// Get task statistics.
    /// </summary>
    [HttpGet("statistics")]
    [ProducesResponseType(typeof(Dictionary<string, int>), StatusCodes.Status200OK)]
    public async Task<ActionResult<Dictionary<string, int>>> GetStatistics()
    {
        try
        {
            var stats = await _taskService.GetStatisticsAsync();
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving statistics");
            return StatusCode(500, "Error retrieving statistics from Redis");
        }
    }

    /// <summary>
    /// Get activity log for a specific task
    /// </summary>
    [HttpGet("{id}/log")]
    public async Task<ActionResult<List<string>>> GetTaskLog(string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Task ID cannot be empty");
            }

            var log = await _taskService.GetActivityLogAsync(id);

            if (log.Count == 0)
            {
                return NotFound($"No activity log found for task {id}");
            }

            return Ok(log);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting activity log for task {TaskId}", id);
            return StatusCode(500, "Error retrieving activity log");
        }
    }
}
