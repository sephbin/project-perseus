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

        public void SubmitElementDeltas(IList<models.ElementDelta> elementDeltas, IList<long> deleted, Document doc, string batchId = null)
        {
            // WriteLog("SubmitElementDeltas");
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
            string revitUsername = app.Username;        // Name from Revit Options
            string revitAccountId = app.LoginUserId;    // Autodesk account GUID (if logged in)
            string windowsUsername = Environment.UserName;
            string machineName = Environment.MachineName;

            var payload = new
            {
                documentGuid = docGuid,
                source_state = currentModelVersion.ToString(),
                timestamp = DateTime.UtcNow.ToString("o"),
                revitUser = revitUsername,
                revitAccountId = revitAccountId,
                windowsUser = windowsUsername,
                machine = machineName,
                batchId = batchId,
                elements = elementDeltas,
                deletedElements = deleted
            };

            var jsonString = JsonUtils.SerializeToJson(payload, null);

            // Execute the request using the HttpClient
            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(scheme, token);

                // 2. Package the JSON string for HTTP
                var content = new System.Net.Http.StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

                try
                {
                    Log.Info($"[ProjectPerseusWeb] POST {ElementsEndpoint}");
                    // 3. Send the POST request safely blocking the thread
                    var responseMessage = client.PostAsync(ElementsEndpoint, content).GetAwaiter().GetResult();

                    // 4. Read the response
                    string response = responseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (responseMessage.IsSuccessStatusCode)
                    {
                        Log.Info($"SubmitElementDeltas success: {response}");
                    }
                    else
                    {
                        Log.Info($"SubmitElementDeltas failed ({responseMessage.StatusCode}): {response}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"SubmitElementDeltas HTTP error: {ex.Message}");
                }
            }
        }

    }
}