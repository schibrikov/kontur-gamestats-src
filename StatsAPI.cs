﻿using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using Kontur.GameStats.Server.Datatypes;
using Newtonsoft.Json;

namespace Kontur.GameStats.Server
{
    public class StatsApi : IStatsApi
    {
        private readonly IDbWorker db;

        private readonly bool enableCache;

        private readonly WeakCache<string, string> playerStatsCache;
        private readonly WeakCache<string, string> serverStatsCache;
        private readonly WeakCache<int, string> recentMatchesReportCache;
        private readonly WeakCache<int, string> bestPlayersReportCache;
        private readonly WeakCache<int, string> popularServersReportCache;

        public void GetServersInfo(HttpListenerContext context)
        {
            EndpointInfo[] servers = db.GetServersInfo();
            string serversJson = JsonConvert.SerializeObject(servers);

            this.SendResponse(context.Response, serversJson, HttpStatusCode.OK);
        }

        public void GetServerInfo(HttpListenerContext context)
        {
            string endpoint = ExtractEndpoint(context.Request);
            EndpointInfo.ServerInfo serverInfo = db.GetServerInfo(endpoint);

            if (serverInfo == null)
            {
                this.SendResponse(context.Response, "", HttpStatusCode.NotFound);
                return;
            }

            string serverInfoJson = JsonConvert.SerializeObject(serverInfo);

            this.SendResponse(context.Response, serverInfoJson, HttpStatusCode.OK);
        }

        public void PutServerInfo(HttpListenerContext context)
        {
            var inpStream = new StreamReader(context.Request.InputStream);

            EndpointInfo.ServerInfo serverInfo =
                JsonConvert.DeserializeObject<EndpointInfo.ServerInfo>(inpStream.ReadToEnd());
            string endPoint = ExtractEndpoint(context.Request);

            db.PutServerInfo(new EndpointInfo(endPoint, serverInfo));

            this.SendResponse(context.Response, "", HttpStatusCode.OK);
        }

        public void GetServerMatch(HttpListenerContext context)
        {
            string endpoint = ExtractEndpoint(context.Request);
            DateTime timestamp = ExtractTimestamp(context.Request);

            MatchInfo matchInfo = db.GetServerMatch(endpoint, timestamp);

            if (matchInfo == null)
            {
                this.SendResponse(context.Response, "", HttpStatusCode.NotFound);
                return;
            }

            string matchInfoJson = JsonConvert.SerializeObject(matchInfo);

            this.SendResponse(context.Response, matchInfoJson, HttpStatusCode.OK);
        }

        public void PutServerMatch(HttpListenerContext context)
        {
            var inpStream = new StreamReader(context.Request.InputStream);

            string endpoint = ExtractEndpoint(context.Request);
            DateTime timestamp = ExtractTimestamp(context.Request);

            MatchInfo matchInfo =
                JsonConvert.DeserializeObject<MatchInfo>(inpStream.ReadToEnd());

            this.SendResponse(context.Response, "",
                db.PutServerMatch(endpoint, timestamp, matchInfo) ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
        }

        public void GetServerStats(HttpListenerContext context)
        {
            string stats = enableCache ? serverStatsCache[ExtractEndpoint(context.Request)] : db.MakeServerStats(ExtractEndpoint(context.Request));

            this.SendResponse(context.Response, stats, HttpStatusCode.OK);
        }

        public void GetPlayerStats(HttpListenerContext context)
        {
            string stats = enableCache ? playerStatsCache[ExtractName(context.Request)] : db.MakePlayerStats(ExtractName(context.Request));

            this.SendResponse(context.Response, stats, HttpStatusCode.OK);
        }

        public void GetRecentMatchesReport(HttpListenerContext context)
        {
            string report = enableCache ? recentMatchesReportCache[ExtractCount(context.Request)]: db.MakeRecentMatchesReport(ExtractCount(context.Request));

            this.SendResponse(context.Response, report, HttpStatusCode.OK);
        }

        public void GetBestPlayersReport(HttpListenerContext context)
        {
            string report = enableCache? bestPlayersReportCache[ExtractCount(context.Request)] : db.MakeBestPlayersReport(ExtractCount(context.Request)); ;

            this.SendResponse(context.Response, report, HttpStatusCode.OK);
        }

        public void GetPopularServersReport(HttpListenerContext context)
        {
            string report = enableCache ? popularServersReportCache[ExtractCount(context.Request)] : db.MakePopularServersReport(ExtractCount(context.Request));

            this.SendResponse(context.Response, report, HttpStatusCode.OK);
        }

        public void SendResponse(HttpListenerResponse response, string body, HttpStatusCode code)
        {
            // IMPORTANT: don't move this line
            // Status code must be assigned before writing

            response.StatusCode = (int)code;

            using (var writer = new StreamWriter(response.OutputStream))
            {
                writer.Write(body);
            }

            response.Close();
        }

        public void HandleIncorrect(HttpListenerContext context)
        {
            var parts = context.Request.RawUrl.Split('/');

            Console.WriteLine("Incorrect \n" +
                              $"1: {parts[1]} \n" +
                              $"2: {parts[2]} \n" +
                              $"3: {parts[3] ?? "null"}");

            this.SendResponse(context.Response, "Incorrect", HttpStatusCode.BadRequest);
        }

        private static string ExtractEndpoint(HttpListenerRequest req)
        {
            // Looking for pattern: /servers/1.1.12.123-1234/*
            const string pattern = "\\/servers\\/(\\d\\.){3}\\d-\\d+\\/?.*";

            if (Regex.IsMatch(req.RawUrl, pattern)) return req.RawUrl.Split('/')[2];
            throw new ArgumentException("Incorrect url");
        }

        private static string ExtractName(HttpListenerRequest req)
        {
            const string pattern = "\\/players\\/[\\w\\d%]+\\/stats";

            if (Regex.IsMatch(req.RawUrl, pattern)) return req.RawUrl.Split('/')[2];
            throw new ArgumentException("Incorrect url");
        }

        private static DateTime ExtractTimestamp(HttpListenerRequest req)
        {
            const string pattern =
                "\\/servers\\/\\d+\\.\\d+\\.\\d+\\.\\d-\\d+\\/matches\\/\\d{1,4}-\\d{2}-\\d{1,2}T\\d{2}:\\d{2}:\\d{2}Z\\/?";

            if (Regex.IsMatch(req.RawUrl, pattern)) return DateTimeOffset.Parse(req.RawUrl.Split('/')[4]).UtcDateTime;
            throw new ArgumentException("Incorrect url");
        }

        private static int ExtractCount(HttpListenerRequest req)
        {
            const string pattern = "\\/reports\\/[\\w-]+\\/?\\/?(\\d+)?$";

            if (!Regex.IsMatch(req.RawUrl, pattern)) throw new ArgumentException("Incorrect Url");

            string[] spl = req.RawUrl.Split('/');
            // If count isn't set, default value = 5
            if (spl.Length < 4 || string.IsNullOrEmpty(spl[3])) return 5;

            int count = int.Parse(spl[3]);

            // 50, no more
            if (count >= 50) return 50;
            // 0, no less
            if (count <= 0) return 0;

            return count;
        }

        public StatsApi(IDbWorker database, bool cacheOn)
        {
            db = database;
            enableCache = cacheOn;

            // Setup cache
            playerStatsCache = new WeakCache<string, string>(db.MakePlayerStats);
            serverStatsCache = new WeakCache<string, string>(db.MakeServerStats);
            recentMatchesReportCache = new WeakCache<int, string>(db.MakeRecentMatchesReport);
            bestPlayersReportCache = new WeakCache<int, string>(db.MakeBestPlayersReport);
            popularServersReportCache = new WeakCache<int, string>(db.MakePopularServersReport);
        }
    }
}