﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReleaseNotesMaker
{
    class Program
    {
        static int Main(string[] args)
        {
            var user = Environment.GetEnvironmentVariable("GITHUB_USER");
            var password = Environment.GetEnvironmentVariable("GITHUB_PASSWORD");

            if (args.Length < 2)
            {
                Console.WriteLine("Usage: [repoUrl] [milestone]");
                Console.WriteLine("Sample:");
                Console.WriteLine("{0}.exe signalr/signalr 2.1.1", typeof(Program).Assembly.GetName().Name);
                return 1;
            }

            string repoURL = args[0];
            string milestone = args[1];

            try
            {
                BuildReleaseNotesForMilestone(user, password, repoURL, milestone).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }

            return 0;
        }

        private static async Task BuildReleaseNotesForMilestone(string user, string password, string repoURL, string milestone)
        {
            List<JToken> issues = await GetIssuesForMilestone(user, password, repoURL);

            var issueGroups = from issue in issues
                              let labels = (from label in issue["labels"]
                                            select label.Value<string>("name")).ToList()
                              let category = Categorize(labels)
                              let issueMilestone = issue["milestone"]
                              where labels.Count > 0 && issueMilestone.HasValues && issueMilestone.Value<string>("title").Equals(milestone)
                              group issue by category into g
                              select g;

            Console.WriteLine("Found ({0}) issues in {1}", issueGroups.Sum(g => g.Count()), milestone);
            Console.WriteLine();

            foreach (var g in issueGroups)
            {
                if (String.IsNullOrEmpty(g.Key))
                {
                    continue;
                }

                Console.WriteLine("### {0}", g.Key);
                Console.WriteLine();

                foreach (var issue in g)
                {
                    Console.WriteLine("* {0} ([#{1}]({2}))", issue["title"], issue["number"], issue["html_url"]);
                }

                Console.WriteLine();
            }
        }

        private static string Categorize(IEnumerable<string> labels)
        {
            if (labels.Any(l => l.Contains("Done")))
            {
                if (labels.Contains("bug"))
                {
                    return "Bugs Fixed";
                }

                if (labels.Contains("feature") || labels.Contains("enhancement"))
                {
                    return "Features";
                }
            }

            return String.Empty;
        }

        private static async Task<List<JToken>> GetIssuesForMilestone(string user, string password, string repoURL)
        {
            // TODO: Make this smarter (have it pull updates from github and update issues.json)
            var issues = new List<JToken>();
            var cacheFile = MakeCacheFile(repoURL);

            var fi = new FileInfo(cacheFile);

            if (!fi.Exists ||
                DateTime.UtcNow.Subtract(fi.LastWriteTimeUtc) > TimeSpan.FromDays(1))
            {
                var parameters = new Dictionary<string, string>
                {
                    { "state", "closed" },
                    { "per_page", "100" }
                };

                issues.AddRange(await GetIssues(user, password, repoURL, parameters));

                if (issues.Count > 0)
                {
                    File.WriteAllText(cacheFile, JsonConvert.SerializeObject(issues));

                    Console.WriteLine("Wrote ({0}) issues to issues.json", issues.Count);
                }
            }
            else
            {
                issues.AddRange(JsonConvert.DeserializeObject<List<JToken>>(File.ReadAllText(cacheFile)));

                Console.WriteLine("Read ({0}) issues from issues.json", issues.Count);
            }

            return issues;
        }

        private static string MakeCacheFile(string repoURL)
        {
            string s = repoURL;
            foreach (var ch in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(ch, '_');
            }
            return s + "_issues.json";
        }

        private static async Task<IList<JToken>> GetIssues(string user, string password, string repoURL, Dictionary<string, string> parameters = null)
        {
            var httpClient = new HttpClient();
            httpClient.SetBasicAuthCredentials(user, password);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Github Stuff");
            var issues = new List<JToken>();
            string resource = AddParameters("https://api.github.com/repos/" + repoURL + "/issues", parameters);
            string lastResource = null;

            while (true)
            {
                Console.WriteLine("GET " + resource);
                var response = await httpClient.GetAsync(resource);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine(response);
                    break;
                }
                else
                {
                    var rateLimitRemainingValue = response.Headers.GetValues("X-RateLimit-Remaining").First();
                    int rateLimitRemaining;
                    if (Int32.TryParse(rateLimitRemainingValue, out rateLimitRemaining) && rateLimitRemaining < 10)
                    {
                        Console.WriteLine(rateLimitRemainingValue + " requests remaining");
                    }

                    var pageIssues = JArray.Parse(await response.Content.ReadAsStringAsync());
                    issues.AddRange(pageIssues);

                    if (resource.Equals(lastResource, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    var resourceLinks = response.GetResourceLinks();

                    resource = resourceLinks[0];

                    if (String.IsNullOrEmpty(lastResource))
                    {
                        lastResource = resourceLinks[1];
                    }
                }
            }

            return issues;
        }

        private static string AddParameters(string resource, Dictionary<string, string> parameters)
        {
            var builder = new UriBuilder(resource);
            if (String.IsNullOrEmpty(builder.Query))
            {
                builder.Query = BuildQueryString(parameters);
            }
            else
            {
                builder.Query += "&" + BuildQueryString(parameters);
            }

            return builder.Uri.ToString();
        }

        private static string BuildQueryString(Dictionary<string, string> parameters)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var pair in parameters)
            {
                if (!first)
                {
                    sb.Append("&");
                }
                sb.Append(pair.Key).Append("=").Append(pair.Value);
                first = false;
            }
            return sb.ToString();
        }
    }

    public static class HttpClientExtensions
    {
        public static void SetBasicAuthCredentials(this HttpClient client, string user, string password)
        {
            if (String.IsNullOrEmpty(user) || String.IsNullOrEmpty(password))
            {
                return;
            }

            string base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(user + ":" + password));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", base64);
        }
    }

    public static class HttpResponseMessageExtensions
    {
        public static IList<string> GetResourceLinks(this HttpResponseMessage response)
        {
            var link = response.Headers.GetValues("Link").First();

            var links = new List<string>();
            foreach (Match match in Regex.Matches(link, "<(.+?)>"))
            {
                links.Add(GetResourceUrlFromMatch(match));
            }
            return links;
        }

        private static string GetResourceUrlFromMatch(Match match)
        {
            return match.Captures[0].Value.Trim(new[] { '<', '>' });
        }
    }
}
