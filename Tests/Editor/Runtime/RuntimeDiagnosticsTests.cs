using System;
using System.Threading;
using System.Threading.Tasks;
using AIBridge.Runtime;
using AIBridge.Runtime.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AIBridge.Editor.Tests
{
    public class RuntimeDiagnosticsTests
    {
        [Test]
        public void Percentile_InterpolatesSortedValues()
        {
            var values = new[] { 10d, 20d, 30d, 40d };

            Assert.That(RuntimePercentile.Calculate(values, 50d), Is.EqualTo(25d));
            Assert.That(RuntimePercentile.Calculate(values, 95d), Is.EqualTo(38.5d).Within(0.0001d));
        }

        [Test]
        public void ProfilerSnapshotData_CarriesRuntimeUnsupportedItems()
        {
            var snapshot = new ProfilerSnapshot
            {
                source = "runtime",
                targetId = "runtime-test",
                stats = new ProfilerStats
                {
                    frame = new ProfilerFrameStats
                    {
                        frameCount = 10,
                        fps = 60d
                    },
                    rendering = new ProfilerRenderingStats
                    {
                        targetFrameRate = 60,
                        vSyncCount = 1,
                        graphicsDeviceType = "Null"
                    }
                },
                unsupported = new[]
                {
                    new ProfilerUnsupportedItem("profilerWindow", "Runtime target cannot open the Unity Editor Profiler window.")
                }
            };

            Assert.AreEqual("runtime", snapshot.source);
            Assert.AreEqual(60d, snapshot.stats.frame.fps);
            Assert.AreEqual("profilerWindow", snapshot.unsupported[0].feature);
        }

        [Test]
        public void LogBuffer_ClearAndFiltersWork()
        {
            var buffer = new AIBridgeRuntimeLogBuffer();
            buffer.Initialize(10);

            try
            {
                LogAssert.Expect(LogType.Log, "aibridge-runtime-log-buffer-test");
                Debug.Log("aibridge-runtime-log-buffer-test");

                var entries = buffer.GetEntries(10, "Log", "runtime-log-buffer", false, Time.frameCount, null);
                Assert.That(entries.Length, Is.EqualTo(1));
                Assert.That(entries[0].stackTrace, Is.Null);

                Assert.That(buffer.Clear(), Is.EqualTo(1));
                Assert.That(buffer.Count, Is.EqualTo(0));
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void LogBuffer_ThreadedLogsDoNotCallUnityFrameApi()
        {
            var buffer = new AIBridgeRuntimeLogBuffer();
            buffer.Initialize(10);

            try
            {
                const string Message = "aibridge-runtime-log-buffer-threaded-test";

                LogAssert.Expect(LogType.Log, Message);
                Assert.That(Task.Run(() => Debug.Log(Message)).Wait(5000), Is.True);

                var deadline = DateTime.UtcNow.AddSeconds(2);
                AIBridgeRuntimeLogEntry[] entries;
                do
                {
                    entries = buffer.GetEntries(10, "Log", "threaded-test", false, Time.frameCount, null);
                    if (entries.Length > 0)
                    {
                        break;
                    }

                    Thread.Sleep(10);
                } while (DateTime.UtcNow < deadline);

                Assert.That(entries.Length, Is.EqualTo(1));
                Assert.That(entries[0].frame, Is.EqualTo(-1));
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void LogBuffer_RingBufferKeepsNewestEntriesInOrder()
        {
            var buffer = new AIBridgeRuntimeLogBuffer();
            buffer.Initialize(3);

            try
            {
                for (var i = 0; i < 5; i++)
                {
                    var message = "aibridge-runtime-log-buffer-ring-" + i;
                    LogAssert.Expect(LogType.Log, message);
                    Debug.Log(message);
                }

                var entries = buffer.GetEntries(10, "Log", "aibridge-runtime-log-buffer-ring-", false, null, null);
                Assert.That(entries.Length, Is.EqualTo(3));
                Assert.That(entries[0].message, Is.EqualTo("aibridge-runtime-log-buffer-ring-2"));
                Assert.That(entries[1].message, Is.EqualTo("aibridge-runtime-log-buffer-ring-3"));
                Assert.That(entries[2].message, Is.EqualTo("aibridge-runtime-log-buffer-ring-4"));
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }
}
