// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using ProtoBuf;

namespace DepotDownloader
{
    [ProtoContract]
    sealed class AppDownloadConfig
    {
        // null on any axis means "no preference" — semantically equivalent to
        // Config.DownloadAllPlatforms / DownloadAllArchs / DownloadAllLanguages.
        [ProtoMember(1)] public string Os { get; set; }
        [ProtoMember(2)] public string Arch { get; set; }
        [ProtoMember(3)] public string Language { get; set; }
    }
}
