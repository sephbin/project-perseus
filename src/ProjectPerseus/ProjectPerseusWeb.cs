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

        public void UploadElements(List<models.Element> eles)
        {
            var jsonString = Utl.SerializeToString(eles, null);
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
                var httpWebRequest = (HttpWebRequest)WebRequest.Create(endpoint);
                httpWebRequest.ContentType = "application/json";
                httpWebRequest.Method = method;
                httpWebRequest.Headers["Authorization"] = $"Token {apiToken}";

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
        }
    }
}