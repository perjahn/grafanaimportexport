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

namespace importdashboards
{
    class Program
    {
        static bool verbose = false;

        static async Task<int> Main(string[] args)
        {
            var parsedArgs = args.ToList();

            var cookie = ExtractArgumentValue(parsedArgs, "-c");
            bool dryrun = ExtractArgumentFlag(parsedArgs, "-d");
            bool force = ExtractArgumentFlag(parsedArgs, "-f");
            bool remove = ExtractArgumentFlag(parsedArgs, "-r");
            var folderUid = ExtractArgumentValue(parsedArgs, "-u");
            verbose = ExtractArgumentFlag(parsedArgs, "-verbose");

            if (parsedArgs.Count != 3)
            {
                string usage =
                    "Usage: importdashboards <folder> <grafanaurl> <authtoken> [-c cookie] [-d] [-f] [-r] [-u folderuid] [-verbose]\n" +
                    "\n" +
                    "folder:        Path to input folder in local file system, should contain json files with definitions of grafana folders and dashboards.\n" +
                    "grafanaurl:    Base url of grafana server, https://example.com\n" +
                    "authtoken:     Grafana api key, generated with read+write access.\n" +
                    "-c cookie:     Optional custom cookie to all http requests (useful for istio).\n" +
                    "-d:            Dry run, simulate all write requests.\n" +
                    "-f:            Force write even if folder/dashboard hasn't changed (will update version).\n" +
                    "-r:            Remove folders and dashboard in grafana that doesn't exist in local folder.\n" +
                    "-u folderuid:  Override import to specific folder, by specifying the folder's uid.\n" +
                    "-verbose:      Verbose logging.";
                Log(usage, ConsoleColor.Red);
                return 1;
            }

            var folder = parsedArgs[0];
            var url = parsedArgs[1];
            var authtoken = parsedArgs[2];

            if (!Directory.Exists(folder))
            {
                Log($"Folder not found: '{folder}'", ConsoleColor.Red);
                return 1;
            }

            var files = Directory.GetFiles(folder, "*.json");
            if (files.Length == 0)
            {
                Log($"No files found in: '{folder}'", ConsoleColor.Red);
                return 1;
            }

            int result = await ImportFoldersAndDashboards(files, url, authtoken, cookie, force, folderUid, remove, dryrun);

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

        static async Task<int> ImportFoldersAndDashboards(string[] files, string url, string authtoken, string? cookie, bool force, string? folderUid, bool remove, bool dryrun)
        {
            var foldersAndDashboards = ReadDashboardFiles(files);

            var folders = foldersAndDashboards.Where(d => d.dashboard["meta"]?["isFolder"]?.Value<bool>() == true).ToArray();
            var dashboards = foldersAndDashboards.Where(d => d.dashboard["meta"]?["isFolder"]?.Value<bool>() == false).ToArray();

            Log($"Found {folders.Length} folder files and {dashboards.Length} dashboard files.", ConsoleColor.Green);

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


            JArray grafanaFoldersAndDashboards = await GetFoldersAndDashboards(client, authtoken, cookie);
            var grafanaFolders = grafanaFoldersAndDashboards
                .Where(d => d is JObject o && o["type"]?.Value<string>() == "dash-folder")
                .Select(d => (JObject)d)
                .ToArray();
            var grafanaDashboards = grafanaFoldersAndDashboards
                .Where(d => d is JObject o && o["type"]?.Value<string>() == "dash-db")
                .Select(d => (JObject)d)
                .ToArray();

            Log($"Found {grafanaFolders.Length} folders and {grafanaDashboards.Length} dashboards in grafana.", ConsoleColor.Green);


            var folderIds = new Dictionary<string, int>();

            foreach (var folder in folders)
            {
                if (folder.dashboard["dashboard"] is JObject jobjectDashboard)
                {
                    await ImportFolder(folder.filename, jobjectDashboard, grafanaFolders, folderIds, folderUid, client, authtoken, cookie, force, dryrun);
                }
                else
                {
                    Log($"Invalid dashboard file, ignoring folder: '{folder.filename}'", ConsoleColor.Yellow);
                }
            }

            foreach (var dashboard in dashboards)
            {
                if (dashboard.dashboard["dashboard"] is JObject jobjectDashboard && dashboard.dashboard["meta"]?["folderId"] is JValue i2)
                {
                    await ImportDashboard(dashboard.filename, jobjectDashboard, grafanaDashboards, folders, folderIds, i2.Value<int>(), folderUid, client, authtoken, cookie, force, dryrun);
                }
                else
                {
                    Log($"Invalid dashboard file, ignoring dashboard: '{dashboard.filename}'", ConsoleColor.Yellow);
                }
            }

            if (remove)
            {
                foreach (var grafanaDashboard in grafanaDashboards)
                {
                    if (!dashboards.Any(d => d.dashboard["dashboard"]?["uid"]?.Value<string>() == grafanaDashboard["uid"]?.Value<string>()))
                    {
                        await RemoveDashboard(client, authtoken, grafanaDashboard, cookie, dryrun);
                    }
                }

                foreach (var grafanaFolder in grafanaFolders)
                {
                    if (!folders.Any(f => f.dashboard["dashboard"]?["uid"]?.Value<string>() == grafanaFolder["uid"]?.Value<string>()))
                    {
                        await RemoveFolder(client, authtoken, grafanaFolder, cookie, dryrun);
                    }
                }
            }

            return 0;
        }

        static async Task RemoveFolder(HttpClient client, string authtoken, JObject folder, string? cookie, bool dryrun)
        {
            string title = folder["title"]?.Value<string>() ?? string.Empty;
            string uid = folder["uid"]?.Value<string>() ?? string.Empty;
            if (uid == string.Empty)
            {
                Log($"Ignoring deletion of folder, couldn't find uid: >>>{folder.ToString()}<<<", ConsoleColor.Yellow);
                return;
            }

            string path = $"api/folders/{uid}";

            Log($"Deleting folder: {client.BaseAddress}{path} (title: '{title}', uid: '{uid}')", ConsoleColor.Green);
            await HttpSend(client, authtoken, cookie, folder, path, HttpMethod.Delete, dryrun);

            return;
        }

        static async Task RemoveDashboard(HttpClient client, string authtoken, JObject dashboard, string? cookie, bool dryrun)
        {
            string title = dashboard["title"]?.Value<string>() ?? string.Empty;
            string uid = dashboard["uid"]?.Value<string>() ?? string.Empty;
            if (uid == string.Empty)
            {
                Log($"Ignoring deletion of dashboard, couldn't find uid: >>>{dashboard.ToString()}<<<", ConsoleColor.Yellow);
                return;
            }

            string path = $"api/dashboards/uid/{uid}";

            Log($"Deleting dashboard: {client.BaseAddress}{path} (title: '{title}', uid: '{uid}')", ConsoleColor.Green);
            await HttpSend(client, authtoken, cookie, dashboard, path, HttpMethod.Delete, dryrun);

            return;
        }

        static List<(string filename, JObject dashboard)> ReadDashboardFiles(string[] files)
        {
            var foldersAndDashboards = new List<(string filename, JObject dashboard)>();

            foreach (var filename in files)
            {
                string json = File.ReadAllText(filename);
                if (json.Length == 0)
                {
                    Log($"Ignoring empty file: '{filename}'", ConsoleColor.Yellow);
                    continue;
                }
                if (!TryParseJObject(json, out JObject dashboard))
                {
                    Log($"Ignoring invalid json file: '{filename}'", ConsoleColor.Yellow);
                    continue;
                }
                foldersAndDashboards.Add(new(filename, dashboard));
            }

            return foldersAndDashboards;
        }

        static async Task ImportFolder(string filename, JObject dashboard, JObject[] grafanaFolders, Dictionary<string, int> folderIds, string? folderUid,
            HttpClient client, string authtoken, string? cookie, bool force, bool dryrun)
        {
            var title = dashboard["title"]?.Value<string>();
            var uid = dashboard["uid"]?.Value<string>();
            if (title == null || uid == null)
            {
                Log($"Invalid dashboard file, ignoring folder: '{filename}'", ConsoleColor.Yellow);
                return;
            }

            var matchingFolders = grafanaFolders.Where(f => f["uid"]?.Value<string>() == uid).ToArray();
            bool exists = matchingFolders.Length >= 1;

            if (exists && !force)
            {
                if (matchingFolders[0]["title"]?.Value<string>() == title)
                {
                    Log($"No changes, ignoring folder: title: '{title}', uid '{uid}'", ConsoleColor.Green);

                    var existingFolderId = matchingFolders[0]["id"]?.Value<int>();
                    if (existingFolderId == null)
                    {
                        Log($"Couldn't find folder id in json: >>>{matchingFolders[0].ToString()}<<<", ConsoleColor.Yellow);
                        return;
                    }
                    folderIds[uid] = existingFolderId.Value;

                    return;
                }
            }

            string operation = exists ? "overwriting existing" : "creating new";

            var importFolder = new JObject();
            importFolder["title"] = title;
            if (exists)
            {
                importFolder["overwrite"] = true;
            }
            else
            {
                importFolder["uid"] = uid;
            }

            string path;
            HttpMethod method;
            if (exists)
            {
                path = $"api/folders/{uid}";
                method = HttpMethod.Put;
            }
            else
            {
                path = "api/folders";
                method = HttpMethod.Post;
            }

            Log($"Importing folder: {filename} -> {client.BaseAddress}{path} (title: '{title}', uid: '{uid}', {operation})", ConsoleColor.Green);
            var folderResult = await HttpSend(client, authtoken, cookie, importFolder, path, method, dryrun);


            var folderId = folderResult["id"]?.Value<int>();
            if (folderId == null)
            {
                Log($"Couldn't parse response: >>>{folderResult.ToString()}<<<", ConsoleColor.Yellow);
                return;
            }
            Log($"Got folderId: {folderId}", ConsoleColor.Green);
            folderIds[uid] = folderId.Value;
        }

        static async Task ImportDashboard(string filename, JObject dashboard, JObject[] grafanaDashboards,
            (string filename, JObject dashboard)[] folders, Dictionary<string, int> folderIds, int oldFolderId, string? folderUid,
            HttpClient client, string authtoken, string? cookie, bool force, bool dryrun)
        {
            var title = dashboard["title"]?.Value<string>();
            var uid = dashboard["uid"]?.Value<string>();
            if (title == null || uid == null)
            {
                Log($"Invalid dashboard file, ignoring dashboard: '{filename}'", ConsoleColor.Yellow);
                return;
            }

            bool exists = grafanaDashboards.Any(f => f["uid"]?.Value<string>() == uid);

            if (exists && !force)
            {
                string pathGet = $"api/dashboards/uid/{uid}";
                Log($"Retrieving existing dashboard: {client.BaseAddress}{pathGet} (title: '{title}', uid: '{uid}')", ConsoleColor.Green);
                var grafanaDashboard = await GetDashboard(client, authtoken, cookie, pathGet);

                if (grafanaDashboard["dashboard"] is JObject d2)
                {
                    if (CompareDashboards(dashboard, d2))
                    {
                        Log($"No changes, ignoring dashboard: title: '{title}', uid '{uid}'", ConsoleColor.Green);
                        return;
                    }
                }
                else
                {
                    Log($"Invalid existing dashboard, ignoring dashboard: '{filename}'", ConsoleColor.Yellow);
                    return;
                }
            }

            string operation = exists ? "overwriting existing" : "creating new";

            var importDashboard = new JObject();
            string errorMessage;

            int folderId = GetNewFolderId(oldFolderId, folders.Select(f => f.dashboard).ToArray(), folderIds, out errorMessage);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Log($"{errorMessage}, ignoring dashboard: '{filename}'", ConsoleColor.Yellow);
                return;
            }

            var dashboardId = GetNewDashboardId(grafanaDashboards, uid, out errorMessage);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Log($"{errorMessage}, ignoring dashboard: '{filename}'", ConsoleColor.Yellow);
                return;
            }

            dashboard["id"] = dashboardId;
            dashboard["uid"] = uid;

            importDashboard["dashboard"] = dashboard;
            importDashboard["folderId"] = folderId;
            importDashboard["overwrite"] = exists;

            string path = "api/dashboards/db";

            Log($"Importing dashboard: {filename} -> {client.BaseAddress}{path} (title: '{title}', uid: '{uid}', folderId: {folderId}, {operation})", ConsoleColor.Green);
            var dashboardResult = await HttpSend(client, authtoken, cookie, importDashboard, path, HttpMethod.Post, dryrun);
        }

        private static bool CompareDashboards(JObject dashboard1, JObject dashboard2)
        {
            var compare1 = (JObject)dashboard1.DeepClone();
            var compare2 = (JObject)dashboard2.DeepClone();

            compare1.Remove("id");
            compare2.Remove("id");
            compare1.Remove("version");
            compare2.Remove("version");

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GrafanaSaveDiff")))
            {
                string s1 = compare1.ToString();
                string s2 = compare2.ToString();

                File.WriteAllText("dashboard1.json", s1);
                File.WriteAllText("dashboard2.json", s2);
            }

            return compare1.ToString() == compare2.ToString();
        }

        static async Task<JObject> GetDashboard(HttpClient client, string authtoken, string? cookie, string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);

            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            LogVerbose($"Request {client.BaseAddress}{path}");

            using var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log(json, ConsoleColor.Red);
            }
            response.EnsureSuccessStatusCode();

            if (!TryParseJObject(json, out JObject jobject))
            {
                File.WriteAllText("error.html", json);
                throw new Exception($"Couldn't parse json (saved to error.html): {GetFirstCharacters(json, 40)}");
            }

            LogVerbose($"Response: >>>{jobject.ToString()}<<<");

            return jobject;
        }

        // When importing a dashboard into grafana, it's a little tricky to figure out in what folder ID it should be imported to.
        // First, lookup the uid for the folder the dashboard will be imported to, going through all local json files.
        // Then, determine what id in grafana that is, from the result of the folders that were just imported.
        static int GetNewFolderId(int oldFolderId, JObject[] oldFolders, Dictionary<string, int> folderIds, out string errorMessage)
        {
            //var oldFolderId = importDashboard["meta"]?["folderId"]?.Value<int>();
            //if (oldFolderId == null)
            //{
            //    errorMessage = "Couldn't find new folder id (1)";
            //    return -1;
            //}

            int folderId;
            if (oldFolderId == 0)
            {
                // General folder
                errorMessage = string.Empty;
                folderId = 0;
            }
            else
            {
                var oldFolders2 = oldFolders.Where(f => f["dashboard"]?["id"]?.Value<int>() == oldFolderId).ToArray();
                if (oldFolders2.Length != 1)
                {
                    errorMessage = $"Couldn't find new folder id (2 {oldFolderId})";
                    return -1;
                }

                var oldFolder = oldFolders2[0];
                var oldUid = oldFolder["dashboard"]?["uid"]?.Value<string>();
                if (oldUid == null)
                {
                    errorMessage = $"Couldn't find new folder id (3 {oldFolderId})";
                    return -1;
                }

                if (!folderIds.ContainsKey(oldUid))
                {
                    errorMessage = $"Couldn't find new folder id (4 {oldFolderId} {oldUid})";
                    return -1;
                }

                errorMessage = string.Empty;
                folderId = folderIds[oldUid];
            }

            return folderId;
        }

        static int? GetNewDashboardId(JObject[] grafanaDashboards, string uid, out string errorMessage)
        {
            var matchingGrafanaDashboards = grafanaDashboards.Where(f => f["uid"]?.Value<string>() == uid).ToArray();

            if (matchingGrafanaDashboards.Length == 0)
            {
                errorMessage = string.Empty;
                return null;
            }

            var id = matchingGrafanaDashboards[0]["id"]?.Value<int>();
            if (id == null)
            {
                errorMessage = "Invalid grafana dashboard";
                return null;
            }

            errorMessage = string.Empty;
            return id.Value;
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

        static async Task<JArray> GetFoldersAndDashboards(HttpClient client, string authtoken, string? cookie)
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
                throw new Exception($"Couldn't parse json (saved to error.html): {GetFirstCharacters(json, 40)}");
            }

            LogVerbose($"Response: >>>{jarray.ToString()}<<<");

            return jarray;
        }

        static string GetFirstCharacters(string text, int characters)
        {
            string oneliner = string.Join(string.Empty, text.ToCharArray().Select(c => char.IsControl(c) ? ' ' : c));
            if (oneliner.Length > characters)
            {
                return oneliner.Substring(0, characters) + "...";
            }
            else
            {
                return oneliner.Substring(0, oneliner.Length);
            }
        }

        static async Task<JObject> HttpSend(HttpClient client, string authtoken, string? cookie, JObject content, string url, HttpMethod method, bool dryrun)
        {
            var request = new HttpRequestMessage(method, url);
            request.Content = new StringContent(content.ToString(), Encoding.UTF8, "application/json");

            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            LogVerbose($"Request {client.BaseAddress}{url}: >>>{content.ToString()}<<<");

            string json;
            if (dryrun)
            {
                return new JObject();
            }

            using var response = await client.SendAsync(request);
            json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log(json, ConsoleColor.Yellow);
                return new JObject();
            }

            if (!TryParseJObject(json, out JObject jobject))
            {
                File.WriteAllText("error.html", json);
                throw new Exception($"Couldn't parse json (saved to error.html): {GetFirstCharacters(json, 40)}");
            }

            LogVerbose($"Response: >>>{jobject.ToString()}<<<");

            return jobject;
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
