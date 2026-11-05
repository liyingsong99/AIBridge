using System;
using System.Threading;

namespace AIBridge.Editor
{
    /// <summary>原生 Profiler/Memory 操作共用的进程内 single-flight；不控制既有命令。</summary>
    public static class ProfilerNativeGate
    {
        private static object _owner;

        public static bool Busy => Interlocked.CompareExchange(ref _owner, null, null) != null;

        internal static bool TryAcquire(out object lease)
        {
            var candidate = new object();
            if (Interlocked.CompareExchange(ref _owner, candidate, null) != null)
            {
                lease = null;
                return false;
            }
            lease = candidate;
            return true;
        }

        internal static void Release(object lease)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            // 旧请求不能释放其它操作的租约；未知原生结果保留租约到域重载。
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _owner, null, lease), lease))
                throw new InvalidOperationException("Native Profiler gate lease does not match.");
        }
    }
}
