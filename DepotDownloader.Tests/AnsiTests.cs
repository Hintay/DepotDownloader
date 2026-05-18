// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DepotDownloader;
using Xunit;

namespace DepotDownloader.Tests
{
    [Collection("AnsiSingleton")]
    public class AnsiTests
    {
        public AnsiTests()
        {
            Ansi.ResetForTests();
        }

        [Fact]
        public void LogLine_OutsideProgress_DoesNotEnqueue()
        {
            Ansi.progressDepth = 0;
            Ansi.LogLine("hello {0}", "world");

            Assert.Empty(Ansi.deferredOutput);
        }

        [Fact]
        public void LogLine_InsideProgress_Enqueues()
        {
            Ansi.progressDepth = 1;
            try
            {
                Ansi.LogLine("hello {0}", "world");
                Ansi.LogWrite("partial");

                Assert.Equal(2, Ansi.deferredOutput.Count);
                var items = Ansi.deferredOutput.ToArray();
                Assert.Equal("hello world" + System.Environment.NewLine, items[0]);
                Assert.Equal("partial", items[1]);
            }
            finally
            {
                Ansi.progressDepth = 0;
            }
        }

        [Fact]
        public async Task LogLine_ConcurrentEnqueue_PreservesItemCount()
        {
            Ansi.progressDepth = 1;
            try
            {
                const int threadsCount = 16;
                const int perThread = 50;

                await Task.WhenAll(Enumerable.Range(0, threadsCount).Select(t =>
                    Task.Run(() =>
                    {
                        for (var i = 0; i < perThread; i++)
                        {
                            Ansi.LogLine("t{0}-i{1}", t, i);
                        }
                    })));

                Assert.Equal(threadsCount * perThread, Ansi.deferredOutput.Count);
            }
            finally
            {
                Ansi.progressDepth = 0;
            }
        }

        [Fact]
        public void LogLine_NoArgs_EnqueuesUnformatted()
        {
            Ansi.progressDepth = 1;
            try
            {
                Ansi.LogLine("raw {0} should NOT be expanded");
                Ansi.LogLine(""); // empty line case

                var items = Ansi.deferredOutput.ToArray();
                Assert.Equal("raw {0} should NOT be expanded" + System.Environment.NewLine, items[0]);
                Assert.Equal(System.Environment.NewLine, items[1]);
            }
            finally
            {
                Ansi.progressDepth = 0;
            }
        }

        [Fact]
        public async Task RunWithProgressAsync_FlushesQueueOnExit()
        {
            // Verifies the final DrainBatch in finally actually empties the
            // queue end-to-end (lifecycle test, not state-poke).
            var counter = new GlobalDownloadCounter();
            counter.Begin(totalSize: 100, useInteractiveProgress: false);

            try
            {
                await Ansi.RunWithProgressAsync(counter, async () =>
                {
                    // Simulate workers writing during Progress
                    Ansi.LogLine("line {0}", 1);
                    Ansi.LogLine("line {0}", 2);
                    Ansi.LogLine("line {0}", 3);
                    await Task.Yield();
                });
            }
            catch (System.NotSupportedException) { /* Spectre in headless test env */ }

            // After exit, the queue must be empty.
            Assert.Empty(Ansi.deferredOutput);
            // progressDepth must be 0.
            Assert.Equal(0, Ansi.progressDepth);
        }

        [Fact]
        public void GlobalCounter_AccumulatesAcrossDepots()
        {
            var counter = new GlobalDownloadCounter();
            counter.Begin(totalSize: 1000, useInteractiveProgress: false);

            // First depot: 0 done + 6 total
            counter.SetCurrentDepot(941951);
            counter.RegisterDepotChunks(completedChunks: 0, totalChunks: 6);

            // Two chunks done
            counter.AddCompletedChunk(bytes: 10, diskBytes: 10, diskElapsed: System.TimeSpan.Zero);
            counter.AddCompletedChunk(bytes: 10, diskBytes: 10, diskElapsed: System.TimeSpan.Zero);

            // Second depot starts: 3 already validated + 10 total
            counter.SetCurrentDepot(941952);
            counter.RegisterDepotChunks(completedChunks: 3, totalChunks: 10);

            // One more chunk
            counter.AddCompletedChunk(bytes: 10, diskBytes: 10, diskElapsed: System.TimeSpan.Zero);

            var desc = counter.BuildProgressDescription();
            // 2 (depot 1 chunks) + 3 (depot 2 validated) + 1 (depot 2 new) = 6
            // 6 (depot 1 total) + 10 (depot 2 total) = 16
            Assert.Contains("C 6/16", desc);
            Assert.Contains("Depot 941952", desc);
        }

        [Fact]
        public void GlobalCounter_SetCurrentDepotPreservesCounters()
        {
            var counter = new GlobalDownloadCounter();
            counter.Begin(totalSize: 1000, useInteractiveProgress: false);

            counter.RegisterDepotChunks(completedChunks: 5, totalChunks: 10);
            counter.SetCurrentDepot(123);
            counter.SetCurrentDepot(456);  // switching depot should NOT reset counters

            var desc = counter.BuildProgressDescription();
            Assert.Contains("C 5/10", desc);
            Assert.Contains("Depot 456", desc);
        }

        [Fact]
        public void AddCompletedChunks_IncrementsGlobalCounter()
        {
            var counter = new GlobalDownloadCounter();
            counter.Begin(totalSize: 1000, useInteractiveProgress: false);
            counter.RegisterDepotChunks(0, 100);

            counter.AddCompletedChunks(5);
            counter.AddCompletedChunks(3);
            counter.AddCompletedChunks(0);    // no-op
            counter.AddCompletedChunks(-1);   // no-op

            var desc = counter.BuildProgressDescription();
            Assert.Contains("C 8/100", desc);
        }
    }
}
