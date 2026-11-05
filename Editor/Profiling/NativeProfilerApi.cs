using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace AIBridge.Editor
{
    internal interface INativeProfilerApi
    {
        NativeProfilerSupport Support { get; }
        NativeProfilerState ReadState();
        bool Save(string path);
        bool Load(string path, bool keepExistingData);
    }

    internal sealed class NativeProfilerState
    {
        public bool Recording;
        public int CurrentTargetGuid;
        public string CurrentTargetName;
    }

    internal sealed class NativeProfilerSupport
    {
        public bool Save;
        public bool Load;
        public bool State;
        public string SaveReturnType;
        public readonly List<object> Unsupported = new List<object>();

        public void Missing(string feature, string reason)
        {
            Unsupported.Add(new { feature, reason });
        }
    }

    /// <summary>只解析已知精确签名；不加载额外包，不尝试其它重载或 JSON 回退。</summary>
    internal sealed class NativeProfilerApi : INativeProfilerApi
    {
        private const string DriverName = "UnityEditorInternal.ProfilerDriver";
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        private readonly MethodInfo _save;
        private readonly MethodInfo _load;
        private readonly MethodInfo _identifier;
        private readonly PropertyInfo _enabled;
        private readonly PropertyInfo _connected;

        public NativeProfilerSupport Support { get; }

        internal NativeProfilerApi(Type driver)
        {
            Support = new NativeProfilerSupport();
            _save = ExactMethod(driver, "SaveProfile", typeof(string));
            _load = ExactMethod(driver, "LoadProfile", typeof(string), typeof(bool));
            _identifier = ExactMethod(driver, "GetConnectionIdentifier", typeof(int));
            _enabled = ExactProperty(driver, "enabled", typeof(bool));
            _connected = ExactProperty(driver, "connectedProfiler", typeof(int));
            Support.Save = _save != null && (_save.ReturnType == typeof(void) || _save.ReturnType == typeof(bool));
            Support.Load = _load != null && _load.ReturnType == typeof(bool);
            Support.State = _enabled != null && _connected != null && _identifier != null
                && _identifier.ReturnType == typeof(string);
            if (Support.Save) Support.SaveReturnType = _save.ReturnType == typeof(void) ? "void" : "bool";
            if (!Support.Save) Support.Missing("save_profile", "Requires static SaveProfile(string) returning void or bool.");
            if (!Support.Load) Support.Missing("load_profile", "Requires static LoadProfile(string, bool) returning bool.");
            if (!Support.State) Support.Missing("current_state",
                "Requires bool enabled, int connectedProfiler and string GetConnectionIdentifier(int).");
        }

        internal static NativeProfilerApi Resolve()
        {
            var primary = typeof(EditorApplication).Assembly;
            var driver = primary.GetType(DriverName, false);
            if (driver != null) return new NativeProfilerApi(driver);
            // Unity 不同版本将内部类型放在不同 Editor 程序集；只查已加载的 UnityEditor 程序集。
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (assembly == primary || (name != "UnityEditor"
                    && !name.StartsWith("UnityEditor.", StringComparison.Ordinal))) continue;
                driver = assembly.GetType(DriverName, false);
                if (driver != null) break;
            }
            return new NativeProfilerApi(driver);
        }

        public NativeProfilerState ReadState()
        {
            if (!Support.State) throw new NotSupportedException("Profiler state signature is unsupported.");
            var guid = (int)_connected.GetValue(null, null);
            return new NativeProfilerState
            {
                Recording = (bool)_enabled.GetValue(null, null),
                CurrentTargetGuid = guid,
                CurrentTargetName = (string)_identifier.Invoke(null, new object[] { guid })
            };
        }

        public bool Save(string path)
        {
            if (!Support.Save) throw new NotSupportedException("SaveProfile signature is unsupported.");
            var result = _save.Invoke(null, new object[] { path });
            return _save.ReturnType == typeof(void) || (bool)result;
        }

        public bool Load(string path, bool keepExistingData)
        {
            if (!Support.Load) throw new NotSupportedException("LoadProfile signature is unsupported.");
            return (bool)_load.Invoke(null, new object[] { path, keepExistingData });
        }

        private static MethodInfo ExactMethod(Type type, string name, params Type[] parameters)
        {
            if (type == null) return null;
            MethodInfo found = null;
            foreach (var method in type.GetMethods(Flags))
            {
                if (method.Name != name || method.IsGenericMethod || method.ContainsGenericParameters) continue;
                var actual = method.GetParameters();
                if (actual.Length != parameters.Length) continue;
                var equal = true;
                for (var i = 0; i < actual.Length; i++)
                    if (actual[i].ParameterType != parameters[i]) equal = false;
                if (!equal) continue;
                if (found != null) return null;
                found = method;
            }
            return found;
        }

        private static PropertyInfo ExactProperty(Type type, string name, Type expected)
        {
            if (type == null) return null;
            PropertyInfo found = null;
            foreach (var property in type.GetProperties(Flags))
            {
                if (property.Name != name || property.PropertyType != expected
                    || property.GetIndexParameters().Length != 0) continue;
                var getter = property.GetGetMethod(true);
                if (getter == null || !getter.IsStatic || getter.GetParameters().Length != 0) continue;
                if (found != null) return null;
                found = property;
            }
            return found;
        }
    }
}
