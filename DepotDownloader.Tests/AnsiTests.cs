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
            // Reset between tests
            Ansi.progressDepth = 0;
            while (Ansi.deferredOutput.TryDequeue(out _)) { }
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
    }
}
