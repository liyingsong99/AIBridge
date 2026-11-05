using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public sealed class NativeProfilerFilesTests
    {
        private string _project;
        private string _root;
        private FakeApi _api;
        private ProfilerNativeFiles.Context _context;
        private readonly List<object> _leases = new List<object>();
        private readonly List<FileStream> _retained = new List<FileStream>();

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "AIBridgeNativeProfilerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_project);
            _root = Path.Combine(_project, ProfilerNativeFiles.NativeDirectory);
            _api = new FakeApi();
            _context = new ProfilerNativeFiles.Context
            {
                ProjectRoot = _project, UnityVersion = "fixture", Api = _api, BusyReason = () => null,
                RetainUnknown = (lease, stream) =>
                {
                    _leases.Add(lease);
                    if (stream != null) _retained.Add(stream);
                }
            };
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var stream in _retained) stream.Dispose();
            foreach (var lease in _leases) ProfilerNativeGate.Release(lease);
            _retained.Clear(); _leases.Clear();
            if (Directory.Exists(_project)) Directory.Delete(_project, true);
        }

        [TestCase(typeof(VoidDriver), "void")]
        [TestCase(typeof(BoolDriver), "bool")]
        public void ExactReflectionSupportsOnlyKnownSaveSignatures(Type driver, string expected)
        {
            var api = new NativeProfilerApi(driver);
            Assert.That(api.Support.Save, Is.True);
            Assert.That(api.Support.Load, Is.True);
            Assert.That(api.Support.State, Is.True);
            Assert.That(api.Support.SaveReturnType, Is.EqualTo(expected));
            var file = Path.Combine(_project, "reflection.data");
            Assert.That(api.Save(file), Is.True);
            Assert.That(new FileInfo(file).Length, Is.GreaterThan(0));
            Assert.That(api.Load(file, false), Is.True);
        }

        [TestCase(typeof(WrongReturnDriver))]
        [TestCase(typeof(WrongParameterDriver))]
        [TestCase(typeof(WrongStateDriver))]
        [TestCase(typeof(object))]
        public void MissingOrUnexpectedTypesDoNotBecomeSupported(Type driver)
        {
            var api = new NativeProfilerApi(driver);
            _context.Api = api;
            var result = Run("save_profile");
            Assert.That(result.success, Is.False);
            Assert.That(Value<string>(result.data, "code"), Is.EqualTo("unsupported"));
            Assert.That(Directory.Exists(_root), Is.False);
            Assert.That(api.Support.Unsupported.Count, Is.GreaterThan(0));
        }

        [TestCase(typeof(VoidDriver))]
        [TestCase(typeof(BoolDriver))]
        public void ReflectedSaveWorksThroughFilePublication(Type driver)
        {
            _context.Api = new NativeProfilerApi(driver);
            var result = Run("save_profile");
            Assert.That(result.success, Is.True, result.error);
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(new byte[] { 1 }));
            Assert.That(Directory.GetFiles(_root), Has.Length.EqualTo(1));
        }

        [Test]
        public void BooleanFalseIsNotSuccessfulSave()
        {
            _context.Api = new NativeProfilerApi(typeof(FalseDriver));
            var result = Run("save_profile");
            Assert.That(result.success, Is.False);
            Assert.That(File.Exists(Target), Is.False);
        }

        [Test]
        public void CapabilitiesHaveNoMutationOrDirectoryCreationEvenWhenRecordingAndBusy()
        {
            _api.State.Recording = true;
            _context.BusyReason = () => throw new Exception("must not inspect mutation gates");
            object lease; Assert.That(ProfilerNativeGate.TryAcquire(out lease), Is.True); _leases.Add(lease);
            var result = Run("native_capabilities", new Dictionary<string, object>());
            Assert.That(result.success, Is.True, result.error);
            Assert.That(Value<bool?>(result.data, "recording"), Is.True);
            Assert.That(Value<int?>(result.data, "currentTargetGuid"), Is.EqualTo(42));
            Assert.That(Value<bool>(result.data, "gateBusy"), Is.True);
            Assert.That(_api.SaveCalls + _api.LoadCalls, Is.Zero);
            Assert.That(Directory.Exists(_root), Is.False);
        }

        [Test]
        public void RealApiIsOnlyInspectedForCapabilitiesNeverInvokedForFiles()
        {
            _context.Api = NativeProfilerApi.Resolve();
            var result = Run("native_capabilities", new Dictionary<string, object>());
            Assert.That(result.success, Is.True, result.error);
            Assert.That(Value<object>(result.data, "supported"), Is.Not.Null);
            Assert.That(Value<object[]>(result.data, "unsupported"), Is.Not.Null);
            Assert.That(Directory.Exists(_root), Is.False);
        }

        [TestCase("save_profile")]
        [TestCase("load_profile")]
        public void ConfirmationRequiresActualTrueWithoutWrites(string action)
        {
            foreach (var value in new object[] { null, false, "true", 1 })
            {
                var parameters = Parameters();
                if (value == null) parameters.Remove("confirm"); else parameters["confirm"] = value;
                Assert.That(Run(action, parameters).success, Is.False);
            }
            Assert.That(Directory.Exists(_root), Is.False);
            Assert.That(_api.SaveCalls + _api.LoadCalls, Is.Zero);
        }

        [TestCase("overwrite")]
        [TestCase("append")]
        [TestCase("target")]
        [TestCase("backend")]
        [TestCase("deepProfile")]
        public void ExtraParametersAreRejectedBeforeAnyWrite(string key)
        {
            var parameters = Parameters(); parameters[key] = true;
            Assert.That(Run("save_profile", parameters).success, Is.False);
            Assert.That(Run("load_profile", parameters).success, Is.False);
            Assert.That(Directory.Exists(_root), Is.False);
        }

        [TestCase("compiling")]
        [TestCase("updating")]
        [TestCase("playing-or-changing-playmode")]
        [TestCase("building-player")]
        public void EditorBusyGatesRejectBothActionsBeforeWrites(string reason)
        {
            _context.BusyReason = () => reason;
            Assert.That(Run("save_profile").success, Is.False);
            Assert.That(Run("load_profile").success, Is.False);
            Assert.That(Directory.Exists(_root), Is.False);
            Assert.That(_api.SaveCalls + _api.LoadCalls, Is.Zero);
        }

        [Test]
        public void RecordingAndSharedGateRejectBothActions()
        {
            _api.State.Recording = true;
            Assert.That(Run("save_profile").success, Is.False);
            Assert.That(Run("load_profile").success, Is.False);
            _api.State.Recording = false;
            object lease; Assert.That(ProfilerNativeGate.TryAcquire(out lease), Is.True); _leases.Add(lease);
            Assert.That(Run("save_profile").success, Is.False);
            Assert.That(Run("load_profile").success, Is.False);
            Assert.That(Directory.Exists(_root), Is.False);
            Assert.That(_api.SaveCalls + _api.LoadCalls, Is.Zero);
        }

        [Test]
        public void GateCannotBeReleasedByForeignLease()
        {
            object lease; Assert.That(ProfilerNativeGate.TryAcquire(out lease), Is.True); _leases.Add(lease);
            Assert.Throws<InvalidOperationException>(() => ProfilerNativeGate.Release(new object()));
            Assert.That(ProfilerNativeGate.Busy, Is.True);
        }

        [Test]
        public void StateIsRecheckedBeforeDirectoryCreation()
        {
            var checks = 0;
            _context.BusyReason = () => ++checks == 2 ? "state changed" : null;
            Assert.That(Run("save_profile").success, Is.False);
            Assert.That(Directory.Exists(_root), Is.False);
            Assert.That(ProfilerNativeGate.Busy, Is.False);
        }

        [TestCase("../escape.data")]
        [TestCase(".aibridge/profiler/native/../native/a.data")]
        [TestCase(".aibridge/profiler/native/nested/a.data")]
        [TestCase(".aibridge/profiler/native/a.json")]
        [TestCase(".aibridge/profiler/native/a.data:stream")]
        [TestCase(".aibridge/profiler/native/CON.data")]
        [TestCase(".aibridge/profiler/native/a.data.")]
        [TestCase(@"\\server\share\a.data")]
        [TestCase(@"\\?\C:\a.data")]
        public void UnsafePathsDoNotCreateAnything(string path)
        {
            var parameters = Parameters(); parameters["path"] = path;
            Assert.That(Run("save_profile", parameters).success, Is.False);
            Assert.That(Run("load_profile", parameters).success, Is.False);
            Assert.That(Directory.Exists(_root), Is.False);
        }

        [Test]
        public void AbsoluteOutsideRejectedAndDirectAbsoluteInsideAccepted()
        {
            Assert.Throws<ArgumentException>(() => ProfilerNativeFiles.ResolvePath(_project, Path.Combine(_project, "outside.data")));
            Assert.That(ProfilerNativeFiles.ResolvePath(_project, Target), Is.EqualTo(Path.GetFullPath(Target)));
        }

        [Test]
        public void PathCaseUsesPlatformRules()
        {
            var path = ".AIBRIDGE/PROFILER/NATIVE/a.DATA";
            if (Path.DirectorySeparatorChar == '\\')
                Assert.DoesNotThrow(() => ProfilerNativeFiles.ResolvePath(_project, path));
            else
                Assert.Throws<ArgumentException>(() => ProfilerNativeFiles.ResolvePath(_project, path));
        }

        [Test]
        public void ReparseAncestorRejectedWhenHostCanCreateOne()
        {
            var backing = Path.Combine(_project, "backing"); Directory.CreateDirectory(backing);
            var link = Path.Combine(_project, ".aibridge");
            var create = typeof(Directory).GetMethod("CreateSymbolicLink", new[] { typeof(string), typeof(string) });
            if (create == null) Assert.Ignore("Host has no symbolic-link API.");
            try { create.Invoke(null, new object[] { link, backing }); }
            catch (Exception ex) { Assert.Ignore("Host cannot create symbolic links: " + ex.GetType().Name); }
            try
            {
                if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) Assert.Ignore("No reparse attribute on host.");
                Assert.That(Run("save_profile").success, Is.False);
                Assert.That(Run("load_profile").success, Is.False);
                Assert.That(Directory.GetFileSystemEntries(backing), Is.Empty);
            }
            finally { Directory.Delete(link); }
        }

        [Test]
        public void SavePublishesOnlyNonEmptyNativeFileWithRequiredMetadata()
        {
            _api.SaveHandler = path => { File.WriteAllBytes(path, new byte[] { 1, 2, 3 }); return true; };
            var result = Run("save_profile");
            Assert.That(result.success, Is.True, result.error);
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(Value<string>(result.data, "path"), Is.EqualTo(ProfilerNativeFiles.NativeDirectory + "/capture.data"));
            Assert.That(Value<long>(result.data, "size"), Is.EqualTo(3));
            Assert.That(Value<string>(result.data, "format"), Is.EqualTo("unity-profiler-data"));
            Assert.That(Value<string>(result.data, "unityVersion"), Is.EqualTo("fixture"));
            Assert.That(Value<int>(result.data, "currentTargetGuid"), Is.EqualTo(42));
            Assert.That(Value<string>(result.data, "currentTargetName"), Is.EqualTo("fixture-target"));
            Assert.That(Value<string>(result.data, "historyProvenance"), Is.EqualTo("unknown"));
            Assert.That(Directory.GetFiles(_root), Has.Length.EqualTo(1));
        }

        [Test]
        public void SaveAllowsNativeExclusiveOutputOpenAndPreservesIdentity()
        {
            _api.SaveHandler = path =>
            {
                using (var native = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                    native.WriteByte(23);
                return true;
            };
            var result = Run("save_profile");
            Assert.That(result.success, Is.True, result.error);
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(new byte[] { 23 }));
            Assert.That(_api.SaveCalls, Is.EqualTo(1));
        }

        [Test]
        public void SaveWithoutPathUsesDirectRandomDataName()
        {
            _api.SaveHandler = path => { File.WriteAllBytes(path, new byte[] { 1 }); return true; };
            var parameters = Parameters(); parameters.Remove("path");
            var result = Run("save_profile", parameters);
            Assert.That(result.success, Is.True, result.error);
            var relative = Value<string>(result.data, "path");
            Assert.That(Path.GetDirectoryName(relative).Replace('\\', '/'), Is.EqualTo(ProfilerNativeFiles.NativeDirectory));
            Assert.That(Path.GetFileNameWithoutExtension(relative), Has.Length.EqualTo(32));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NoOutputOrZeroOutputNeverSucceeds(bool writesZero)
        {
            _api.SaveHandler = path => { if (writesZero) File.WriteAllBytes(path, new byte[0]); return true; };
            var result = Run("save_profile");
            Assert.That(result.success, Is.False);
            Assert.That(File.Exists(Target), Is.False);
            Assert.That(ProfilerNativeGate.Busy, Is.False);
            if (Path.DirectorySeparatorChar == '\\')
                Assert.That(Directory.GetFiles(_root), Is.Empty);
            else
                Assert.That(Value<string>(result.data, "retainedTemporaryPath"), Is.Not.Null);
        }

        [Test]
        public void PreExistingTargetIsPreservedAndApiNotCalled()
        {
            CreateTarget(new byte[] { 7, 8 });
            Assert.That(Run("save_profile").success, Is.False);
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(new byte[] { 7, 8 }));
            Assert.That(_api.SaveCalls, Is.Zero);
            Assert.That(Directory.GetFiles(_root), Has.Length.EqualTo(1));
        }

        [Test]
        public void TargetCreationRaceFailsAndPreservesOtherFile()
        {
            _api.SaveHandler = path =>
            {
                File.WriteAllBytes(path, new byte[] { 1 });
                File.WriteAllBytes(Target, new byte[] { 9 });
                return true;
            };
            Assert.That(Run("save_profile").success, Is.False);
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(new byte[] { 9 }));
            Assert.That(_api.SaveCalls, Is.EqualTo(1));
        }

        [Test]
        public void NativeSaveThrowPreservesTemporaryAndHoldsSharedGate()
        {
            string temporary = null;
            _api.SaveHandler = path =>
            {
                temporary = path; File.WriteAllBytes(path, new byte[] { 5 });
                throw new InvalidOperationException("ambiguous native outcome");
            };
            var result = Run("save_profile");
            Assert.That(result.success, Is.False);
            Assert.That(Value<bool>(result.data, "sideEffectsUnknown"), Is.True);
            Assert.That(Value<bool>(result.data, "nativeMayStillRun"), Is.True);
            Assert.That(File.Exists(temporary), Is.True);
            Assert.That(File.Exists(Target), Is.False);
            Assert.That(ProfilerNativeGate.Busy, Is.True);
            Assert.That(Run("save_profile").success, Is.False);
            Assert.That(_api.SaveCalls, Is.EqualTo(1));
        }

        [Test]
        public void LoadRequiresPathExistingNonEmptyBoundedFile()
        {
            var parameters = Parameters(); parameters.Remove("path");
            Assert.That(Run("load_profile", parameters).success, Is.False);
            Assert.That(Run("load_profile").success, Is.False);
            CreateTarget(new byte[0]);
            Assert.That(Run("load_profile").success, Is.False);
            Assert.That(ProfilerNativeFiles.IsValidLoadSize(0), Is.False);
            Assert.That(ProfilerNativeFiles.IsValidLoadSize(ProfilerNativeFiles.MaxLoadBytes), Is.True);
            Assert.That(ProfilerNativeFiles.IsValidLoadSize(ProfilerNativeFiles.MaxLoadBytes + 1), Is.False);
            Assert.That(_api.LoadCalls, Is.Zero);
        }

        [Test]
        public void LoadUsesReplacementAndHoldsReadOnlySourceLock()
        {
            CreateTarget(new byte[] { 1 });
            _api.LoadHandler = (path, keep) =>
            {
                Assert.That(keep, Is.False);
                Assert.Throws<IOException>(() => { using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } });
                Assert.That(ProfilerNativeGate.Busy, Is.True);
                return true;
            };
            var result = Run("load_profile");
            Assert.That(result.success, Is.True, result.error);
            Assert.That(_api.LoadCalls, Is.EqualTo(1));
            Assert.That(ProfilerNativeGate.Busy, Is.False);
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(new byte[] { 1 }));
        }

        [Test]
        public void LoadFalseRemainsFailureWithUnknownHistorySideEffects()
        {
            CreateTarget(new byte[] { 1 });
            _api.LoadHandler = (path, keep) => false;
            var result = Run("load_profile");
            Assert.That(result.success, Is.False);
            Assert.That(Value<bool>(result.data, "sideEffectsUnknown"), Is.True);
            Assert.That(Value<bool>(result.data, "nativeMayStillRun"), Is.False);
            Assert.That(_api.LoadCalls, Is.EqualTo(1));
            Assert.That(ProfilerNativeGate.Busy, Is.False);
        }

        [Test]
        public void LoadThrowRetainsSourceLockAndDoesNotReplayOrClear()
        {
            CreateTarget(new byte[] { 1 });
            _api.LoadHandler = (path, keep) => throw new InvalidOperationException("unknown");
            var result = Run("load_profile");
            Assert.That(result.success, Is.False);
            Assert.That(Value<bool>(result.data, "sideEffectsUnknown"), Is.True);
            Assert.That(Value<bool>(result.data, "nativeMayStillRun"), Is.True);
            Assert.Throws<IOException>(() => { using (File.Open(Target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } });
            Assert.That(_api.LoadCalls, Is.EqualTo(1));
            Assert.That(ProfilerNativeGate.Busy, Is.True);
        }

        private string Target => Path.Combine(_root, "capture.data");

        private void CreateTarget(byte[] bytes)
        {
            Directory.CreateDirectory(_root); File.WriteAllBytes(Target, bytes);
        }

        private static Dictionary<string, object> Parameters()
        {
            return new Dictionary<string, object>
            {
                { "confirm", true }, { "path", ProfilerNativeFiles.NativeDirectory + "/capture.data" }
            };
        }

        private CommandResult Run(string action, Dictionary<string, object> parameters = null)
        {
            return ProfilerNativeFiles.Execute(new CommandRequest
            {
                id = "fixture", type = "profiler", @params = parameters ?? Parameters()
            }, action, _context);
        }

        private static T Value<T>(object value, string property)
        {
            return (T)value.GetType().GetProperty(property).GetValue(value, null);
        }

        private sealed class FakeApi : INativeProfilerApi
        {
            public NativeProfilerSupport Support { get; } = new NativeProfilerSupport
                { Save = true, Load = true, State = true, SaveReturnType = "bool" };
            internal readonly NativeProfilerState State = new NativeProfilerState
                { CurrentTargetGuid = 42, CurrentTargetName = "fixture-target" };
            internal Func<string, bool> SaveHandler = path => true;
            internal Func<string, bool, bool> LoadHandler = (path, keep) => true;
            internal int SaveCalls;
            internal int LoadCalls;
            public NativeProfilerState ReadState() { return State; }
            public bool Save(string path) { SaveCalls++; return SaveHandler(path); }
            public bool Load(string path, bool keepExistingData) { LoadCalls++; return LoadHandler(path, keepExistingData); }
        }

        private class StateDriver
        {
            public static bool enabled => false;
            public static int connectedProfiler => 42;
            public static string GetConnectionIdentifier(int guid) { return "fixture-target"; }
            public static bool LoadProfile(string path, bool keepExistingData) { return !keepExistingData; }
        }

        private sealed class VoidDriver : StateDriver
        {
            public static void SaveProfile(string path) { File.WriteAllBytes(path, new byte[] { 1 }); }
        }

        private sealed class BoolDriver : StateDriver
        {
            public static bool SaveProfile(string path) { File.WriteAllBytes(path, new byte[] { 1 }); return true; }
        }

        private sealed class FalseDriver : StateDriver
        {
            public static bool SaveProfile(string path) { File.WriteAllBytes(path, new byte[] { 1 }); return false; }
        }

        private sealed class WrongReturnDriver : StateDriver
        {
            public static int SaveProfile(string path) { return 1; }
            public new static void LoadProfile(string path, bool keepExistingData) { }
        }

        private sealed class WrongParameterDriver : StateDriver
        {
            public static bool SaveProfile(string path, bool overwrite) { return true; }
        }

        private sealed class WrongStateDriver
        {
            public static string enabled => "false";
            public static long connectedProfiler => 42;
            public static object GetConnectionIdentifier(int guid) { return "fixture"; }
            public static bool SaveProfile(string path) { return true; }
            public static bool LoadProfile(string path, bool keepExistingData) { return true; }
        }
    }
}
