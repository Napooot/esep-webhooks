using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace EsepWebhook
{
    public class Function
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        // API Gateway proxy handler
        public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            context.Logger.LogInformation("Lambda invoked via API Gateway proxy.");
            try
            {
                // Raw body from API Gateway proxy
                string rawBody = request?.Body ?? string.Empty;
                context.Logger.LogInformation($"Raw body (first 1000 chars): {rawBody.Substring(0, Math.Min(1000, rawBody.Length))}");

                JObject payload = null;

                // Try to parse the raw body as JSON
                if (!string.IsNullOrWhiteSpace(rawBody))
                {
                    try
                    {
                        payload = JObject.Parse(rawBody);
                    }
                    catch (JsonReaderException)
                    {
                        // rawBody might be a JSON-encoded string; try to unquote
                        if (rawBody.StartsWith("\"") && rawBody.EndsWith("\""))
                        {
                            try
                            {
                                string unquoted = JsonConvert.DeserializeObject<string>(rawBody);
                                payload = JObject.Parse(unquoted);
                            }
                            catch (Exception ex)
                            {
                                context.Logger.LogInformation($"Unquoted parse failed: {ex.Message}");
                            }
                        }
                    }
                }

                // If still null, see if gateway put the useful JSON in request.Body -> "body" (double-wrapped)
                if (payload == null && !string.IsNullOrWhiteSpace(rawBody))
                {
                    try
                    {
                        var root = JObject.Parse(rawBody);
                        if (root.TryGetValue("body", out JToken bodyToken))
                        {
                            string bodyString = bodyToken.Type == JTokenType.String ? bodyToken.ToString() : bodyToken.ToString(Formatting.None);
                            try
                            {
                                payload = JObject.Parse(bodyString);
                                context.Logger.LogInformation("Detected and parsed proxy-wrapped body.");
                            }
                            catch (JsonReaderException)
                            {
                                context.Logger.LogInformation("Proxy-wrapped body is not valid JSON.");
                            }
                        }
                    }
                    catch { /* ignore */ }
                }

                // Extract issue.html_url or fallback to issue.title or full payload
                string issueUrl = payload?.SelectToken("issue.html_url")?.ToString();
                if (string.IsNullOrWhiteSpace(issueUrl))
                {
                    issueUrl = payload?.SelectToken("issue.title")?.ToString() ?? payload?.ToString(Formatting.None) ?? "<no-issue>";
                }

                // Post to Slack
                string slackUrl = Environment.GetEnvironmentVariable("SLACK_URL");
                if (string.IsNullOrWhiteSpace(slackUrl))
                {
                    context.Logger.LogError("SLACK_URL not set.");
                    return new APIGatewayProxyResponse { StatusCode = 500, Body = "Missing SLACK_URL" };
                }

                var slackPayload = new { text = $"Issue Created: {issueUrl}" };
                string slackJson = JsonConvert.SerializeObject(slackPayload);

                context.Logger.LogInformation($"Posting to Slack (payload length {slackJson.Length})");
                var content = new StringContent(slackJson, Encoding.UTF8, "application/json");

                var httpResponse = await _httpClient.PostAsync(slackUrl, content);
                string slackResponseBody = await httpResponse.Content.ReadAsStringAsync();

                context.Logger.LogInformation($"Slack returned {(int)httpResponse.StatusCode}: {slackResponseBody.Substring(0, Math.Min(400, slackResponseBody.Length))}");

                if (!httpResponse.IsSuccessStatusCode)
                {
                    return new APIGatewayProxyResponse
                    {
                        StatusCode = 502,
                        Body = $"Slack returned {(int)httpResponse.StatusCode}: {slackResponseBody}"
                    };
                }

                // Success
                return new APIGatewayProxyResponse
                {
                    StatusCode = 200,
                    Body = "ok"
                };
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Unhandled exception: {ex}");
                return new APIGatewayProxyResponse
                {
                    StatusCode = 500,
                    Body = $"Exception: {ex.Message}"
                };
            }
        }
    }
}
