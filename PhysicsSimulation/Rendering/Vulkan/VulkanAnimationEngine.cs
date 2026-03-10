// ============================================================
//  VulkanAnimationEngine.cs
//  EurekaSharp — Vulkan Backend  (v2 — оптимизированная версия)
//
//  Изменения относительно v1:
//
//  1. INDIRECT DRAW (критично — draw calls)
//     Вместо N вызовов CmdDrawIndexed — один CmdDrawIndexedIndirect.
//     _bufIndirect[frame] содержит массив VkDrawIndexedIndirectCommand.
//     gl_InstanceIndex в шейдере = firstInstance = PrimitiveId.
//     Результат: при 1000 примитивах — 1 draw call вместо 1000.
//
//  2. PER-FRAME BUFFERS (критично — гонка данных)
//     _bufRenderInstances / _bufAnimEntries / _bufAnimIndex / _bufMorphDesc
//     и _bufIndirect — созданы в MaxFramesInFlight = 2 копиях.
//     CPU пишет в буфер [CurrentFrame] только после WaitForFences.
//     GPU frame N и N-1 никогда не делят один буфер.
//
//  3. PERSISTENT MAPPING (важно — map/unmap overhead)
//     Все per-frame host-visible буферы открыты постоянно через
//     VulkanBuffer.EnablePersistentMap(). Write() = прямой CopyBlock.
//
//  4. DEFERRED BUFFER DELETION (важно — DeviceWaitIdle в рантайме)
//     При росте per-frame буферов старый буфер идёт в _deletionQueue[frame].
//     Удаление происходит в начале следующего кадра с тем же frame slot'ом,
//     когда fence уже сигнализировал завершение GPU.
//     DeviceWaitIdle остаётся ТОЛЬКО для геометрии и при Dispose.
//
//  5. CONDITIONAL GEOMETRY REBUILD (важно — полный rebuild каждый кадр)
//     VulkanSceneGpu.Update теперь делает полный rebuild только если
//     хотя бы один IsDynamic примитив фактически изменил геометрию
//     (т.е. IsGeometryRegistered == false после InvalidateGeometry()).
//
//  6. UPLOAD TIMING (важно — гонка данных)
//     FlushPendingUploads(frame) вызывается из VulkanSceneGpu.Render()
//     ПОСЛЕ BeginFrame() (после WaitForFences), а не в Update().
//     Update() только обновляет CPU-зеркала (_renderInstances, _morphDescs).
//
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using PhysicsSimulation.Base;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;
using Silk.NET.Vulkan;

namespace PhysicsSimulation.Rendering.Vulkan
{
    public sealed unsafe class VulkanAnimationEngine : IDisposable
    {
        // ── Binding slots (совпадают с шейдерами) ────────────────────────────
        private const uint BINDING_ANIM_ENTRIES = 0;
        private const uint BINDING_ANIM_INDEX   = 1;
        private const uint BINDING_MORPH_DESC   = 2;
        private const uint BINDING_GEOMETRY     = 3;
        private const uint BINDING_RENDER_INST  = 4;

        // ── Размеры структур ──────────────────────────────────────────────────
        private const int ANIM_ENTRY_SIZE  = 80;
        private const int ANIM_INDEX_SIZE  = 16;
        private const int MORPH_DESC_SIZE  = 32;
        private const int RENDER_INST_SIZE = 96;

        private const int MaxFrames = VulkanContext.MaxFramesInFlight;

        // ── VkDrawIndexedIndirectCommand (Vulkan spec 20 bytes) ───────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct IndirectDrawCmd
        {
            public uint IndexCount;
            public uint InstanceCount;  // всегда 1
            public uint FirstIndex;     // начало в index buffer
            public int  VertexOffset;   // всегда 0 (смещение читается из inst.meta.x)
            public uint FirstInstance;  // = PrimitiveId → gl_InstanceIndex в шейдере
        }

        // ── Vulkan-объекты ────────────────────────────────────────────────────
        private readonly VulkanContext         _ctx;
        private readonly VulkanMemoryAllocator _vma;
        private readonly GeometryArena         _arena;
        private readonly List<PrimitiveGpu>    _primitives;

        // ── PER-FRAME буферы (CPU пишет каждый кадр) ─────────────────────────
        // Индекс = ctx.CurrentFrame (0..MaxFrames-1)
        private readonly VulkanBuffer[] _bufAnimEntries     = new VulkanBuffer[MaxFrames];
        private readonly VulkanBuffer[] _bufAnimIndex       = new VulkanBuffer[MaxFrames];
        private readonly VulkanBuffer[] _bufMorphDesc       = new VulkanBuffer[MaxFrames];
        private readonly VulkanBuffer[] _bufRenderInstances = new VulkanBuffer[MaxFrames];
        private readonly VulkanBuffer[] _bufIndirect        = new VulkanBuffer[MaxFrames];

        // ── SHARED буферы (только на сцене-изменении, с DeviceWaitIdle) ──────
        private VulkanBuffer _bufGeometry = null!;
        private VulkanBuffer _bufIndex    = null!;
        private VulkanBuffer _bufUniforms = null!;   // binding 5, не используется шейдерами

        // Rebuild index only on topology change, not every frame.
        private bool _indexDirty = true;

        // ── Pipelines ─────────────────────────────────────────────────────────
        private Pipeline       _animComputePipeline;
        private Pipeline       _morphComputePipeline;
        private Pipeline       _graphicsPipeline;
        private PipelineLayout _computeLayout;
        private PipelineLayout _graphicsLayout;

        // ── Descriptors (per-frame) ───────────────────────────────────────────
        private DescriptorSetLayout   _descriptorSetLayout;
        private DescriptorPool        _descriptorPool;
        private readonly DescriptorSet[] _descriptorSets = new DescriptorSet[MaxFrames];

        // ── CPU mirrors ───────────────────────────────────────────────────────
        private MorphDescCpu[]      _morphDescs      = [];
        private RenderInstanceCpu[] _renderInstances = [];
        private List<AnimEntryCpu>  _uploadedAnimEntries = new();

        // ── Dirty tracking для per-frame anim entry upload ────────────────────
        // Bitmask: бит F установлен → нужно залить anim entries в буфер[F]
        private int _animEntriesDirtyMask = 0;

        // ── Deferred deletion (заменяет DeviceWaitIdle при росте буфера) ──────
        private readonly Queue<VulkanBuffer>[] _deletionQueue;

        // ── Geometry staging (CPU-side, переиспользуются между кадрами) ──────
        private float[]  _geometryStagingBuf = [];
        private ushort[] _indexStagingBuf    = [];
        private int[]    _primIndexStart     = [];
        private int[]    _primIndexCount     = [];

        // ── Config ────────────────────────────────────────────────────────────
        public float AspectRatio { get; set; } = 16f / 9f;

        public static string ShaderPath { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "Vulkan");

        // ── DynOverride ───────────────────────────────────────────────────────
        public struct DynOverride
        {
            public int     Pid;
            public float   PosX, PosY;
            public float   Rotation, Scale;
            public Vector4 Color;
            public bool    HasPos, HasRot, HasScale, HasColor;
        }

        private bool _disposed;

        // ─────────────────────────────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────────────────────────────

        public VulkanAnimationEngine(
            VulkanContext ctx,
            VulkanMemoryAllocator vma,
            GeometryArena arena,
            IEnumerable<PrimitiveGpu> primitives)
        {
            _ctx        = ctx;
            _vma        = vma;
            _arena      = arena;
            _primitives = primitives.OrderBy(p => p.PrimitiveId).ToList();

            for (int i = 0; i < _primitives.Count; i++)
                if (_primitives[i].PrimitiveId == -1)
                    _primitives[i].PrimitiveId = i;

            _morphDescs      = new MorphDescCpu[_primitives.Count];
            _renderInstances = new RenderInstanceCpu[_primitives.Count];

            _deletionQueue = new Queue<VulkanBuffer>[MaxFrames];
            for (int f = 0; f < MaxFrames; f++)
                _deletionQueue[f] = new Queue<VulkanBuffer>();

            CreateBuffers();
            CreateDescriptorSetLayout();
            CreateDescriptorPool();
            AllocateAndUpdateAllDescriptorSets();
            CreateComputePipelines();
            CreateGraphicsPipeline();
            InitMorphDescs();
            InitRenderInstances();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  1. Buffer creation
        // ─────────────────────────────────────────────────────────────────────

        private void CreateBuffers()
        {
            int safeCount = Math.Max(1, _primitives.Count);

            // Shared buffers — upload с DeviceWaitIdle (только при смене сцены)
            _bufGeometry = _vma.CreateVertexBuffer(256 * 1024);
            _bufIndex    = _vma.CreateIndexBuffer(256 * 1024);
            _bufUniforms = _vma.CreateUniformBuffer((ulong)sizeof(FramePushConstants));

            // Per-frame buffers — CPU пишет каждый кадр, persistent map
            ulong indirectSize = (ulong)(Math.Max(1, _primitives.Count) * sizeof(IndirectDrawCmd) * 2);
            for (int f = 0; f < MaxFrames; f++)
            {
                _bufAnimEntries[f]     = _vma.CreateStorageBuffer((ulong)(512 * ANIM_ENTRY_SIZE));
                _bufAnimIndex[f]       = _vma.CreateStorageBuffer((ulong)(safeCount * ANIM_INDEX_SIZE));
                _bufMorphDesc[f]       = _vma.CreateStorageBuffer((ulong)(safeCount * MORPH_DESC_SIZE));
                _bufRenderInstances[f] = _vma.CreateStorageBuffer((ulong)(safeCount * RENDER_INST_SIZE));
                _bufIndirect[f]        = _vma.CreateIndirectBuffer(indirectSize);

                // Persistent mapping — Write() не будет делать Map/Unmap каждый кадр
                _bufAnimEntries[f].EnablePersistentMap();
                _bufAnimIndex[f].EnablePersistentMap();
                _bufMorphDesc[f].EnablePersistentMap();
                _bufRenderInstances[f].EnablePersistentMap();
                _bufIndirect[f].EnablePersistentMap();
            }

            DebugManager.Memory($"VulkanAnimationEngine: Буферы созданы ({_primitives.Count} примитивов, {MaxFrames} frames in flight).");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  2. Descriptor set layout
        // ─────────────────────────────────────────────────────────────────────

        private void CreateDescriptorSetLayout()
        {
            var bindings = new DescriptorSetLayoutBinding[]
            {
                MakeBinding(BINDING_ANIM_ENTRIES, DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit),
                MakeBinding(BINDING_ANIM_INDEX,   DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit),
                MakeBinding(BINDING_MORPH_DESC,   DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit),
                MakeBinding(BINDING_GEOMETRY,     DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit | ShaderStageFlags.VertexBit),
                MakeBinding(BINDING_RENDER_INST,  DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit | ShaderStageFlags.VertexBit),
                MakeBinding(5,                    DescriptorType.UniformBuffer,  ShaderStageFlags.VertexBit)
            };

            fixed (DescriptorSetLayoutBinding* p = bindings)
            {
                var info = new DescriptorSetLayoutCreateInfo
                {
                    SType        = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = (uint)bindings.Length,
                    PBindings    = p
                };
                VulkanContext.Check(
                    _ctx.Vk.CreateDescriptorSetLayout(_ctx.Device, &info, null, out _descriptorSetLayout),
                    "CreateDescriptorSetLayout");
            }
        }

        private static DescriptorSetLayoutBinding MakeBinding(
            uint binding, DescriptorType type, ShaderStageFlags stages) =>
            new() { Binding = binding, DescriptorType = type, DescriptorCount = 1, StageFlags = stages };

        // ─────────────────────────────────────────────────────────────────────
        //  3. Descriptor pool + sets
        // ─────────────────────────────────────────────────────────────────────

        private void CreateDescriptorPool()
        {
            var sizes = new DescriptorPoolSize[]
            {
                new() { Type = DescriptorType.StorageBuffer, DescriptorCount = 5u * MaxFrames },
                new() { Type = DescriptorType.UniformBuffer, DescriptorCount = 1u * MaxFrames }
            };

            fixed (DescriptorPoolSize* p = sizes)
            {
                var info = new DescriptorPoolCreateInfo
                {
                    SType         = StructureType.DescriptorPoolCreateInfo,
                    MaxSets       = (uint)MaxFrames,
                    PoolSizeCount = (uint)sizes.Length,
                    PPoolSizes    = p
                };
                VulkanContext.Check(
                    _ctx.Vk.CreateDescriptorPool(_ctx.Device, &info, null, out _descriptorPool),
                    "CreateDescriptorPool");
            }
        }

        private void AllocateAndUpdateAllDescriptorSets()
        {
            // Выделяем MaxFrames дескриптор-сетов за один вызов
            var layouts = new DescriptorSetLayout[MaxFrames];
            for (int f = 0; f < MaxFrames; f++) layouts[f] = _descriptorSetLayout;

            fixed (DescriptorSetLayout* pLayouts = layouts)
            fixed (DescriptorSet* pSets = _descriptorSets)
            {
                var allocInfo = new DescriptorSetAllocateInfo
                {
                    SType              = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool     = _descriptorPool,
                    DescriptorSetCount = (uint)MaxFrames,
                    PSetLayouts        = pLayouts
                };
                VulkanContext.Check(
                    _ctx.Vk.AllocateDescriptorSets(_ctx.Device, &allocInfo, pSets),
                    "AllocateDescriptorSets");
            }

            for (int f = 0; f < MaxFrames; f++)
                WriteDescriptorSet(f);

            DebugManager.Memory($"Descriptors updated OK ({MaxFrames} sets, 6 bindings each)");
        }

        /// <summary>
        /// Записывает/обновляет дескрипторы для конкретного frame slot'а.
        /// Вызывается при инициализации и после роста per-frame буфера.
        /// </summary>
        private void WriteDescriptorSet(int frame)
        {
            var ds = _descriptorSets[frame];

            var biAnimEntries     = BufInfo(_bufAnimEntries[frame]);
            var biAnimIndex       = BufInfo(_bufAnimIndex[frame]);
            var biMorphDesc       = BufInfo(_bufMorphDesc[frame]);
            var biGeometry        = BufInfo(_bufGeometry);
            var biRenderInstances = BufInfo(_bufRenderInstances[frame]);
            var biUniforms        = BufInfo(_bufUniforms);

            var writes = stackalloc WriteDescriptorSet[6];
            writes[0] = StorageWrite(ds, BINDING_ANIM_ENTRIES, &biAnimEntries);
            writes[1] = StorageWrite(ds, BINDING_ANIM_INDEX,   &biAnimIndex);
            writes[2] = StorageWrite(ds, BINDING_MORPH_DESC,   &biMorphDesc);
            writes[3] = StorageWrite(ds, BINDING_GEOMETRY,     &biGeometry);
            writes[4] = StorageWrite(ds, BINDING_RENDER_INST,  &biRenderInstances);
            writes[5] = UniformWrite(ds, 5,                    &biUniforms);

            _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 6, writes, 0, null);
        }

        private static DescriptorBufferInfo BufInfo(VulkanBuffer b) =>
            new() { Buffer = b.Handle, Offset = 0, Range = b.Size };

        private static WriteDescriptorSet StorageWrite(
            DescriptorSet ds, uint binding, DescriptorBufferInfo* bi) => new()
        {
            SType           = StructureType.WriteDescriptorSet,
            DstSet          = ds,
            DstBinding      = binding,
            DescriptorCount = 1,
            DescriptorType  = DescriptorType.StorageBuffer,
            PBufferInfo     = bi
        };

        private static WriteDescriptorSet UniformWrite(
            DescriptorSet ds, uint binding, DescriptorBufferInfo* bi) => new()
        {
            SType           = StructureType.WriteDescriptorSet,
            DstSet          = ds,
            DstBinding      = binding,
            DescriptorCount = 1,
            DescriptorType  = DescriptorType.UniformBuffer,
            PBufferInfo     = bi
        };

        // ─────────────────────────────────────────────────────────────────────
        //  4. Compute + Graphics pipelines (без изменений относительно v1)
        // ─────────────────────────────────────────────────────────────────────

        private void CreateComputePipelines()
        {
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset     = 0,
                Size       = (uint)sizeof(FramePushConstants)
            };

            fixed (DescriptorSetLayout* pDsl = &_descriptorSetLayout)
            {
                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType                  = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount         = 1,
                    PSetLayouts            = pDsl,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges    = &pushRange
                };
                VulkanContext.Check(
                    _ctx.Vk.CreatePipelineLayout(_ctx.Device, &layoutInfo, null, out _computeLayout),
                    "CreatePipelineLayout (compute)");
            }

            _animComputePipeline  = CreateComputePipeline("anim_compute.spv");
            _morphComputePipeline = CreateComputePipeline("morph_compute.spv");

            DebugManager.Scene("VulkanAnimationEngine: Compute pipelines созданы.");
        }

        private Pipeline CreateComputePipeline(string spvFileName)
        {
            var spvPath = Path.Combine(ShaderPath, spvFileName);
            if (!File.Exists(spvPath))
            {
                DebugManager.Warn($"SPIR-V не найден: {spvPath}");
                return default;
            }

            var code = File.ReadAllBytes(spvPath);
            ShaderModule shaderModule;
            fixed (byte* pCode = code)
            {
                var moduleInfo = new ShaderModuleCreateInfo
                {
                    SType    = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode    = (uint*)pCode
                };
                VulkanContext.Check(
                    _ctx.Vk.CreateShaderModule(_ctx.Device, &moduleInfo, null, out shaderModule),
                    $"CreateShaderModule ({spvFileName})");
            }

            byte* entryName = (byte*)Marshal.StringToHGlobalAnsi("main");
            var stageInfo = new PipelineShaderStageCreateInfo
            {
                SType  = StructureType.PipelineShaderStageCreateInfo,
                Stage  = ShaderStageFlags.ComputeBit,
                Module = shaderModule,
                PName  = entryName
            };
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType  = StructureType.ComputePipelineCreateInfo,
                Stage  = stageInfo,
                Layout = _computeLayout
            };

            VulkanContext.Check(
                _ctx.Vk.CreateComputePipelines(_ctx.Device, default, 1, &pipelineInfo, null, out var pipeline),
                $"CreateComputePipeline ({spvFileName})");

            _ctx.Vk.DestroyShaderModule(_ctx.Device, shaderModule, null);
            Marshal.FreeHGlobal((IntPtr)entryName);
            return pipeline;
        }

        private void CreateGraphicsPipeline()
        {
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit,
                Offset     = 0,
                Size       = (uint)sizeof(FramePushConstants)
            };

            fixed (DescriptorSetLayout* pDsl = &_descriptorSetLayout)
            {
                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType                  = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount         = 1,
                    PSetLayouts            = pDsl,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges    = &pushRange
                };
                VulkanContext.Check(
                    _ctx.Vk.CreatePipelineLayout(_ctx.Device, &layoutInfo, null, out _graphicsLayout),
                    "CreatePipelineLayout (graphics)");
            }

            var vertPath = Path.Combine(ShaderPath, "render.vert.spv");
            var fragPath = Path.Combine(ShaderPath, "render.frag.spv");

            if (!File.Exists(vertPath) || !File.Exists(fragPath))
            {
                DebugManager.Warn("Graphics SPIR-V не найдены. Pipeline не создан.");
                return;
            }

            var vertModule = CreateShaderModule(File.ReadAllBytes(vertPath));
            var fragModule = CreateShaderModule(File.ReadAllBytes(fragPath));
            byte* main     = (byte*)Marshal.StringToHGlobalAnsi("main");

            var stages = new PipelineShaderStageCreateInfo[]
            {
                new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit,   Module = vertModule, PName = main },
                new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragModule, PName = main }
            };

            var vertexInputState = new PipelineVertexInputStateCreateInfo
            {
                SType                           = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount   = 0,
                VertexAttributeDescriptionCount = 0
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType                  = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology               = PrimitiveTopology.LineStrip,
                PrimitiveRestartEnable = true
            };

            var dynamicStates = new DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            fixed (DynamicState* pDyn = dynamicStates)
            {
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType             = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)dynamicStates.Length,
                    PDynamicStates    = pDyn
                };
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1
                };
                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType       = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode    = CullModeFlags.None,
                    FrontFace   = FrontFace.CounterClockwise,
                    LineWidth   = 1.0f
                };
                var multisampling = new PipelineMultisampleStateCreateInfo
                {
                    SType                = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    BlendEnable         = true,
                    SrcColorBlendFactor = BlendFactor.SrcAlpha,
                    DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp        = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One,
                    DstAlphaBlendFactor = BlendFactor.Zero,
                    AlphaBlendOp        = BlendOp.Add,
                    ColorWriteMask      = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                         ColorComponentFlags.BBit | ColorComponentFlags.ABit
                };
                var blending = new PipelineColorBlendStateCreateInfo
                {
                    SType           = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments    = &blendAttachment
                };

                fixed (PipelineShaderStageCreateInfo* pStages = stages)
                {
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType               = StructureType.GraphicsPipelineCreateInfo,
                        StageCount          = (uint)stages.Length,
                        PStages             = pStages,
                        PVertexInputState   = &vertexInputState,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState      = &viewportState,
                        PRasterizationState = &rasterizer,
                        PMultisampleState   = &multisampling,
                        PColorBlendState    = &blending,
                        PDynamicState       = &dynamicState,
                        Layout              = _graphicsLayout,
                        RenderPass          = _ctx.RenderPass,
                        Subpass             = 0
                    };
                    VulkanContext.Check(
                        _ctx.Vk.CreateGraphicsPipelines(_ctx.Device, default, 1, &pipelineInfo, null, out _graphicsPipeline),
                        "CreateGraphicsPipeline");
                }
            }

            _ctx.Vk.DestroyShaderModule(_ctx.Device, vertModule, null);
            _ctx.Vk.DestroyShaderModule(_ctx.Device, fragModule, null);
            Marshal.FreeHGlobal((IntPtr)main);

            DebugManager.Scene("VulkanAnimationEngine: Graphics pipeline создан.");
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            fixed (byte* pCode = code)
            {
                var info = new ShaderModuleCreateInfo
                {
                    SType    = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode    = (uint*)pCode
                };
                VulkanContext.Check(
                    _ctx.Vk.CreateShaderModule(_ctx.Device, &info, null, out var module),
                    "CreateShaderModule");
                return module;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  5. Geometry + Index upload (SHARED буферы)
        //     HOT PATH: copy XY coords → _bufGeometry
        //     COLD PATH (_indexDirty): build restart-index table → _bufIndex
        // ─────────────────────────────────────────────────────────────────────

        public void UploadGeometryFromPrimitives()
        {
            if (_disposed) return;
            int totalVerts = _arena.TotalVertexCount;
            if (totalVerts <= 0) return;

            int primCount  = _primitives.Count;
            int geomNeeded = totalVerts * 2;

            if (_geometryStagingBuf.Length < geomNeeded)
                _geometryStagingBuf = new float[geomNeeded];

            // HOT: copy positions
            foreach (var p in _primitives)
            {
                if (p.VertexOffsetRaw < 0 || p.VertexCount <= 0) continue;
                var cached = p.GetVertices();
                if (cached is not { Length: > 0 }) continue;
                int offsetRaw = p.VertexOffsetRaw;
                int limit     = Math.Min(cached.Length, p.VertexCount);
                for (int k = 0; k < limit; k++)
                {
                    int idx = (offsetRaw + k) * 2;
                    _geometryStagingBuf[idx]     = cached[k].X;
                    _geometryStagingBuf[idx + 1] = cached[k].Y;
                }
            }

            // COLD: rebuild index topology
            if (_indexDirty)
            {
                if (_indexStagingBuf.Length < totalVerts) _indexStagingBuf = new ushort[totalVerts];
                if (_primIndexStart.Length < primCount)
                {
                    _primIndexStart = new int[primCount];
                    _primIndexCount = new int[primCount];
                }

                int cur = 0;
                foreach (var p in _primitives)
                {
                    int pid = p.PrimitiveId;
                    if (p.VertexOffsetRaw < 0 || p.VertexCount <= 0)
                    {
                        if (pid >= 0 && pid < primCount) { _primIndexStart[pid] = cur; _primIndexCount[pid] = 0; }
                        continue;
                    }
                    int offsetRaw = p.VertexOffsetRaw;
                    int limit     = p.VertexCount;
                    int start     = cur;
                    for (int k = 0; k < limit; k++)
                    {
                        float vx = _geometryStagingBuf[(offsetRaw + k) * 2];
                        _indexStagingBuf[cur++] = float.IsNaN(vx) ? (ushort)0xFFFF : (ushort)k;
                    }
                    if (pid >= 0 && pid < primCount) { _primIndexStart[pid] = start; _primIndexCount[pid] = cur - start; }
                }

                ulong idxReq = (ulong)(cur * sizeof(ushort));
                if (idxReq > _bufIndex.Size)
                {
                    // Geometry/index buffer growth: DeviceWaitIdle OK (only on scene change)
                    _ctx.Vk.DeviceWaitIdle(_ctx.Device);
                    _bufIndex.Dispose();
                    _bufIndex = _vma.CreateIndexBuffer(idxReq * 4);
                }
                _vma.Upload(_bufIndex, new ReadOnlySpan<ushort>(_indexStagingBuf, 0, cur));
                _indexDirty = false;
            }

            ulong geomReq = (ulong)(geomNeeded * sizeof(float));
            if (geomReq > _bufGeometry.Size)
            {
                _ctx.Vk.DeviceWaitIdle(_ctx.Device);
                _bufGeometry.Dispose();
                _bufGeometry = _vma.CreateVertexBuffer(geomReq * 4);
                // После роста геометрического буфера — обновить binding во ВСЕХ дескрипторных сетах
                UpdateGeometryBindingAllFrames();
            }
            _vma.Upload(_bufGeometry, new ReadOnlySpan<float>(_geometryStagingBuf, 0, geomNeeded));
        }

        /// <summary>
        /// Обновляет binding GEOMETRY во всех per-frame descriptor sets после роста буфера.
        /// </summary>
        private void UpdateGeometryBindingAllFrames()
        {
            var bufInfo = BufInfo(_bufGeometry);
            for (int f = 0; f < MaxFrames; f++)
            {
                var write = StorageWrite(_descriptorSets[f], BINDING_GEOMETRY, &bufInfo);
                _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  6. Descriptor rebuild (вызывается из VulkanSceneGpu при смене сцены)
        // ─────────────────────────────────────────────────────────────────────

        public void RebuildAllDescriptors()
        {
            // На смене сцены DeviceWaitIdle допустим — это не рантайм операция.
            _ctx.Vk.DeviceWaitIdle(_ctx.Device);
            _indexDirty = true;
            InitMorphDescs();
            InitRenderInstances();

            // Заливаем начальное состояние во все frame slots сразу
            for (int f = 0; f < MaxFrames; f++)
            {
                UploadMorphDescs(f);
                UploadRenderInstances(f);
            }

            // Все frame slots нуждаются в обновлении anim entries при следующей возможности
            _animEntriesDirtyMask = (1 << MaxFrames) - 1;
        }

        private void InitMorphDescs()
        {
            for (int i = 0; i < _primitives.Count; i++)
            {
                var p = _primitives[i];
                _morphDescs[i] = new MorphDescCpu
                {
                    OffsetA     = p.VertexOffsetA >= 0 ? p.VertexOffsetA : p.VertexOffsetRaw,
                    OffsetB     = p.VertexOffsetB >= 0 ? p.VertexOffsetB : p.VertexOffsetRaw,
                    OffsetM     = p.VertexOffsetM >= 0 ? p.VertexOffsetM : p.VertexOffsetRaw,
                    VertexCount = p.VertexCount
                };
            }
        }

        private void InitRenderInstances()
        {
            for (int i = 0; i < _primitives.Count; i++)
                _renderInstances[i] = _primitives[i].ToRenderInstanceCpu();
        }

        private void UploadMorphDescs(int frame)
        {
            if (_disposed || _morphDescs.Length == 0) return;
            GrowIfNeeded(ref _bufMorphDesc[frame], frame,
                (ulong)(_morphDescs.Length * MORPH_DESC_SIZE),
                BINDING_MORPH_DESC, DescriptorType.StorageBuffer,
                sz => { var b = _vma.CreateStorageBuffer(sz); b.EnablePersistentMap(); return b; });
            _vma.Upload(_bufMorphDesc[frame], _morphDescs);
        }

        private void UploadRenderInstances(int frame)
        {
            if (_disposed || _renderInstances.Length == 0) return;
            GrowIfNeeded(ref _bufRenderInstances[frame], frame,
                (ulong)(_renderInstances.Length * RENDER_INST_SIZE),
                BINDING_RENDER_INST, DescriptorType.StorageBuffer,
                sz => { var b = _vma.CreateStorageBuffer(sz); b.EnablePersistentMap(); return b; });
            _vma.Upload(_bufRenderInstances[frame], _renderInstances);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  7. Per-frame flush (вызывается из VulkanSceneGpu.Render ПОСЛЕ BeginFrame)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Сначала освобождаем буферы прошлых кадров для этого slot'а (fence уже завершён),
        /// затем заливаем CPU-данные в GPU-буферы текущего кадра.
        /// Вызывать из Render() сразу после BeginFrame().
        /// </summary>
        public void FlushPendingUploads(int frame)
        {
            if (_disposed) return;

            // 7a. Deferred deletion: fence уже сигнализирован для этого slot'а
            FlushDeletionQueue(frame);

            // 7b. Render instances — всегда, т.к. DynCallbacks могут изменить состояние
            UploadRenderInstances(frame);

            // 7c. Morph descs — всегда (compute шейдер читает offsets + initial t)
            UploadMorphDescs(frame);

            // 7d. Anim entries — только если есть новые entries для этого frame slot'а
            if ((_animEntriesDirtyMask & (1 << frame)) != 0)
            {
                UploadAnimEntries(frame);
                UploadAnimIndex(frame);
                _animEntriesDirtyMask &= ~(1 << frame);
            }
        }

        private void FlushDeletionQueue(int frame)
        {
            while (_deletionQueue[frame].TryDequeue(out var buf))
                buf.Dispose();
        }

        private void UploadAnimEntries(int frame)
        {
            if (_uploadedAnimEntries.Count == 0) return;
            var bytes = PrimitiveGpu.SerializeAnimEntries(_uploadedAnimEntries);
            ulong required = (ulong)bytes.Length;

            GrowIfNeeded(ref _bufAnimEntries[frame], frame, required,
                BINDING_ANIM_ENTRIES, DescriptorType.StorageBuffer,
                sz => { var b = _vma.CreateStorageBuffer(sz * 2); b.EnablePersistentMap(); return b; });

            _vma.Upload(_bufAnimEntries[frame], bytes);
        }

        private void UploadAnimIndex(int frame)
        {
            int count = _primitives.Count;
            if (count == 0) return;
            var index = PrimitiveGpu.BuildAnimIndex(_uploadedAnimEntries, count);

            GrowIfNeeded(ref _bufAnimIndex[frame], frame,
                (ulong)(count * ANIM_INDEX_SIZE),
                BINDING_ANIM_INDEX, DescriptorType.StorageBuffer,
                sz => { var b = _vma.CreateStorageBuffer(sz * 2); b.EnablePersistentMap(); return b; });

            _vma.Upload(_bufAnimIndex[frame], index);
        }

        /// <summary>
        /// Растим per-frame буфер без DeviceWaitIdle:
        /// старый буфер идёт в очередь удаления этого frame slot'а,
        /// удалится когда fence[frame] следующий раз будет сигнализирован.
        /// </summary>
        private void GrowIfNeeded(
            ref VulkanBuffer buf, int frame, ulong required,
            uint binding, DescriptorType descType,
            Func<ulong, VulkanBuffer> factory)
        {
            if (required <= buf.Size) return;

            // Старый буфер — в очередь удаления (fence ещё не сигнализирован)
            _deletionQueue[frame].Enqueue(buf);

            // Создаём новый с запасом ×2
            buf = factory(required * 2);

            // Обновляем дескриптор для этого frame slot'а
            var bi    = BufInfo(buf);
            var write = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _descriptorSets[frame],
                DstBinding      = binding,
                DescriptorCount = 1,
                DescriptorType  = descType,
                PBufferInfo     = &bi
            };
            _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  8. Animation accumulation (вызывается из Update — только CPU)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Собирает новые анимационные entries из примитивов.
        /// НЕ заливает на GPU — только помечает все frame slots как dirty.
        /// Заливка произойдёт в FlushPendingUploads() при следующем Render.
        /// </summary>
        public void UploadPendingAnimationsAndIndex()
        {
            if (_disposed) return;
            bool hasNew = false;

            foreach (var prim in _primitives)
            {
                for (int i = 0; i < prim.PendingAnimations.Count; i++)
                {
                    var entry = prim.PendingAnimations[i];
                    if (!entry.PendingOnGpu) continue;
                    if (entry.PrimitiveId < 0 || entry.PrimitiveId >= _primitives.Count) continue;

                    _uploadedAnimEntries.Add(entry);
                    entry.PendingOnGpu = false;
                    prim.PendingAnimations[i] = entry;
                    hasNew = true;
                }
            }

            if (hasNew)
                _animEntriesDirtyMask = (1 << MaxFrames) - 1;  // все frames нуждаются в обновлении
        }

        // ─────────────────────────────────────────────────────────────────────
        //  9. DynOverrides (только CPU — запись в _renderInstances[])
        // ─────────────────────────────────────────────────────────────────────

        public void ApplyDynOverrides(List<DynOverride> overrides)
        {
            if (_disposed) return;
            foreach (var ov in overrides)
            {
                if (ov.Pid < 0 || ov.Pid >= _renderInstances.Length) continue;
                ref var inst = ref _renderInstances[ov.Pid];

                if (ov.HasPos)
                    inst.TransformRow2 = new Vector4(ov.PosX, ov.PosY, inst.TransformRow2.Z, inst.TransformRow2.W);

                if (ov.HasRot)
                {
                    float c  = MathF.Cos(ov.Rotation), s = MathF.Sin(ov.Rotation);
                    float sc = MathF.Sqrt(inst.TransformRow0.X * inst.TransformRow0.X +
                                          inst.TransformRow0.Y * inst.TransformRow0.Y);
                    if (sc < 1e-6f) sc = 1f;
                    inst.TransformRow0 = new Vector4(sc * c,  sc * s, 0, 0);
                    inst.TransformRow1 = new Vector4(-sc * s, sc * c, 0, 0);
                }
                if (ov.HasScale)
                {
                    float c = MathF.Cos(ov.Rotation), s = MathF.Sin(ov.Rotation);
                    inst.TransformRow0 = new Vector4(ov.Scale * c,    ov.Scale * s,  0, 0);
                    inst.TransformRow1 = new Vector4(-ov.Scale * s,   ov.Scale * c,  0, 0);
                }
                if (ov.HasColor)
                    inst.Color = ov.Color;
            }
            // НЕ вызываем UploadRenderInstances здесь — это сделает FlushPendingUploads(frame)
        }

        // ─────────────────────────────────────────────────────────────────────
        //  10. Compute dispatch (вызывается из RecordCommandBuffer до RenderPass)
        // ─────────────────────────────────────────────────────────────────────

        public void RecordComputeCommands(CommandBuffer cmd, int frame, float time)
        {
            if (_primitives.Count == 0) return;
            if (_uploadedAnimEntries.Count == 0) return;
            if (_animComputePipeline.Handle == 0) return;

            var ds = _descriptorSets[frame];

            // ── 1. Anim compute: один dispatch на все примитивы ───────────────
            _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _animComputePipeline);
            _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _computeLayout, 0, 1, &ds, 0, null);

            var animPush = new FramePushConstants { Time = time, AspectRatio = AspectRatio, PrimIndex = -1 };
            _ctx.Vk.CmdPushConstants(cmd, _computeLayout,
                ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FramePushConstants), &animPush);

            uint animGroups = (uint)Math.Max(1, (_primitives.Count + 63) / 64);
            _ctx.Vk.CmdDispatch(cmd, animGroups, 1, 1);

            // ── 2. Барьер: anim → morph ───────────────────────────────────────
            var ssboBarrier = new MemoryBarrier
            {
                SType         = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
            };
            _ctx.Vk.CmdPipelineBarrier(cmd,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.ComputeShaderBit,
                DependencyFlags.None,
                1, &ssboBarrier, 0, null, 0, null);

            // ── 3. Morph compute: по одному dispatch на примитив с морф-таргетами ──
            if (_morphComputePipeline.Handle != 0)
            {
                bool pipelineBound = false;
                foreach (var p in _primitives)
                {
                    if (p.VertexOffsetA < 0) continue;
                    if (!pipelineBound)
                    {
                        _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _morphComputePipeline);
                        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _computeLayout, 0, 1, &ds, 0, null);
                        pipelineBound = true;
                    }

                    var morphPush = new FramePushConstants { Time = time, AspectRatio = AspectRatio, PrimIndex = p.PrimitiveId };
                    _ctx.Vk.CmdPushConstants(cmd, _computeLayout,
                        ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FramePushConstants), &morphPush);

                    uint morphGroups = (uint)Math.Max(1u, ((uint)p.VertexCount + 255) / 256);
                    _ctx.Vk.CmdDispatch(cmd, morphGroups, 1, 1);
                }
            }

            // ── 4. Финальный барьер: compute → vertex shader ──────────────────
            var vsBarrier = new MemoryBarrier
            {
                SType         = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit
            };
            _ctx.Vk.CmdPipelineBarrier(cmd,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.VertexShaderBit,
                DependencyFlags.None,
                1, &vsBarrier, 0, null, 0, null);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  11. RenderAll — ОДИН CmdDrawIndexedIndirect вместо N CmdDrawIndexed
        // ─────────────────────────────────────────────────────────────────────

        public void RenderAll(CommandBuffer cmd, int frame)
        {
            if (_disposed) return;
            if (_graphicsPipeline.Handle == 0) return;

            _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _graphicsPipeline);

            var viewport = new Viewport
            {
                X = 0, Y = 0,
                Width    = _ctx.SwapchainExtent.Width,
                Height   = _ctx.SwapchainExtent.Height,
                MinDepth = 0f, MaxDepth = 1f
            };
            var scissor = new Rect2D { Offset = new Offset2D(0, 0), Extent = _ctx.SwapchainExtent };
            _ctx.Vk.CmdSetViewport(cmd, 0, 1, &viewport);
            _ctx.Vk.CmdSetScissor(cmd, 0, 1, &scissor);

            var ds = _descriptorSets[frame];
            _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics,
                    _graphicsLayout, 0, 1, &ds, 0, null);

            _ctx.Vk.CmdBindIndexBuffer(cmd, _bufIndex.Handle, 0, IndexType.Uint16);

            // Один push constants для всего draw — primIndex не нужен (используется gl_InstanceIndex)
            var push = new FramePushConstants { AspectRatio = AspectRatio, PrimIndex = 0, Time = 0f };
            _ctx.Vk.CmdPushConstants(cmd, _graphicsLayout,
                ShaderStageFlags.VertexBit, 0, (uint)sizeof(FramePushConstants), &push);

            // Строим indirect buffer: одна запись на примитив с ненулевой геометрией
            int primCount = _primitives.Count;
            var cmds      = stackalloc IndirectDrawCmd[primCount];
            int drawCount = 0;

            for (int i = 0; i < primCount; i++)
            {
                var p   = _primitives[i];
                int pid = p.PrimitiveId;
                if (p.VertexCount <= 0 || pid < 0) continue;

                int idxCount = pid < _primIndexCount.Length ? _primIndexCount[pid] : 0;
                if (idxCount <= 0) continue;

                cmds[drawCount++] = new IndirectDrawCmd
                {
                    IndexCount    = (uint)idxCount,
                    InstanceCount = 1,
                    FirstIndex    = (uint)(pid < _primIndexStart.Length ? _primIndexStart[pid] : 0),
                    VertexOffset  = 0,
                    FirstInstance = (uint)i   // → gl_InstanceIndex в render.vert
                };
            }

            if (drawCount == 0) return;

            // Заливаем indirect buffer для текущего frame slot'а
            // (persistent mapped → просто memcpy)
            _bufIndirect[frame].Write(new ReadOnlySpan<IndirectDrawCmd>(cmds, drawCount));

            // Один вызов вместо N — это и есть главная оптимизация
            _ctx.Vk.CmdDrawIndexedIndirect(
                cmd,
                _bufIndirect[frame].Handle,
                offset:    0,
                drawCount: (uint)drawCount,
                stride:    (uint)sizeof(IndirectDrawCmd));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  12. Swapchain recreate
        // ─────────────────────────────────────────────────────────────────────

        public void NotifySwapchainRecreated(VulkanContext ctx)
        {
            if (_graphicsPipeline.Handle != 0)
                ctx.Vk.DestroyPipeline(ctx.Device, _graphicsPipeline, null);
            CreateGraphicsPipeline();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  13. Dispose
        // ─────────────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _ctx.Vk.DeviceWaitIdle(_ctx.Device);

            // Flush all deferred deletions
            for (int f = 0; f < MaxFrames; f++)
                FlushDeletionQueue(f);

            if (_computeLayout.Handle   != 0) _ctx.Vk.DestroyPipelineLayout(_ctx.Device, _computeLayout,   null);
            if (_graphicsLayout.Handle  != 0) _ctx.Vk.DestroyPipelineLayout(_ctx.Device, _graphicsLayout,  null);
            if (_animComputePipeline.Handle  != 0) _ctx.Vk.DestroyPipeline(_ctx.Device, _animComputePipeline,  null);
            if (_morphComputePipeline.Handle != 0) _ctx.Vk.DestroyPipeline(_ctx.Device, _morphComputePipeline, null);
            if (_graphicsPipeline.Handle     != 0) _ctx.Vk.DestroyPipeline(_ctx.Device, _graphicsPipeline,     null);

            _ctx.Vk.DestroyDescriptorPool(_ctx.Device, _descriptorPool, null);
            _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _descriptorSetLayout, null);

            // Shared buffers
            _bufGeometry?.Dispose();
            _bufIndex?.Dispose();
            _bufUniforms?.Dispose();

            // Per-frame buffers
            for (int f = 0; f < MaxFrames; f++)
            {
                _bufAnimEntries[f]?.Dispose();
                _bufAnimIndex[f]?.Dispose();
                _bufMorphDesc[f]?.Dispose();
                _bufRenderInstances[f]?.Dispose();
                _bufIndirect[f]?.Dispose();
            }
        }
    }
}