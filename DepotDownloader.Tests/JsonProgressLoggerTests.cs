using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace DepotDownloader.Tests
{
    public class JsonProgressLoggerTests
    {
        private string CaptureStdout(Action action)
        {
            var original = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            try { action(); } finally { Console.SetOut(original); }
            return sw.ToString();
        }

        [Fact]
        public void EmitAppStart_when_disabled_writes_nothing()
        {
            JsonProgressLogger.Enabled = false;
            var output = CaptureStdout(() => JsonProgressLogger.EmitAppStart(1086940, new uint[] { 1086941 }));
            Assert.Equal(string.Empty, output);
        }

        [Fact]
        public void EmitAppStart_when_enabled_writes_single_json_line()
        {
            JsonProgressLogger.Enabled = true;
            var output = CaptureStdout(() => JsonProgressLogger.EmitAppStart(1086940, new uint[] { 1086941, 1086942 }));
            Assert.EndsWith("\n", output);
            var line = output.TrimEnd('\n');
            Assert.DoesNotContain("\n", line);

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.Equal("app_start", root.GetProperty("type").GetString());
            Assert.Equal(1086940u, root.GetProperty("app_id").GetUInt32());
            var depots = root.GetProperty("depots").EnumerateArray();
            Assert.Equal(2, root.GetProperty("depots").GetArrayLength());
        }

        [Fact]
        public void EmitDepotDone_failed_includes_error_string()
        {
            JsonProgressLogger.Enabled = true;
            var output = CaptureStdout(() => JsonProgressLogger.EmitDepotDone(1086941, false, "Manifest mismatch"));
            using var doc = JsonDocument.Parse(output.Trim());
            Assert.Equal("depot_done", doc.RootElement.GetProperty("type").GetString());
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Manifest mismatch", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public void EmitProgress_throttles_to_4_per_second_per_depot()
        {
            JsonProgressLogger.Enabled = true;
            JsonProgressLogger.ResetThrottleForTests();
            var output = CaptureStdout(() =>
            {
                for (int i = 0; i < 10; i++)
                    JsonProgressLogger.EmitProgress(1086941, downloaded: (ulong)(i * 1000), percent: i * 10, speedBps: 0, etaSec: 0);
            });
            // First call always emits; subsequent within 250ms are dropped.
            var lines = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
        }
    }
}
