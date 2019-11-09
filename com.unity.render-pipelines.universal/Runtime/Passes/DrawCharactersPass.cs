using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering.Universal
{
	/// <summary>
	/// Render all objects that have a 'DepthOnly' pass into the given depth buffer.
	///
	/// You can use this pass to prime a depth buffer for subsequent rendering.
	/// Use it as a z-prepass, or use it to generate a depth buffer.
	/// </summary>
	internal class DrawForegroundPass : ScriptableRenderPass
	{
		int kDepthBufferBits = 32;

		private RenderTargetHandle depthAttachmentHandle { get; set; }
        private RenderTargetHandle colorAttachmentHandle { get; set; }
        internal RenderTextureDescriptor descriptor { get; private set; }

		FilteringSettings m_FilteringSettings;
		string m_ProfilerTag = "Render Characters Pass";
        List<ShaderTagId> m_ShaderTagIdList = new List<ShaderTagId>();

        /// <summary>
        /// Create the DepthOnlyPass
        /// </summary>
        public DrawForegroundPass(RenderPassEvent evt, RenderQueueRange renderQueueRange)
		{
			m_FilteringSettings = new FilteringSettings(renderQueueRange, ForwardRenderer.CutoutMask);

            m_ShaderTagIdList.Add(new ShaderTagId("UniversalForward"));
            m_ShaderTagIdList.Add(new ShaderTagId("LightweightForward"));
            m_ShaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));

            renderPassEvent = evt;
		}

		/// <summary>
		/// Configure the pass
		/// </summary>
		public void Setup(
			 RenderTextureDescriptor baseDescriptor,
			 RenderTargetHandle depthAttachmentHandle,
             RenderTargetHandle colorAttachmentHandle)
		{
			this.depthAttachmentHandle = depthAttachmentHandle;
            this.colorAttachmentHandle = colorAttachmentHandle;
			baseDescriptor.colorFormat = RenderTextureFormat.Depth;
			baseDescriptor.depthBufferBits = kDepthBufferBits;

			descriptor = baseDescriptor;
		}

		public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
		{
			cmd.GetTemporaryRT(depthAttachmentHandle.id, descriptor, FilterMode.Point);
            //cmd.GetTemporaryRT(colorAttachmentHandle.id, descriptor, FilterMode.Point);
            ConfigureTarget(colorAttachmentHandle.Identifier(), depthAttachmentHandle.Identifier());
			ConfigureClear(ClearFlag.Depth, Color.black);
		}

		/// <inheritdoc/>
		public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
		{
			CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
			using (new ProfilingSample(cmd, m_ProfilerTag))
			{
				context.ExecuteCommandBuffer(cmd);
				cmd.Clear();


				var sortFlags = renderingData.cameraData.defaultOpaqueSortFlags;
				var drawSettings = CreateDrawingSettings(m_ShaderTagIdList, ref renderingData, sortFlags);

				ref CameraData cameraData = ref renderingData.cameraData;
				Camera camera = cameraData.camera;
				if (cameraData.isStereoEnabled)
					context.StartMultiEye(camera);

				context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref m_FilteringSettings);

			}
			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
		}

		/// <inheritdoc/>
		public override void FrameCleanup(CommandBuffer cmd)
		{
			if (cmd == null)
				throw new ArgumentNullException("cmd");

			//if (depthAttachmentHandle != RenderTargetHandle.CameraTarget)
			//{
			//	cmd.ReleaseTemporaryRT(depthAttachmentHandle.id);
			//	depthAttachmentHandle = RenderTargetHandle.CameraTarget;
			//}

            if (colorAttachmentHandle != RenderTargetHandle.CameraTarget)
            {
                cmd.ReleaseTemporaryRT(colorAttachmentHandle.id);
                colorAttachmentHandle = RenderTargetHandle.CameraTarget;
            }
        }
	}
}
