using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Expand source Depth texture into destination.
    /// </summary>
    internal class ForegroundDepthExpansionPass : ScriptableRenderPass
    {
        RenderTextureDescriptor m_Descriptor;
        private RenderTargetHandle source { get; set; }
        RenderPassData m_Materials;
        const string m_ProfilerTag = "Expand Foreground Depth";

        readonly GraphicsFormat m_DefaultDepthFormat;
        public float BlurRadius = 100.0f;

        public ForegroundDepthExpansionPass(RenderPassEvent evt, ForwardRendererData rendererData)
        {
            m_Materials = new RenderPassData(rendererData);
            renderPassEvent = evt;

            m_DefaultDepthFormat = GraphicsFormat.R32_SFloat;
        }

        /// <summary>
        /// Configure the pass with the source and destination to execute on.
        /// </summary>
        /// <param name="source">Source Render Target</param>
        /// <param name="destination">Destination Render Targt</param>
        public void Setup(in RenderTextureDescriptor baseDescriptor, in RenderTargetHandle source)
        {
            this.source = source;
            m_Descriptor = baseDescriptor;
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);

            Render(cmd, ref renderingData);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        void Render(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ref var cameraData = ref renderingData.cameraData;

            int wh = m_Descriptor.width;// / 2;
            int hh = m_Descriptor.height;// / 2;

            // Assumes a radius of 1 is 1 at 1080p
            // Past a certain radius our gaussian kernel will look very bad so we'll clamp it for
            // very high resolutions (4K+).
            float maxRadius = BlurRadius * (wh / 1080f);
            maxRadius = Mathf.Min(maxRadius, 5f);

            m_Materials.m_ExpandDepthMaterial.SetFloat(ShaderConstants._MaxRadius, maxRadius);

            // Temporary textures
            cmd.GetTemporaryRT(ShaderConstants._PingTexture, GetStereoCompatibleDescriptor(wh, hh, m_DefaultDepthFormat), FilterMode.Bilinear);
            cmd.GetTemporaryRT(ShaderConstants._PongTexture, GetStereoCompatibleDescriptor(wh, hh, m_DefaultDepthFormat), FilterMode.Bilinear);
            

            // Blur
            cmd.SetGlobalTexture(ShaderConstants._SourceTexture, source.id);
            cmd.Blit(source.id, ShaderConstants._PingTexture, m_Materials.m_ExpandDepthMaterial, 0);
            cmd.Blit(ShaderConstants._PingTexture, ShaderConstants._PongTexture, m_Materials.m_ExpandDepthMaterial, 1);

            // Levels
            cmd.Blit(ShaderConstants._PongTexture, ShaderConstants._PingTexture, m_Materials.m_ExpandDepthMaterial, 2);

            // source.id gets bound to the Depth/Stencil slot rather than as RT0
            //cmd.Blit(ShaderConstants._PongTexture, source.id, m_Materials.m_ExpandDepthMaterial, 2);

            // Error: Graphics.CopyTexture called with null source texture
            // cmd.ConvertTexture(ShaderConstants._PingTexture, source.id);

            // Both _MainTex and RT0 remain unbound
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetViewport(cameraData.camera.pixelRect);
            cmd.SetGlobalTexture(ShaderConstants._SourceTexture, ShaderConstants._PingTexture);
            // Force pongtexture as the DS to avoid source.id being used.
            cmd.SetRenderTarget(source.id, (RenderTargetIdentifier)ShaderConstants._PongTexture);
            cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_Materials.m_ExpandDepthMaterial, 0, 3);

            // Cleanup
            cmd.ReleaseTemporaryRT(ShaderConstants._PingTexture);
            cmd.ReleaseTemporaryRT(ShaderConstants._PongTexture);
        }

        /// <inheritdoc/>
        public override void FrameCleanup(CommandBuffer cmd)
        {
            if (cmd == null)
                throw new ArgumentNullException("cmd");
        }

        RenderTextureDescriptor GetStereoCompatibleDescriptor(int width, int height, GraphicsFormat format)
        {
            // Inherit the VR setup from the camera descriptor
            var desc = m_Descriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.width = width;
            desc.height = height;
            desc.graphicsFormat = format;
            return desc;
        }

        [Serializable, ReloadGroup]
        class RenderPassData : ScriptableObject
        {
            public readonly Material m_ExpandDepthMaterial;

            public RenderPassData(ForwardRendererData data)
            {
                m_ExpandDepthMaterial = Load(data.shaders.expandDepthPS);
            }

            Material Load(Shader shader)
            {
                if (shader == null)
                {
                    Debug.LogErrorFormat($"Missing shader. {GetType().DeclaringType.Name} render pass will not execute. Check for missing reference in the renderer resources.");
                    return null;
                }

                return CoreUtils.CreateEngineMaterial(shader);
            }
        }

            // Precomputed shader ids to same some CPU cycles (mostly affects mobile)
            static class ShaderConstants
        {
            public static readonly int _TempTarget = Shader.PropertyToID("_TempTarget");

            public static readonly int _MaxRadius = Shader.PropertyToID("_MaxRadius");
            public static readonly int _PongTexture = Shader.PropertyToID("_PongTexture");
            public static readonly int _PingTexture = Shader.PropertyToID("_PingTexture");
            public static readonly int _SourceTexture = Shader.PropertyToID("_MainTex");
        }
    }
}
