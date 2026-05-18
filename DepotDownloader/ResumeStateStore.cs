// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ProtoBuf;
using SteamKit2;

namespace DepotDownloader
{
    class ResumeStateStore
    {
        static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(1);

        readonly string filePath;
        readonly object sync = new();

        bool dirty;
        DateTime lastSaveTime;

        public ResumeState State { get; }
        public bool CanUseForResume { get; }

        ResumeStateStore(string filePath, ResumeState state, bool canUseForResume)
        {
            this.filePath = filePath;
            State = state;
            CanUseForResume = canUseForResume;
        }

        public static ResumeStateStore Open(
            string configDir,
            uint appId,
            uint depotId,
            ulong manifestId,
            string installRoot,
            IReadOnlyCollection<DepotManifest.FileData> files,
            bool ignoreExisting,
            GlobalDownloadCounter downloadCounter)
        {
            var resumeDir = Path.Combine(configDir, "resume");
            var filePath = Path.Combine(resumeDir, $"{depotId}_{manifestId}.resume");
            var loaded = false;
            ResumeState state = null;

            if (!ignoreExisting && File.Exists(filePath))
            {
                try
                {
                    using var fs = File.Open(filePath, FileMode.Open);
                    using var ds = new DeflateStream(fs, CompressionMode.Decompress);
                    state = Serializer.Deserialize<ResumeState>(ds);
                    loaded = state != null && state.IsValidFor(appId, depotId, manifestId, installRoot, files);
                }
                catch (Exception ex)
                {
                    downloadCounter?.Log("Warning: failed to load resume state {0}: {1}", filePath, ex.Message);
                }

                if (!loaded)
                {
                    TryDelete(filePath, downloadCounter);
                }
            }

            state ??= ResumeState.Create(appId, depotId, manifestId, installRoot, files);

            return new ResumeStateStore(filePath, state, loaded);
        }

        public bool MarkChunkCompleted(DepotManifest.FileData file, DepotManifest.ChunkData chunk, GlobalDownloadCounter downloadCounter)
        {
            if (!State.MarkChunkCompleted(file, chunk))
            {
                return false;
            }

            SaveIfDue(downloadCounter);
            return true;
        }

        public void ClearFile(DepotManifest.FileData file, GlobalDownloadCounter downloadCounter)
        {
            if (State.ClearFile(file))
            {
                MarkDirty();
                SaveIfDue(downloadCounter);
            }
        }

        public void SaveIfDirty(GlobalDownloadCounter downloadCounter, bool force)
        {
            lock (sync)
            {
                if (!dirty)
                {
                    return;
                }

                if (!force && DateTime.UtcNow - lastSaveTime < SaveInterval)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                    var tempPath = filePath + ".tmp";
                    using (var fs = File.Open(tempPath, FileMode.Create))
                    using (var ds = new DeflateStream(fs, CompressionMode.Compress))
                    {
                        lock (State.SyncRoot)
                        {
                            Serializer.Serialize(ds, State);
                        }
                    }

                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }

                    File.Move(tempPath, filePath);
                    dirty = false;
                    lastSaveTime = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    downloadCounter?.Log("Warning: failed to save resume state {0}: {1}", filePath, ex.Message);
                }
            }
        }

        public void Delete(GlobalDownloadCounter downloadCounter)
        {
            lock (sync)
            {
                dirty = false;
                TryDelete(filePath, downloadCounter);
            }
        }

        void SaveIfDue(GlobalDownloadCounter downloadCounter)
        {
            MarkDirty();
            SaveIfDirty(downloadCounter, force: false);
        }

        void MarkDirty()
        {
            lock (sync)
            {
                dirty = true;
            }
        }

        static void TryDelete(string path, GlobalDownloadCounter downloadCounter)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                downloadCounter?.Log("Warning: failed to delete resume state {0}: {1}", path, ex.Message);
            }
        }
    }
}
