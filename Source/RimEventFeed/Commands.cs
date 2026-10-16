using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using RimWorld;
using Verse;

// Research build: a loopback command channel. The listener thread only authenticates and queues;
// the game's main thread runs each command in GameComponentUpdate, which keeps running while paused.
namespace RimEventFeed
{
    public static class CommandServer
    {
        public const string Prefix = "http://127.0.0.1:8798/";
        private const int TimeoutMs = 5000;

        public static readonly ConcurrentQueue<Pending> Inbox = new ConcurrentQueue<Pending>();
        public static string TokenPath { get; private set; }
        private static string token;

        public static void Start()
        {
            token = Guid.NewGuid().ToString("N");
            var dir = Path.Combine(GenFilePaths.ConfigFolderPath, "RimEventFeed");
            Directory.CreateDirectory(dir);
            TokenPath = Path.Combine(dir, "token");
            File.WriteAllText(TokenPath, token);

            var listener = new HttpListener();
            listener.Prefixes.Add(Prefix);
            listener.Start();
            new Thread(() => Listen(listener)) { IsBackground = true, Name = "RimEventFeed.Commands" }.Start();
        }

        private static void Listen(HttpListener listener)
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception) { return; }
                // An exception escaping a pool thread would take the game down with it.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(ctx); }
                    catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
                });
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            int status;
            object body;
            if (req.Headers["Origin"] != null)
            {
                // A browser page never gets to drive the game.
                (status, body) = (403, Error("requests from a browser origin are refused"));
            }
            else if (req.Headers["Authorization"] != "Bearer " + token)
            {
                (status, body) = (401, Error("missing or wrong bearer token"));
            }
            else
            {
                var action = req.Url.AbsolutePath.Trim('/');
                var expected = action == "colonists" ? "GET" : "POST";
                if (!Actions.Allowed.Contains(action))
                    (status, body) = (404, Error("unknown action: " + action));
                else if (req.HttpMethod != expected)
                    (status, body) = (405, Error(action + " takes " + expected));
                else
                    (status, body) = RunOnMainThread(action, req.QueryString);
            }

            var bytes = Encoding.UTF8.GetBytes(Json.Write(body));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            try { ctx.Response.OutputStream.Write(bytes, 0, bytes.Length); }
            finally { ctx.Response.Close(); }
        }

        private static (int, object) RunOnMainThread(string action, NameValueCollection args)
        {
            var p = new Pending(action, args);
            Inbox.Enqueue(p);
            if (!p.Done.Wait(TimeoutMs) && p.TryExpire())
                return (503, Error("not run within " + TimeoutMs + " ms: no game loaded, or the main thread is stalled"));
            p.Done.Wait();
            return (p.Status, p.Result);
        }

        public static Dictionary<string, object> Error(string message) =>
            new Dictionary<string, object> { ["ok"] = false, ["error"] = message };
    }

    public class Pending
    {
        private const int Queued = 0, Taken = 1, Expired = 2;
        private int state;
        private readonly Stopwatch sinceQueued = Stopwatch.StartNew();

        public readonly string Action;
        public readonly NameValueCollection Args;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim();
        public int Status;
        public object Result;

        public Pending(string action, NameValueCollection args) => (Action, Args) = (action, args);

        public long WaitedMs => sinceQueued.ElapsedMilliseconds;

        // Exactly one of TryTake (main thread) and TryExpire (HTTP thread) wins,
        // so a timed-out command can never run later when a game loads.
        public bool TryTake() => Interlocked.CompareExchange(ref state, Taken, Queued) == Queued;
        public bool TryExpire() => Interlocked.CompareExchange(ref state, Expired, Queued) == Queued;
    }

    public class CommandRunner : GameComponent
    {
        public CommandRunner(Game game) { }

        public override void GameComponentUpdate()
        {
            while (CommandServer.Inbox.TryDequeue(out var p))
            {
                if (!p.TryTake()) continue;
                var waited = p.WaitedMs;
                try
                {
                    (p.Status, p.Result) = Actions.Run(p.Action, p.Args);
                }
                catch (Exception ex)
                {
                    (p.Status, p.Result) = (500, CommandServer.Error(ex.GetType().Name + ": " + ex.Message));
                }
                if (p.Result is Dictionary<string, object> d) d["waitedMs"] = waited;
                Emit.Event("command", new Dictionary<string, object>
                {
                    ["action"] = p.Action,
                    ["args"] = string.Join("&", p.Args.AllKeys.Select(k => k + "=" + p.Args[k])),
                    ["status"] = p.Status,
                    ["result"] = p.Result,
                });
                p.Done.Set();
            }
        }
    }

    // The allowlist. Every action is reversible by another action on this list.
    public static class Actions
    {
        public static readonly HashSet<string> Allowed =
            new HashSet<string> { "colonists", "pause", "resume", "speed", "draft", "undraft" };

        public static (int, object) Run(string action, NameValueCollection args)
        {
            var tm = Find.TickManager;
            switch (action)
            {
                case "colonists":
                    return Ok(new Dictionary<string, object>
                    {
                        ["colonists"] = Find.Maps.SelectMany(m => m.mapPawns.FreeColonists).Select(c => (object)new Dictionary<string, object>
                        {
                            ["thingId"] = c.ThingID,
                            ["label"] = c.LabelShort,
                            ["drafted"] = c.Drafted,
                            ["downed"] = c.Downed,
                            ["mentalState"] = c.MentalStateDef?.defName,
                        }).ToList(),
                    });
                case "pause":
                    if (!tm.Paused) tm.TogglePaused();
                    return Ok(Clock(tm));
                case "resume":
                    if (tm.Paused) tm.TogglePaused();
                    return Ok(Clock(tm));
                case "speed":
                    if (!int.TryParse(args["value"], out var v) || v < 1 || v > 3)
                        return (400, CommandServer.Error("speed takes value=1|2|3"));
                    tm.CurTimeSpeed = (TimeSpeed)v;
                    return Ok(Clock(tm));
                case "draft":
                case "undraft":
                    var id = args["pawn"];
                    var pawn = Find.Maps.SelectMany(m => m.mapPawns.FreeColonists).FirstOrDefault(c => c.ThingID == id);
                    if (pawn == null) return (404, CommandServer.Error("no free colonist with thingId " + id));
                    if (pawn.drafter == null || !pawn.drafter.ShowDraftGizmo || pawn.Downed || pawn.InMentalState)
                        return (409, CommandServer.Error(pawn.LabelShort + " cannot be drafted now"));
                    pawn.drafter.Drafted = action == "draft";
                    return Ok(new Dictionary<string, object> { ["thingId"] = id, ["drafted"] = pawn.Drafted });
                default:
                    return (404, CommandServer.Error("unknown action: " + action));
            }
        }

        private static Dictionary<string, object> Clock(TickManager tm) =>
            new Dictionary<string, object> { ["paused"] = tm.Paused, ["speed"] = tm.CurTimeSpeed.ToString() };

        private static (int, object) Ok(Dictionary<string, object> d)
        {
            d["ok"] = true;
            return (200, d);
        }
    }
}
