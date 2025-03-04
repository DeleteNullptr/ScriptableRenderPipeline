using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    using AntialiasingMode = HDAdditionalCameraData.AntialiasingMode;

    // This holds all the matrix data we need for rendering, including data from the previous frame
    // (which is the main reason why we need to keep them around for a minimum of one frame).
    // HDCameras are automatically created & updated from a source camera and will be destroyed if
    // not used during a frame.
    public class HDCamera
    {
        public struct ViewConstants
        {
            public Matrix4x4 viewMatrix;
            public Matrix4x4 invViewMatrix;
            public Matrix4x4 projMatrix;
            public Matrix4x4 invProjMatrix;
            public Matrix4x4 viewProjMatrix;
            public Matrix4x4 invViewProjMatrix;
            public Matrix4x4 nonJitteredViewProjMatrix;

            // View-projection matrix from the previous frame (non-jittered)
            public Matrix4x4 prevViewProjMatrix;
            public Matrix4x4 prevInvViewProjMatrix;
            public Matrix4x4 prevViewProjMatrixNoCameraTrans;

            // Utility matrix (used by sky) to map screen position to WS view direction
            public Matrix4x4 pixelCoordToViewDirWS;

            public Vector3 worldSpaceCameraPos;
            public float pad0;
            public Vector3 worldSpaceCameraPosViewOffset;
            public float pad1;
            public Vector3 prevWorldSpaceCameraPos;
            public float pad2;
        };

        public ViewConstants mainViewConstants;

        public Vector4   screenSize;
        public Frustum   frustum;
        public Vector4[] frustumPlaneEquations;
        public Camera    camera;
        public Vector4   taaJitter;
        public int       taaFrameIndex;
        public float     taaSharpenStrength;
        public Vector4   zBufferParams;
        public Vector4   unity_OrthoParams;
        public Vector4   projectionParams;
        public Vector4   screenParams;
        public int       volumeLayerMask;
        public Transform volumeAnchor;
        // This will have the correct viewport position and the size will be full resolution (ie : not taking dynamic rez into account)
        public Rect      finalViewport;

        public RTHandleProperties historyRTHandleProperties { get { return m_HistoryRTSystem.rtHandleProperties; } }

        public bool colorPyramidHistoryIsValid = false;
        public bool volumetricHistoryIsValid   = false; // Contains garbage otherwise
        public int  colorPyramidHistoryMipCount = 0;
        public VBufferParameters[] vBufferParams; // Double-buffered

        float m_AmbientOcclusionResolutionScale = 0.0f; // Factor used to track if history should be reallocated for Ambient Occlusion

        // We need to keep this here as culling is done before volume update. That means that culling for the light will be left with the state used by the last
        // updated camera which is not necessarily the camera we are culling for. This should be fixed if we end up having scriptable culling, as the culling for
        // the lights can be done after the volume update.
        internal float shadowMaxDistance = 500.0f;

        // XR multipass and instanced views are supported (see XRSystem)
        XRPass m_XRPass;
        public XRPass xr { get { return m_XRPass; } }
        public ViewConstants[] xrViewConstants;

        // XR View Constants arrays (required due to limitations of API for StructuredBuffer)
        Matrix4x4[] xrViewMatrix = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Matrix4x4[] xrInvViewMatrix = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Matrix4x4[] xrProjMatrix = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Matrix4x4[] xrInvProjMatrix = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Matrix4x4[] xrViewProjMatrix = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Matrix4x4[] xrInvViewProjMatrix = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Matrix4x4[] xrNonJitteredViewProjMatrix = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Matrix4x4[] xrPrevViewProjMatrix = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Matrix4x4[] xrPrevInvViewProjMatrix = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Matrix4x4[] xrPrevViewProjMatrixNoCameraTrans = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Matrix4x4[] xrPixelCoordToViewDirWS = new Matrix4x4[ShaderConfig.s_XrMaxViews];
        Vector4[] xrWorldSpaceCameraPos = new Vector4[ShaderConfig.s_XrMaxViews];
        Vector4[] xrWorldSpaceCameraPosViewOffset = new Vector4[ShaderConfig.s_XrMaxViews];
        Vector4[] xrPrevWorldSpaceCameraPos = new Vector4[ShaderConfig.s_XrMaxViews];

        // Recorder specific
        IEnumerator<Action<RenderTargetIdentifier, CommandBuffer>> m_RecorderCaptureActions;
        int m_RecorderTempRT = Shader.PropertyToID("TempRecorder");
        MaterialPropertyBlock m_RecorderPropertyBlock = new MaterialPropertyBlock();

        // Non oblique projection matrix (RHS)
        // TODO: this code is never used and not compatible with XR
        public Matrix4x4 nonObliqueProjMatrix
        {
            get
            {
                return m_AdditionalCameraData != null
                    ? m_AdditionalCameraData.GetNonObliqueProjection(camera)
                    : GeometryUtils.CalculateProjectionMatrix(camera);
            }
        }

        // This is the viewport size actually used for this camera (as it can be altered by VR for example)
        int m_ActualWidth;
        int m_ActualHeight;

        // Current mssa sample
        MSAASamples m_msaaSamples;
        FrameSettings m_frameSettings;

        public int actualWidth { get { return m_ActualWidth; } }
        public int actualHeight { get { return m_ActualHeight; } }

        public MSAASamples msaaSamples { get { return m_msaaSamples; } }

        public FrameSettings frameSettings { get { return m_frameSettings; } }

        // Always true for cameras that just got added to the pool - needed for previous matrices to
        // avoid one-frame jumps/hiccups with temporal effects (motion blur, TAA...)
        public bool isFirstFrame { get; private set; }

        // Ref: An Efficient Depth Linearization Method for Oblique View Frustums, Eq. 6.
        // TODO: pass this as "_ZBufferParams" if the projection matrix is oblique.
        public Vector4 invProjParam
        {
            get
            {
                var p = mainViewConstants.projMatrix;
                return new Vector4(
                    p.m20 / (p.m00 * p.m23),
                    p.m21 / (p.m11 * p.m23),
                    -1f / p.m23,
                    (-p.m22 + p.m20 * p.m02 / p.m00 + p.m21 * p.m12 / p.m11) / p.m23
                    );
            }
        }

        public bool isMainGameView { get { return camera.cameraType == CameraType.Game && camera.targetTexture == null; } }

        // Helper property to inform how many views are rendered simultaneously
        public int viewCount { get => Math.Max(1, xr.viewCount); }

        public bool clearDepth
        {
            get { return m_AdditionalCameraData != null ? m_AdditionalCameraData.clearDepth : camera.clearFlags != CameraClearFlags.Nothing; }
        }

        public HDAdditionalCameraData.ClearColorMode clearColorMode
        {
            get
            {
                if (m_AdditionalCameraData != null)
                {
                    return m_AdditionalCameraData.clearColorMode;
                }

                if (camera.clearFlags == CameraClearFlags.Skybox)
                    return HDAdditionalCameraData.ClearColorMode.Sky;
                else if (camera.clearFlags == CameraClearFlags.SolidColor)
                    return HDAdditionalCameraData.ClearColorMode.Color;
                else // None
                    return HDAdditionalCameraData.ClearColorMode.None;
            }
        }

        public Color backgroundColorHDR
        {
            get
            {
                if (m_AdditionalCameraData != null)
                {
                    return m_AdditionalCameraData.backgroundColorHDR;
                }

                // The scene view has no additional data so this will correctly pick the editor preference backround color here.
                return camera.backgroundColor.linear;
            }
        }

        public HDAdditionalCameraData.FlipYMode flipYMode
        {
            get
            {
                if (m_AdditionalCameraData != null)
                    return m_AdditionalCameraData.flipYMode;
                return HDAdditionalCameraData.FlipYMode.Automatic;
            }
        }

        // This value will always be correct for the current camera, no need to check for
        // game view / scene view / preview in the editor, it's handled automatically
        public AntialiasingMode antialiasing { get; private set; } = AntialiasingMode.None;

        public HDAdditionalCameraData.SMAAQualityLevel SMAAQuality { get; private set; } = HDAdditionalCameraData.SMAAQualityLevel.Medium;


        public bool dithering => m_AdditionalCameraData != null && m_AdditionalCameraData.dithering;

        public bool stopNaNs => m_AdditionalCameraData != null && m_AdditionalCameraData.stopNaNs;

        public HDPhysicalCamera physicalParameters => m_AdditionalCameraData?.physicalParameters;

        public IEnumerable<AOVRequestData> aovRequests =>
            m_AdditionalCameraData != null && !m_AdditionalCameraData.Equals(null)
                ? m_AdditionalCameraData.aovRequests
                : Enumerable.Empty<AOVRequestData>();

        public bool invertFaceCulling
            => m_AdditionalCameraData != null && m_AdditionalCameraData.invertFaceCulling;

        public LayerMask probeLayerMask
            => m_AdditionalCameraData != null
            ? m_AdditionalCameraData.probeLayerMask
            : (LayerMask)~0;

        internal float probeRangeCompressionFactor
            => m_AdditionalCameraData != null
            ? m_AdditionalCameraData.probeCustomFixedExposure
            : 1.0f;

        static Dictionary<(Camera, int), HDCamera> s_Cameras = new Dictionary<(Camera, int), HDCamera>();
        static List<(Camera, int)> s_Cleanup = new List<(Camera, int)>(); // Recycled to reduce GC pressure

        HDAdditionalCameraData m_AdditionalCameraData = null; // Init in Update

        BufferedRTHandleSystem m_HistoryRTSystem = new BufferedRTHandleSystem();

        int m_NumColorPyramidBuffersAllocated = 0;
        int m_NumVolumetricBuffersAllocated   = 0;

        public HDCamera(Camera cam)
        {
            camera = cam;

            frustum = new Frustum();
            frustum.planes = new Plane[6];
            frustum.corners = new Vector3[8];

            frustumPlaneEquations = new Vector4[6];

            Reset();
        }

        public bool IsTAAEnabled()
        {
            return antialiasing == AntialiasingMode.TemporalAntialiasing;
        }

        // Pass all the systems that may want to update per-camera data here.
        // That way you will never update an HDCamera and forget to update the dependent system.
        // NOTE: This function must be called only once per rendering (not frame, as a single camera can be rendered multiple times with different parameters during the same frame)
        // Otherwise, previous frame view constants will be wrong.
        public void Update(FrameSettings currentFrameSettings, HDRenderPipeline hdrp, MSAASamples msaaSamples, XRPass xrPass)
        {
            // store a shortcut on HDAdditionalCameraData (done here and not in the constructor as
            // we don't create HDCamera at every frame and user can change the HDAdditionalData later (Like when they create a new scene).
            camera.TryGetComponent<HDAdditionalCameraData>(out m_AdditionalCameraData);

            m_XRPass = xrPass;
            m_frameSettings = currentFrameSettings;

            UpdateAntialiasing();

            // Handle memory allocation.
            {
                bool isCurrentColorPyramidRequired = m_frameSettings.IsEnabled(FrameSettingsField.RoughRefraction) || m_frameSettings.IsEnabled(FrameSettingsField.Distortion);
                bool isHistoryColorPyramidRequired = m_frameSettings.IsEnabled(FrameSettingsField.SSR) || antialiasing == AntialiasingMode.TemporalAntialiasing;
                bool isVolumetricHistoryRequired = m_frameSettings.IsEnabled(FrameSettingsField.Volumetrics) && m_frameSettings.IsEnabled(FrameSettingsField.ReprojectionForVolumetrics);

                int numColorPyramidBuffersRequired = 0;
                if (isCurrentColorPyramidRequired)
                    numColorPyramidBuffersRequired = 1;
                if (isHistoryColorPyramidRequired) // Superset of case above
                    numColorPyramidBuffersRequired = 2;

                int numVolumetricBuffersRequired = isVolumetricHistoryRequired ? 2 : 0; // History + feedback

                if ((m_NumColorPyramidBuffersAllocated != numColorPyramidBuffersRequired) ||
                    (m_NumVolumetricBuffersAllocated != numVolumetricBuffersRequired))
                {
                    // Reinit the system.
                    colorPyramidHistoryIsValid = false;
                    hdrp.DeinitializeVolumetricLightingPerCameraData(this);

                    // The history system only supports the "nuke all" option.
                    m_HistoryRTSystem.Dispose();
                    m_HistoryRTSystem = new BufferedRTHandleSystem();

                    if (numColorPyramidBuffersRequired != 0)
                    {
                        AllocHistoryFrameRT((int)HDCameraFrameHistoryType.ColorBufferMipChain, HistoryBufferAllocatorFunction, numColorPyramidBuffersRequired);
                        colorPyramidHistoryIsValid = false;
                    }

                    hdrp.InitializeVolumetricLightingPerCameraData(this, numVolumetricBuffersRequired, RTHandles.defaultRTHandleSystem);

                    // Mark as init.
                    m_NumColorPyramidBuffersAllocated = numColorPyramidBuffersRequired;
                    m_NumVolumetricBuffersAllocated = numVolumetricBuffersRequired;
                }
            }

            // Update viewport
            {
                if (xr.enabled)
                {
                    finalViewport = xr.GetViewport();
                }
                else
                {
                    finalViewport = new Rect(camera.pixelRect.x, camera.pixelRect.y, camera.pixelWidth, camera.pixelHeight);
                }

                m_ActualWidth = Math.Max((int)finalViewport.size.x, 1);
                m_ActualHeight = Math.Max((int)finalViewport.size.y, 1);
            }

            Vector2Int nonScaledViewport = new Vector2Int(m_ActualWidth, m_ActualHeight);
            if (isMainGameView)
            {
                Vector2Int scaledSize = DynamicResolutionHandler.instance.GetRTHandleScale(new Vector2Int(m_ActualWidth, m_ActualHeight));
                m_ActualWidth = scaledSize.x;
                m_ActualHeight = scaledSize.y;
            }

            var screenWidth = m_ActualWidth;
            var screenHeight = m_ActualHeight;

            m_msaaSamples = msaaSamples;

            screenSize = new Vector4(screenWidth, screenHeight, 1.0f / screenWidth, 1.0f / screenHeight);
            screenParams = new Vector4(screenSize.x, screenSize.y, 1 + screenSize.z, 1 + screenSize.w);

            UpdateAllViewConstants();
            isFirstFrame = false;

            hdrp.UpdateVolumetricLightingPerCameraData(this);

            UpdateVolumeParameters();

            // Here we use the non scaled resolution for the RTHandleSystem ref size because we assume that at some point we will need full resolution anyway.
            // This is necessary because we assume that after post processes, we have the full size render target for debug rendering
            // The only point of calling this here is to grow the render targets. The call in BeginRender will setup the current RTHandle viewport size.
            RTHandles.SetReferenceSize(nonScaledViewport.x, nonScaledViewport.y, m_msaaSamples);
        }

        // Updating RTHandle needs to be done at the beginning of rendering (not during update of HDCamera which happens in batches)
        // The reason is that RTHandle will hold data necessary to setup RenderTargets and viewports properly.
        public void BeginRender()
        {
            RTHandles.SetReferenceSize(m_ActualWidth, m_ActualHeight, m_msaaSamples);
            m_HistoryRTSystem.SwapAndSetReferenceSize(m_ActualWidth, m_ActualHeight, m_msaaSamples);

            m_RecorderCaptureActions = CameraCaptureBridge.GetCaptureActions(camera);
        }

        void UpdateAntialiasing()
        {
            // Handle post-process AA
            //  - If post-processing is disabled all together, no AA
            //  - In scene view, only enable TAA if animated materials are enabled
            //  - Else just use the currently set AA mode on the camera
            {
                if (!m_frameSettings.IsEnabled(FrameSettingsField.Postprocess) || !CoreUtils.ArePostProcessesEnabled(camera))
                    antialiasing = AntialiasingMode.None;
#if UNITY_EDITOR
                else if (camera.cameraType == CameraType.SceneView)
                {
                    var mode = HDRenderPipelinePreferences.sceneViewAntialiasing;

                    if (mode == AntialiasingMode.TemporalAntialiasing && !CoreUtils.AreAnimatedMaterialsEnabled(camera))
                        antialiasing = AntialiasingMode.None;
                    else
                        antialiasing = mode;
                }
#endif
                else if (m_AdditionalCameraData != null)
                {
                    antialiasing = m_AdditionalCameraData.antialiasing;
                    SMAAQuality = m_AdditionalCameraData.SMAAQuality;
                    taaSharpenStrength = m_AdditionalCameraData.taaSharpenStrength;
                }
                else
                    antialiasing = AntialiasingMode.None;
            }

            if (antialiasing != AntialiasingMode.TemporalAntialiasing)
            {
                taaFrameIndex = 0;
                taaJitter = Vector4.zero;
            }
        }

        void GetXrViewParameters(int xrViewIndex, out Matrix4x4 proj, out Matrix4x4 view, out Vector3 cameraPosition)
        {
            proj = xr.GetProjMatrix(xrViewIndex);
            view = xr.GetViewMatrix(xrViewIndex);
            cameraPosition = view.inverse.GetColumn(3);
        }

        void UpdateAllViewConstants()
        {
            // Allocate or resize view constants buffers
            if (xrViewConstants == null || xrViewConstants.Length != viewCount)
            {
                xrViewConstants = new ViewConstants[viewCount];
            }

            UpdateAllViewConstants(IsTAAEnabled(), true);
        }

        public void UpdateAllViewConstants(bool jitterProjectionMatrix)
        {
            UpdateAllViewConstants(jitterProjectionMatrix, false);
        }

        void UpdateAllViewConstants(bool jitterProjectionMatrix, bool updatePreviousFrameConstants)
        {
            var proj = camera.projectionMatrix;
            var view = camera.worldToCameraMatrix;
            var cameraPosition = camera.transform.position;

            // XR multipass support
            if (xr.enabled && viewCount == 1)
                GetXrViewParameters(0, out proj, out view, out cameraPosition);

            UpdateViewConstants(ref mainViewConstants, proj, view, cameraPosition, jitterProjectionMatrix, updatePreviousFrameConstants);

            // XR single-pass support
            if (xr.singlePassEnabled)
            {
                for (int viewIndex = 0; viewIndex < viewCount; ++viewIndex)
                {
                    GetXrViewParameters(viewIndex, out proj, out view, out cameraPosition);
                    UpdateViewConstants(ref xrViewConstants[viewIndex], proj, view, cameraPosition, jitterProjectionMatrix, updatePreviousFrameConstants);

                    // Compute offset between the main camera and the instanced views
                    xrViewConstants[viewIndex].worldSpaceCameraPosViewOffset = xrViewConstants[viewIndex].worldSpaceCameraPos - mainViewConstants.worldSpaceCameraPos;
                }
            }
            else
            {
                // Compute shaders always use the XR single-pass path due to the lack of multi-compile
                xrViewConstants[0] = mainViewConstants;
            }

            // Update frustum and projection parameters
            {
                var projMatrix = mainViewConstants.projMatrix;
                var invProjMatrix = mainViewConstants.invProjMatrix;
                var viewProjMatrix = mainViewConstants.viewProjMatrix;

                if (xr.enabled)
                {
                    var combinedProjMatrix = xr.cullingParams.stereoProjectionMatrix;
                    var combinedViewMatrix = xr.cullingParams.stereoViewMatrix;

                    if (ShaderConfig.s_CameraRelativeRendering != 0)
                    {
                        var combinedOrigin = combinedViewMatrix.inverse.GetColumn(3) - (Vector4)(camera.transform.position);
                        combinedViewMatrix.SetColumn(3, combinedOrigin);
                    }

                    projMatrix = GL.GetGPUProjectionMatrix(combinedProjMatrix, true);
                    invProjMatrix = projMatrix.inverse;
                    viewProjMatrix = projMatrix * combinedViewMatrix;
                }

                UpdateFrustum(projMatrix, invProjMatrix, viewProjMatrix);
            }

            m_RecorderCaptureActions = CameraCaptureBridge.GetCaptureActions(camera);
        }

        void UpdateViewConstants(ref ViewConstants viewConstants, Matrix4x4 projMatrix, Matrix4x4 viewMatrix, Vector3 cameraPosition, bool jitterProjectionMatrix, bool updatePreviousFrameConstants)
        {
             // If TAA is enabled projMatrix will hold a jittered projection matrix. The original,
            // non-jittered projection matrix can be accessed via nonJitteredProjMatrix.
            var nonJitteredCameraProj = projMatrix;
            var cameraProj = jitterProjectionMatrix
                ? GetJitteredProjectionMatrix(nonJitteredCameraProj)
                : nonJitteredCameraProj;

            // The actual projection matrix used in shaders is actually massaged a bit to work across all platforms
            // (different Z value ranges etc.)
            var gpuProj = GL.GetGPUProjectionMatrix(cameraProj, true); // Had to change this from 'false'
            var gpuView = viewMatrix;
            var gpuNonJitteredProj = GL.GetGPUProjectionMatrix(nonJitteredCameraProj, true);

            if (ShaderConfig.s_CameraRelativeRendering != 0)
            {
                // Zero out the translation component.
                gpuView.SetColumn(3, new Vector4(0, 0, 0, 1));
            }

            var gpuVP = gpuNonJitteredProj * gpuView;

            // A camera can be rendered multiple times in a single frame with different resolution/fov that would change the projection matrix
            // In this case we need to update previous rendering information.
            // We need to make sure that this code is not called more than once for one camera rendering (not frame! multiple renderings can happen in one frame) otherwise we'd overwrite previous rendering info
            // Note: if your first rendered view during the frame is not the Game view, everything breaks.
            if (updatePreviousFrameConstants)
            {
                if (isFirstFrame)
                {
                    viewConstants.prevWorldSpaceCameraPos = cameraPosition;
                    viewConstants.prevViewProjMatrix = gpuVP;
                    viewConstants.prevInvViewProjMatrix = viewConstants.prevViewProjMatrix.inverse;
                }
                else
                {
                    viewConstants.prevWorldSpaceCameraPos = viewConstants.worldSpaceCameraPos;
                    viewConstants.prevViewProjMatrix = viewConstants.nonJitteredViewProjMatrix;
                    viewConstants.prevViewProjMatrixNoCameraTrans = viewConstants.prevViewProjMatrix;
                }
            }

            viewConstants.viewMatrix = gpuView;
            viewConstants.invViewMatrix = gpuView.inverse;
            viewConstants.projMatrix = gpuProj;
            viewConstants.invProjMatrix = gpuProj.inverse;
            viewConstants.viewProjMatrix = gpuProj * gpuView;
            viewConstants.invViewProjMatrix = viewConstants.viewProjMatrix.inverse;
            viewConstants.nonJitteredViewProjMatrix = gpuNonJitteredProj * gpuView;
            viewConstants.worldSpaceCameraPos = cameraPosition;
            viewConstants.worldSpaceCameraPosViewOffset = Vector3.zero;
            viewConstants.pixelCoordToViewDirWS = ComputePixelCoordToWorldSpaceViewDirectionMatrix(viewConstants, screenSize);

            if (updatePreviousFrameConstants)
            {
                Vector3 cameraDisplacement = viewConstants.worldSpaceCameraPos - viewConstants.prevWorldSpaceCameraPos;
                viewConstants.prevWorldSpaceCameraPos -= viewConstants.worldSpaceCameraPos; // Make it relative w.r.t. the curr cam pos
                viewConstants.prevViewProjMatrix *= Matrix4x4.Translate(cameraDisplacement); // Now prevViewProjMatrix correctly transforms this frame's camera-relative positionWS
                viewConstants.prevInvViewProjMatrix = viewConstants.prevViewProjMatrix.inverse;
            }
            else
            {
                Matrix4x4 noTransViewMatrix = viewMatrix;
                noTransViewMatrix.SetColumn(3, new Vector4(0, 0, 0, 1));
                viewConstants.prevViewProjMatrixNoCameraTrans = gpuNonJitteredProj * noTransViewMatrix;
            }
        }

        void UpdateFrustum(Matrix4x4 projMatrix, Matrix4x4 invProjMatrix, Matrix4x4 viewProjMatrix)
        {
            float n = camera.nearClipPlane;
            float f = camera.farClipPlane;

            // Analyze the projection matrix.
            // p[2][3] = (reverseZ ? 1 : -1) * (depth_0_1 ? 1 : 2) * (f * n) / (f - n)
            float scale     = projMatrix[2, 3] / (f * n) * (f - n);
            bool  depth_0_1 = Mathf.Abs(scale) < 1.5f;
            bool  reverseZ  = scale > 0;
            bool  flipProj  = invProjMatrix.MultiplyPoint(new Vector3(0, 1, 0)).y < 0;

            // http://www.humus.name/temp/Linearize%20depth.txt
            if (reverseZ)
            {
                zBufferParams = new Vector4(-1 + f / n, 1, -1 / f + 1 / n, 1 / f);
            }
            else
            {
                zBufferParams = new Vector4(1 - f / n, f / n, 1 / f - 1 / n, 1 / n);
            }

            projectionParams = new Vector4(flipProj ? -1 : 1, n, f, 1.0f / f);

            float orthoHeight = camera.orthographic ? 2 * camera.orthographicSize : 0;
            float orthoWidth  = orthoHeight * camera.aspect;
            unity_OrthoParams = new Vector4(orthoWidth, orthoHeight, 0, camera.orthographic ? 1 : 0);

            Frustum.Create(frustum, viewProjMatrix, depth_0_1, reverseZ);

            // Left, right, top, bottom, near, far.
            for (int i = 0; i < 6; i++)
            {
                frustumPlaneEquations[i] = new Vector4(frustum.planes[i].normal.x, frustum.planes[i].normal.y, frustum.planes[i].normal.z, frustum.planes[i].distance);
            }
        }

        void UpdateVolumeParameters()
        {
            volumeAnchor = null;
            volumeLayerMask = -1;
            if (m_AdditionalCameraData != null)
            {
                volumeLayerMask = m_AdditionalCameraData.volumeLayerMask;
                volumeAnchor = m_AdditionalCameraData.volumeAnchorOverride;
            }
            else
            {
                // Temporary hack:
                // For scene view, by default, we use the "main" camera volume layer mask if it exists
                // Otherwise we just remove the lighting override layers in the current sky to avoid conflicts
                // This is arbitrary and should be editable in the scene view somehow.
                if (camera.cameraType == CameraType.SceneView)
                {
                    var mainCamera = Camera.main;
                    bool needFallback = true;
                    if (mainCamera != null)
                    {
                        var mainCamAdditionalData = mainCamera.GetComponent<HDAdditionalCameraData>();
                        if (mainCamAdditionalData != null)
                        {
                            volumeLayerMask = mainCamAdditionalData.volumeLayerMask;
                            volumeAnchor = mainCamAdditionalData.volumeAnchorOverride;
                            needFallback = false;
                        }
                    }

                    if (needFallback)
                    {
                        HDRenderPipeline hdPipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;
                        // If the override layer is "Everything", we fall-back to "Everything" for the current layer mask to avoid issues by having no current layer
                        // In practice we should never have "Everything" as an override mask as it does not make sense (a warning is issued in the UI)
                        if (hdPipeline.asset.currentPlatformRenderPipelineSettings.lightLoopSettings.skyLightingOverrideLayerMask == -1)
                            volumeLayerMask = -1;
                        else
                            // Remove lighting override mask and layer 31 which is used by preview/lookdev
                            volumeLayerMask = (-1 & ~(hdPipeline.asset.currentPlatformRenderPipelineSettings.lightLoopSettings.skyLightingOverrideLayerMask | (1 << 31)));
                    }
                }
            }

            // If no override is provided, use the camera transform.
            if (volumeAnchor == null)
                volumeAnchor = camera.transform;
        }

        public void GetPixelCoordToViewDirWS(Vector4 resolution, ref Matrix4x4[] transforms)
        {
            if (xr.singlePassEnabled)
            {
                for (int viewIndex = 0; viewIndex < viewCount; ++viewIndex)
                {
                    transforms[viewIndex] = ComputePixelCoordToWorldSpaceViewDirectionMatrix(xrViewConstants[viewIndex], resolution);
                }
            }
            else
            {
                transforms[0] = ComputePixelCoordToWorldSpaceViewDirectionMatrix(mainViewConstants, resolution);
            }
        }

        Matrix4x4 GetJitteredProjectionMatrix(Matrix4x4 origProj)
        {
            // Do not add extra jitter in VR (micro-variations from head tracking are enough)
            if (xr.enabled)
            {
                taaJitter = Vector4.zero;
                return origProj;
            }

            // The variance between 0 and the actual halton sequence values reveals noticeable
            // instability in Unity's shadow maps, so we avoid index 0.
            float jitterX = HaltonSequence.Get((taaFrameIndex & 1023) + 1, 2) - 0.5f;
            float jitterY = HaltonSequence.Get((taaFrameIndex & 1023) + 1, 3) - 0.5f;
            taaJitter = new Vector4(jitterX, jitterY, jitterX / m_ActualWidth, jitterY / m_ActualHeight);

            const int kMaxSampleCount = 8;
            if (++taaFrameIndex >= kMaxSampleCount)
                taaFrameIndex = 0;

            Matrix4x4 proj;

            if (camera.orthographic)
            {
                float vertical = camera.orthographicSize;
                float horizontal = vertical * camera.aspect;

                var offset = taaJitter;
                offset.x *= horizontal / (0.5f * m_ActualWidth);
                offset.y *= vertical / (0.5f * m_ActualHeight);

                float left = offset.x - horizontal;
                float right = offset.x + horizontal;
                float top = offset.y + vertical;
                float bottom = offset.y - vertical;

                proj = Matrix4x4.Ortho(left, right, bottom, top, camera.nearClipPlane, camera.farClipPlane);
            }
            else
            {
                var planes = origProj.decomposeProjection;

                float vertFov = Math.Abs(planes.top) + Math.Abs(planes.bottom);
                float horizFov = Math.Abs(planes.left) + Math.Abs(planes.right);

                var planeJitter = new Vector2(jitterX * horizFov / m_ActualWidth,
                    jitterY * vertFov / m_ActualHeight);

                planes.left += planeJitter.x;
                planes.right += planeJitter.x;
                planes.top += planeJitter.y;
                planes.bottom += planeJitter.y;

                proj = Matrix4x4.Frustum(planes);
            }

            return proj;
        }

        public Matrix4x4 ComputePixelCoordToWorldSpaceViewDirectionMatrix(ViewConstants viewConstants, Vector4 resolution)
        {
            // In XR mode, use a more generic matrix to account for asymmetry in the projection
            if (xr.enabled)
            {
                var transform = Matrix4x4.Scale(new Vector3(-1.0f, -1.0f, -1.0f)) * viewConstants.invViewProjMatrix;
                transform = transform * Matrix4x4.Scale(new Vector3(1.0f, -1.0f, 1.0f));
                transform = transform * Matrix4x4.Translate(new Vector3(-1.0f, -1.0f, 0.0f));
                transform = transform * Matrix4x4.Scale(new Vector3(2.0f * resolution.z, 2.0f * resolution.w, 1.0f));

                return transform.transpose;
            }

            float verticalFoV = camera.GetGateFittedFieldOfView() * Mathf.Deg2Rad;
            Vector2 lensShift = camera.GetGateFittedLensShift();

            return HDUtils.ComputePixelCoordToWorldSpaceViewDirectionMatrix(verticalFoV, lensShift, resolution, viewConstants.viewMatrix, false);
        }

        // Warning: different views can use the same camera!
        public long GetViewID()
        {
            long viewID = camera.GetInstanceID();
            // Make it positive.
            viewID += (-(long)int.MinValue) + 1;
            return viewID;
        }

        public void Reset()
        {
            isFirstFrame = true;
        }

        public void Dispose()
        {
            if (m_HistoryRTSystem != null)
            {
                m_HistoryRTSystem.Dispose();
                m_HistoryRTSystem = null;
            }
        }

        // BufferedRTHandleSystem API expects an allocator function. We define it here.
        static RTHandle HistoryBufferAllocatorFunction(string viewName, int frameIndex, RTHandleSystem rtHandleSystem)
        {
            frameIndex &= 1;
            var hdPipeline = (HDRenderPipeline)RenderPipelineManager.currentPipeline;

            return rtHandleSystem.Alloc(Vector2.one, TextureXR.slices, colorFormat: (GraphicsFormat)hdPipeline.currentPlatformRenderPipelineSettings.colorBufferFormat,
                                        dimension: TextureXR.dimension, enableRandomWrite: true, useMipMap: true, autoGenerateMips: false, useDynamicScale: true,
                                        name: string.Format("CameraColorBufferMipChain{0}", frameIndex));
        }

        // Pass all the systems that may want to initialize per-camera data here.
        // That way you will never create an HDCamera and forget to initialize the data.
        public static HDCamera GetOrCreate(Camera camera, int xrMultipassId = 0)
        {
            HDCamera hdCamera;

            if (!s_Cameras.TryGetValue((camera, xrMultipassId), out hdCamera))
            {
                hdCamera = new HDCamera(camera);
                s_Cameras.Add((camera, xrMultipassId), hdCamera);
            }

            return hdCamera;
        }

        public static void ClearAll()
        {
            foreach (var cam in s_Cameras)
            {
                cam.Value.ReleaseHistoryBuffer();
                cam.Value.Dispose();
            }

            s_Cameras.Clear();
            s_Cleanup.Clear();
        }

        // Look for any camera that hasn't been used in the last frame and remove them from the pool.
        public static void CleanUnused()
        {

            foreach (var key in s_Cameras.Keys)
            {
                var camera = s_Cameras[key];

                // Unfortunately, the scene view camera is always isActiveAndEnabled==false so we can't rely on this. For this reason we never release it (which should be fine in the editor)
                if (camera.camera != null && camera.camera.cameraType == CameraType.SceneView)
                    continue;

                bool hasPersistentHistory = camera.m_AdditionalCameraData != null && camera.m_AdditionalCameraData.hasPersistentHistory;
                // We keep preview camera around as they are generally disabled/enabled every frame. They will be destroyed later when camera.camera is null
                if (camera.camera == null || (!camera.camera.isActiveAndEnabled && camera.camera.cameraType != CameraType.Preview && !hasPersistentHistory))
                    s_Cleanup.Add(key);
            }

            foreach (var cam in s_Cleanup)
            {
                s_Cameras[cam].Dispose();
                s_Cameras.Remove(cam);
            }

            s_Cleanup.Clear();
        }

        // Set up UnityPerView CBuffer.
        public void SetupGlobalParams(CommandBuffer cmd, float time, float lastTime, int frameCount)
        {
            bool taaEnabled = m_frameSettings.IsEnabled(FrameSettingsField.Postprocess)
                && antialiasing == AntialiasingMode.TemporalAntialiasing
                && camera.cameraType == CameraType.Game;

            cmd.SetGlobalMatrix(HDShaderIDs._ViewMatrix,                mainViewConstants.viewMatrix);
            cmd.SetGlobalMatrix(HDShaderIDs._InvViewMatrix,             mainViewConstants.invViewMatrix);
            cmd.SetGlobalMatrix(HDShaderIDs._ProjMatrix,                mainViewConstants.projMatrix);
            cmd.SetGlobalMatrix(HDShaderIDs._InvProjMatrix,             mainViewConstants.invProjMatrix);
            cmd.SetGlobalMatrix(HDShaderIDs._ViewProjMatrix,            mainViewConstants.viewProjMatrix);
            cmd.SetGlobalMatrix(HDShaderIDs._InvViewProjMatrix,         mainViewConstants.invViewProjMatrix);
            cmd.SetGlobalMatrix(HDShaderIDs._NonJitteredViewProjMatrix, mainViewConstants.nonJitteredViewProjMatrix);
            cmd.SetGlobalMatrix(HDShaderIDs._PrevViewProjMatrix,        mainViewConstants.prevViewProjMatrix);
            cmd.SetGlobalMatrix(HDShaderIDs._PrevInvViewProjMatrix,     mainViewConstants.prevInvViewProjMatrix);
            cmd.SetGlobalMatrix(HDShaderIDs._CameraViewProjMatrix,      mainViewConstants.viewProjMatrix);
            cmd.SetGlobalVector(HDShaderIDs._WorldSpaceCameraPos,       mainViewConstants.worldSpaceCameraPos);
            cmd.SetGlobalVector(HDShaderIDs._PrevCamPosRWS,             mainViewConstants.prevWorldSpaceCameraPos);
            cmd.SetGlobalVector(HDShaderIDs._ScreenSize,                screenSize);
            cmd.SetGlobalVector(HDShaderIDs._RTHandleScale,             RTHandles.rtHandleProperties.rtHandleScale);
            cmd.SetGlobalVector(HDShaderIDs._RTHandleScaleHistory,      m_HistoryRTSystem.rtHandleProperties.rtHandleScale);
            cmd.SetGlobalVector(HDShaderIDs._ZBufferParams,             zBufferParams);
            cmd.SetGlobalVector(HDShaderIDs._ProjectionParams,          projectionParams);
            cmd.SetGlobalVector(HDShaderIDs.unity_OrthoParams,          unity_OrthoParams);
            cmd.SetGlobalVector(HDShaderIDs._ScreenParams,              screenParams);
            cmd.SetGlobalVector(HDShaderIDs._TaaFrameInfo,              new Vector4(taaSharpenStrength, 0, taaFrameIndex, taaEnabled ? 1 : 0));
            cmd.SetGlobalVector(HDShaderIDs._TaaJitterStrength,         taaJitter);
            cmd.SetGlobalVectorArray(HDShaderIDs._FrustumPlanes,        frustumPlaneEquations);


            // Time is also a part of the UnityPerView CBuffer.
            // Different views can have different values of the "Animated Materials" setting.
            bool animateMaterials = CoreUtils.AreAnimatedMaterialsEnabled(camera);

            // We also enable animated materials in previews so the shader graph main preview works with time parameters.
            animateMaterials |= camera.cameraType == CameraType.Preview;

            float  ct = animateMaterials ? time     : 0;
            float  pt = animateMaterials ? lastTime : 0;
            float  dt = Time.deltaTime;
            float sdt = Time.smoothDeltaTime;

            cmd.SetGlobalVector(HDShaderIDs._Time,                  new Vector4(ct * 0.05f, ct, ct * 2.0f, ct * 3.0f));
            cmd.SetGlobalVector(HDShaderIDs._SinTime,               new Vector4(Mathf.Sin(ct * 0.125f), Mathf.Sin(ct * 0.25f), Mathf.Sin(ct * 0.5f), Mathf.Sin(ct)));
            cmd.SetGlobalVector(HDShaderIDs._CosTime,               new Vector4(Mathf.Cos(ct * 0.125f), Mathf.Cos(ct * 0.25f), Mathf.Cos(ct * 0.5f), Mathf.Cos(ct)));
            cmd.SetGlobalVector(HDShaderIDs.unity_DeltaTime,        new Vector4(dt, 1.0f / dt, sdt, 1.0f / sdt));
            cmd.SetGlobalVector(HDShaderIDs._TimeParameters,        new Vector4(ct, Mathf.Sin(ct), Mathf.Cos(ct), 0.0f));
            cmd.SetGlobalVector(HDShaderIDs._LastTimeParameters,    new Vector4(pt, Mathf.Sin(pt), Mathf.Cos(pt), 0.0f));

            cmd.SetGlobalInt(HDShaderIDs._FrameCount,        frameCount);

            float exposureMultiplierForProbes = 1.0f / Mathf.Max(probeRangeCompressionFactor, 1e-6f);
            cmd.SetGlobalFloat(HDShaderIDs._ProbeExposureScale, exposureMultiplierForProbes);

            // TODO: qualify this code with xr.singlePassEnabled when compute shaders can use keywords
            if (true)
            {
                cmd.SetGlobalInt(HDShaderIDs._XRViewCount, viewCount);

                // Convert AoS to SoA for GPU constant buffer until we can use StructuredBuffer via command buffer
                for (int i = 0; i < viewCount; i++)
                {
                    xrViewMatrix[i] = xrViewConstants[i].viewMatrix;
                    xrInvViewMatrix[i] = xrViewConstants[i].invViewMatrix;
                    xrProjMatrix[i] = xrViewConstants[i].projMatrix;
                    xrInvProjMatrix[i] = xrViewConstants[i].invProjMatrix;
                    xrViewProjMatrix[i] = xrViewConstants[i].viewProjMatrix;
                    xrInvViewProjMatrix[i] = xrViewConstants[i].invViewProjMatrix;
                    xrNonJitteredViewProjMatrix[i] = xrViewConstants[i].nonJitteredViewProjMatrix;
                    xrPrevViewProjMatrix[i] = xrViewConstants[i].prevViewProjMatrix;
                    xrPrevInvViewProjMatrix[i] = xrViewConstants[i].prevInvViewProjMatrix;
                    xrPrevViewProjMatrixNoCameraTrans[i] = xrViewConstants[i].prevViewProjMatrixNoCameraTrans;
                    xrPixelCoordToViewDirWS[i] = xrViewConstants[i].pixelCoordToViewDirWS;
                    xrWorldSpaceCameraPos[i] = xrViewConstants[i].worldSpaceCameraPos;
                    xrWorldSpaceCameraPosViewOffset[i] = xrViewConstants[i].worldSpaceCameraPosViewOffset;
                    xrPrevWorldSpaceCameraPos[i] = xrViewConstants[i].prevWorldSpaceCameraPos;
                }

                cmd.SetGlobalMatrixArray(HDShaderIDs._XRViewMatrix, xrViewMatrix);
                cmd.SetGlobalMatrixArray(HDShaderIDs._XRInvViewMatrix, xrInvViewMatrix);
                cmd.SetGlobalMatrixArray(HDShaderIDs._XRProjMatrix, xrProjMatrix);
                cmd.SetGlobalMatrixArray(HDShaderIDs._XRInvProjMatrix, xrInvProjMatrix);
                cmd.SetGlobalMatrixArray(HDShaderIDs._XRViewProjMatrix, xrViewProjMatrix);
                cmd.SetGlobalMatrixArray(HDShaderIDs._XRInvViewProjMatrix, xrInvViewProjMatrix);
                cmd.SetGlobalMatrixArray(HDShaderIDs._XRNonJitteredViewProjMatrix, xrNonJitteredViewProjMatrix);
                cmd.SetGlobalMatrixArray(HDShaderIDs._XRPrevViewProjMatrix, xrPrevViewProjMatrix);
                cmd.SetGlobalMatrixArray(HDShaderIDs._XRPrevInvViewProjMatrix, xrPrevInvViewProjMatrix);
                cmd.SetGlobalMatrixArray(HDShaderIDs._XRPrevViewProjMatrixNoCameraTrans, xrPrevViewProjMatrixNoCameraTrans);
                cmd.SetGlobalMatrixArray(HDShaderIDs._XRPixelCoordToViewDirWS, xrPixelCoordToViewDirWS);
                cmd.SetGlobalVectorArray(HDShaderIDs._XRWorldSpaceCameraPos, xrWorldSpaceCameraPos);
                cmd.SetGlobalVectorArray(HDShaderIDs._XRWorldSpaceCameraPosViewOffset, xrWorldSpaceCameraPosViewOffset);
                cmd.SetGlobalVectorArray(HDShaderIDs._XRPrevWorldSpaceCameraPos, xrPrevWorldSpaceCameraPos);
            }

        }

        public RTHandle GetPreviousFrameRT(int id)
        {
            return m_HistoryRTSystem.GetFrameRT(id, 1);
        }

        public RTHandle GetCurrentFrameRT(int id)
        {
            return m_HistoryRTSystem.GetFrameRT(id, 0);
        }

        // Allocate buffers frames and return current frame
        public RTHandle AllocHistoryFrameRT(int id, Func<string, int, RTHandleSystem, RTHandle> allocator, int bufferCount)
        {
            m_HistoryRTSystem.AllocBuffer(id, (rts, i) => allocator(camera.name, i, rts), bufferCount);
            return m_HistoryRTSystem.GetFrameRT(id, 0);
        }

        public void AllocateAmbientOcclusionHistoryBuffer(float scaleFactor)
        {
            if (scaleFactor != m_AmbientOcclusionResolutionScale || GetCurrentFrameRT((int)HDCameraFrameHistoryType.AmbientOcclusion) == null)
            {
                ReleaseHistoryFrameRT((int)HDCameraFrameHistoryType.AmbientOcclusion);

                RTHandle Allocator(string id, int frameIndex, RTHandleSystem rtHandleSystem)
                {
                    return rtHandleSystem.Alloc(Vector2.one * scaleFactor, TextureXR.slices, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_UInt, dimension: TextureXR.dimension, useDynamicScale: true, enableRandomWrite: true, name: string.Format("AO Packed history_{0}", frameIndex));
                }

                AllocHistoryFrameRT((int)HDCameraFrameHistoryType.AmbientOcclusion, Allocator, 2);

                m_AmbientOcclusionResolutionScale = scaleFactor;
            }
        }

        public void ReleaseHistoryFrameRT(int id)
        {
            m_HistoryRTSystem.ReleaseBuffer(id);
        }

        void ReleaseHistoryBuffer()
        {
            m_HistoryRTSystem.ReleaseAll();
        }

        internal void ExecuteCaptureActions(RTHandle input, CommandBuffer cmd)
        {
            if (m_RecorderCaptureActions == null || !m_RecorderCaptureActions.MoveNext())
                return;

            // We need to blit to an intermediate texture because input resolution can be bigger than the camera resolution
            // Since recorder does not know about this, we need to send a texture of the right size.
            cmd.GetTemporaryRT(m_RecorderTempRT, actualWidth, actualHeight, 0, FilterMode.Point, input.rt.graphicsFormat);

            var blitMaterial = HDUtils.GetBlitMaterial(input.rt.dimension);

            var rtHandleScale = RTHandles.rtHandleProperties.rtHandleScale;
            Vector2 viewportScale = new Vector2(rtHandleScale.x, rtHandleScale.y);

            m_RecorderPropertyBlock.SetTexture(HDShaderIDs._BlitTexture, input);
            m_RecorderPropertyBlock.SetVector(HDShaderIDs._BlitScaleBias, viewportScale);
            m_RecorderPropertyBlock.SetFloat(HDShaderIDs._BlitMipLevel, 0);
            cmd.SetRenderTarget(m_RecorderTempRT);
            cmd.DrawProcedural(Matrix4x4.identity, blitMaterial, 0, MeshTopology.Triangles, 3, 1, m_RecorderPropertyBlock);

            for (m_RecorderCaptureActions.Reset(); m_RecorderCaptureActions.MoveNext();)
                m_RecorderCaptureActions.Current(m_RecorderTempRT, cmd);
        }

        class ExecuteCaptureActionsPassData
        {
            public RenderGraphResource input;
            public RenderGraphMutableResource tempTexture;
            public IEnumerator<Action<RenderTargetIdentifier, CommandBuffer>> recorderCaptureActions;
            public Vector2 viewportScale;
            public Material blitMaterial;
        }

        internal void ExecuteCaptureActions(RenderGraph renderGraph, RenderGraphResource input)
        {
            if (m_RecorderCaptureActions == null || !m_RecorderCaptureActions.MoveNext())
                return;

            using (var builder = renderGraph.AddRenderPass<ExecuteCaptureActionsPassData>("Execute Capture Actions", out var passData))
            {
                var inputDesc = renderGraph.GetTextureDesc(input);
                var rtHandleScale = renderGraph.rtHandleProperties.rtHandleScale;
                passData.viewportScale = new Vector2(rtHandleScale.x, rtHandleScale.y);
                passData.blitMaterial = HDUtils.GetBlitMaterial(inputDesc.dimension);
                passData.recorderCaptureActions = m_RecorderCaptureActions;
                passData.input = builder.ReadTexture(input);
                // We need to blit to an intermediate texture because input resolution can be bigger than the camera resolution
                // Since recorder does not know about this, we need to send a texture of the right size.
                passData.tempTexture = builder.WriteTexture(renderGraph.CreateTexture(new TextureDesc(actualWidth, actualHeight)
                    { colorFormat = inputDesc.colorFormat, name = "TempCaptureActions" }));

                builder.SetRenderFunc(
                (ExecuteCaptureActionsPassData data, RenderGraphContext ctx) =>
                {
                    var tempRT = ctx.resources.GetTexture(data.tempTexture);
                    var mpb = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                    mpb.SetTexture(HDShaderIDs._BlitTexture, ctx.resources.GetTexture(data.input));
                    mpb.SetVector(HDShaderIDs._BlitScaleBias, data.viewportScale);
                    mpb.SetFloat(HDShaderIDs._BlitMipLevel, 0);
                    ctx.cmd.SetRenderTarget(tempRT);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, data.blitMaterial, 0, MeshTopology.Triangles, 3, 1, mpb);

                    for (data.recorderCaptureActions.Reset(); data.recorderCaptureActions.MoveNext();)
                        data.recorderCaptureActions.Current(tempRT, ctx.cmd);
                });
            }
        }

        // VisualSky is the sky used for rendering in the main view.
        // LightingSky is the sky used for lighting the scene (ambient probe and sky reflection)
        // It's usually the visual sky unless a sky lighting override is setup.
        //      Ambient Probe: Only used if Ambient Mode is set to dynamic in the Visual Environment component. Updated according to the Update Mode parameter.
        //      (Otherwise it uses the one from the static lighting sky)
        //      Sky Reflection Probe : Always used and updated according to the Update Mode parameter.
        internal SkyUpdateContext   visualSky { get; private set; } = new SkyUpdateContext();
        internal SkyUpdateContext   lightingSky { get; private set; } = null;
        // We need to cache this here because it's need in SkyManager.SetupAmbientProbe
        // The issue is that this is called during culling which happens before Volume updates so we can't query it via volumes in there.
        internal SkyAmbientMode skyAmbientMode { get; private set; }
        internal SkyUpdateContext   m_LightingOverrideSky = new SkyUpdateContext();

        internal void UpdateCurrentSky(SkyManager skyManager)
        {
#if UNITY_EDITOR
            if (HDUtils.IsRegularPreviewCamera(camera))
            {
                visualSky.skySettings = skyManager.GetDefaultPreviewSkyInstance();
                lightingSky = visualSky;
                skyAmbientMode = SkyAmbientMode.Dynamic;
            }
            else
#endif
            {
                skyAmbientMode = VolumeManager.instance.stack.GetComponent<VisualEnvironment>().skyAmbientMode.value;

                visualSky.skySettings = SkyManager.GetSkySetting(VolumeManager.instance.stack);

                // Now, see if we have a lighting override
                // Update needs to happen before testing if the component is active other internal data structure are not properly updated yet.
                VolumeManager.instance.Update(skyManager.lightingOverrideVolumeStack, volumeAnchor, skyManager.lightingOverrideLayerMask);
                if (VolumeManager.instance.IsComponentActiveInMask<VisualEnvironment>(skyManager.lightingOverrideLayerMask))
                {
                    SkySettings newSkyOverride = SkyManager.GetSkySetting(skyManager.lightingOverrideVolumeStack);
                    if (m_LightingOverrideSky.skySettings != null && newSkyOverride == null)
                    {
                        // When we switch from override to no override, we need to make sure that the visual sky will actually be properly re-rendered.
                        // Resetting the visual sky hash will ensure that.
                        visualSky.skyParametersHash = -1;
                    }

                    m_LightingOverrideSky.skySettings = newSkyOverride;
                    lightingSky = m_LightingOverrideSky;

                }
                else
                {
                    lightingSky = visualSky;
                }
            }
        }
    }
}
