using System.Collections.Generic;
using System.IO;
using System.Net;

namespace ProjectPerseus
{
    public class ProjectPerseusWeb
    {
        private string _baseUrl;
        private string _apiToken;
        private string ElementsEndpoint => $"{_baseUrl}/rapi/elements/";

        public ProjectPerseusWeb(string baseUrl, string apiToken)
        {
            _baseUrl = baseUrl;
            _apiToken = apiToken;
        }

        public void SubmitElementDeltas(IList<models.ElementDelta> elementDeltas)
        {
            var jsonString = Utl.SerializeToJson(elementDeltas, null);
            WebHelper.Post(ElementsEndpoint, _apiToken, jsonString);
        }

        private static class WebHelper
        {
            public static string Post(string endpoint, string apiToken, string json)
            {
                return PerformRequest(endpoint, apiToken, json, "POST");
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