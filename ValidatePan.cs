
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

public class ValidatePan
{
    private readonly ILogger _logger;
    private static readonly Regex PanRegex = new(@"^[A-Z]{5}[0-9]{4}[A-Z]$", RegexOptions.Compiled);
    private static readonly HashSet<string> DenyList = new(StringComparer.OrdinalIgnoreCase) { "ABCDE1234F", "AAAAA1111A" };

    public ValidatePan(ILoggerFactory loggerFactory) => _logger = loggerFactory.CreateLogger<ValidatePan>();

    [Function("ValidatePan")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var payload = JsonSerializer.Deserialize<Request>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var res = req.CreateResponse();
        res.Headers.Add("Content-Type", "application/json");

        if (payload is null || string.IsNullOrWhiteSpace(payload.Pan) || string.IsNullOrWhiteSpace(payload.Dob))
        {
            res.StatusCode = HttpStatusCode.BadRequest;
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = "pan and dob are required" }));
            return res;
        }

        var isFormatValid = PanRegex.IsMatch(payload.Pan.Trim());
        var isDenied = DenyList.Contains(payload.Pan.Trim());
        var dobIsValid = DateOnly.TryParse(payload.Dob, out var dob) && dob <= DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-18));

        var result = new
        {
            pan = payload.Pan,
            formatValid = isFormatValid,
            dobValid = dobIsValid,
            isDenied,
            canProceed = isFormatValid && dobIsValid && !isDenied,
            messages = new[]
            {
                !isFormatValid ? "Invalid PAN format." : null,
                !dobIsValid ? "Applicant must be 18+ and DOB must be valid." : null,
                isDenied ? "PAN is deny-listed." : null
            }.Where(m => m != null)
        };

        res.StatusCode = HttpStatusCode.OK;
        await res.WriteStringAsync(JsonSerializer.Serialize(result));
        return res;
    }

    private record Request(string Pan, string Dob);
}
