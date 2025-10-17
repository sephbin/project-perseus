using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;



namespace ProjectPerseus
{
    
    public class ProjectPerseusWeb
    {
        private void WriteLog(string content)
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
        private string ElementsEndpoint => $"{_baseUrl}/rapi/elements/";
        private string StateUpdateEndpoint => $"{_baseUrl}/stateupdate/";

        public ProjectPerseusWeb(string baseUrl, string apiToken)
        {
            _baseUrl = baseUrl;
            _apiToken = apiToken;
        }

        public void SubmitElementDeltas(IList<models.ElementDelta> elementDeltas)
        {
            //WriteLog("SubmitElementDeltas");
            var jsonString = Utl.SerializeToJson(elementDeltas, null);
            //WriteLog(jsonString);
            WebHelper.Post(ElementsEndpoint, _apiToken, jsonString);
            //WriteLog("// SubmitElementDeltas");
        }

        public void SubmitElementState(IList<models.ElementDelta> elementDeltas)
        {
            WriteLog("SubmitElementState");
            int chunkSize = Math.Min(elementDeltas.Count, 10000);
            WriteLog("chunkSize");
            int total = elementDeltas.Count;
            WriteLog("total");
            int totalChunks = (int)Math.Ceiling((double)total / chunkSize);
            WriteLog("totalChunks");
            WriteLog($"SubmitElementState: {total} elements to upload in {totalChunks} chunks (chunk size {chunkSize})");
            for (int i = 0; i < total; i += chunkSize)
            {
                WriteLog("for");
                var chunk = elementDeltas.Skip(i).Take(chunkSize).ToList();
                WriteLog("chunk");
                WriteLog(chunk.ToString());
                string jsonString = Utl.SerializeToJson(chunk, null);

                WriteLog("jsonString");

                WriteLog($"Uploading chunk {i / chunkSize + 1} of {totalChunks}, containing {chunk.Count} elements");

                try
                {
                    var preview = jsonString.Length > 5000 ? jsonString.Substring(0, 5000) + "..." : jsonString;
                    WriteLog(preview);
                    WebHelper.Post(StateUpdateEndpoint, _apiToken, jsonString);
                    
                    WriteLog($"Chunk {i / chunkSize + 1} uploaded successfully");
                }
                catch (Exception ex)
                {
                    WriteLog($"Error posting chunk {i / chunkSize + 1}: {ex.Message}");
                }
            }




            //var jsonString = Utl.SerializeToJson(elementDeltas, null);
            //WriteLog(jsonString);
            //try
            //{
            //    WebHelper.Post(StateUpdateEndpoint, _apiToken, jsonString);
            //}
            //catch (Exception ex)
            //{
            //    WriteLog($"Error posting data: {ex.Message}");
            //}

            WriteLog("// SubmitElementState");
        }

        private static class WebHelper
        {
            public static string Post(string endpoint, string apiToken, string json)
            {
                return PerformRequest(endpoint, apiToken, json, "POST");
            }
            public static string Get(string endpoint, string apiToken, string json)
            {
                return PerformRequest(endpoint, apiToken, null, "GET");
            }

            private static string PerformRequest(string endpoint, string apiToken, string json, string method)
            {
                try
                {
                    var httpWebRequest = (HttpWebRequest)WebRequest.Create(endpoint);
                    httpWebRequest.ContentType = "application/json";
                    httpWebRequest.Method = method;
                    httpWebRequest.Headers["Authorization"] = $"Token {apiToken}";
                    // set timeout to 5 minutes
                    httpWebRequest.Timeout = 300000;

                    using (var streamWriter = new StreamWriter(httpWebRequest.GetRequestStream()))
                    {
                        streamWriter.Write(json);
                    }

                    var httpResponse = (HttpWebResponse)httpWebRequest.GetResponse();
                    using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                    {
                        return streamReader.ReadToEnd();
                    }
                }
                catch (WebException ex)
                {
                    var response = ex.Response as HttpWebResponse;
                    if (response != null)
                    {
                        var responseStream = response.GetResponseStream();
                        if (responseStream != null)
                        {
                            using (var reader = new StreamReader(responseStream))
                            {
                                var error = reader.ReadToEnd();
                                Log.Error(error);
                            }
                        }
                    }

                    throw;
                }
            }
        }
    }
}