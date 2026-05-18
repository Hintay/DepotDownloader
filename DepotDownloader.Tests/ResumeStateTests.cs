// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DepotDownloader;
using SteamKit2;
using Xunit;

namespace DepotDownloader.Tests
{
    public class ResumeStateTests
    {
        [Fact]
        public void MarkChunkCompleted_AllowsDuplicateChunkIdsAtDifferentOffsets()
        {
            var duplicateChunkId = Convert.FromHexString("04FA06E30C20BB2C070E3AB23B50ACC88EC60DF4");
            var firstChunk = CreateChunk(duplicateChunkId, offset: 0);
            var secondChunk = CreateChunk(duplicateChunkId, offset: 1024);
            var file = new DepotManifest.FileData
            {
                FileName = "repeated-data.bin",
                TotalSize = 2048,
                FileHash = new byte[20],
                Chunks = new List<DepotManifest.ChunkData> { firstChunk, secondChunk },
            };

            var state = ResumeState.Create(1, 2, 3, ".", new[] { file });

            Assert.True(state.MarkChunkCompleted(file, firstChunk));
            Assert.True(state.IsChunkCompleted(file, firstChunk));
            Assert.False(state.IsChunkCompleted(file, secondChunk));

            Assert.True(state.MarkChunkCompleted(file, secondChunk));
            Assert.True(state.IsChunkCompleted(file, secondChunk));
        }

        [Fact]
        public void IsValidFor_MigratesOldResumeStateWithoutChunkOffsets()
        {
            var duplicateChunkId = Convert.FromHexString("04FA06E30C20BB2C070E3AB23B50ACC88EC60DF4");
            var firstChunk = CreateChunk(duplicateChunkId, offset: 0);
            var secondChunk = CreateChunk(duplicateChunkId, offset: 1024);
            var file = new DepotManifest.FileData
            {
                FileName = "repeated-data.bin",
                TotalSize = 2048,
                FileHash = new byte[20],
                Chunks = new List<DepotManifest.ChunkData> { firstChunk, secondChunk },
            };
            var state = ResumeState.Create(1, 2, 3, ".", new[] { file });
            Assert.True(state.MarkChunkCompleted(file, firstChunk));

            // Simulate a resume file written before ChunkOffsets existed.
            var resumeFile = state.Files[0];
            SetProperty(resumeFile, nameof(ResumeFileState.ChunkOffsets), new List<ulong>());
            SetField<object>(resumeFile, "chunkIndexByIdentity", null);

            Assert.True(state.IsValidFor(1, 2, 3, ".", new[] { file }));
            Assert.True(state.IsChunkCompleted(file, firstChunk));
            Assert.False(state.IsChunkCompleted(file, secondChunk));
        }

        [Fact]
        public void RecordValidatedChunkCompleted_PersistsOnlyMatchedChunks()
        {
            var firstChunk = CreateChunk(Convert.FromHexString("04FA06E30C20BB2C070E3AB23B50ACC88EC60DF4"), offset: 0);
            var secondChunk = CreateChunk(Convert.FromHexString("50B9444144D1AC4E14A3747E8C3C2B418729B2F0"), offset: 1024);
            var file = new DepotManifest.FileData
            {
                FileName = "partially-validated.bin",
                TotalSize = 2048,
                FileHash = new byte[20],
                Chunks = new List<DepotManifest.ChunkData> { firstChunk, secondChunk },
            };
            var configDir = Path.Combine(Path.GetTempPath(), $"dd-resume-validated-{Guid.NewGuid():N}");

            try
            {
                var store = ResumeStateStore.Open(configDir, 1, 2, 3, ".", new[] { file }, ignoreExisting: true, downloadCounter: null);

                ContentDownloader.RecordValidatedChunkCompleted(store, file, firstChunk, matched: true, downloadCounter: null);
                ContentDownloader.RecordValidatedChunkCompleted(store, file, secondChunk, matched: false, downloadCounter: null);

                Assert.True(store.State.IsChunkCompleted(file, firstChunk));
                Assert.False(store.State.IsChunkCompleted(file, secondChunk));
            }
            finally
            {
                if (Directory.Exists(configDir))
                {
                    Directory.Delete(configDir, recursive: true);
                }
            }
        }

        [Fact]
        public void ShouldTreatExistingFileAsPreallocatedEmpty_OnlyForLoadedResumeWithNoCompletedChunks()
        {
            var chunk = CreateChunk(Convert.FromHexString("04FA06E30C20BB2C070E3AB23B50ACC88EC60DF4"), offset: 0);
            var file = new DepotManifest.FileData
            {
                FileName = "preallocated.bin",
                TotalSize = 1024,
                FileHash = new byte[20],
                Chunks = new List<DepotManifest.ChunkData> { chunk },
            };
            var configDir = Path.Combine(Path.GetTempPath(), $"dd-resume-empty-{Guid.NewGuid():N}");
            var dataPath = Path.Combine(configDir, file.FileName);
            var originalVerifyAll = ContentDownloader.Config.VerifyAll;

            try
            {
                Directory.CreateDirectory(configDir);
                using (var fs = File.Create(dataPath))
                {
                    fs.SetLength((long)file.TotalSize);
                }

                var writer = ResumeStateStore.Open(configDir, 1, 2, 3, ".", new[] { file }, ignoreExisting: true, downloadCounter: null);
                Assert.True(writer.MarkChunkCompleted(file, chunk, downloadCounter: null));
                writer.ClearFile(file, downloadCounter: null);
                writer.SaveIfDirty(downloadCounter: null, force: true);

                var loaded = ResumeStateStore.Open(configDir, 1, 2, 3, ".", new[] { file }, ignoreExisting: false, downloadCounter: null);
                var newState = ResumeStateStore.Open(Path.Combine(configDir, "new"), 1, 2, 3, ".", new[] { file }, ignoreExisting: true, downloadCounter: null);

                ContentDownloader.Config.VerifyAll = false;
                Assert.True(ContentDownloader.ShouldTreatExistingFileAsPreallocatedEmpty(loaded, file, new FileInfo(dataPath)));
                Assert.False(ContentDownloader.ShouldTreatExistingFileAsPreallocatedEmpty(newState, file, new FileInfo(dataPath)));

                Assert.True(loaded.MarkChunkCompleted(file, chunk, downloadCounter: null));
                Assert.False(ContentDownloader.ShouldTreatExistingFileAsPreallocatedEmpty(loaded, file, new FileInfo(dataPath)));

                ContentDownloader.Config.VerifyAll = true;
                loaded.ClearFile(file, downloadCounter: null);
                Assert.False(ContentDownloader.ShouldTreatExistingFileAsPreallocatedEmpty(loaded, file, new FileInfo(dataPath)));
            }
            finally
            {
                ContentDownloader.Config.VerifyAll = originalVerifyAll;

                if (Directory.Exists(configDir))
                {
                    Directory.Delete(configDir, recursive: true);
                }
            }
        }

        [Fact]
        public void GetVerifyWorkForExistingFile_SkipsPreallocatedEmptyResumeFile()
        {
            var chunk = CreateChunk(Convert.FromHexString("04FA06E30C20BB2C070E3AB23B50ACC88EC60DF4"), offset: 0);
            var file = new DepotManifest.FileData
            {
                FileName = "preallocated.bin",
                TotalSize = 1024,
                FileHash = new byte[20],
                Chunks = new List<DepotManifest.ChunkData> { chunk },
            };
            var configDir = Path.Combine(Path.GetTempPath(), $"dd-verify-work-empty-{Guid.NewGuid():N}");
            var dataPath = Path.Combine(configDir, file.FileName);
            var originalVerifyAll = ContentDownloader.Config.VerifyAll;

            try
            {
                Directory.CreateDirectory(configDir);
                using (var fs = File.Create(dataPath))
                {
                    fs.SetLength((long)file.TotalSize);
                }

                var writer = ResumeStateStore.Open(configDir, 1, 2, 3, ".", new[] { file }, ignoreExisting: true, downloadCounter: null);
                Assert.True(writer.MarkChunkCompleted(file, chunk, downloadCounter: null));
                writer.ClearFile(file, downloadCounter: null);
                writer.SaveIfDirty(downloadCounter: null, force: true);

                var loaded = ResumeStateStore.Open(configDir, 1, 2, 3, ".", new[] { file }, ignoreExisting: false, downloadCounter: null);

                ContentDownloader.Config.VerifyAll = false;
                var (verifyBytes, verifyChunks) = ContentDownloader.GetVerifyWorkForExistingFile(file, oldManifestFile: null, loaded, dataPath);

                Assert.Equal(0UL, verifyBytes);
                Assert.Equal(0, verifyChunks);
            }
            finally
            {
                ContentDownloader.Config.VerifyAll = originalVerifyAll;

                if (Directory.Exists(configDir))
                {
                    Directory.Delete(configDir, recursive: true);
                }
            }
        }

        static DepotManifest.ChunkData CreateChunk(byte[] chunkId, ulong offset)
        {
            return new DepotManifest.ChunkData
            {
                ChunkID = (byte[])chunkId.Clone(),
                Offset = offset,
                CompressedLength = 1024,
                UncompressedLength = 1024,
            };
        }

        static void SetProperty<T>(object instance, string propertyName, T value)
        {
            instance.GetType().GetProperty(propertyName).SetValue(instance, value);
        }

        static void SetField<T>(object instance, string fieldName, T value)
        {
            instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
        }
    }
}
