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

        private const int ChunkSize = 500;

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

            var chunks = Enumerable.Range(0, (int)Math.Ceiling((double)elementDeltas.Count / ChunkSize))
                .Select(i => elementDeltas.Skip(i * ChunkSize).Take(ChunkSize).ToList())
                .ToList();

            // If there are no elements at all we still need to send one chunk (for deletions).
            if (chunks.Count == 0)
                chunks.Add(new List<models.ElementDelta>());

            int totalChunks = chunks.Count;
            Log.Info($"[ProjectPerseusWeb] Submitting {elementDeltas.Count} elements in {totalChunks} chunk(s) of {ChunkSize}.");

            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(scheme, token);

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
                    var content = new System.Net.Http.StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

                    try
                    {
                        Log.Info($"[ProjectPerseusWeb] POST {ElementsEndpoint} (chunk {i + 1}/{totalChunks}, {chunks[i].Count} elements)");
                        var responseMessage = client.PostAsync(ElementsEndpoint, content).GetAwaiter().GetResult();
                        string response = responseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                        if (responseMessage.IsSuccessStatusCode)
                        {
                            Log.Info($"SubmitElementDeltas chunk {i + 1}/{totalChunks} success: {response}");
                        }
                        else
                        {
                            Log.Info($"SubmitElementDeltas chunk {i + 1}/{totalChunks} failed ({responseMessage.StatusCode}): {response}");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"SubmitElementDeltas chunk {i + 1}/{totalChunks} HTTP error: {ex.Message}");
                        return;
                    }
                }
            }
        }

    }
}