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
            var payload = ElementPayload.Build(elementDeltas, "upload");
            SubmitElementPayload(payload, deleted, doc, batchId);
        }

        /// <summary>
        /// Posts pre-serialised elements (see ElementPayload) to the elements endpoint, split into chunks under the size
        /// limit by their actual JSON size. <paramref name="chunkProgress"/> is called with (chunks sent, total) after
        /// each successful chunk. Deletions ride on the last chunk.
        /// </summary>
        internal void SubmitElementPayload(ElementPayload payload, IList<long> deleted, Document doc, string batchId = null,
                                           Action<int, int> chunkProgress = null)
        {
            string token = ProjectPerseus.auth.AuthService.GetAuthTokenSafely();
            string scheme = ProjectPerseus.auth.AuthService.GetAuthSchemeSafely();
            if (string.IsNullOrEmpty(token))
            {
                Log.Info("Sync aborted: User failed authentication.");
                return;
            }

            var docGuid = ModelGuidStorage.GetOrCreate(doc);
            var app = doc.Application;
            string revitUsername = app.Username;
            string revitAccountId = app.LoginUserId;
            string windowsUsername = Environment.UserName;
            string machineName = Environment.MachineName;
            string timestamp = DateTime.UtcNow.ToString("o");
            string sourceState = RevitFacade.GetDocumentVersionGuid(doc).ToString();

            // Each chunk targets ~90% of the server limit, measured on the real JSON (no re-serialisation).
            var chunks = payload.Chunks((long)(MaxSinglePayloadBytes * 0.9));
            int totalChunks = chunks.Count;
            Log.Info($"[ProjectPerseusWeb] Payload: {payload.Elements.Count} elements, {payload.TotalChars / 1024} KB — " +
                     (totalChunks == 1 ? "single request." : $"{totalChunks} chunks."));

            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(scheme, token);
                for (int i = 0; i < totalChunks; i++)
                {
                    bool isLastChunk = i == totalChunks - 1;
                    var header = new
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
                    };
                    string json = payload.Envelope(header, chunks[i].From, chunks[i].Count,
                                                   isLastChunk ? deleted : (IList<long>)new List<long>());
                    if (!PostJson(client, json, i + 1, totalChunks))
                        return;
                    chunkProgress?.Invoke(i + 1, totalChunks);
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
            var wrapper = Newtonsoft.Json.JsonConvert.DeserializeObject<
                Newtonsoft.Json.Linq.JObject>(json);
            var raw = wrapper?["violations"] as Newtonsoft.Json.Linq.JArray
                      ?? new Newtonsoft.Json.Linq.JArray();
            return raw.Cast<Newtonsoft.Json.Linq.JObject>()
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