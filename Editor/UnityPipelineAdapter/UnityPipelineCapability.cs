using System;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    /// <summary>
    /// Unity Pipeline 可选适配层的能力与就绪状态快照。
    /// </summary>
    [Serializable]
    public sealed class UnityPipelineCapability
    {
        public bool compiled;
        public string packageVersion;
        public bool editorSupported;
        public string descriptorPath;
        public bool serverReady;
        public string errorCode;
        public string message;

        public bool IsAvailable
        {
            get
            {
                // 包已加载不等于服务可连接；只有官方 Pipeline 服务已发现时才允许调用适配层。
                return compiled && editorSupported && serverReady && !string.IsNullOrEmpty(packageVersion);
            }
        }
    }
}
