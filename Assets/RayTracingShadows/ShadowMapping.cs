using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteInEditMode]
public class ShadowMapping : MonoBehaviour
{
    public ComputeShader shadowMappingCS = null;

    public Light dirLight = null;

    public Material shadowMapBlitMat = null;

    [Range(0, 1)]
    public float shadowSpread = 0.01f;

    private uint cameraWidth = 0;
    private uint cameraHeight = 0;

    private RenderTexture shadowMapTexture = null;

    private RayTracingAccelerationStructure rtas = null;

    private Camera cam;

    private int frameIndex = 0;
    private int temporalAccumulationStep = 0;
    private Matrix4x4 prevCameraMatrix = Matrix4x4.identity;
    private Matrix4x4 prevProjMatrix = Matrix4x4.identity;
    private Matrix4x4 prevLightMatrix = Matrix4x4.identity;
    private float prevShadowSpread = 0.0f;

    private void ReleaseResources()
    {
        if (rtas != null)
        {
            rtas.Release();
            rtas = null;
        }

        if (shadowMapTexture)
        {
            shadowMapTexture.Release();
            shadowMapTexture = null;
        }

        cameraWidth = 0;
        cameraHeight = 0;
    }

    private void CreateResources()
    {
        if (rtas == null)
        {
            RayTracingAccelerationStructure.Settings settings = new RayTracingAccelerationStructure.Settings();
            settings.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything;
            settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Manual;
            settings.layerMask = 255;

            rtas = new RayTracingAccelerationStructure(settings);
        }

        if (cameraWidth != Camera.main.pixelWidth || cameraHeight != Camera.main.pixelHeight)
        {
            if (shadowMapTexture)
                shadowMapTexture.Release();

            shadowMapTexture = new RenderTexture(Camera.main.pixelWidth, Camera.main.pixelHeight, 0, RenderTextureFormat.RHalf);
            shadowMapTexture.enableRandomWrite = true;
            shadowMapTexture.Create();

            cameraWidth = (uint)Camera.main.pixelWidth;
            cameraHeight = (uint)Camera.main.pixelHeight;

            temporalAccumulationStep = 0;
        }
    }
    
    void OnDisable()
    {
        ReleaseResources();

        if (cam != null)
        {
            cam.RemoveAllCommandBuffers();
        }
    }
    private void Update()
    {
        CreateResources();
    }    

    private void OnEnable()
    {
        cam = Camera.main;
        if (cam)
            cam.depthTextureMode = DepthTextureMode.Depth;

        frameIndex = 0;
    }

    void OnPreRender()
    {
        if (!SystemInfo.supportsInlineRayTracing)
        {
            Debug.Log("Ray Queries (DXR 1.1) are not supported by this GPU or by the current graphics API.");
            return;
        }

        if (dirLight == null || dirLight.type != UnityEngine.LightType.Directional)
        {
            Debug.Log("Please assign a Directional Light.");
            return;
        }

        if (rtas == null)
            return;

        if (!cam)
            return;

        if (frameIndex == 0)
        {
            temporalAccumulationStep = 0;
            prevLightMatrix = dirLight.transform.localToWorldMatrix;
            prevCameraMatrix = cam.cameraToWorldMatrix;
            prevProjMatrix = cam.projectionMatrix;
            prevShadowSpread = shadowSpread;
        }

        RayTracingInstanceCullingConfig cullingConfig = new RayTracingInstanceCullingConfig();

        cullingConfig.flags = RayTracingInstanceCullingFlags.EnableLODCulling;

        cullingConfig.lodParameters.fieldOfView = cam.fieldOfView;
        cullingConfig.lodParameters.cameraPixelHeight = cam.pixelHeight;
        cullingConfig.lodParameters.isOrthographic = false;
        cullingConfig.lodParameters.cameraPosition = cam.transform.position;

        cullingConfig.subMeshFlagsConfig.opaqueMaterials = RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly;
        cullingConfig.subMeshFlagsConfig.transparentMaterials = RayTracingSubMeshFlags.Disabled;
        cullingConfig.subMeshFlagsConfig.alphaTestedMaterials = RayTracingSubMeshFlags.Disabled;

        List<RayTracingInstanceCullingTest> instanceTests = new List<RayTracingInstanceCullingTest>();

        RayTracingInstanceCullingTest instanceTest = new RayTracingInstanceCullingTest();
        instanceTest.allowOpaqueMaterials = true;
        instanceTest.allowAlphaTestedMaterials = true;
        instanceTest.allowTransparentMaterials = false;
        instanceTest.layerMask = -1;
        instanceTest.shadowCastingModeMask = (1 << (int)ShadowCastingMode.On) | (1 << (int)ShadowCastingMode.TwoSided);
        instanceTest.instanceMask = 1 << 0;

        instanceTests.Add(instanceTest);

        cullingConfig.instanceTests = instanceTests.ToArray();

        rtas.ClearInstances();
        RayTracingInstanceCullingResults cullingResults = rtas.CullInstances(ref cullingConfig);

        int kernelIndex = shadowMappingCS.FindKernel("CSMain");
        if (kernelIndex == -1)
            return;

        if (!shadowMappingCS.IsSupported(kernelIndex))
        {
            Debug.Log("Compute shader " + shadowMappingCS.name + " failed to compile or is not supported.");
            return;
        }

        uint threadGroupSizeX;
        uint threadGroupSizeY;
        uint threadGroupSizeZ;
        shadowMappingCS.GetKernelThreadGroupSizes(kernelIndex, out threadGroupSizeX, out threadGroupSizeY, out threadGroupSizeZ);

        cam.RemoveAllCommandBuffers();

        CommandBuffer cmdBufferDirLight = new CommandBuffer();
        cmdBufferDirLight.name = "RT DirLight";

        cmdBufferDirLight.BuildRayTracingAccelerationStructure(rtas);

        float invHalfTanFOV = 1.0f / Mathf.Tan(Mathf.Deg2Rad * cam.fieldOfView * 0.5f);
        float aspectRatio = cam.pixelHeight / (float)cam.pixelWidth;

        Vector4 DepthToViewParams = new Vector4(
                2.0f / (invHalfTanFOV * aspectRatio * cam.pixelWidth),
                2.0f / (invHalfTanFOV * cam.pixelHeight),
                1.0f / (invHalfTanFOV * aspectRatio),
                1.0f / invHalfTanFOV
                );

        if (prevCameraMatrix != cam.cameraToWorldMatrix ||
            prevProjMatrix != cam.projectionMatrix ||
            prevLightMatrix != dirLight.transform.localToWorldMatrix || 
            prevShadowSpread != shadowSpread ||
            cullingResults.transformsChanged)
        {
            temporalAccumulationStep = 0;
        }

        cmdBufferDirLight.SetComputeVectorParam(shadowMappingCS, "g_DepthToViewParams", DepthToViewParams);
        cmdBufferDirLight.SetComputeIntParam(shadowMappingCS, "g_FrameIndex", frameIndex);
        cmdBufferDirLight.SetComputeVectorParam(shadowMappingCS, "g_LightDir", dirLight.transform.forward);
        cmdBufferDirLight.SetComputeFloatParam(shadowMappingCS, "g_ShadowSpread", shadowSpread * 0.1f);
        cmdBufferDirLight.SetRayTracingAccelerationStructure(shadowMappingCS, kernelIndex, "g_AccelStruct", rtas);
        cmdBufferDirLight.SetComputeTextureParam(shadowMappingCS, kernelIndex, "g_Output", shadowMapTexture);
        cmdBufferDirLight.SetComputeIntParam(shadowMappingCS, "g_TemporalAccumulationStep", temporalAccumulationStep);

        Matrix4x4 lightMatrix = dirLight.transform.localToWorldMatrix;
        lightMatrix.SetColumn(2, -lightMatrix.GetColumn(2));
        cmdBufferDirLight.SetComputeMatrixParam(shadowMappingCS, "g_LightMatrix", lightMatrix);

        cmdBufferDirLight.DispatchCompute(shadowMappingCS, kernelIndex, (int)((cam.pixelWidth + threadGroupSizeX + 1) / threadGroupSizeX),(int)((cam.pixelHeight + threadGroupSizeY + 1) / threadGroupSizeY), 1);

        cmdBufferDirLight.Blit(shadowMapTexture, BuiltinRenderTextureType.CurrentActive, shadowMapBlitMat);

        cam.AddCommandBuffer(CameraEvent.AfterFinalPass, cmdBufferDirLight);

        prevCameraMatrix = cam.cameraToWorldMatrix;
        prevProjMatrix = cam.projectionMatrix;
        prevLightMatrix = dirLight.transform.localToWorldMatrix;
        prevShadowSpread = shadowSpread;

        temporalAccumulationStep++;
        frameIndex++;
    }
}
