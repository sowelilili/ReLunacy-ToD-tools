using System.Numerics;
using ReLunacy.Engine.Rendering;
using Vortice.Vulkan;
using NeoVeldrid;

namespace ReLunacy.Engine.Rendering.Vulkan;

/// <summary>
/// Draws a level's static collision as an overlay, the way the navmesh is drawn: its own pipeline in
/// the load pass after the scene, uploaded once per level and toggled live (see CollisionGeometry for
/// how the buffers are laid out and why they are instanced).
///
/// Unlike the navmesh surface this one WRITES depth. Collision is a closed, mostly opaque mass of
/// overlapping shells; without depth writes, whatever was drawn last would cover the faces in front of
/// it. It still blends over the level, so lowering the opacity shows the art through the collision
/// without showing the collision through itself. It is biased towards the viewer, since collision
/// usually sits exactly on the art it was built from and would otherwise z-fight it.
///
/// Faces are flat-shaded from their screen-space derivatives and outlined from their barycentrics
/// (taken from gl_VertexIndex % 3; the pool is unshared), so no edge geometry is needed: a level's
/// collision has millions of edges, which the navmesh's instanced thin boxes could not afford.
/// Hiding a category, a primitive kind, a region, a material or a triangle-flag value collapses the
/// vertices in the vertex shader rather than rebuilding the batches; so does the editor's per-instance
/// hidden bit (the instance-flag filter), and its selected bits tint the faces in edit mode.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    // uParams: x opacity, y 1 = outlines, z 1 = edit mode (selection highlighted), w wallbounce minimum tilt.
    // uFlags: x visible categories (CollisionCategories), y colour mode (CollisionColorMode), z visible
    // regions (bit N = the Nth region given to CollisionGeometry.Build), w visible primitive kinds.
    // uMat0/uMat1: visible materials, bit m of the 256. uMisc.x: visible triangle-flag values, bit f of 32.
    private const string CollisionPushGlsl = @"layout(push_constant) uniform PC { vec4 uParams; uvec4 uFlags; uvec4 uMat0; uvec4 uMat1; uvec4 uMisc; vec4 uSelColor; };";
    private const int CollisionPushSize = 96;

    private const string CollisionVertexGlsl = @"#version 450
layout(set = 0, binding = 0) uniform Mvp { mat4 uMvp; };
" + CollisionPushGlsl + @"
layout(location = 0) in vec3 inPos;
layout(location = 1) in uint inAttr;
layout(location = 2) in vec4 inRow0;
layout(location = 3) in vec4 inRow1;
layout(location = 4) in vec4 inRow2;
layout(location = 5) in vec4 inRow3;
layout(location = 6) in uint inInstance;
layout(location = 7) in uint inRegions;
layout(location = 8) in uint inWallbounceBase;
layout(location = 9) in uint inWallbounceOn;
// Wallbounce tilts, one per placed mesh triangle (CollisionWallbounce): placement i's triangle t is at
// int(inWallbounceBase) + gl_VertexIndex / 3, the base already less its shape's first triangle.
layout(set = 1, binding = 0, std430) readonly buffer Wallbounce { uint uTilt[]; };
layout(location = 0) out vec3 vWorld;
layout(location = 1) out vec3 vBary;
layout(location = 2) flat out vec3 vColor;
layout(location = 3) flat out float vSelected;
// Bits 0-4 triangle flags, 5-7 shape kind, 8-15 wallbounce tilt (degrees, 0 = none).
layout(location = 4) flat out uint vFace;

// Mirror CollisionGeometry.IdColor / MaterialColor / FlagColor / the Type colours.
vec3 hueColour(float h, float saturation) {
    vec3 rgb = clamp(vec3(abs(h * 6.0 - 3.0) - 1.0, 2.0 - abs(h * 6.0 - 2.0), 2.0 - abs(h * 6.0 - 4.0)), 0.0, 1.0);
    return 0.95 * mix(vec3(1.0), rgb, saturation);
}
vec3 idColour(uint id) { return hueColour(fract(0.08 + float(id) * 0.618034), 0.7); }
vec3 materialColour(uint m) { return hueColour(fract(float(m) * 0.618034), 0.6); }
vec3 flagColour(uint f) {
    return f == 0u ? vec3(0.3) : vec3((f & 1u) != 0u ? 1.0 : 0.15, (f & 2u) != 0u ? 1.0 : 0.15, (f & 4u) != 0u ? 1.0 : 0.15);
}
vec3 typeColour(uint kind, bool tie) {
    if (kind == 0u) return tie ? vec3(1.0, 0.62, 0.18) : vec3(0.30, 0.78, 0.42);
    if (kind == 1u) return vec3(0.25, 0.55, 1.0);
    if (kind == 2u) return vec3(0.85, 0.35, 0.95);
    if (kind == 3u) return vec3(1.0, 0.32, 0.36);
    return vec3(0.95, 0.92, 0.30);
}

void main()
{
    uint kind = (inAttr >> 13) & 7u;
    bool tie = (inInstance & 1u) != 0u;
    uint category = kind != 0u ? 4u : (tie ? 2u : 1u);
    // Hidden: by category, region, the instance-flag filter (bit 2, set by the editor), and per face by
    // material and triangle flags (meshes) or primitive kind. A face's three corners share their
    // attributes, so collapsing each one collapses the face.
    bool visible = (uFlags.x & category) != 0u && (uFlags.z & inRegions) != 0u && (inInstance & 4u) == 0u;
    if (kind == 0u)
    {
        uint material = inAttr & 0xFFu;
        uint word = material < 128u ? uMat0[material >> 5] : uMat1[(material >> 5) - 4u];
        visible = visible && (word & (1u << (material & 31u))) != 0u && (uMisc.x & (1u << ((inAttr >> 8) & 0x1Fu))) != 0u;
    }
    else
    {
        visible = visible && (uFlags.w & (1u << (kind - 1u))) != 0u;
    }
    if (!visible) { gl_Position = vec4(0.0, 0.0, 0.0, 1.0); return; }

    mat4 model = mat4(inRow0, inRow1, inRow2, inRow3);
    vec4 world = model * vec4(inPos, 1.0);
    vWorld = world.xyz;
    int corner = gl_VertexIndex % 3;
    vBary = vec3(corner == 0 ? 1.0 : 0.0, corner == 1 ? 1.0 : 0.0, corner == 2 ? 1.0 : 0.0);
    vSelected = uParams.z > 0.5 && ((inAttr & 0x10000u) != 0u || (inInstance & 2u) != 0u) ? 1.0 : 0.0;

    uint mode = uFlags.y;
    uint flags = kind == 0u ? (inAttr >> 8) & 0x1Fu : 0u;
    uint tilt = kind == 0u && inWallbounceOn != 0u ? min(uTilt[int(inWallbounceBase) + gl_VertexIndex / 3], 255u) : 0u;
    vFace = flags | (kind << 5) | (tilt << 8);
    // Bit 17: a primitive that carries a material (a Moby's, see MobyCollisionGeometry).
    if (mode == 1u)      vColor = kind == 0u || (inAttr & 0x20000u) != 0u ? materialColour(inAttr & 0xFFu) : vec3(0.5);
    else if (mode == 2u) vColor = kind == 0u ? flagColour(flags) : vec3(0.5);
    else if (mode == 3u) vColor = idColour(inInstance >> 16);
    else if (mode >= 4u) vColor = vec3(0.25); // WrenchBoost / Wallbounce: the fragment shader picks the spots
    else                 vColor = typeColour(kind, tie);
    gl_Position = uMvp * world;
}";

    private const string CollisionFragmentGlsl = @"#version 450
" + CollisionPushGlsl + @"
layout(location = 0) in vec3 vWorld;
layout(location = 1) in vec3 vBary;
layout(location = 2) flat in vec3 vColor;
layout(location = 3) flat in float vSelected;
layout(location = 4) flat in uint vFace;
layout(location = 0) out vec4 o;
void main()
{
    // Flat face normal from the position's derivatives. The shade uses |N.L| so both sides of a face
    // look the same: collision is often seen from behind (Tie shells, the camera inside a mesh).
    // A face seen edge-on - which is what the camera passing through one does - has parallel
    // derivatives, so their cross product is zero and normalize() would return NaN. That NaN went into
    // the scene colour and from there into bloom's history, where it never left: a black square that
    // grew over the viewport until the level was reloaded. Fall back to an arbitrary normal instead.
    vec3 c = cross(dFdx(vWorld), dFdy(vWorld));
    float len2 = dot(c, c);
    vec3 n = len2 > 1e-30 ? c * inversesqrt(len2) : vec3(0.0, 1.0, 0.0);
    float shade = 0.45 + 0.55 * abs(dot(n, normalize(vec3(0.35, 1.0, 0.55))));
    vec3 rgb = vColor * shade;
    uint flags = vFace & 0x1Fu;
    bool mesh = ((vFace >> 5) & 7u) == 0u;
    float tilt = float(vFace >> 8);
    if (uFlags.y == 4u && mesh && (flags & 1u) != 0u && abs(n.y) >= 0.99985) // cos(1 degree)
        rgb = vec3(1.0, 0.55, 0.0);
    else if (uFlags.y == 5u && tilt > 0.0 && tilt >= uParams.w)
        rgb = mix(vec3(1.0, 0.9, 0.1), vec3(1.0, 0.1, 0.05), clamp((tilt - 6.0) / 44.0, 0.0, 1.0)) * (0.55 + 0.45 * shade);
    // Outlines: black within about a pixel and a half of an edge. fwidth is taken unconditionally,
    // since derivatives are undefined inside non-uniform control flow. It is zero on an edge-on face,
    // and smoothstep with equal edges is undefined, hence the floor.
    vec3 width = max(fwidth(vBary), vec3(1e-6));
    vec3 inside = smoothstep(width * 0.5, width * 1.5, vBary);
    float edge = min(min(inside.x, inside.y), inside.z);
    if (uParams.y > 0.5) rgb *= edge;
    // Premultiplied, with the real alpha in A for the blend (see the navmesh surface's note).
    float alpha = uParams.x;
    // Selected (edit mode): tinted with the selection colour and drawn opaque with a bright rim, so a
    // selection reads through any opacity setting.
    if (vSelected > 0.5)
    {
        rgb = mix(mix(rgb, uSelColor.rgb, 0.65), uSelColor.rgb, 1.0 - edge);
        alpha = 1.0;
    }
    o = vec4(rgb * alpha, alpha);
}";

    private VkShaderModule _colVs, _colFs;
    private VkPipelineLayout _colLayout;
    private VkPipeline _colPipeline;
    // Set 1: the wallbounce tilt buffer (a one-word placeholder until SetCollisionWallbounce).
    private VkDescriptorSetLayout _colWbSetLayout;
    private VkDescriptorPool _colWbPool;
    private VkDescriptorSet _colWbSet;
    private VkBuffer _colWbBuffer;
    private VkDeviceMemory _colWbMemory;

    private VkBuffer _colVertexBuffer, _colInstanceBuffer;
    private VkDeviceMemory _colVbMemory, _colInstanceMemory;
    // Kept mapped (host-coherent) so the collision editor can rewrite a face's or an instance's
    // attribute word in place. The race with a frame in flight only shows as a one-frame-late highlight.
    private CollisionVertex* _colVertexMapped;
    private CollisionInstanceData* _colInstanceMapped;
    private int _colVertexCount, _colInstanceCount;
    private CollisionBatch[] _colBatches = [];

    public bool CollisionVisible { get; set; }

    /// <summary>0..1; at 1 the collision hides the art behind it.</summary>
    public float CollisionOpacity { get; set; } = 0.85f;

    public bool CollisionOutlines { get; set; } = true;

    public CollisionColorMode CollisionColorMode { get; set; } = CollisionColorMode.Type;

    public CollisionCategories CollisionCategories { get; set; } = CollisionCategories.All;

    /// <summary>Bit N shows the Nth region's collision (see CollisionGeometry.Build).</summary>
    public uint CollisionRegionMask { get; set; } = uint.MaxValue;

    public CollisionPrimitiveKinds CollisionPrimitiveKinds { get; set; } = CollisionPrimitiveKinds.All;

    /// <summary>Bit m shows material m (mesh faces). Eight words, 256 materials.</summary>
    public uint[] CollisionMaterialMask { get; } = [.. Enumerable.Repeat(uint.MaxValue, 8)];

    /// <summary>Bit f shows the faces whose five triangle-flag bits equal f.</summary>
    public uint CollisionTriangleFlagMask { get; set; } = uint.MaxValue;

    /// <summary>Highlights the faces and instances the editor marked selected.</summary>
    public bool CollisionEditMode { get; set; }

    public Vector4 CollisionSelectionColor { get; set; } = new(1f, 0.85f, 0.2f, 1f);

    /// <summary>Smallest tilt from vertical, in degrees, that <see cref="CollisionColorMode.Wallbounce"/> highlights.</summary>
    public float WallbounceMinTilt { get; set; } = 20f;

    /// <summary>True once collision has been uploaded, so the UI can grey out its toggle on a level
    /// that has none.</summary>
    public bool HasCollision => _colBatches.Length > 0;

    private void CreateCollisionPipeline()
    {
        _colVs = Module(CollisionVertexGlsl, ShaderStages.Vertex);
        _colFs = Module(CollisionFragmentGlsl, ShaderStages.Fragment);

        CreateCollisionWallbounceSet();
        VkDescriptorSetLayout* setLayouts = stackalloc VkDescriptorSetLayout[2] { _descLayout, _colWbSetLayout };
        var push = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, offset = 0, size = CollisionPushSize };
        var layoutInfo = new VkPipelineLayoutCreateInfo { setLayoutCount = 2, pSetLayouts = setLayouts, pushConstantRangeCount = 1, pPushConstantRanges = &push };
        VkPipelineLayout layout;
        Check(_api.vkCreatePipelineLayout(&layoutInfo, &layout), "vkCreatePipelineLayout(collision)");
        _colLayout = layout;

        var entry = "main"u8;
        fixed (byte* name = entry)
        {
            VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Vertex, module = _colVs, pName = name };
            stages[1] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Fragment, module = _colFs, pName = name };

            VkVertexInputBindingDescription* binds = stackalloc VkVertexInputBindingDescription[2];
            binds[0] = new VkVertexInputBindingDescription { binding = 0, stride = (uint)sizeof(CollisionVertex), inputRate = VkVertexInputRate.Vertex };
            binds[1] = new VkVertexInputBindingDescription { binding = 1, stride = (uint)sizeof(CollisionInstanceData), inputRate = VkVertexInputRate.Instance };
            VkVertexInputAttributeDescription* attrs = stackalloc VkVertexInputAttributeDescription[10];
            attrs[0] = new VkVertexInputAttributeDescription { location = 0, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 0 };
            attrs[1] = new VkVertexInputAttributeDescription { location = 1, binding = 0, format = VkFormat.R32Uint, offset = 12 };
            for (uint r = 0; r < 4; r++)
                attrs[r + 2] = new VkVertexInputAttributeDescription { location = r + 2, binding = 1, format = VkFormat.R32G32B32A32Sfloat, offset = r * 16 };
            attrs[6] = new VkVertexInputAttributeDescription { location = 6, binding = 1, format = VkFormat.R32Uint, offset = 64 };
            attrs[7] = new VkVertexInputAttributeDescription { location = 7, binding = 1, format = VkFormat.R32Uint, offset = 68 };
            // CollisionInstanceData.WallbounceBase / WallbounceOn (see SetCollisionWallbounce).
            attrs[8] = new VkVertexInputAttributeDescription { location = 8, binding = 1, format = VkFormat.R32Uint, offset = 72 };
            attrs[9] = new VkVertexInputAttributeDescription { location = 9, binding = 1, format = VkFormat.R32Uint, offset = 76 };
            var vin = new VkPipelineVertexInputStateCreateInfo
            {
                vertexBindingDescriptionCount = 2,
                pVertexBindingDescriptions = binds,
                vertexAttributeDescriptionCount = 10,
                pVertexAttributeDescriptions = attrs,
            };

            var ia = new VkPipelineInputAssemblyStateCreateInfo { topology = VkPrimitiveTopology.TriangleList };
            var vp = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
            // Reversed depth (GreaterOrEqual): a negative bias pulls the collision towards the viewer,
            // off the art it usually coincides with.
            var raster = new VkPipelineRasterizationStateCreateInfo
            {
                polygonMode = VkPolygonMode.Fill,
                cullMode = VkCullModeFlags.None,
                frontFace = VkFrontFace.CounterClockwise,
                lineWidth = 1f,
                depthBiasEnable = true,
                depthBiasConstantFactor = 0f,
                depthBiasSlopeFactor = -1.5f,
                depthBiasClamp = 0f,
            };
            var ms = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };
            var depth = new VkPipelineDepthStencilStateCreateInfo { depthTestEnable = true, depthWriteEnable = true, depthCompareOp = VkCompareOp.GreaterOrEqual };
            // The colour write mask drops A, so the opaque target's alpha (the bloom glow mask) is kept.
            var blendAttach = new VkPipelineColorBlendAttachmentState
            {
                blendEnable = true,
                srcColorBlendFactor = VkBlendFactor.One,
                dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                colorBlendOp = VkBlendOp.Add,
                srcAlphaBlendFactor = VkBlendFactor.Zero,
                dstAlphaBlendFactor = VkBlendFactor.One,
                alphaBlendOp = VkBlendOp.Add,
                colorWriteMask = VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B,
            };
            var blend = new VkPipelineColorBlendStateCreateInfo { attachmentCount = 1, pAttachments = &blendAttach };
            VkDynamicState* dyn = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynState = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 2, pDynamicStates = dyn };
            var info = new VkGraphicsPipelineCreateInfo
            {
                stageCount = 2, pStages = stages, pVertexInputState = &vin, pInputAssemblyState = &ia,
                pViewportState = &vp, pRasterizationState = &raster, pMultisampleState = &ms,
                pDepthStencilState = &depth, pColorBlendState = &blend, pDynamicState = &dynState,
                layout = _colLayout, renderPass = _rpOpaque, subpass = 0,
            };
            VkPipeline p;
            Check(_api.vkCreateGraphicsPipelines(default, 1, &info, &p), "vkCreateGraphicsPipelines(collision)");
            _colPipeline = p;
        }
    }

    /// <summary>
    /// Uploads the level's collision once (see CollisionGeometry.Build). Passing empty spans frees what
    /// was uploaded, which is what a level without collision wants.
    /// </summary>
    public void SetCollision(ReadOnlySpan<CollisionVertex> vertices, ReadOnlySpan<CollisionInstanceData> instances, ReadOnlySpan<CollisionBatch> batches)
    {
        WaitForPendingFrames();
        DestroyCollisionBuffers();
        _colBatches = [];
        if (vertices.IsEmpty || instances.IsEmpty || batches.IsEmpty) return;

        ulong vSize = (ulong)vertices.Length * (ulong)sizeof(CollisionVertex);
        (_colVertexBuffer, _colVbMemory) = CreateBuffer(vSize, VkBufferUsageFlags.VertexBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* vp;
        Check(_api.vkMapMemory(_colVbMemory, 0, vSize, 0, &vp), "vkMapMemory(collisionVb)");
        vertices.CopyTo(new Span<CollisionVertex>(vp, vertices.Length));
        _colVertexMapped = (CollisionVertex*)vp;
        _colVertexCount = vertices.Length;

        ulong iSize = (ulong)instances.Length * (ulong)sizeof(CollisionInstanceData);
        (_colInstanceBuffer, _colInstanceMemory) = CreateBuffer(iSize, VkBufferUsageFlags.VertexBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* ip;
        Check(_api.vkMapMemory(_colInstanceMemory, 0, iSize, 0, &ip), "vkMapMemory(collisionInstances)");
        instances.CopyTo(new Span<CollisionInstanceData>(ip, instances.Length));
        _colInstanceMapped = (CollisionInstanceData*)ip;
        _colInstanceCount = instances.Length;

        _colBatches = batches.ToArray();
    }

    private void CreateCollisionWallbounceSet()
    {
        var binding = new VkDescriptorSetLayoutBinding { binding = 0, descriptorType = VkDescriptorType.StorageBuffer, descriptorCount = 1, stageFlags = VkShaderStageFlags.Vertex };
        var layoutInfo = new VkDescriptorSetLayoutCreateInfo { bindingCount = 1, pBindings = &binding };
        VkDescriptorSetLayout setLayout;
        Check(_api.vkCreateDescriptorSetLayout(&layoutInfo, &setLayout), "vkCreateDescriptorSetLayout(collisionWallbounce)");
        _colWbSetLayout = setLayout;

        var size = new VkDescriptorPoolSize { type = VkDescriptorType.StorageBuffer, descriptorCount = 1 };
        var poolInfo = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 1, pPoolSizes = &size };
        VkDescriptorPool pool;
        Check(_api.vkCreateDescriptorPool(&poolInfo, &pool), "vkCreateDescriptorPool(collisionWallbounce)");
        _colWbPool = pool;

        var alloc = new VkDescriptorSetAllocateInfo { descriptorPool = _colWbPool, descriptorSetCount = 1, pSetLayouts = &setLayout };
        VkDescriptorSet set;
        Check(_api.vkAllocateDescriptorSets(&alloc, &set), "vkAllocateDescriptorSets(collisionWallbounce)");
        _colWbSet = set;
        UploadCollisionWallbounceWords([0u]);
    }

    private void UploadCollisionWallbounceWords(ReadOnlySpan<uint> words)
    {
        if (_colWbBuffer.Handle != 0) { _api.vkDestroyBuffer(_colWbBuffer); _colWbBuffer = default; }
        if (_colWbMemory.Handle != 0) { _api.vkFreeMemory(_colWbMemory); _colWbMemory = default; }
        ulong size = (ulong)Math.Max(words.Length, 1) * sizeof(uint);
        (_colWbBuffer, _colWbMemory) = CreateBuffer(size, VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* p;
        Check(_api.vkMapMemory(_colWbMemory, 0, size, 0, &p), "vkMapMemory(collisionWallbounce)");
        words.CopyTo(new Span<uint>(p, words.Length));
        _api.vkUnmapMemory(_colWbMemory);
        var info = new VkDescriptorBufferInfo { buffer = _colWbBuffer, offset = 0, range = size };
        var write = new VkWriteDescriptorSet { dstSet = _colWbSet, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.StorageBuffer, pBufferInfo = &info };
        _api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    /// <summary>
    /// Hands the overlay the wallbounce analysis (CollisionWallbounce.Words): one tilt word per placed mesh
    /// triangle, and per uploaded instance its base into them (already less its shape's first triangle),
    /// or -1 for an instance without results. Cleared by the next <see cref="SetCollision"/>.
    /// </summary>
    public void SetCollisionWallbounce(ReadOnlySpan<uint> words, ReadOnlySpan<int> instanceBases)
    {
        if (_colInstanceMapped == null || instanceBases.Length != _colInstanceCount) return;
        WaitForPendingFrames();
        UploadCollisionWallbounceWords(words);
        for (int i = 0; i < instanceBases.Length; i++)
        {
            _colInstanceMapped[i].WallbounceBase = unchecked((uint)instanceBases[i]);
            _colInstanceMapped[i].WallbounceOn = instanceBases[i] == -1 ? 0u : 1u;
        }
    }

    /// <summary>Rewrites the attribute word of pool vertices [first, first + attributes.Length).</summary>
    public void UpdateCollisionVertexAttributes(uint first, ReadOnlySpan<uint> attributes)
    {
        if (_colVertexMapped == null || first + (ulong)attributes.Length > (ulong)_colVertexCount) return;
        for (int i = 0; i < attributes.Length; i++)
        {
            CollisionVertex* v = _colVertexMapped + first + i;
            *v = new CollisionVertex(v->Position, attributes[i]);
        }
    }

    /// <summary>Rewrites one instance's matrix (a Tie that moved carries its collision with it).</summary>
    public void UpdateCollisionInstanceTransform(int index, Matrix4x4 transform)
    {
        if (_colInstanceMapped == null || (uint)index >= (uint)_colInstanceCount) return;
        _colInstanceMapped[index].Transform = transform;
    }

    /// <summary>Rewrites one instance's attribute word.</summary>
    public void UpdateCollisionInstanceAttributes(int index, uint attributes)
    {
        if (_colInstanceMapped == null || (uint)index >= (uint)_colInstanceCount) return;
        _colInstanceMapped[index].Attributes = attributes;
    }

    private void DrawCollision()
    {
        if (!CollisionVisible || _colBatches.Length == 0 || CollisionCategories == CollisionCategories.None || CollisionRegionMask == 0) return;

        VkDescriptorSet s0 = _descSet;
        byte* pc = stackalloc byte[CollisionPushSize];
        *(Vector4*)pc = new Vector4(Math.Clamp(CollisionOpacity, 0f, 1f), CollisionOutlines ? 1f : 0f, CollisionEditMode ? 1f : 0f, WallbounceMinTilt);
        uint* words = (uint*)(pc + 16);
        words[0] = (uint)CollisionCategories;
        words[1] = (uint)CollisionColorMode;
        words[2] = CollisionRegionMask;
        words[3] = (uint)CollisionPrimitiveKinds;
        for (int i = 0; i < 8; i++) words[4 + i] = CollisionMaterialMask[i];
        words[12] = CollisionTriangleFlagMask;
        words[13] = words[14] = words[15] = 0;
        *(Vector4*)(pc + 80) = CollisionSelectionColor;

        _api.vkCmdBindPipeline(_cmd, VkPipelineBindPoint.Graphics, _colPipeline);
        _api.vkCmdBindDescriptorSets(_cmd, VkPipelineBindPoint.Graphics, _colLayout, 0, 1, &s0, 0, null);
        VkDescriptorSet s1 = _colWbSet;
        _api.vkCmdBindDescriptorSets(_cmd, VkPipelineBindPoint.Graphics, _colLayout, 1, 1, &s1, 0, null);
        _api.vkCmdPushConstants(_cmd, _colLayout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, CollisionPushSize, pc);
        VkBuffer* buffers = stackalloc VkBuffer[2] { _colVertexBuffer, _colInstanceBuffer };
        ulong* offsets = stackalloc ulong[2] { 0, 0 };
        _api.vkCmdBindVertexBuffers(_cmd, 0, 2, buffers, offsets);
        // One instanced draw per unique shape. Every shape's range starts on a multiple of three, so
        // gl_VertexIndex % 3 is the corner within its triangle.
        foreach (var batch in _colBatches)
            _api.vkCmdDraw(_cmd, batch.VertexCount, batch.InstanceCount, batch.FirstVertex, batch.FirstInstance);
    }

    private void DestroyCollisionBuffers()
    {
        if (_colVertexMapped != null) { _api.vkUnmapMemory(_colVbMemory); _colVertexMapped = null; }
        if (_colInstanceMapped != null) { _api.vkUnmapMemory(_colInstanceMemory); _colInstanceMapped = null; }
        _colVertexCount = _colInstanceCount = 0;
        if (_colVertexBuffer.Handle != 0) { _api.vkDestroyBuffer(_colVertexBuffer); _colVertexBuffer = default; }
        if (_colVbMemory.Handle != 0) { _api.vkFreeMemory(_colVbMemory); _colVbMemory = default; }
        if (_colInstanceBuffer.Handle != 0) { _api.vkDestroyBuffer(_colInstanceBuffer); _colInstanceBuffer = default; }
        if (_colInstanceMemory.Handle != 0) { _api.vkFreeMemory(_colInstanceMemory); _colInstanceMemory = default; }
    }

    private void DestroyCollisionPipeline()
    {
        DestroyCollisionBuffers();
        if (_colWbBuffer.Handle != 0) _api.vkDestroyBuffer(_colWbBuffer);
        if (_colWbMemory.Handle != 0) _api.vkFreeMemory(_colWbMemory);
        if (_colWbPool.Handle != 0) _api.vkDestroyDescriptorPool(_colWbPool);
        if (_colWbSetLayout.Handle != 0) _api.vkDestroyDescriptorSetLayout(_colWbSetLayout);
        if (_colPipeline.Handle != 0) _api.vkDestroyPipeline(_colPipeline);
        if (_colLayout.Handle != 0) _api.vkDestroyPipelineLayout(_colLayout);
        if (_colVs.Handle != 0) _api.vkDestroyShaderModule(_colVs);
        if (_colFs.Handle != 0) _api.vkDestroyShaderModule(_colFs);
    }
}
