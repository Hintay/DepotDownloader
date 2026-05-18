// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DepotDownloader
{
    static class JsonProgressLogger
    {
        public static bool Enabled { get; set; }

        // Per-depot throttle: at most one progress event per 250 ms.
        static readonly Dictionary<uint, long> LastEmitTicks = new();
        const long ThrottleTicks = TimeSpan.TicksPerMillisecond * 250;

        static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

        public static void EmitAppStart(uint appId, IEnumerable<uint> depotIds)
        {
            if (!Enabled) return;
            using var ms = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(ms, WriterOptions))
            {
                w.WriteStartObject();
                w.WriteString("type", "app_start");
                w.WriteNumber("app_id", appId);
                w.WritePropertyName("depots");
                w.WriteStartArray();
                foreach (var d in depotIds) w.WriteNumberValue(d);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            WriteLine(ms.ToArray());
        }

        public static void EmitSessionReady(string cmEndpoint)
        {
            if (!Enabled) return;
            EmitSimple("session_ready", w => w.WriteString("cm_endpoint", cmEndpoint ?? string.Empty));
        }

        public static void EmitDepotStart(uint depotId, ulong manifestId, ulong? totalBytes)
        {
            if (!Enabled) return;
            EmitSimple("depot_start", w =>
            {
                w.WriteNumber("depot_id", depotId);
                w.WriteString("manifest_id", manifestId.ToString());
                if (totalBytes.HasValue) w.WriteNumber("total_bytes", totalBytes.Value);
            });
        }

        public static void EmitProgress(uint depotId, ulong downloaded, double percent, ulong speedBps, long etaSec)
        {
            if (!Enabled) return;
            var now = DateTime.UtcNow.Ticks;
            lock (LastEmitTicks)
            {
                if (LastEmitTicks.TryGetValue(depotId, out var last) && now - last < ThrottleTicks)
                    return;
                LastEmitTicks[depotId] = now;
            }
            EmitSimple("progress", w =>
            {
                w.WriteNumber("depot_id", depotId);
                w.WriteNumber("downloaded", downloaded);
                w.WriteNumber("percent", Math.Round(percent, 2));
                w.WriteNumber("speed_bps", speedBps);
                w.WriteNumber("eta_sec", etaSec);
            });
        }

        public static void EmitDepotDone(uint depotId, bool success, string error = null)
        {
            if (!Enabled) return;
            EmitSimple("depot_done", w =>
            {
                w.WriteNumber("depot_id", depotId);
                w.WriteBoolean("success", success);
                if (!success && !string.IsNullOrEmpty(error)) w.WriteString("error", error);
            });
            lock (LastEmitTicks) LastEmitTicks.Remove(depotId);
        }

        public static void EmitAcfWritten(string path)
        {
            if (!Enabled) return;
            EmitSimple("acf_written", w => w.WriteString("path", path ?? string.Empty));
        }

        public static void EmitAppDone(bool success, IEnumerable<uint> okDepots, IEnumerable<uint> failedDepots)
        {
            if (!Enabled) return;
            EmitSimple("app_done", w =>
            {
                w.WriteBoolean("success", success);
                w.WritePropertyName("depots_ok");
                w.WriteStartArray();
                foreach (var d in okDepots) w.WriteNumberValue(d);
                w.WriteEndArray();
                w.WritePropertyName("depots_failed");
                w.WriteStartArray();
                foreach (var d in failedDepots) w.WriteNumberValue(d);
                w.WriteEndArray();
            });
        }

        static void EmitSimple(string type, Action<Utf8JsonWriter> writeBody)
        {
            using var ms = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(ms, WriterOptions))
            {
                w.WriteStartObject();
                w.WriteString("type", type);
                writeBody(w);
                w.WriteEndObject();
            }
            WriteLine(ms.ToArray());
        }

        static void WriteLine(byte[] jsonBytes)
        {
            // Console.OpenStandardOutput would bypass redirection in tests; use Console.Out.
            Console.Out.Write(System.Text.Encoding.UTF8.GetString(jsonBytes));
            Console.Out.WriteLine();
            Console.Out.Flush();
        }

        // Test hook
        internal static void ResetThrottleForTests()
        {
            lock (LastEmitTicks) LastEmitTicks.Clear();
        }
    }
}
