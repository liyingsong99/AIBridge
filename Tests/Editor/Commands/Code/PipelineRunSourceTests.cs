using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace AIBridge.Editor.Tests
{
    public sealed class PipelineRunSourceTests
    {
        private string _root;
        private string _file;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Directory.GetParent(Application.dataPath).FullName, ".aibridge", "tmp", "run-source-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, ".aibridge", "code"));
            _file = Path.Combine(_root, ".aibridge", "code", "task.cs");
            File.WriteAllText(_file, "public static class Fixture { public static int Run() { return 1; } }");
        }

        [TearDown]
        public void TearDown()
        {
            // 仅移除本用例 GUID 创建的独立根；不触及项目用户源码或操作日志。
            if (!string.IsNullOrEmpty(_root) && Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [TestCase("Assets/outside.cs")]
        [TestCase(".aibridge/code/../outside.cs")]
        [TestCase(".aibridge/code/sub/task.cs")]
        [TestCase(".aibridge/code/task.csx")]
        public void ValidatePath_RejectsUnsupportedSource(string file)
        {
            Assert.Throws<ArgumentException>(() => PipelineRunSource.ValidatePath(_root, file));
        }

        [Test]
        public void CapturedSource_DoesNotRereadMutatedOriginal_AndMapsOriginalPath()
        {
            var captured = PipelineRunSource.ReadSource(_root, _file);
            File.WriteAllText(_file, "invalid replacement");
            using (var snapshot = PipelineRunSource.Capture(_root, captured, _file))
            {
                var text = File.ReadAllText(snapshot.Path);
                Assert.That(text, Does.Contain(captured));
                Assert.That(text, Does.Not.Contain("invalid replacement"));
                Assert.That(text, Does.StartWith("#line 1 \"" + _file.Replace("\\", "/") + "\""));
                Assert.That(snapshot.Path, Is.Not.EqualTo(_file));
            }
        }

        [Test]
        public void ValidatePath_RejectsLinkedSource()
        {
            var link = Path.Combine(Path.GetDirectoryName(_file), "linked.cs");
            var create = typeof(File).GetMethod("CreateSymbolicLink", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), typeof(string) }, null);
            if (create == null) Assert.Ignore("Host profile does not expose symbolic-link creation; real reparse test not verified.");
            try { create.Invoke(null, new object[] { link, _file }); }
            catch (TargetInvocationException) { Assert.Ignore("Host cannot create a symbolic link; reparse test not verified."); }
            Assert.Throws<IOException>(() => PipelineRunSource.ValidatePath(_root, link));
        }

        [Test]
        public void Snapshot_IsLockedAgainstWriteAndDeleteUntilDisposed()
        {
            var captured = PipelineRunSource.ReadSource(_root, _file);
            var snapshot = PipelineRunSource.Capture(_root, captured, _file);
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    Assert.Throws<IOException>(() => File.WriteAllText(snapshot.Path, "replaced"));
                    Assert.Throws<IOException>(() => File.Delete(snapshot.Path));
                }
                Assert.That(File.ReadAllText(snapshot.Path), Does.Contain(captured));
            }
            finally { snapshot.DeleteAfterConfirmedOutcome(); }
            Assert.That(File.Exists(snapshot.Path), Is.False);
        }

        [TestCase("")]
        [TestCase(" ")]
        public void ReadSource_RejectsEmptyFile(string content)
        {
            File.WriteAllText(_file, content);
            Assert.Throws<ArgumentException>(() => PipelineRunSource.ReadSource(_root, _file));
        }

        [Test]
        public void ReadAndCapture_RejectOversizedSource()
        {
            var content = new string('x', 512 * 1024 + 1);
            File.WriteAllText(_file, content);
            Assert.Throws<ArgumentException>(() => PipelineRunSource.ReadSource(_root, _file));
            Assert.Throws<ArgumentException>(() => PipelineRunSource.Capture(_root, content, _file));
        }

        [TestCase("{}")]
        [TestCase("null")]
        [TestCase("7")]
        [TestCase("[] , \"other\":true")]
        [TestCase("[ {\"a\":1,\"a\":2} ]")]
        [TestCase("[01]")]
        [TestCase("[1e9999]")]
        [TestCase("[999999999999999999999999999999999999]")]
        [TestCase("[\u00a00]")]
        [TestCase("[1\u0661]")]
        [TestCase("[tru]")]
        [TestCase("[\"\\u 001\"]")]
        [TestCase("[\"literal\nnewline\"]")]
        public void RunArguments_RejectNonArrayAndAmbiguousJson(string json)
        {
            var method = typeof(CodeCommand).GetMethod("ReadPipelineRunArguments", BindingFlags.Static | BindingFlags.NonPublic);
            var request = new CommandRequest { @params = new Dictionary<string, object> { { "args", json } } };
            Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { request }));
        }

        [Test]
        public void RunArguments_PreservesNestedNullAndReturnsCanonicalArray()
        {
            var method = typeof(CodeCommand).GetMethod("ReadPipelineRunArguments", BindingFlags.Static | BindingFlags.NonPublic);
            var request = new CommandRequest { @params = new Dictionary<string, object> { { "args", "[ { \"a\" : null }, [null, false] ]" } } };
            Assert.That(method.Invoke(null, new object[] { request }), Is.EqualTo("[{\"a\":null},[null,false]]"));
        }
    }
}
