using Microsoft.AspNetCore.Mvc;
using RedisClass.Interfaces.Vins;
using RedisClass.Models.Vins;

namespace RedisClass.Controllers.Vins;

[ApiController]
[Route("api/[controller]")]
public class VinController : ControllerBase
{
    private readonly IVinService _vinService;
    private readonly ILogger<VinController> _logger;

    public VinController(IVinService vinService, ILogger<VinController> logger)
    {
        _vinService = vinService;
        _logger = logger;
    }

    /// <summary>
    /// Process a batch of VIN records from external document.
    /// </summary>
    [HttpPost("process-batch")]
    public async Task<ActionResult<VinCheckResult>> ProcessBatch(
        [FromBody] List<VinRecord> records,
        [FromQuery] string? batchId = null)
    {
        try
        {
            if (records == null || records.Count == 0)
                return BadRequest("Records list cannot be empty");

            var result = await _vinService.ProcessBatchAsync(records, batchId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing VIN batch");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get VIN record by chassis number (telaio).
    /// </summary>
    [HttpGet("telaio/{telaio}")]
    public async Task<ActionResult<VinRecord>> GetByTelaio(string telaio)
    {
        try
        {
            var record = await _vinService.GetByTelaioAsync(telaio);
            if (record == null)
                return NotFound($"Telaio {telaio} not found");

            return Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting VIN by telaio");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get VIN record by license plate (targa).
    /// </summary>
    [HttpGet("targa/{targa}")]
    public async Task<ActionResult<VinRecord>> GetByTarga(string targa)
    {
        try
        {
            var record = await _vinService.GetByTargaAsync(targa);
            if (record == null)
                return NotFound($"Targa {targa} not found");

            return Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting VIN by targa");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Clear all VIN data (use with caution).
    /// </summary>
    [HttpDelete("clear-all")]
    public async Task<IActionResult> ClearAll()
    {
        try
        {
            await _vinService.ClearAllAsync();
            return Ok(new { message = "All VIN data cleared" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing VIN data");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
