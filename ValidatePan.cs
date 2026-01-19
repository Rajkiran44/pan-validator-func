
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;

public class ValidatePan
{
    private readonly ILogger _logger;

    // PAN: 5 letters + 4 digits + 1 letter (uppercase)
    private static readonly Regex PanRegex = new(@"^[A-Z]{5}[0-9]{4}[A-Z]$", RegexOptions.Compiled);

    // Keep deny list out of logs; ideally move to App Config/KeyVault
    private static readonly HashSet<string> DenyList = new(StringComparer.OrdinalIgnoreCase) { "ABCDE1234F", "AAAAA1111A" };

    public ValidatePan(ILoggerFactory loggerFactory) => _logger = loggerFactory.CreateLogger<ValidatePan>();

    [Function("ValidatePan")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        // Default JSON options
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        try
        {
            var rawBody = await new StreamReader(req.Body, Encoding.UTF8).ReadToEndAsync();
            _logger.LogInformation("ValidatePan invoked. InvocationId={InvocationId}, BodyLength={Length}",
                req.FunctionContext.InvocationId, rawBody?.Length ?? 0);

            Request? payload;
            try
            {
                payload = JsonSerializer.Deserialize<Request>(rawBody, jsonOptions);
            }
            catch (Exception jsonEx)
            {
                _logger.LogWarning(jsonEx, "JSON deserialization failed. InvocationId={InvocationId}", req.FunctionContext.InvocationId);
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = "Invalid JSON payload. Expecting { pan: string, dob: 'yyyy-MM-dd' }" }, jsonOptions));
                return bad;
            }

            var res = req.CreateResponse();
            res.Headers.Add("Content-Type", "application/json");

            if (payload is null || string.IsNullOrWhiteSpace(payload.Pan) || string.IsNullOrWhiteSpace(payload.Dob))
            {
                res.StatusCode = HttpStatusCode.BadRequest;
                await res.WriteStringAsync(JsonSerializer.Serialize(new { error = "pan and dob are required" }, jsonOptions));
                return res;
            }

            var pan = payload.Pan.Trim().ToUpperInvariant();

            // Validate PAN format
            var isFormatValid = PanRegex.IsMatch(pan);

            // Deny list
            var isDenied = DenyList.Contains(pan);

            // DOB: enforce yyyy-MM-dd and 18+
            var dobRaw = payload.Dob.Trim();
            var dobFormatOk = DateOnly.TryParseExact(
                dobRaw,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dobParsed
            );

            var isAdult = dobFormatOk && dobParsed <= DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-18));

            // Build messages
            var messages = new List<string>();
            if (!isFormatValid) messages.Add("Invalid PAN format.");
            if (!dobFormatOk) messages.Add("DOB must be in yyyy-MM-dd format.");
            else if (!isAdult) messages.Add("Applicant must be 18+.");
            if (isDenied) messages.Add("PAN is deny-listed.");

            // Mask PAN in response to avoid echoing full identifier
            var maskedPan = MaskPan(pan);

            var result = new
            {
                pan = maskedPan,
                formatValid = isFormatValid,
                dobValid = dobFormatOk && isAdult,
                isDenied,
                canProceed = isFormatValid && dobFormatOk && isAdult && !isDenied,
                messages
            };

            res.StatusCode = HttpStatusCode.OK;
            await res.WriteStringAsync(JsonSerializer.Serialize(result, jsonOptions));
            return res;
        }
        catch (Exception ex)
        {
            // Never leak details to clients; log the exception server-side
            _logger.LogError(ex, "Unhandled error in ValidatePan. InvocationId={InvocationId}", req.FunctionContext.InvocationId);
            var res = req.CreateResponse(HttpStatusCode.InternalServerError);
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = "Internal server error" }));
            return res;
        }
    }

    private static string MaskPan(string pan)
    {
        // *****1234* (keep only digits visible)
        var m = Regex.Match(pan, @"^([A-Z]{5})([0-9]{4})([A-Z])$");
        if (!m.Success) return "**********";
        return $"*****{m.Groups[2].Value}*";
    }

    private record Request(string Pan, string Dob);
}
