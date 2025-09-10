using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace GetAccountsLambda
{
    public class Function
    {
        private static readonly HttpClient Http = new HttpClient();

        public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            var headers = request?.Headers ?? new Dictionary<string,string>();
            var correlationId = headers.ContainsKey("X_CORRELATION_ID") ? headers["X_CORRELATION_ID"] : context.AwsRequestId;
            context.Logger.LogLine($"START - Request received - {correlationId}");

            string email = null;
            if (request?.QueryStringParameters != null && request.QueryStringParameters.TryGetValue("email", out var e))
                email = e?.Trim();

            if (string.IsNullOrEmpty(email))
            {
                var bad = new { error = new { errorCode = 400, errorDateTime = DateTime.UtcNow, errorMessage = "BAD REQUEST", errorDescription = "email query parameter is required" } };
                return new APIGatewayProxyResponse { StatusCode = 400, Body = JsonSerializer.Serialize(bad), Headers = new Dictionary<string,string>{{"Content-Type","application/json"}} };
            }

            // escape single quotes by doubling them for SOQL
            var soqlEmail = email.Replace("'", "''");
            var instanceUrl = Environment.GetEnvironmentVariable("SALESFORCE_INSTANCE_URL");
            var accessToken = Environment.GetEnvironmentVariable("SALESFORCE_ACCESS_TOKEN");

            if (string.IsNullOrEmpty(instanceUrl) || string.IsNullOrEmpty(accessToken))
            {
                var err = new { error = "Salesforce credentials not configured" };
                return new APIGatewayProxyResponse { StatusCode = 500, Body = JsonSerializer.Serialize(err), Headers = new Dictionary<string,string>{{"Content-Type","application/json"}} };
            }

            var soql = $"select id, Account_Status__c, Salutation, firstname, lastname, PersonEmail, PersonBirthdate, Phone, PersonMobilePhone, PersonMailingAddress, Hotlisted__c, Source__c from account where PersonEmail = '{soqlEmail}'";
            var url = instanceUrl.TrimEnd('/') + "/services/data/v57.0/query?q=" + Uri.EscapeDataString(soql);

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage res;
            try
            {
                res = await Http.SendAsync(req);
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Salesforce request failed: {ex.Message}");
                var err = new { error = ex.Message };
                return new APIGatewayProxyResponse { StatusCode = 502, Body = JsonSerializer.Serialize(err), Headers = new Dictionary<string,string>{{"Content-Type","application/json"}} };
            }

            var content = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                context.Logger.LogLine($"Salesforce returned error {res.StatusCode}: {content}");
                var err = new { error = content };
                return new APIGatewayProxyResponse { StatusCode = 502, Body = JsonSerializer.Serialize(err), Headers = new Dictionary<string,string>{{"Content-Type","application/json"}} };
            }

            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("records", out var records) || records.GetArrayLength() == 0)
            {
                // No records found => 204 with empty array (as in original Mule flow)
                return new APIGatewayProxyResponse { StatusCode = 204, Body = "[]", Headers = new Dictionary<string,string>{{"Content-Type","application/json"}} };
            }

            var list = new List<object>();
            foreach (var r in records.EnumerateArray())
            {
                string GetTrim(string prop)
                {
                    if (r.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null)
                        return v.ToString().Trim();
                    return null;
                }

                string mailingStreet = null, mailingPostalCode = null, mailingCity = null, mailingCountry = null;
                if (r.TryGetProperty("PersonMailingAddress", out var addr) && addr.ValueKind == JsonValueKind.Object)
                {
                    if (addr.TryGetProperty("street", out var s)) mailingStreet = s.ToString().Trim();
                    if (addr.TryGetProperty("postalCode", out var p)) mailingPostalCode = p.ToString().Trim();
                    if (addr.TryGetProperty("city", out var c)) mailingCity = c.ToString().Trim();
                    if (addr.TryGetProperty("country", out var co)) mailingCountry = co.ToString().Trim();
                }

                bool? hotlisted = null;
                if (r.TryGetProperty("Hotlisted__c", out var hot) && hot.ValueKind != JsonValueKind.Null)
                {
                    if (hot.ValueKind == JsonValueKind.True || hot.ValueKind == JsonValueKind.False)
                        hotlisted = hot.GetBoolean();
                }

                var obj = new {
                    id = GetTrim("Id"),
                    salutation = GetTrim("Salutation"),
                    firstName = GetTrim("FirstName"),
                    lastName = GetTrim("LastName"),
                    personBirthdate = r.TryGetProperty("PersonBirthdate", out var bd) && bd.ValueKind != JsonValueKind.Null ? bd.ToString() : null,
                    phone = GetTrim("Phone"),
                    mobilePhone = GetTrim("PersonMobilePhone"),
                    personEmail = GetTrim("PersonEmail"),
                    mailingStreet = mailingStreet,
                    mailingPostalCode = mailingPostalCode,
                    mailingCity = mailingCity,
                    mailingCountry = mailingCountry,
                    accountStatus = GetTrim("Account_Status__c"),
                    accountSource = GetTrim("Source__c") ?? string.Empty,
                    hotlisted = hotlisted
                };

                list.Add(obj);
            }

            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            var body = JsonSerializer.Serialize(list, options);
            context.Logger.LogLine($"END - {correlationId}");
            return new APIGatewayProxyResponse { StatusCode = 200, Body = body, Headers = new Dictionary<string,string>{{"Content-Type","application/json"}} };
        }
    }
}
