// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProtoBuf;
using SteamKit2;

namespace DepotDownloader
{
    [ProtoContract]
    class ResumeState
    {
        public const int CurrentVersion = 1;

        [ProtoMember(1)]
        public int Version { get; private set; }

        [ProtoMember(2)]
        public uint AppId { get; private set; }

        [ProtoMember(3)]
        public uint DepotId { get; private set; }

        [ProtoMember(4)]
        public ulong ManifestId { get; private set; }

        [ProtoMember(5)]
        public string InstallRoot { get; private set; }

        [ProtoMember(6)]
        public List<ResumeFileState> Files { get; private set; }

        [ProtoIgnore]
        Dictionary<string, ResumeFileState> filesByName;

        [ProtoIgnore]
        readonly object sync = new();

        [ProtoIgnore]
        public object SyncRoot => sync;

        ResumeState()
        {
            Files = [];
        }

        public static ResumeState Create(uint appId, uint depotId, ulong manifestId, string installRoot, IEnumerable<DepotManifest.FileData> files)
        {
            var state = new ResumeState
            {
                Version = CurrentVersion,
                AppId = appId,
                DepotId = depotId,
                ManifestId = manifestId,
                InstallRoot = NormalizeInstallRoot(installRoot),
                Files = files
                    .Where(file => !file.Flags.HasFlag(EDepotFileFlag.Directory))
                    .Select(file => new ResumeFileState(file))
                    .ToList()
            };

            state.RebuildLookup();
            return state;
        }

        public static string NormalizeInstallRoot(string installRoot)
        {
            return Path.GetFullPath(installRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public bool IsValidFor(uint appId, uint depotId, ulong manifestId, string installRoot, IReadOnlyCollection<DepotManifest.FileData> files)
        {
            if (Version != CurrentVersion || AppId != appId || DepotId != depotId || ManifestId != manifestId)
            {
                return false;
            }

            if (!string.Equals(InstallRoot, NormalizeInstallRoot(installRoot), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var manifestFiles = files.Where(file => !file.Flags.HasFlag(EDepotFileFlag.Directory)).ToList();
            if (Files.Count != manifestFiles.Count)
            {
                return false;
            }

            RebuildLookup();

            foreach (var file in manifestFiles)
            {
                if (!filesByName.TryGetValue(file.FileName, out var resumeFile) || !resumeFile.IsValidFor(file))
                {
                    return false;
                }
            }

            return true;
        }

        public bool HasCompletedChunks(DepotManifest.FileData file)
        {
            lock (sync)
            {
                return TryGetFile(file.FileName, out var resumeFile) && resumeFile.HasCompletedChunks();
            }
        }

        public bool IsChunkCompleted(DepotManifest.FileData file, DepotManifest.ChunkData chunk)
        {
            lock (sync)
            {
                return TryGetFile(file.FileName, out var resumeFile) && resumeFile.IsChunkCompleted(chunk);
            }
        }

        public bool MarkChunkCompleted(DepotManifest.FileData file, DepotManifest.ChunkData chunk)
        {
            lock (sync)
            {
                return TryGetFile(file.FileName, out var resumeFile) && resumeFile.MarkChunkCompleted(chunk);
            }
        }

        public bool ClearFile(DepotManifest.FileData file)
        {
            lock (sync)
            {
                return TryGetFile(file.FileName, out var resumeFile) && resumeFile.ClearCompletedChunks();
            }
        }

        bool TryGetFile(string fileName, out ResumeFileState file)
        {
            RebuildLookup();
            return filesByName.TryGetValue(fileName, out file);
        }

        void RebuildLookup()
        {
            filesByName ??= Files.ToDictionary(file => file.FileName, StringComparer.Ordinal);
        }
    }

    [ProtoContract]
    class ResumeFileState
    {
        [ProtoMember(1)]
        public string FileName { get; private set; }

        [ProtoMember(2)]
        public ulong TotalSize { get; private set; }

        [ProtoMember(3)]
        public byte[] FileHash { get; private set; }

        [ProtoMember(4)]
        public List<byte[]> ChunkIds { get; private set; }

        [ProtoMember(5)]
        public byte[] CompletedChunks { get; private set; }

        [ProtoMember(6)]
        public List<ulong> ChunkOffsets { get; private set; }

        [ProtoIgnore]
        Dictionary<ChunkKey, int> chunkIndexByIdentity;

        ResumeFileState()
        {
            ChunkIds = [];
            CompletedChunks = [];
            ChunkOffsets = [];
        }

        public ResumeFileState(DepotManifest.FileData file)
        {
            FileName = file.FileName;
            TotalSize = file.TotalSize;
            FileHash = file.FileHash;
            ChunkIds = file.Chunks.Select(chunk => chunk.ChunkID).ToList();
            ChunkOffsets = file.Chunks.Select(chunk => chunk.Offset).ToList();
            CompletedChunks = new byte[(ChunkIds.Count + 7) / 8];
        }

        public bool IsValidFor(DepotManifest.FileData file)
        {
            if (!string.Equals(FileName, file.FileName, StringComparison.Ordinal)
                || TotalSize != file.TotalSize
                || !FileHash.SequenceEqual(file.FileHash)
                || ChunkIds.Count != file.Chunks.Count
                || CompletedChunks.Length != (file.Chunks.Count + 7) / 8)
            {
                return false;
            }

            if (!EnsureChunkOffsets(file))
            {
                return false;
            }

            for (var i = 0; i < ChunkIds.Count; i++)
            {
                if (!ChunkIds[i].SequenceEqual(file.Chunks[i].ChunkID) || ChunkOffsets[i] != file.Chunks[i].Offset)
                {
                    return false;
                }
            }

            RebuildLookup();
            return true;
        }

        bool EnsureChunkOffsets(DepotManifest.FileData file)
        {
            if (ChunkOffsets == null || ChunkOffsets.Count == 0)
            {
                ChunkOffsets = file.Chunks.Select(chunk => chunk.Offset).ToList();
                chunkIndexByIdentity = null;
            }

            return ChunkOffsets.Count == file.Chunks.Count;
        }

        public bool HasCompletedChunks()
        {
            return CompletedChunks.Any(value => value != 0);
        }

        public bool IsChunkCompleted(DepotManifest.ChunkData chunk)
        {
            return TryGetChunkIndex(chunk, out var index) && IsBitSet(index);
        }

        public bool MarkChunkCompleted(DepotManifest.ChunkData chunk)
        {
            if (!TryGetChunkIndex(chunk, out var index) || IsBitSet(index))
            {
                return false;
            }

            CompletedChunks[index / 8] |= (byte)(1 << (index % 8));
            return true;
        }

        public bool ClearCompletedChunks()
        {
            var hadCompletedChunks = HasCompletedChunks();
            Array.Clear(CompletedChunks);
            return hadCompletedChunks;
        }

        bool TryGetChunkIndex(DepotManifest.ChunkData chunk, out int index)
        {
            RebuildLookup();
            return chunkIndexByIdentity.TryGetValue(ChunkKey.From(chunk), out index);
        }

        bool IsBitSet(int index)
        {
            return (CompletedChunks[index / 8] & (1 << (index % 8))) != 0;
        }

        void RebuildLookup()
        {
            chunkIndexByIdentity ??= ChunkIds
                .Select((chunkId, index) => (key: new ChunkKey(Convert.ToHexString(chunkId), ChunkOffsets[index]), index))
                .ToDictionary(item => item.key, item => item.index);
        }

        readonly record struct ChunkKey(string Id, ulong Offset)
        {
            public static ChunkKey From(DepotManifest.ChunkData chunk)
            {
                return new ChunkKey(Convert.ToHexString(chunk.ChunkID), chunk.Offset);
            }
        }
    }
}
