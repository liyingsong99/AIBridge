using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using NUnit.Framework;
using UnityEngine;

namespace AIBridge.Editor.Tests
{
    public sealed class PipelineRunJobSessionTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private Type _adapter;
        private Type _descriptorType;
        private string _directory;
        private string _path;
        private PipelineRunSource.Snapshot _snapshot;
        private PipelineRunJobSession _session;
        private bool _enabled, _accepted, _busy;

        [SetUp]
        public void SetUp()
        {
            _adapter = Type.GetType("AIBridge.Editor.UnityPipelineAdapter.UnityPipelineAdapter, cn.lys.aibridge.Editor.UnityPipelineAdapter");
            if (_adapter == null) Assert.Ignore("Optional adapter unavailable.");
            _descriptorType = _adapter.GetNestedType("PipelineServerDescriptor", BindingFlags.NonPublic);
            _enabled = AIBridgeProjectSettings.Instance.EnableCodeExecution;
            _accepted = AIBridgeProjectSettings.Instance.CodeExecutionRiskAccepted;
            _busy = (bool)_adapter.GetField("s_EvalRequestActive", PrivateStatic).GetValue(null);
            AIBridgeProjectSettings.Instance.EnableCodeExecution = true;
            AIBridgeProjectSettings.Instance.CodeExecutionRiskAccepted = true;
            _directory = Path.GetFullPath(Path.Combine(".aibridge/tmp", "job-session-fixture-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "descriptor.json");
            var source = Path.Combine(_directory, "snapshot.cs");
            File.WriteAllText(source, "// controlled fixture");
            _snapshot = new PipelineRunSource.Snapshot(source, new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        [TearDown]
        public void TearDown()
        {
            if (_adapter == null) return;
            // 只移除本fixture新增的强引用；保留用户已存在unknown快照。
            var retained = (IList)_adapter.GetField("UnknownRunSnapshots", PrivateStatic).GetValue(null);
            retained.Remove(_snapshot);
            _snapshot?.Dispose();
            _adapter.GetField("s_EvalRequestActive", PrivateStatic).SetValue(null, _busy);
            AIBridgeProjectSettings.Instance.EnableCodeExecution = _enabled;
            AIBridgeProjectSettings.Instance.CodeExecutionRiskAccepted = _accepted;
            if (_directory != null && Directory.Exists(_directory)) Directory.Delete(_directory, true);
            _session = null; _snapshot = null;
        }

        [Test]
        public void BodyKeepsNestedNullArgumentsAndExplicitDetachedFlag()
        {
            Create();
            var text = (string)_session.GetType().GetField("_body", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_session);
            var body = AIBridgeJson.DeserializeObjectStrict(text);
            Assert.That(body["job"], Is.EqualTo(true)); Assert.That(body["command"], Is.EqualTo("script run"));
            var parameters = (Dictionary<string, object>)body["parameters"];
            var args = (List<object>)parameters["args"];
            var item = (Dictionary<string, object>)args[0];
            Assert.That(item.ContainsKey("nullable"), Is.True); Assert.That(item["nullable"], Is.Null);
            Assert.That(parameters["mode"], Is.EqualTo("ephemeral"));
            Assert.That(_session.TargetIdentity, Has.Length.EqualTo(64));
        }

        [TestCase("pid", 888L)]
        [TestCase("port", 8888L)]
        [TestCase("projectPath", "other")]
        [TestCase("unityVersion", "other")]
        [TestCase("startedAt", "other")]
        [TestCase("evalToken", "other")]
        public async Task ChangedIdentityRejectsBeforePostAndSubmissionCannotReplay(string key, object value)
        {
            var data = Create(); data[key] = value; File.WriteAllText(_path, AIBridgeJson.Serialize(data));
            var first = await _session.SubmitAsync();
            Assert.That(first.valid, Is.False); Assert.That(first.errorCode, Is.EqualTo("pipelineTargetChanged"));
            var second = await _session.SubmitAsync();
            Assert.That(second.errorCode, Is.EqualTo("pipelineJobReplayDenied"));
            _session.Release(false);
            Assert.That(File.Exists(_snapshot.Path), Is.True);
        }

        [Test]
        public void UnsubmittedReleaseDeletesOnlyOwnSnapshot()
        {
            Create(); _session.Release(false);
            Assert.That(File.Exists(_snapshot.Path), Is.False);
        }

        [Test]
        public async Task CancelRequiresConfirmationBeforeAnyIo()
        {
            Create();
            var value = await _session.CancelAsync(false);
            Assert.That(value.valid, Is.False); Assert.That(value.errorCode, Is.EqualTo("pipelineConfirmationRequired"));
            Assert.That(File.Exists(_snapshot.Path), Is.True);
            _session.Release(false);
        }

        private Dictionary<string, object> Create()
        {
            var data = new Dictionary<string, object>
            {
                { "pid", 123L }, { "port", 1L }, { "projectPath", _directory }, { "unityVersion", "fixture" },
                { "startedAt", "2026-11-05T01:00:00Z" }, { "evalToken", "fixture-secret" }
            };
            var json = AIBridgeJson.Serialize(data); File.WriteAllText(_path, json);
            var descriptor = JsonUtility.FromJson(json, _descriptorType);
            var type = _adapter.GetNestedType("RunJobSession", BindingFlags.NonPublic);
            _session = (PipelineRunJobSession)Activator.CreateInstance(type, new object[]
            { _snapshot, descriptor, _path, "Fixture.Run", "[{\"nullable\":null}]", 1000, true });
            return data;
        }
    }
}
