// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;

namespace DepotDownloader
{
    static class DepotKeyStore
    {
        private static readonly Dictionary<uint, byte[]> depotKeysCache = new Dictionary<uint, byte[]>();

        public sealed class LuaDepotData
        {
            public Dictionary<uint, ulong> ManifestIds { get; } = [];
            public Dictionary<uint, ulong> AppTokens { get; } = [];
            public HashSet<uint> OwnedApps { get; } = [];
        }

        public static void AddAll(string[] values)
        {
            foreach (string value in values)
            {
                string[] split = value.Split(';');

                if (split.Length != 2)
                {
                    throw new FormatException($"Invalid depot key line: {value}");
                }

                depotKeysCache.Add(uint.Parse(split[0]), StringToByteArray(split[1]));
            }
        }

        public static LuaDepotData AddFromLua(string lua)
        {
            var data = new LuaDepotData();
            var script = new Script(CoreModules.None);

            script.Globals["addappid"] = CallbackFunction.FromDelegate(script, new Func<ScriptExecutionContext, CallbackArguments, DynValue>((context, args) =>
            {
                if (TryGetUInt32(args, 0, out var appId))
                {
                    // Always record as an "owned app" — covers both the 1-arg
                    // form (DLC / main app declaration) and the 3-arg form
                    // (which also registers a depot key for the same id).
                    data.OwnedApps.Add(appId);

                    if (args.Count >= 3 && TryGetString(args[2], out var depotKey))
                    {
                        depotKeysCache[appId] = StringToByteArray(depotKey);
                    }
                }

                return DynValue.Nil;
            }));

            script.Globals["setManifestid"] = CallbackFunction.FromDelegate(script, new Func<ScriptExecutionContext, CallbackArguments, DynValue>((context, args) =>
            {
                if (TryGetUInt32(args, 0, out var depotId) && TryGetUInt64(args, 1, out var manifestId))
                {
                    data.ManifestIds[depotId] = manifestId;
                }

                return DynValue.Nil;
            }));

            script.Globals["addtoken"] = CallbackFunction.FromDelegate(script, new Func<ScriptExecutionContext, CallbackArguments, DynValue>((context, args) =>
            {
                if (TryGetUInt32(args, 0, out var appId) && TryGetUInt64(args, 1, out var token))
                {
                    data.AppTokens[appId] = token;
                }

                return DynValue.Nil;
            }));

            // Map any unknown global to a no-op callback so unsupported helper
            // calls in third-party manifest scripts (addchunkid, setappinstalldir,
            // case-variant names, etc.) don't abort the script with
            // "attempt to call a nil value" and lose every entry that follows.
            // Each unknown name is reported once when actually invoked.
            var reportedMissing = new HashSet<string>();
            var meta = new Table(script);
            meta["__index"] = CallbackFunction.FromDelegate(script, new Func<ScriptExecutionContext, CallbackArguments, DynValue>((context, args) =>
            {
                var name = args.Count >= 2 && args[1].Type == DataType.String ? args[1].String : null;
                return DynValue.NewCallback((ctx, callArgs) =>
                {
                    if (name != null && reportedMissing.Add(name))
                    {
                        Console.WriteLine("Warning: Lua function '{0}' is not supported and will be ignored.", name);
                    }
                    return DynValue.Nil;
                });
            }));
            script.Globals.MetaTable = meta;

            script.DoString(lua);

            return data;
        }

        private static bool TryGetUInt32(CallbackArguments args, int index, out uint value)
        {
            value = 0;

            if (args.Count <= index)
            {
                return false;
            }

            if (!TryGetUInt64(args[index], out var ulongValue) || ulongValue > uint.MaxValue)
            {
                return false;
            }

            value = (uint)ulongValue;
            return true;
        }

        private static bool TryGetUInt64(CallbackArguments args, int index, out ulong value)
        {
            value = 0;

            return args.Count > index && TryGetUInt64(args[index], out value);
        }

        private static bool TryGetUInt64(DynValue dynValue, out ulong value)
        {
            value = 0;

            if (dynValue.Type == DataType.Number)
            {
                var number = dynValue.Number;
                if (number < 0 || number % 1 != 0 || number > ulong.MaxValue)
                {
                    return false;
                }

                value = (ulong)number;
                return true;
            }

            return dynValue.Type == DataType.String && ulong.TryParse(dynValue.String, out value);
        }

        private static bool TryGetString(DynValue dynValue, out string value)
        {
            value = null;

            if (dynValue.Type != DataType.String || string.IsNullOrWhiteSpace(dynValue.String))
            {
                return false;
            }

            value = dynValue.String;
            return true;
        }

        private static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                .Where(x => x % 2 == 0)
                .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                .ToArray();
        }

        public static bool ContainsKey(uint depotId)
        {
            return depotKeysCache.ContainsKey(depotId);
        }

        public static byte[] Get(uint depotId)
        {
            return depotKeysCache[depotId];
        }


    }
}
