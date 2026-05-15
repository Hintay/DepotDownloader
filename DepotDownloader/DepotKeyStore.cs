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
                if (TryGetUInt32(args, 0, out var depotId) && args.Count >= 3 && TryGetString(args[2], out var depotKey))
                {
                    depotKeysCache[depotId] = StringToByteArray(depotKey);
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
