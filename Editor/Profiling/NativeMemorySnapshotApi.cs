using System;
using System.Collections.Generic;
using System.Reflection;

namespace AIBridge.Editor
{
    internal interface INativeMemorySnapshotApi
    {
        NativeMemorySnapshotSupport Support { get; }
        NativeMemorySnapshotState ReadState();
        void TakeSnapshot(string path, Action<string, bool> callback);
    }

    internal sealed class NativeMemorySnapshotSupport
    {
        public bool Capture;
        public string ApiTypeName;
        public string FlagsTypeName;
        public readonly List<object> Unsupported = new List<object>();

        public void Missing(string feature, string reason)
        {
            Unsupported.Add(new { feature, reason });
        }
    }

    internal sealed class NativeMemorySnapshotState
    {
        public int CurrentTargetGuid;
        public string CurrentTargetName;
        public bool ProfilerEnabled;
    }

    /// <summary>只反射已加载 Unity 模块中的精确 Memory Snapshot API，不加载包、不猜重载。</summary>
    internal sealed class NativeMemorySnapshotApi : INativeMemorySnapshotApi
    {
        private const string ExperimentalTypeName = "UnityEngine.Profiling.Memory.Experimental.MemoryProfiler";
        private const string CurrentTypeName = "Unity.Profiling.Memory.MemoryProfiler";
        private const string ExperimentalFlagsTypeName = "UnityEngine.Profiling.Memory.Experimental.CaptureFlags";
        private const string CurrentFlagsTypeName = "Unity.Profiling.Memory.CaptureFlags";
        private const string ProfilerDriverTypeName = "UnityEditorInternal.ProfilerDriver";
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        private readonly MethodInfo _takeSnapshot;
        private readonly object _flags;
        private readonly NativeProfilerApi _profiler;

        public NativeMemorySnapshotSupport Support { get; }

        internal NativeMemorySnapshotApi(Type memoryType, Type flagsType, Type profilerDriver = null)
        {
            Support = new NativeMemorySnapshotSupport
            {
                ApiTypeName = memoryType == null ? null : memoryType.FullName,
                FlagsTypeName = flagsType == null ? null : flagsType.FullName
            };
            _profiler = profilerDriver == null ? null : new NativeProfilerApi(profilerDriver);
            _takeSnapshot = ExactTakeSnapshot(memoryType, flagsType);
            _flags = BuildFlags(flagsType);
            Support.Capture = _takeSnapshot != null && _flags != null && _profiler != null && _profiler.Support.State;
            if (_takeSnapshot == null) Support.Missing("capture_snapshot", "Requires exact TakeSnapshot(string, Action<string,bool>, CaptureFlags).");
            if (_flags == null) Support.Missing("capture_flags", "Requires CaptureFlags.ManagedObjects and CaptureFlags.NativeObjects.");
            if (_profiler == null || !_profiler.Support.State) Support.Missing("current_state", "Requires native Profiler current target and enabled state.");
        }

        internal static NativeMemorySnapshotApi Resolve()
        {
            var current = FindLoadedType(CurrentTypeName);
            var currentFlags = FindLoadedType(CurrentFlagsTypeName);
            if (current != null && currentFlags != null)
                return new NativeMemorySnapshotApi(current, currentFlags, FindLoadedType(ProfilerDriverTypeName));
            var experimental = FindLoadedType(ExperimentalTypeName);
            var experimentalFlags = FindLoadedType(ExperimentalFlagsTypeName);
            return new NativeMemorySnapshotApi(experimental, experimentalFlags, FindLoadedType(ProfilerDriverTypeName));
        }

        public NativeMemorySnapshotState ReadState()
        {
            if (_profiler == null || !_profiler.Support.State) throw new NotSupportedException("Profiler state signature is unsupported.");
            var state = _profiler.ReadState();
            return new NativeMemorySnapshotState
            {
                CurrentTargetGuid = state.CurrentTargetGuid,
                CurrentTargetName = state.CurrentTargetName,
                ProfilerEnabled = state.Recording
            };
        }

        public void TakeSnapshot(string path, Action<string, bool> callback)
        {
            if (!Support.Capture) throw new NotSupportedException("Memory snapshot capture signature is unsupported.");
            _takeSnapshot.Invoke(null, new object[] { path, callback, _flags });
        }

        private static MethodInfo ExactTakeSnapshot(Type memoryType, Type flagsType)
        {
            if (memoryType == null || flagsType == null) return null;
            MethodInfo found = null;
            foreach (var method in memoryType.GetMethods(StaticFlags))
            {
                if (method.Name != "TakeSnapshot" || method.ReturnType != typeof(void) || method.IsGenericMethod) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 3 || parameters[0].ParameterType != typeof(string)
                    || parameters[1].ParameterType != typeof(Action<string, bool>) || parameters[2].ParameterType != flagsType) continue;
                if (found != null) return null;
                found = method;
            }
            return found;
        }

        private static object BuildFlags(Type flagsType)
        {
            if (flagsType == null || !flagsType.IsEnum) return null;
            try
            {
                if (!Enum.IsDefined(flagsType, "ManagedObjects") || !Enum.IsDefined(flagsType, "NativeObjects")) return null;
                var managed = Enum.Parse(flagsType, "ManagedObjects", false);
                var native = Enum.Parse(flagsType, "NativeObjects", false);
                return Enum.ToObject(flagsType, Convert.ToUInt64(managed) | Convert.ToUInt64(native));
            }
            catch (Exception) { return null; }
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name != "UnityEditor" && !name.StartsWith("UnityEditor.", StringComparison.Ordinal)
                    && name != "UnityEngine" && !name.StartsWith("UnityEngine.", StringComparison.Ordinal)) continue;
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
