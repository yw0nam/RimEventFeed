using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Net.Http;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

// Research build: exports raw game events as JSON so their shape can be inspected.
// Each event is written to Player.log and POSTed to a local receiver (tools/dump_receiver.py).
namespace RimEventFeed
{
    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            new Harmony("yw0nam.rimeventfeed").PatchAll();
            Log.Message("[RimEventFeed] loaded, posting to " + Outbox.Url);
            try
            {
                CommandServer.Start();
                Log.Message("[RimEventFeed] commands on " + CommandServer.Prefix + ", token in " + CommandServer.TokenPath);
            }
            catch (Exception ex)
            {
                Log.Error("[RimEventFeed] command server failed to start: " + ex);
            }
        }
    }

    // Funnel overload: the two label/text overloads build a ChoiceLetter and call this one.
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter),
        new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class ReceiveLetterPatch
    {
        public static void Postfix(Letter let, int delayTicks)
        {
            // A delayed letter is queued here and passes through this method again when it arrives.
            if (delayTicks > 0 || !let.CanShowInLetterStack) return;
            Emit.Event("letter", new Dictionary<string, object>
            {
                ["letterId"] = let.ID,
                ["letterDef"] = let.def?.defName,
                ["letterClass"] = let.GetType().Name,
                ["label"] = Describe.Text(let.Label),
                ["text"] = let is ChoiceLetter choice ? Describe.Text(choice.Text) : null,
                ["targets"] = Describe.Targets(let.lookTargets),
            });
        }
    }

    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    public static class IncidentPatch
    {
        public static void Postfix(IncidentWorker __instance, IncidentParms parms, bool __result)
        {
            Emit.Event("incident", new Dictionary<string, object>
            {
                ["incidentDef"] = __instance.def?.defName,
                ["category"] = __instance.def?.category?.defName,
                ["succeeded"] = __result,
                ["points"] = parms.points,
                ["forced"] = parms.forced,
                ["faction"] = Describe.Faction(parms.faction),
                ["target"] = parms.target?.ToString(),
            });
        }
    }

    // Every mental break goes through MentalBreakWorker.TryStart, but four subclasses override it
    // (RunWild, WildDecree, Catatonic start no mental state at all), so each declaring type is patched.
    [HarmonyPatch]
    public static class MentalBreakPatch
    {
        // HumanityBreak calls base.TryStart; only the outermost call is the event.
        private static int depth;

        public static IEnumerable<MethodBase> TargetMethods() =>
            AccessTools.AllTypes()
                .Where(t => typeof(MentalBreakWorker).IsAssignableFrom(t))
                .Select(t => AccessTools.DeclaredMethod(t, nameof(MentalBreakWorker.TryStart),
                    new[] { typeof(Pawn), typeof(string), typeof(bool) }))
                .Where(m => m != null);

        public static void Prefix() => depth++;

        // Runs after Postfix and also when TryStart throws, so depth cannot stay raised.
        public static void Finalizer() => depth--;

        public static void Postfix(MentalBreakWorker __instance, Pawn pawn, string reason, bool causedByMood, bool __result)
        {
            if (depth > 1 || !__result) return;
            Emit.Event("mentalBreak", new Dictionary<string, object>
            {
                ["mentalBreakDef"] = __instance.def?.defName,
                ["workerClass"] = __instance.GetType().Name,
                ["intensity"] = __instance.def?.intensity.ToString(),
                ["mentalStateDef"] = __instance.def?.mentalState?.defName,
                ["label"] = __instance.def?.label,
                ["reason"] = reason,
                ["causedByMood"] = causedByMood,
                ["pawn"] = Describe.Thing(pawn),
            });
        }
    }

    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    public static class MentalStatePatch
    {
        public static void Postfix(Pawn ___pawn, MentalStateDef stateDef, string reason, bool causedByMood, bool __result)
        {
            // The game probes many states that fail; only a state that actually started is an event.
            if (!__result) return;
            Emit.Event("mentalState", new Dictionary<string, object>
            {
                ["mentalStateDef"] = stateDef?.defName,
                ["label"] = stateDef?.label,
                ["reason"] = reason,
                ["causedByMood"] = causedByMood,
                ["pawn"] = Describe.Thing(___pawn),
            });
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.NotifyPlayerOfKilled))]
    public static class KilledPatch
    {
        public static void Postfix(Pawn ___pawn, DamageInfo? dinfo, Hediff hediff)
        {
            Emit.Event("death", new Dictionary<string, object>
            {
                ["pawn"] = Describe.Thing(___pawn),
                ["damageDef"] = dinfo?.Def?.defName,
                ["instigator"] = Describe.Thing(dinfo?.Instigator),
                ["hediffDef"] = hediff?.def?.defName,
            });
        }
    }

    // All Messages.Message overloads build a Message and call this one.
    [HarmonyPatch(typeof(Messages), nameof(Messages.Message), new[] { typeof(Message), typeof(bool) })]
    public static class MessagePatch
    {
        public static void Postfix(Message msg)
        {
            // Rejected (duplicate) messages are not shown; input feedback is not a game event.
            if (!Messages.IsLive(msg)) return;
            if (msg.def == MessageTypeDefOf.RejectInput || msg.def == MessageTypeDefOf.CautionInput
                || msg.def == MessageTypeDefOf.SilentInput) return;
            Emit.Event("message", new Dictionary<string, object>
            {
                ["messageDef"] = msg.def?.defName,
                ["text"] = msg.text?.StripTags(),
                ["targets"] = Describe.Targets(msg.lookTargets),
            });
        }
    }

    public static class Describe
    {
        // Resolve turns (*Name)...(/Name) markup into rich text; StripTags then removes <color> and friends.
        public static string Text(TaggedString s) => s.Resolve().StripTags();

        public static object Thing(Thing t)
        {
            if (t == null) return null;
            var d = new Dictionary<string, object>
            {
                ["thingId"] = t.ThingID,
                ["def"] = t.def?.defName,
                ["label"] = t.LabelShort,
                ["faction"] = Faction(t.Faction),
            };
            if (t is Pawn p)
            {
                d["kindDef"] = p.kindDef?.defName;
                d["isColonist"] = p.IsColonist;
            }
            return d;
        }

        public static object Faction(Faction f) =>
            f == null ? null : new Dictionary<string, object> { ["loadId"] = f.loadID, ["def"] = f.def?.defName, ["name"] = f.Name };

        public static object Targets(LookTargets targets)
        {
            if (targets?.targets == null) return null;
            var list = new List<object>();
            foreach (GlobalTargetInfo t in targets.targets)
                list.Add(t.HasThing ? Thing(t.Thing) : new Dictionary<string, object> { ["label"] = t.Label });
            return list;
        }
    }

    public static class Emit
    {
        public static void Event(string kind, Dictionary<string, object> fields)
        {
            var e = new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["ticksGame"] = Find.TickManager?.TicksGame,
                ["sentAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["fields"] = fields,
            };
            string json;
            try { json = Json.Write(e); }
            catch (Exception ex) { Log.Warning("[RimEventFeed] failed to serialize " + kind + ": " + ex.Message); return; }
            Log.Message("[RimEventFeed] " + json);
            Outbox.Enqueue(json);
        }
    }

    public static class Json
    {
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            Append(sb, v);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: AppendString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case Dictionary<string, object> d:
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in d)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        AppendString(sb, kv.Key);
                        sb.Append(':');
                        Append(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case List<object> list:
                    sb.Append('[');
                    for (var n = 0; n < list.Count; n++)
                    {
                        if (n > 0) sb.Append(',');
                        Append(sb, list[n]);
                    }
                    sb.Append(']');
                    break;
                default: AppendString(sb, v.ToString()); break;
            }
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    // The main thread only enqueues; one background thread posts. Receiver down = event dropped.
    public static class Outbox
    {
        public const string Url = "http://127.0.0.1:8799/events";
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private static readonly BlockingCollection<string> Queue = new BlockingCollection<string>(boundedCapacity: 256);
        private static readonly Thread Worker = StartWorker();

        public static void Enqueue(string json) => Queue.TryAdd(json);

        private static Thread StartWorker()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "RimEventFeed.Outbox" };
            t.Start();
            return t;
        }

        private static void Run()
        {
            foreach (var json in Queue.GetConsumingEnumerable())
            {
                try
                {
                    using (var body = new StringContent(json, Encoding.UTF8, "application/json"))
                        Client.PostAsync(Url, body).GetAwaiter().GetResult().Dispose();
                }
                catch (Exception)
                {
                    // Never call Verse.Log from this thread.
                }
            }
        }
    }
}
