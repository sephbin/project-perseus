using ProjectPerseus.revit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;



using ProjectPerseus.logging;
using ProjectPerseus.util;
namespace ProjectPerseus.web
{

    public class ProjectPerseusWeb
    {
        private static void WriteLog(string content)
        {
            string roamingFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appSpecificFolderPath = Path.Combine(roamingFolderPath, "ProjectPerseus");
            Directory.CreateDirectory(appSpecificFolderPath); // Creates the directory if it doesn't exist
            string filePath = Path.Combine(appSpecificFolderPath, "medusa.log");
            try
            {
                File.AppendAllText(filePath, content + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving file: {ex.Message}");
            }
        }
        private string _baseUrl;
        private string _apiToken;
        //private string ElementsEndpoint => $"{_baseUrl}/rapi/elements/";
        private string ElementsEndpoint => $"{_baseUrl}/add_to_crud_queue/";
        

        public ProjectPerseusWeb(string baseUrl, string apiToken)
        {
            _baseUrl = baseUrl;
            _apiToken = apiToken;

        }

        // Send as a single request if the serialized JSON is under this size (70 MB).
        // When chunking is required, chunk size is derived from actual element density
        // so each chunk targets ~90% of this limit.
        private const int MaxSinglePayloadBytes = 70 * 1024 * 1024;

        public void SubmitElementDeltas(IList<models.ElementDelta> elementDeltas, IList<long> deleted, Document doc, string batchId = null)
        {
            string token = ProjectPerseus.auth.AuthService.GetAuthTokenSafely();
            string scheme = ProjectPerseus.auth.AuthService.GetAuthSchemeSafely();
            if (string.IsNullOrEmpty(token))
            {
                Log.Info("Sync aborted: User failed authentication.");
                return;
            }

            var revit = new RevitFacade(doc);
            var docGuid = ModelGuidStorage.GetOrCreate(revit.Document);
            var currentModelVersion = RevitFacade.GetDocumentVersionGuid(revit.Document);

            var app = doc.Application;
            string revitUsername = app.Username;
            string revitAccountId = app.LoginUserId;
            string windowsUsername = Environment.UserName;
            string machineName = Environment.MachineName;
            string timestamp = DateTime.UtcNow.ToString("o");
            string sourceState = currentModelVersion.ToString();

            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(scheme, token);

                // Serialize the full payload first to measure its size.
                // Newtonsoft escapes non-ASCII as \uXXXX so JSON output is always ASCII
                // and string.Length equals the byte count exactly.
                var singlePayload = new
                {
                    documentGuid = docGuid,
                    source_state = sourceState,
                    timestamp = timestamp,
                    revitUser = revitUsername,
                    revitAccountId = revitAccountId,
                    windowsUser = windowsUsername,
                    machine = machineName,
                    batchId = batchId,
                    chunkIndex = 0,
                    totalChunks = 1,
                    elements = elementDeltas,
                    deletedElements = deleted
                };

                var singleJson = JsonUtils.SerializeToJson(singlePayload, null);
                bool mustChunk = singleJson.Length > MaxSinglePayloadBytes;

                Log.Info($"[ProjectPerseusWeb] Payload: {elementDeltas.Count} elements, {singleJson.Length / 1024} KB — {(mustChunk ? "chunking" : "single request")}.");

                if (!mustChunk)
                {
                    PostJson(client, singleJson, 1, 1);
                    return;
                }

                // Derive chunk size from actual element density so each chunk targets ~90% of the limit.
                int avgBytesPerElement = elementDeltas.Count > 0 ? singleJson.Length / elementDeltas.Count : singleJson.Length;
                int chunkSize = Math.Max(1, (int)((MaxSinglePayloadBytes * 0.9) / avgBytesPerElement));

                // Payload too large: split by derived chunk size and re-serialize each chunk.
                var chunks = Enumerable.Range(0, (int)Math.Ceiling((double)elementDeltas.Count / chunkSize))
                    .Select(i => elementDeltas.Skip(i * chunkSize).Take(chunkSize).ToList())
                    .ToList();

                if (chunks.Count == 0)
                    chunks.Add(new List<models.ElementDelta>());

                int totalChunks = chunks.Count;
                Log.Info($"[ProjectPerseusWeb] Submitting {elementDeltas.Count} elements in {totalChunks} chunk(s) of ~{chunkSize} (avg {avgBytesPerElement} bytes/element).");

                for (int i = 0; i < totalChunks; i++)
                {
                    bool isLastChunk = i == totalChunks - 1;

                    var payload = new
                    {
                        documentGuid = docGuid,
                        source_state = sourceState,
                        timestamp = timestamp,
                        revitUser = revitUsername,
                        revitAccountId = revitAccountId,
                        windowsUser = windowsUsername,
                        machine = machineName,
                        batchId = batchId,
                        chunkIndex = i,
                        totalChunks = totalChunks,
                        elements = chunks[i],
                        deletedElements = isLastChunk ? deleted : (IList<long>)new List<long>()
                    };

                    var jsonString = JsonUtils.SerializeToJson(payload, null);
                    if (!PostJson(client, jsonString, i + 1, totalChunks))
                        return;
                }
            }
        }

        public static void SubmitActions(string baseUrl, List<models.ActionDto> actions)
        {
            if (actions == null || actions.Count == 0) return;

            string token  = auth.AuthService.GetAuthTokenSafely();
            string scheme = auth.AuthService.GetAuthSchemeSafely();

            var payload = actions.Select(a => new
            {
                correlation_id    = a.CorrelationId,
                session_id        = a.SessionId,
                doc_guid          = a.DocGuid,
                action_type       = a.ActionType,
                element_id        = a.ElementId,
                element_unique_id = a.ElementUniqueId,
                element_name      = a.ElementName,
                element_category  = a.ElementCategory,
                transaction_name  = a.TransactionName,
                revit_user        = a.RevitUser,
                client_timestamp  = a.TimestampUtc,
                description       = a.Description,
            }).ToList();

            var json = util.JsonUtils.SerializeToJson(payload, null);
            var endpoint = $"{baseUrl}/ingest_actions/";
            Log.Info($"[ProjectPerseusWeb] SubmitActions: {actions.Count} action(s) → {endpoint}");
            WebHelper.Post(endpoint, token, json, scheme);
        }

        public static List<models.ViolationHighlightDto> GetViolations(string baseUrl, string sourceGuid)
        {
            string token  = auth.AuthService.GetAuthTokenSafely();
            string scheme = auth.AuthService.GetAuthSchemeSafely();
            string url    = $"{baseUrl.TrimEnd('/')}/source/{sourceGuid}/violations/";
            string json   = WebHelper.Get(url, token, null, scheme);
            var raw = Newtonsoft.Json.JsonConvert.DeserializeObject<
                List<Newtonsoft.Json.Linq.JObject>>(json);
            return (raw ?? new List<Newtonsoft.Json.Linq.JObject>())
                .Select(o => new models.ViolationHighlightDto
                {
                    ElementUniqueId = o["element"]?["unique_id"]?.ToString(),
                    ElementName     = o["element"]?["name"]?.ToString(),
                    RuleName        = o["rule_name"]?.ToString(),
                    Severity        = o["severity"]?.ToString(),
                    Message         = o["message"]?.ToString(),
                })
                .Where(d => d.ElementUniqueId != null)
                .ToList();
        }

        private bool PostJson(System.Net.Http.HttpClient client, string json, int chunkNumber, int totalChunks)
        {
            var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            try
            {
                Log.Info($"[ProjectPerseusWeb] POST {ElementsEndpoint} (chunk {chunkNumber}/{totalChunks})");
                var responseMessage = client.PostAsync(ElementsEndpoint, content).GetAwaiter().GetResult();
                string response = responseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (responseMessage.IsSuccessStatusCode)
                {
                    Log.Info($"SubmitElementDeltas chunk {chunkNumber}/{totalChunks} success: {response}");
                    return true;
                }

                Log.Info($"SubmitElementDeltas chunk {chunkNumber}/{totalChunks} failed ({responseMessage.StatusCode}): {response}");
                return false;
            }
            catch (Exception ex)
            {
                Log.Info($"SubmitElementDeltas chunk {chunkNumber}/{totalChunks} HTTP error: {ex.Message}");
                return false;
            }
        }

    }
}