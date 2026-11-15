using Azure.Core;
using AzureLabs.Api.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureLabs.Api.Controllers
{
    public sealed record UploadSasRequest(string FileName);

    [ApiController]
    [Authorize(Policy = "ApiAccess")]
    [Route("events/{eventId:guid}/documents")]
    public class EventDocumentsController(EventDocumentStore eventDocumentStore) : ControllerBase
    {
        private const long BodyLimit = DocumentFileRules.MaxBytes + 64 * 1024;

        [HttpPost]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(BodyLimit)]
        [RequestFormLimits(MultipartBodyLengthLimit = BodyLimit)]
        [ProducesResponseType(typeof(UploadResult), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(UploadResult), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(UploadResult), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Upload([FromRoute] Guid eventId, [FromForm] IFormFile file, [FromQuery] bool overWrite, CancellationToken ct)
        {
            var (Rule, FileName, Error) = await DocumentFileRules.ValidateAsync(file, ct);
            if (Error is not null)
            {
                return Problem(title: "Invalid File", detail: Error, statusCode: StatusCodes.Status400BadRequest);
            }

            await using var stream = file.OpenReadStream();

            var result = await eventDocumentStore.UploadAsync(eventId, FileName, Rule!.ContentType, stream, overWrite, ct);

            if (result.Conflict)
            {
                return Problem(title: $"A document with the name '{result.BlobName}' already exists for event '{eventId}. Use ?OverWrite=true' to replace it.", statusCode: StatusCodes.Status409Conflict);
            }

            return Created($"/events/{eventId}/documents/{FileName}/download-sas", result);

        }

        [HttpGet("{fileName}/download-sas")]
        [ProducesResponseType(typeof(SasResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(SasResult), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(SasResult), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GenerateDownloadSas([FromRoute] Guid eventId, [FromRoute] string fileName, CancellationToken ct)
        {
            if(!DocumentFileRules.IsSafeName(fileName))
            {
                return Problem(title: "Invalid File Name", detail: "The provided file name is not allowed.", statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await eventDocumentStore.GetDownloadSasAsync(eventId, fileName, ct);

            if(result is null)
            {
                return Problem(title: "File Not Found", detail: $"The document '{fileName}' for event '{eventId}' does not exist.", statusCode: StatusCodes.Status404NotFound);
            }

            Response.Headers.CacheControl = "no-store";
            return Ok(result);
        }

        [HttpGet("upload-sas")]
        [ProducesResponseType(typeof(SasResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(SasResult), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetUploadSas([FromRoute] Guid eventId, [FromBody] UploadSasRequest request, CancellationToken ct)
        {
            if (!DocumentFileRules.TryGetRule(request.FileName, out var fileRule))
            {
                return Problem(title: "Invalid File Name or Type", statusCode: StatusCodes.Status400BadRequest);
            }

            Response.Headers.CacheControl = "no-store";
            return Ok(await eventDocumentStore.GetUploadSasAsync(eventId, request.FileName, fileRule.ContentType, ct));
        }

        [HttpGet("{fileName}/complete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
        public async Task<IActionResult> Complete([FromRoute] Guid eventId, [FromRoute] string fileName, CancellationToken ct)
        {
            if (!DocumentFileRules.TryGetRule(fileName, out var fileRule))
            {
                return Problem(title: "Invalid File Name or Type", statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await eventDocumentStore.CompleteAsync(eventId, fileName, fileRule, ct);

            return result.Status switch
            {
                CompleteStatus.Completed => Ok(new { status = "Active", eTag = result.ETag }),
                CompleteStatus.NotFound => Problem(title: "File Not Found", detail: $"The document '{fileName}' for event '{eventId}' does not exist.", statusCode: StatusCodes.Status404NotFound),
                CompleteStatus.Invalid => Problem(title: "File Validation Error", detail: result.Error, statusCode: StatusCodes.Status400BadRequest),
                _ => Problem(title: "Unknown Error", statusCode: StatusCodes.Status422UnprocessableEntity)
            };

        }


    }
}