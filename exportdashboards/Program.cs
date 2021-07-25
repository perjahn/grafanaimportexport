using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace exportdashboards
{
    class Program
    {
        static bool verbose = false;

        static async Task<int> Main(string[] args)
        {
            var parsedArgs = args.ToList();

            var cookie = ExtractArgumentValue(parsedArgs, "-c");
            verbose = ExtractArgumentFlag(parsedArgs, "-verbose");

            if (parsedArgs.Count != 3)
            {
                Log("Usage: exportdashboards <grafanaurl> <authtoken> <folder> [-c cookie] [-verbose]", ConsoleColor.Red);
                return 1;
            }

            var url = parsedArgs[0];
            var authtoken = parsedArgs[1];
            var folder = parsedArgs[2];

            if (!Directory.Exists(folder))
            {
                Log($"Creating folder: '{folder}'", ConsoleColor.Green);
                Directory.CreateDirectory(folder);
            }

            int result = await ExportDashboards(folder, url, authtoken, cookie);

            return result;
        }

        static string? ExtractArgumentValue(List<string> args, string flagname)
        {
            int index = args.IndexOf(flagname);
            if (index < 0 || index >= args.Count - 1)
            {
                return null;
            }

            var values = args[index + 1];
            args.RemoveRange(index, 2);
            return values;
        }

        static bool ExtractArgumentFlag(List<string> args, string flagname)
        {
            int index = args.IndexOf(flagname);
            if (index < 0)
            {
                return false;
            }

            args.RemoveAt(index);
            return true;
        }

        static async Task<int> ExportDashboards(string folder, string url, string authtoken, string? cookie)
        {
            string baseaddress = GetBaseAddress(url);
            if (baseaddress == string.Empty)
            {
                Log($"Invalid url: '{url}'", ConsoleColor.Red);
                return 1;
            }

            //var cookieContainer = new CookieContainer();
            //cookieContainer.Add(baseAddress, new Cookie("CookieName", "cookie_value"));
            //var handler = new HttpClientHandler() { CookieContainer = cookieContainer })
            var handler = new HttpClientHandler { UseCookies = false };
            //var handler = new LoggingHandler(new HttpClientHandler());
            var client = new HttpClient(handler);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authtoken);
            client.BaseAddress = new Uri(baseaddress);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));


            JArray dashboards = await GetDashboards(client, authtoken, cookie);

            Log($"Found {dashboards.Count} dashboards in grafana.", ConsoleColor.Green);

            foreach (JObject dashboard in dashboards)
            {
                await ExportDashboard(folder, dashboard, client, authtoken, cookie);
            }

            return 0;
        }

        static async Task ExportDashboard(string folder, JObject dashboard, HttpClient client, string authtoken, string? cookie)
        {
            var uid = dashboard["uid"]?.Value<string>();
            if (uid == null)
            {
                Log($"Invalid dashboard, ignoring dashboard: '{dashboard.ToString()}'", ConsoleColor.Yellow);
                return;
            }

            string filename = Path.Combine(folder, $"{uid}.json");

            string url = $"api/dashboards/uid/{uid}";

            Log($"Exporting dashboard: {uid} -> {filename}", ConsoleColor.Green);

            var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            LogVerbose($"Request {client.BaseAddress}{url}");

            using var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log(json, ConsoleColor.Yellow);
                return;
            }

            if (!TryParseJObject(json, out JObject jobject))
            {
                Log($"Couldn't parse json: {json}", ConsoleColor.Yellow);
                return;
            }

            LogVerbose($"Response: >>>{jobject.ToString()}<<<");

            Log($"Saving: '{filename}'", ConsoleColor.Green);
            File.WriteAllText(filename, jobject.ToString());
        }

        static string GetBaseAddress(string url)
        {
            int end;
            if (url.StartsWith("https://"))
            {
                end = url.IndexOf("/", 8);
            }
            else if (url.StartsWith("http://"))
            {
                end = url.IndexOf("/", 7);
            }
            else
            {
                return string.Empty;
            }

            if (end < 0)
            {
                return url;
            }
            return url.Substring(0, end);
        }

        static async Task<JArray> GetDashboards(HttpClient client, string authtoken, string? cookie)
        {
            string url = "api/search";
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            LogVerbose($"Request {client.BaseAddress}{url}");

            using var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log(json, ConsoleColor.Red);
            }
            response.EnsureSuccessStatusCode();

            if (!TryParseJArray(json, out JArray jarray))
            {
                File.WriteAllText("error.html", json);
                throw new Exception($"Couldn't parse json (saved to error.html): {GetFirstCharacters(json, 10)}");
            }

            LogVerbose($"Response: >>>{jarray.ToString()}<<<");

            return jarray;
        }

        static string GetFirstCharacters(string text, int characters)
        {
            if (characters < text.Length)
            {
                return text.Substring(0, characters) + "...";
            }
            else
            {
                return text.Substring(0, text.Length);
            }
        }

        static bool TryParseJObject(string json, out JObject jobject)
        {
            try
            {
                jobject = JObject.Parse(json);
                return true;
            }
            catch
            {
                jobject = new JObject();
                return false;
            }
        }

        static bool TryParseJArray(string json, out JArray jarray)
        {
            try
            {
                jarray = JArray.Parse(json);
                return true;
            }
            catch
            {
                jarray = new JArray();
                return false;
            }
        }

        static void Log(string message)
        {
            Console.WriteLine(message);
        }

        static void Log(string message, ConsoleColor color)
        {
            var oldcolor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ForegroundColor = oldcolor;
        }

        static void LogVerbose(string message)
        {
            if (verbose)
            {
                var oldcolor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(message);
                Console.ForegroundColor = oldcolor;
            }
        }
    }

    public class LoggingHandler : DelegatingHandler
    {
        public LoggingHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Console.WriteLine("Request:");
            Console.WriteLine(request.ToString());
            if (request.Content != null)
            {
                Console.WriteLine(await request.Content.ReadAsStringAsync());
            }
            Console.WriteLine();

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

            Console.WriteLine("Response:");
            Console.WriteLine(response.ToString());
            if (response.Content != null)
            {
                Console.WriteLine(await response.Content.ReadAsStringAsync());
            }
            Console.WriteLine();

            return response;
        }
    }
}
